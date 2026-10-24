using System.Collections.Concurrent;
using Nation.Library;

namespace Nation.Streaming;

/// <summary>
/// One Track object per YouTube Music song, however many times it shows up (search,
/// radio, a playlist, a saved list). The rest of Nation compares tracks by reference
/// ("already waiting in the queue", "this list is already playing"), so the same
/// song must always be the same object.
/// </summary>
public static class StreamTracks
{
    public const string WatchPrefix = "https://music.youtube.com/watch?v=";

    private static readonly ConcurrentDictionary<string, Track> ByVideo = new();
    private static int _nextId = 2_000_000; // outside library (scan order), playlists (500k) and other apps (1M)

    public static bool IsStream(string path) => path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static string? VideoIdOf(string url)
    {
        int at = url.IndexOf("v=", StringComparison.Ordinal);
        if (at < 0) return null;
        string id = url[(at + 2)..];
        int end = id.IndexOf('&');
        return end < 0 ? id : id[..end];
    }

    /// <summary>
    /// The song's track, created on first sight; later sightings get that same object.
    /// Cards and artist pages leave the length out, so a later sighting that has it
    /// replaces the entry (whatever already holds the old object keeps it).
    /// </summary>
    public static Track Get(string videoId, string title, string artist, string album, TimeSpan duration, string? cover)
    {
        var known = ByVideo.GetOrAdd(videoId, id => Make(id, title, artist, album, duration, cover));
        if (known.Duration > TimeSpan.Zero || duration <= TimeSpan.Zero) return known;
        var better = Make(videoId, title, artist, album.Length > 0 ? album : known.Album, duration, cover ?? known.CoverUrl) with { Id = known.Id };
        return ByVideo.TryUpdate(videoId, better, known) ? better : ByVideo[videoId];
    }

    /// <summary>For a bare URL (session, a list written by another player): what we know about it, or a placeholder.</summary>
    public static Track FromUrl(string url)
    {
        string id = VideoIdOf(url) ?? url;
        return ByVideo.TryGetValue(id, out var t) ? t : Get(id, id, "YouTube Music", "", TimeSpan.Zero, null);
    }

    private static Track Make(string id, string title, string artist, string album, TimeSpan duration, string? cover) =>
        new(WatchPrefix + id, title, artist, album, 0, duration, "YouTube Music", 0, 0, 0)
        {
            Id = Interlocked.Increment(ref _nextId),
            CoverUrl = cover,
        };
}
