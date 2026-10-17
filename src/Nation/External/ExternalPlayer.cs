using System.Security.Cryptography;
using Nation.Audio;
using Nation.Library;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Nation.External;

/// <summary>
/// Follows whatever other app is playing (Spotify, YouTube Music in a browser…)
/// through Windows' global media session, so Nation can act as its skin: the app
/// keeps streaming and decoding, Nation shows its track, cover, progress and lyrics
/// and sends play/pause/next/seek back. Session choice is the same as Shade.Agent's.
/// WinRT events arrive on their own threads; they publish an immutable State that
/// the UI thread copies once per frame in Refresh().
/// </summary>
public sealed class ExternalPlayer : IPlayback, IDisposable
{
    /// <summary>External track ids start here so they never collide with library scan indexes.</summary>
    public const int IdBase = 1_000_000;

    private sealed record State(
        GlobalSystemMediaTransportControlsSession? Session,
        string? AppName,
        string? Process,
        bool Browser,
        Track? Track,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus Status,
        double Position,
        DateTime PositionUtc,
        bool? Shuffle,
        MediaPlaybackAutoRepeatMode? Repeat);

    private static readonly State Empty = new(null, null, null, false, null,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed, 0, DateTime.MinValue, null, null);

    private readonly string _ownName;
    private readonly object _lock = new();
    private readonly AppVolume _volume = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    // Every session gets a PlaybackInfoChanged hook: Windows raises SessionsChanged only
    // on add/remove, not when a background session starts playing.
    private readonly List<GlobalSystemMediaTransportControlsSession> _watched = new();
    private volatile State _state = Empty;
    private int _trackRequest;
    private int _nextId = IdBase;

    public AutoResetEvent Wake { get; } = new(false);
    public string? Error { get; private set; }

    private ExternalPlayer()
    {
        // Nation publishes its own media session; never follow ourselves.
        _ownName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "Nation";
    }

    /// <summary>Starts watching in the background (startup is not delayed); null on systems without SMTC.</summary>
    public static ExternalPlayer? TryStart()
    {
        try
        {
            var player = new ExternalPlayer();
            _ = player.InitAsync();
            return player;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Diagnostic for --sessions: every media session Windows reports, one per line.</summary>
    public static async Task<string> DescribeSessionsAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var current = manager.GetCurrentSession()?.SourceAppUserModelId;
        var lines = new List<string>();
        foreach (var s in manager.GetSessions())
        {
            string status, text;
            try { status = s.GetPlaybackInfo().PlaybackStatus.ToString(); } catch (Exception) { status = "?"; }
            try { var p = await s.TryGetMediaPropertiesAsync(); text = $"{p.Title} · {p.Artist} · {p.AlbumTitle}"; } catch (Exception) { text = "?"; }
            string id = s.SourceAppUserModelId ?? "";
            lines.Add($"{(id == current ? "*" : " ")} {id} [{FriendlyAppName(id)} / proceso {AppVolume.ProcessNameFor(id)}] {status}: {text}");
        }
        return lines.Count == 0 ? "(ninguna sesión de medios)" : string.Join(Environment.NewLine, lines);
    }

    private async Task InitAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            lock (_lock)
            {
                _manager = manager;
                manager.CurrentSessionChanged += (_, _) => Reattach();
                manager.SessionsChanged += (_, _) => { lock (_lock) { WatchSessions(); AttachBest(); } };
                WatchSessions();
                AttachBest();
            }
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    private void Reattach() { lock (_lock) AttachBest(); }

    private void WatchSessions()
    {
        foreach (var s in _watched) s.PlaybackInfoChanged -= OnAnyPlaybackInfoChanged;
        _watched.Clear();
        if (_manager is null) return;
        foreach (var s in _manager.GetSessions())
        {
            s.PlaybackInfoChanged += OnAnyPlaybackInfoChanged;
            _watched.Add(s);
        }
    }

    private void OnAnyPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => Reattach();

    // Browsers count as players too (any tab with media); when several sessions play, music apps win.
    private static readonly string[] BrowserHints = { "brave", "chrome", "msedge", "firefox", "opera", "308046B0AF4A39CB" };

    private bool IsOwn(GlobalSystemMediaTransportControlsSession s) =>
        (s.SourceAppUserModelId ?? "").Contains(_ownName, StringComparison.OrdinalIgnoreCase);

    private static bool IsBrowser(GlobalSystemMediaTransportControlsSession s) =>
        BrowserHints.Any(h => (s.SourceAppUserModelId ?? "").Contains(h, StringComparison.OrdinalIgnoreCase));

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch (Exception) { return false; } // a session that just closed throws
    }

