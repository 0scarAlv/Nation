using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Nation.Library;

namespace Nation.Lyrics;

/// <summary>
/// Local-first lyrics lookup: embedded tags → sidecar .lrc/.txt → LRCLIB (optional).
/// Unsynced local lyrics are still checked against LRCLIB for a synced version.
/// Online results, including misses, are cached on disk so each song hits the
/// network at most once.
/// </summary>
public sealed class LyricsProvider
{
    private readonly HttpClient? _http;
    private readonly string _cacheDir;

    public LyricsProvider(bool online)
    {
        _cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nation", "lyrics");
        if (!online) return;
        _http = new HttpClient { BaseAddress = new Uri("https://lrclib.net/"), Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Nation/0.3 (terminal music player)");
    }

    public async Task<LyricSheet?> FindAsync(Track track, CancellationToken ct)
    {
        var local = FromTags(track.Path) ?? FromSidecar(track.Path);
        if (local is { Synced: true }) return local;

        // Local text exists but has no timing: a synced online version is an upgrade.
        var online = await FromCacheOrOnlineAsync(track, ct);
        if (online is { Synced: true }) return online;
        return local ?? online;
    }

    private static LyricSheet? FromTags(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.Average | TagLib.ReadStyle.PictureLazy);

            // ID3 SYLT: real synced lyrics stored in the MP3 itself.
            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
            {
                var sylt = id3.GetFrames<TagLib.Id3v2.SynchronisedLyricsFrame>()
                    .FirstOrDefault(f => f.Format == TagLib.Id3v2.TimestampFormat.AbsoluteMilliseconds && f.Text.Length > 0);
                if (sylt is not null)
                    return LyricSheet.FromSynced(sylt.Text.Select(t => new LyricLine(t.Time / 1000.0, t.Text.Trim())), LyricsSource.Embedded);
            }

            var text = file.Tag.Lyrics;
            // Some taggers write Vorbis comments as UNSYNCEDLYRICS instead of LYRICS.
            if (string.IsNullOrWhiteSpace(text) && file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
                text = xiph.GetFirstField("UNSYNCEDLYRICS") ?? xiph.GetFirstField("SYNCEDLYRICS");
            return LyricSheet.Parse(text, LyricsSource.Embedded);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static LyricSheet? FromSidecar(string path)
    {
        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        foreach (var ext in new[] { ".lrc", ".txt" })
        {
            var candidate = stem + ext;
            if (File.Exists(candidate))
                return LyricSheet.Parse(File.ReadAllText(candidate), LyricsSource.Sidecar);
        }
        return null;
    }

    private async Task<LyricSheet?> FromCacheOrOnlineAsync(Track track, CancellationToken ct)
    {
        var key = CacheKey(track);
        var lrc = Path.Combine(_cacheDir, key + ".lrc");
        var txt = Path.Combine(_cacheDir, key + ".txt");
        var miss = Path.Combine(_cacheDir, key + ".none");

        if (File.Exists(lrc)) return LyricSheet.Parse(await File.ReadAllTextAsync(lrc, ct), LyricsSource.Online);
        if (File.Exists(txt)) return LyricSheet.Parse(await File.ReadAllTextAsync(txt, ct), LyricsSource.Online);
        if (File.Exists(miss) || _http is null) return null;

        var found = await FetchAsync(track, ct);

        Directory.CreateDirectory(_cacheDir);
        if (found?.Synced is { Length: > 0 } synced)
        {
            await File.WriteAllTextAsync(lrc, synced, CancellationToken.None);
            return LyricSheet.Parse(synced, LyricsSource.Online);
        }
        if (found?.Plain is { Length: > 0 } plain)
        {
            await File.WriteAllTextAsync(txt, plain, CancellationToken.None);
            return LyricSheet.Parse(plain, LyricsSource.Online);
        }
        if (found is not null) await File.WriteAllTextAsync(miss, "", CancellationToken.None);
        return null;
    }

    /// <summary>
    /// Same strategy as Shade's LyricsProvider: exact /api/get first, then fuzzy
    /// /api/search picking the closest duration. Returns null on network failure
    /// (not cached, retried next time) and an empty result on a confirmed miss.
    /// </summary>
    private async Task<LrcLibResponse?> FetchAsync(Track track, CancellationToken ct)
    {
        try
        {
            int duration = (int)Math.Round(track.Duration.TotalSeconds);
            var get = $"api/get?track_name={Esc(track.Title)}&artist_name={Esc(track.Artist)}&album_name={Esc(track.Album)}&duration={duration}";
            using (var response = await _http!.GetAsync(get, ct))
            {
                if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<LrcLibResponse>(ct) is { } exact && exact.HasAny)
                    return exact;
            }

            var search = $"api/search?track_name={Esc(track.Title)}&artist_name={Esc(track.Artist)}";
            using (var response = await _http.GetAsync(search, ct))
            {
                if (!response.IsSuccessStatusCode) return null;
                var candidates = await response.Content.ReadFromJsonAsync<List<LrcLibResponse>>(ct) ?? new();
                var best = candidates
                    .Where(c => c.HasAny)
                    // Prefer synced, then closest duration; reject wildly different songs.
                    .Where(c => duration == 0 || Math.Abs(c.Duration.GetValueOrDefault() - duration) <= 10)
                    .OrderByDescending(c => c.Synced is { Length: > 0 })
                    .ThenBy(c => Math.Abs(c.Duration.GetValueOrDefault() - duration))
                    .FirstOrDefault();
                return best ?? new LrcLibResponse(null, null, null);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Esc(string s) => Uri.EscapeDataString(s);

    private static string CacheKey(Track t)
    {
        var raw = $"{t.Artist}\n{t.Title}\n{t.Album}\n{(int)t.Duration.TotalSeconds}".ToLowerInvariant();
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
    }

    private sealed record LrcLibResponse(
        [property: JsonPropertyName("syncedLyrics")] string? Synced,
        [property: JsonPropertyName("plainLyrics")] string? Plain,
        [property: JsonPropertyName("duration")] double? Duration)
    {
        public bool HasAny => !string.IsNullOrWhiteSpace(Synced) || !string.IsNullOrWhiteSpace(Plain);
    }
}
