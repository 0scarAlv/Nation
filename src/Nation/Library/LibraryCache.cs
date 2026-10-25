using System.Text.Json;

namespace Nation.Library;

/// <summary>
/// Remembers parsed tags per file (keyed by path, invalidated by size + mtime) so
/// startup only stats files instead of parsing every one with TagLib. Parsing a
/// FLAC reads its embedded cover block too, which made cold scans CPU- and
/// GC-heavy (hundreds of MB of short-lived buffers for a few hundred tracks).
/// </summary>
public static class LibraryCache
{
    // 2: genre + ISRC (for listening stats); older caches rescan once.
    private const int FormatVersion = 2;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nation", "library.json");

    public sealed record Entry(
        string Path, long Size, long MTime,
        string Title, string Artist, string Album, uint Year, long DurationMs,
        string Codec, int SampleRate, int BitsPerSample, int Bitrate, string? Genre, string? Isrc)
    {
        public Track ToTrack() => new(Path, Title, Artist, Album, Year, TimeSpan.FromMilliseconds(DurationMs),
            Codec, SampleRate, BitsPerSample, Bitrate) { Genre = Genre, Isrc = Isrc };

        public static Entry From(Track t, long size, long mtime) => new(t.Path, size, mtime,
            t.Title, t.Artist, t.Album, t.Year, (long)t.Duration.TotalMilliseconds,
            t.Codec, t.SampleRate, t.BitsPerSample, t.Bitrate, t.Genre, t.Isrc);
    }

    private sealed record CacheFile(int Version, List<Entry> Entries);

    public static Dictionary<string, Entry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new(StringComparer.OrdinalIgnoreCase);
            using var stream = File.OpenRead(FilePath);
            var file = JsonSerializer.Deserialize<CacheFile>(stream);
            if (file is null || file.Version != FormatVersion) return new(StringComparer.OrdinalIgnoreCase);
            return file.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Corrupt cache: rebuild from scratch rather than fail to start.
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void Save(Dictionary<string, Entry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Drop entries for files that no longer exist so the cache can't grow forever.
            var alive = entries.Values.Where(e => File.Exists(e.Path)).ToList();
            var tmp = FilePath + ".tmp";
            using (var stream = File.Create(tmp))
                JsonSerializer.Serialize(stream, new CacheFile(FormatVersion, alive));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // Cache is an optimization only.
        }
    }
}
