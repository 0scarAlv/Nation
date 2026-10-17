using System.Collections.Concurrent;
using Nation.Art;
using Nation.Library;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Nation.Remote;

/// <summary>
/// Publishes Nation to Windows' System Media Transport Controls. That makes the keyboard
/// media keys (play/pause, next, previous, stop) work globally, even while the terminal
/// is not focused, and shows title/artist/cover in the Windows volume flyout.
/// A console app has no window to hang SMTC on, so it borrows the one owned by a
/// silent WinRT MediaPlayer whose built-in command handling is turned off.
/// Button events arrive on a thread-pool thread; they are queued for the UI thread.
/// </summary>
public sealed class MediaSession : IDisposable
{
    private readonly MediaPlayer _host;
    private readonly SystemMediaTransportControls _smtc;

    public ConcurrentQueue<Action> Commands { get; } = new();
    public AutoResetEvent Wake { get; } = new(false);
    public string? Error { get; private set; }

    /// <summary>Called on the UI thread: play, pause, stop, next, back, seek (ms), shuffle, repeat.</summary>
    public Action<string, long>? OnCommand { get; set; }

    // Last state pushed, so per-frame Update calls only touch SMTC when something changed.
    private Track? _shownTrack;
    private MediaPlaybackStatus _shownStatus = MediaPlaybackStatus.Closed;
    private bool _shownShuffle, _shownRepeat;
    private bool _timelineStale = true;
    private double _timelinePos;
    private DateTime _timelineUtc;
    // The cover task updates the display from a pool thread; serialize it with track changes.
    private readonly object _displayLock = new();

    public MediaSession()
    {
        _host = new MediaPlayer();
        _host.CommandManager.IsEnabled = false;
        _smtc = _host.SystemMediaTransportControls;
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = _smtc.IsPauseEnabled = _smtc.IsStopEnabled = true;
        _smtc.IsNextEnabled = _smtc.IsPreviousEnabled = true;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        _smtc.ButtonPressed += (_, e) =>
        {
            string? command = e.Button switch
            {
                SystemMediaTransportControlsButton.Play => "play",
                SystemMediaTransportControlsButton.Pause => "pause",
                SystemMediaTransportControlsButton.Stop => "stop",
                SystemMediaTransportControlsButton.Next => "next",
                SystemMediaTransportControlsButton.Previous => "back",
                _ => null,
            };
            if (command is not null) Enqueue(command, 0);
        };
        _smtc.PlaybackPositionChangeRequested += (_, e) => Enqueue("seek", (long)e.RequestedPlaybackPosition.TotalMilliseconds);
        _smtc.ShuffleEnabledChangeRequested += (_, e) => { if (e.RequestedShuffleEnabled != _shownShuffle) Enqueue("shuffle", 0); };
        _smtc.AutoRepeatModeChangeRequested += (_, e) =>
        {
            if ((e.RequestedAutoRepeatMode != MediaPlaybackAutoRepeatMode.None) != _shownRepeat) Enqueue("repeat", 0);
        };
    }

    /// <summary>Tries to create the session; null (with nothing thrown) on systems without SMTC.</summary>
    public static MediaSession? TryCreate()
    {
        try { return new MediaSession(); }
        catch (Exception) { return null; }
    }

    private void Enqueue(string command, long arg)
    {
        var handler = OnCommand;
        if (handler is null) return;
        Commands.Enqueue(() => handler(command, arg));
        Wake.Set();
    }

    /// <summary>Called every frame from the UI thread; cheap unless the state actually changed.</summary>
    public void Update(Track? track, bool idle, bool paused, bool shuffle, bool repeat, double position, double duration)
    {
        try
        {
            if (!ReferenceEquals(track, _shownTrack))
            {
                _shownTrack = track;
                ShowTrack(track);
                _timelineStale = true;
            }

            var status = track is null ? MediaPlaybackStatus.Closed
                : idle ? MediaPlaybackStatus.Stopped
                : paused ? MediaPlaybackStatus.Paused
                : MediaPlaybackStatus.Playing;
            if (status != _shownStatus)
            {
                _shownStatus = status;
                _smtc.PlaybackStatus = status;
                _timelineStale = true;
            }
            if (shuffle != _shownShuffle) _smtc.ShuffleEnabled = _shownShuffle = shuffle;
            if (repeat != _shownRepeat)
                _smtc.AutoRepeatMode = (_shownRepeat = repeat) ? MediaPlaybackAutoRepeatMode.List : MediaPlaybackAutoRepeatMode.None;

            // Readers extrapolate position from the last update while playing, so only
            // republish the timeline on changes, seeks (drift from the expected spot) or every 5 s.
            double expected = _timelinePos + (status == MediaPlaybackStatus.Playing ? (DateTime.UtcNow - _timelineUtc).TotalSeconds : 0);
            if (track is not null && duration > 0 &&
                (_timelineStale || Math.Abs(position - expected) > 1.5 || (DateTime.UtcNow - _timelineUtc).TotalSeconds > 5))
            {
                _timelineStale = false;
                _timelinePos = position;
                _timelineUtc = DateTime.UtcNow;
                _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
                {
                    StartTime = TimeSpan.Zero,
                    MinSeekTime = TimeSpan.Zero,
                    Position = TimeSpan.FromSeconds(Math.Clamp(position, 0, duration)),
                    MaxSeekTime = TimeSpan.FromSeconds(duration),
                    EndTime = TimeSpan.FromSeconds(duration),
                });
            }
        }
        catch (Exception ex)
        {
            // SMTC is a nicety; never let it take the player down.
            Error = ex.Message;
        }
    }

    private void ShowTrack(Track? track)
    {
        var updater = _smtc.DisplayUpdater;
        lock (_displayLock)
        {
            updater.ClearAll();
            if (track is not null)
            {
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = track.Title;
                updater.MusicProperties.Artist = track.Artist;
                updater.MusicProperties.AlbumTitle = track.Album;
            }
            updater.Update();
        }
        if (track is null) return;

        // Cover is read from disk off the UI thread, then attached if the track is still current.
        _ = Task.Run(async () =>
        {
            try
            {
                var (_, bytes) = CoverArt.FindCover(track);
                if (bytes is null || !ReferenceEquals(_shownTrack, track)) return;
                var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                lock (_displayLock)
                {
                    if (!ReferenceEquals(_shownTrack, track)) { stream.Dispose(); return; }
                    updater.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
                    updater.Update();
                }
            }
            catch (Exception)
            {
                // No cover in the flyout; text metadata is already up.
            }
        });
    }

    public void Dispose()
    {
        try
        {
            _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
            _smtc.DisplayUpdater.ClearAll();
            _smtc.IsEnabled = false;
        }
        catch (Exception) { }
        _host.Dispose();
        Wake.Dispose();
    }
}
