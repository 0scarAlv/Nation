using System.Text.Json;

namespace Nation.Library;

/// <summary>What a streamed song needs to show up again after a restart (it has no file to read tags from).</summary>
public sealed record StreamInfo(string Path, string Title, string Artist, string Album, double Seconds, string? Cover)
{
    public static StreamInfo From(Track t) => new(t.Path, t.Title, t.Artist, t.Album, t.Duration.TotalSeconds, t.CoverUrl);

    public Track ToTrack() => Streaming.StreamTracks.VideoIdOf(Path) is { } id
        ? Streaming.StreamTracks.Get(id, Title, Artist, Album, TimeSpan.FromSeconds(Seconds), Cover)
        : Streaming.StreamTracks.FromUrl(Path);
}

/// <summary>
/// Last track, position and volume, plus the playlist it came from and the songs
/// waiting in "a continuación", so the next start picks up where playback left off.
/// Saved to %LOCALAPPDATA%\Nation\session.json every few seconds and on exit
/// (closing the terminal window skips the exit path, hence the periodic saves).
/// </summary>
public sealed record Session(string Path, double Position, double Volume, string? Playlist = null, string[]? UpNext = null, StreamInfo[]? Streams = null)
{
    private static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nation", "session.json");

    public static Session? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Session>(File.ReadAllText(FilePath)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing the resume point isn't worth an error.
        }
    }
}