    /// <summary>
    /// Sticky to the attached session while it plays; else a playing music app, a playing
    /// browser, then Windows' own pick (the app last touched), then any other session.
    /// </summary>
    private GlobalSystemMediaTransportControlsSession? SelectBest()
    {
        if (_manager is null) return null;
        var sessions = _manager.GetSessions().Where(s => !IsOwn(s)).ToList();
        if (_session is not null && sessions.Any(s => s.SourceAppUserModelId == _session.SourceAppUserModelId) && IsPlaying(_session))
            return _session;
        var playing = sessions.Where(IsPlaying).ToList();
        var current = _manager.GetCurrentSession();
        if (current is not null && IsOwn(current)) current = null;
        return playing.FirstOrDefault(s => !IsBrowser(s))
            ?? playing.FirstOrDefault()
            // Keep showing the attached app while it's paused instead of hopping around.
            ?? sessions.FirstOrDefault(s => s.SourceAppUserModelId == _session?.SourceAppUserModelId)
            ?? current
            ?? sessions.FirstOrDefault();
    }

    private void AttachBest()
    {
        GlobalSystemMediaTransportControlsSession? best;
        try { best = SelectBest(); }
        catch (Exception ex) { Error = ex.Message; return; }
        if (best is not null && best.SourceAppUserModelId == _session?.SourceAppUserModelId) return;
        Detach();
        if (best is null)
        {
            Publish(Empty);
            return;
        }
        Attach(best);
    }

    private void Attach(GlobalSystemMediaTransportControlsSession session)
    {
        _session = session;
        var appId = session.SourceAppUserModelId ?? "";
        Publish(Empty with { Session = session, AppName = FriendlyAppName(appId), Process = AppVolume.ProcessNameFor(appId), Browser = IsBrowser(session) });
        session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        session.PlaybackInfoChanged += OnPlaybackChanged;
        session.TimelinePropertiesChanged += OnTimelineChanged;
        ReadPlayback(session);
        _ = ReadTrackAsync(session);
    }

