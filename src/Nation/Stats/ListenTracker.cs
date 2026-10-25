using Nation.Audio;
using Nation.Library;

namespace Nation.Stats;

/// <summary>
/// Turns one playback source (Nation's own player, or the app it follows) into listens.
/// Observe() runs once per frame: time only accrues while the source reports playing,
/// so pauses and seeks don't inflate it. A listen ends when the song changes, the same
/// song starts over, or playback has been stopped for a long while.
/// </summary>
public sealed class ListenTracker
{
    // Below this a listen is a skip, not worth a row.
    private const long MinPlayedMs = 5_000;
    // Frames can be up to 1 s apart while idle; anything longer is the machine sleeping, not music.
    private static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    public static readonly string Device = "pc:" + Environment.MachineName.ToLowerInvariant();

    private readonly ListenLog _log;
    private Open? _open;
    private DateTime _lastTick;

    private sealed class Open
    {
        public required string Id, Key, Source;
        public required Track Track;
        public required bool Music;
        public DateTime StartedAt, LastPlaying, LastSave;
        public double PlayedMs, LastPosition;
    }

    private readonly bool _positionIsReliable;

    /// <param name="positionIsReliable">True for Nation's own player, whose position only moves while audio does.</param>
    public ListenTracker(ListenLog log, bool positionIsReliable)
    {
        _log = log;
        _positionIsReliable = positionIsReliable;
    }

    /// <param name="source">"local", "youtube", or the followed app's name.</param>
    /// <param name="music">False for things that are probably not music (a browser video without an album).</param>
    public void Observe(IPlayback playback, string source, bool music = true)
    {
        var now = DateTime.UtcNow;
        var step = now - _lastTick;
        _lastTick = now;
        if (step > MaxStep || step < TimeSpan.Zero) step = TimeSpan.Zero;

        var track = playback.Current;
        string? key = track is null ? null : KeyOf(track, source);
        double position = playback.Position;

        if (_open is { } open)
        {
            // Starting the same song over (repeat, or seeking back to the top after hearing most of it) is a new listen.
            bool restarted = key == open.Key && position < 5 && open.LastPosition > Math.Max(30, playback.Duration * 0.5);
            if (key != open.Key || restarted || (!playback.Playing && now - open.LastPlaying > StaleAfter))
                Close();
        }

        if (_open is null && track is not null && key is not null && playback.Playing)
        {
            _open = new Open
            {
                Id = Guid.NewGuid().ToString("N"), Key = key, Source = source, Track = track, Music = music,
                StartedAt = now, LastPlaying = now, LastSave = now,
            };
            step = TimeSpan.Zero;
        }

        if (_open is { } current)
        {
            double previous = current.LastPosition;
            current.LastPosition = position;
            if (playback.Playing)
            {
                double ms = step.TotalMilliseconds;
                // mpv reports "playing" while a stream is still buffering; count only time the position moved.
                // Seeks (jumps either way) fall back to wall time. Other apps' positions are unreliable, so they get wall time.
                double moved = (position - previous) * 1000;
                // (mpv's position moves in uneven steps, so add what it moved rather than capping at the frame time.)
                if (_positionIsReliable && moved >= 0 && moved < ms + 1000) ms = moved;
                current.PlayedMs += ms;
                current.LastPlaying = now;
            }
            // Periodic saves: closing the terminal window kills Nation without running Flush().
            if (now - current.LastSave > SaveEvery && current.PlayedMs >= MinPlayedMs)
            {
                current.LastSave = now;
                Save(current);
            }
        }
    }

    /// <summary>Ends the open listen (song change, exit).</summary>
    public void Close()
    {
        if (_open is { } open && open.PlayedMs >= MinPlayedMs) Save(open);
        _open = null;
    }

    private void Save(Open o)
    {
        var t = o.Track;
        try
        {
            _log.Upsert(new Listen(
                o.Id, Device, o.Source,
                ToUnixMs(o.StartedAt), ToUnixMs(o.LastPlaying), (long)o.PlayedMs, (long)t.Duration.TotalMilliseconds,
                t.Title, t.Artist, t.Album, t.Genre, t.Isrc, t.IsExternal || t.IsStream ? null : t.Path,
                TrackKeyOf(t), o.Music));
        }
        catch (Exception)
        {
            // A locked or broken journal must never interrupt the music.
        }
    }

    /// <summary>
    /// Which song this is across devices: the ISRC when the file has one, else folded
    /// artist + title (same rule the phone uses, so both sides land on one key).
    /// </summary>
    public static string TrackKeyOf(Track t) => t.Isrc is { Length: > 0 } isrc
        ? "isrc:" + isrc
        : "tag:" + TrackSearch.Fold(t.Artist.Trim()) + "|" + TrackSearch.Fold(t.Title.Trim());

    // Change detection only: other apps' tracks have no stable Id, so compare what they show.
    private static string KeyOf(Track t, string source) =>
        t.IsExternal ? source + "|" + t.Title + "|" + t.Artist + "|" + t.Album : t.Path;

    private static long ToUnixMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();
}