    private void Detach()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackChanged;
        _session.TimelinePropertiesChanged -= OnTimelineChanged;
        _session = null;
    }

    // Handlers re-read through _session rather than the sender: the projection may wrap
    // the same native session in a different object per event.
    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e)
    {
        var session = _session;
        if (session is not null) _ = ReadTrackAsync(session);
    }

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => ReadPlaybackLocked();
    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => ReadPlaybackLocked();

    private void ReadPlaybackLocked()
    {
        lock (_lock) if (_session is { } session) ReadPlayback(session);
    }

    private void Publish(State state)
    {
        _state = state;
        Wake.Set();
    }

    /// <summary>Status, timeline, shuffle/repeat. Caller holds the lock.</summary>
    private void ReadPlayback(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var info = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var s = _state;
            // Readers extrapolate from the moment the app last stamped its position.
            var stamped = timeline.LastUpdatedTime.UtcDateTime;
            if (stamped == DateTime.MinValue || stamped > DateTime.UtcNow) stamped = DateTime.UtcNow;
            double duration = (timeline.EndTime - timeline.StartTime).TotalSeconds;
            var track = s.Track;
            // The timeline often lands after the title: fill in (or correct) the duration, same track id.
            if (track is not null && duration > 0 && Math.Abs(track.Duration.TotalSeconds - duration) > 1)
                track = track with { Duration = TimeSpan.FromSeconds(duration) };
            Publish(s with
            {
                Track = track,
                Status = info.PlaybackStatus,
                Position = (timeline.Position - timeline.StartTime).TotalSeconds,
                PositionUtc = stamped,
                Shuffle = info.IsShuffleActive,
                Repeat = info.AutoRepeatMode,
            });
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    private async Task ReadTrackAsync(GlobalSystemMediaTransportControlsSession session)
    {
        int request = Interlocked.Increment(ref _trackRequest);
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props is null) return;
            var cover = await ReadCoverAsync(props.Thumbnail);
            double duration;
            try { var t = session.GetTimelineProperties(); duration = (t.EndTime - t.StartTime).TotalSeconds; }
            catch (Exception) { duration = 0; }

            lock (_lock)
            {
                // A newer change (or another session) already won the race.
                if (request != _trackRequest || !ReferenceEquals(session, _session)) return;
                string title = Clean(props.Title), artist = Clean(props.Artist), album = Clean(props.AlbumTitle);
                // Apps blank their metadata for a moment between songs; keep the last one up.
                if (title.Length == 0) return;
                // Some players (browsers) leave the artist in AlbumArtist only.
                if (artist.Length == 0) artist = Clean(props.AlbumArtist);

                var s = _state;
                var old = s.Track;
                bool same = old is not null && old.Title == title && old.Artist == artist && old.Album == album;
                // The cover often comes in a second event; never drop one we already have.
                var (bytes, key) = cover ?? (same ? (old!.Cover, old.CoverKey) : (null, null));
                if (same && key == old!.CoverKey && (duration <= 0 || Math.Abs(old.Duration.TotalSeconds - duration) <= 1)) return;

                var track = new Track("", title, artist, album, 0,
                    TimeSpan.FromSeconds(Math.Max(0, duration > 0 ? duration : old is not null && same ? old.Duration.TotalSeconds : 0)),
                    $"vía {s.AppName}", 0, 0, 0)
                {
                    Id = same ? old!.Id : Interlocked.Increment(ref _nextId),
                    Source = s.AppName ?? "otra app",
                    Cover = bytes,
                    CoverKey = key,
                };
                Publish(s with { Track = track });
            }
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    private static async Task<(byte[]? Bytes, string? Key)?> ReadCoverAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return null;
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size == 0) return null;
            using var reader = new DataReader(stream);
            uint size = (uint)stream.Size;
            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            return (bytes, "smtc:" + Convert.ToHexString(SHA1.HashData(bytes))[..16]);
        }
        catch (Exception) { return null; }
    }

    // Some apps prefix metadata with a byte-order mark or pad it with whitespace.
    private static string Clean(string? text) => (text ?? "").Replace("﻿", "").Trim();

    // ── IPlayback ──────────────────────────────────────────────────────

    private State _shown = Empty;
    private double _position;
    private (float Volume, bool Muted)? _appVolume;
    private DateTime _volumeReadUtc = DateTime.MinValue;
    private int _volumeReading;

    /// <summary>True while another app has a media session Nation can follow.</summary>
    public bool HasSession => _shown.Session is not null;
    public string? AppName => _shown.AppName;

    /// <summary>
    /// Worth following on its own: a music app, or a browser tab with music metadata
    /// (YouTube Music reports an album; a regular YouTube video doesn't).
    /// </summary>
    public bool LooksLikeMusic => !_shown.Browser || _shown.Track is { Album.Length: > 0 };

    public void Refresh()
    {
        var s = _state;
        if (!ReferenceEquals(s.Session, _shown.Session)) _volumeReadUtc = DateTime.MinValue;
        _shown = s;
        double pos = s.Position;
        if (s.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing && s.PositionUtc != DateTime.MinValue)
            pos += (DateTime.UtcNow - s.PositionUtc).TotalSeconds;
        double duration = Duration;
        _position = duration > 0 ? Math.Clamp(pos, 0, duration) : Math.Max(0, pos);
    }

    public Track? Current => _shown.Track;
    public bool Playing => _shown.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    public bool Paused => _shown.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
    public bool Idle => !Playing && !Paused;
    public bool Shuffled => _shown.Shuffle == true;
    public bool Repeat => _shown.Repeat is { } r && r != MediaPlaybackAutoRepeatMode.None;
    public double Position => _position;
    public double Duration => _shown.Track?.Duration.TotalSeconds ?? 0;

    public double Volume { get { PollVolume(); return _appVolume is { } v ? Math.Round(v.Volume * 100) : double.NaN; } }
    public bool Muted { get { PollVolume(); return _appVolume?.Muted == true; } }

    /// <summary>The mixer is read off the UI thread every 2 s, only while the volume is on screen.</summary>
    private void PollVolume()
    {
        if (DateTime.UtcNow - _volumeReadUtc < TimeSpan.FromSeconds(2) || _shown.Process is not { } process) return;
        if (Interlocked.Exchange(ref _volumeReading, 1) == 1) return;
        _volumeReadUtc = DateTime.UtcNow;
        _ = Task.Run(() =>
        {
            try { _appVolume = _volume.Read(process); }
            finally { Volatile.Write(ref _volumeReading, 0); }
        });
    }

    public void TogglePause() => Send(s => s.TryTogglePlayPauseAsync());
    public void SetPaused(bool paused) => Send(s => paused ? s.TryPauseAsync() : s.TryPlayAsync());
    public void Next() => Send(s => s.TrySkipNextAsync());
    public void Previous() => Send(s => s.TrySkipPreviousAsync());
    public void SeekRelative(double seconds) => SeekAbsolute(_position + seconds);

    public void SeekAbsolute(double seconds)
    {
        double target = Math.Max(0, seconds);
        if (Duration > 0) target = Math.Min(target, Duration - 0.5);
        Send(s => s.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(target).Ticks), accepted =>
        {
            // Show the new spot right away; the app's own timeline update follows.
            lock (_lock) if (accepted && ReferenceEquals(_state.Session, _shown.Session))
                Publish(_state with { Position = target, PositionUtc = DateTime.UtcNow });
        });
    }

    public void ToggleShuffle()
    {
        if (_shown.Shuffle is not { } on) return; // the app doesn't expose shuffle
        Send(s => s.TryChangeShuffleActiveAsync(!on));
    }

    /// <summary>Nation's repeat is on/off; Spotify-style apps cycle off → list → track → off.</summary>
    public void ToggleRepeat()
    {
        if (_shown.Repeat is not { } mode) return;
        var next = mode switch
        {
            MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List,
            MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
            _ => MediaPlaybackAutoRepeatMode.None,
        };
        Send(s => s.TryChangeAutoRepeatModeAsync(next));
    }

    public void ToggleMute()
    {
        bool mute = !Muted;
        ChangeVolume(v => v.Mute = mute, cached => (cached.Volume, mute));
    }

    public void AddVolume(int delta)
    {
        float Step(float v) => Math.Clamp((float)Math.Round(v * 100 + delta) / 100f, 0, 1);
        ChangeVolume(v => v.Volume = Step(v.Volume), cached => (Step(cached.Volume), cached.Muted));
    }

    private void ChangeVolume(Action<NAudio.CoreAudioApi.SimpleAudioVolume> change, Func<(float Volume, bool Muted), (float, bool)> optimistic)
    {
        if (_shown.Process is not { } process) return;
        if (_appVolume is { } cached) _appVolume = optimistic(cached);
        _volumeReadUtc = DateTime.UtcNow; // don't let a poll in flight overwrite the optimistic value
        _ = Task.Run(() => _volume.Apply(process, change));
    }

    private void Send(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> op, Action<bool>? done = null)
    {
        var session = _shown.Session;
        if (session is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                bool accepted = await op(session);
                done?.Invoke(accepted);
            }
            catch (Exception ex) { Error = ex.Message; }
        });
    }

    // SourceAppUserModelId is an exe name ("Spotify.exe"), a packaged AUMID or, for
    // Firefox, an opaque hash: map the common ones to a display name (same list as Shade).
    private static readonly (string Hint, string Name)[] KnownApps =
    {
        ("spotify", "Spotify"), ("msedge", "Edge"), ("chrome", "Chrome"), ("firefox", "Firefox"),
        ("308046B0AF4A39CB", "Firefox"), ("brave", "Brave"), ("opera", "Opera"),
        ("youtube", "YouTube Music"), ("zunemusic", "Reproductor multimedia"), ("applemusic", "Apple Music"),
        ("tidal", "TIDAL"), ("deezer", "Deezer"), ("vlc", "VLC"), ("foobar", "foobar2000"), ("tauon", "Tauon"),
    };

    private static string FriendlyAppName(string appId)
    {
        foreach (var (hint, name) in KnownApps)
            if (appId.Contains(hint, StringComparison.OrdinalIgnoreCase)) return name;
        var fragment = AppVolume.ProcessNameFor(appId).Split('.').Last();
        return fragment.Length == 0 ? "otra app" : char.ToUpperInvariant(fragment[0]) + fragment[1..];
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Detach();
            foreach (var s in _watched) s.PlaybackInfoChanged -= OnAnyPlaybackInfoChanged;
            _watched.Clear();
        }
        _volume.Dispose();
        Wake.Dispose();
    }
}
