using System.IO.Compression;

namespace Nation.Streaming;

/// <summary>
/// yt-dlp is what turns a YouTube Music link into audio. Nation uses one found on PATH,
/// or keeps its own copy in %LOCALAPPDATA%\Nation\bin\yt-dlp, downloaded only when the
/// user asks. Nation's copy is the official unpacked build (yt-dlp_win.zip): the
/// single-file yt-dlp.exe unpacks itself on every run, which alone took ~5 s here,
/// against ~1.5 s for the unpacked one. It's refreshed weekly when a new release is
/// out, because YouTube changes break old versions.
/// </summary>
public static class YtDlp
{
    private static readonly string BinDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nation", "bin");

    private static string OwnDir => Path.Combine(BinDir, "yt-dlp");
    private static string OwnExe => Path.Combine(OwnDir, "yt-dlp.exe");
    private static string VersionFile => Path.Combine(OwnDir, "nation-version.txt");
    private const string Releases = "https://github.com/yt-dlp/yt-dlp/releases/latest";

    // Asked every frame while browsing YouTube: look on disk at most every few seconds.
    private static bool _available;
    private static long _checkedAt = -1_000_000;

    public static bool Available
    {
        get
        {
            long now = Environment.TickCount64;
            if (!_available && now - _checkedAt > 3000)
            {
                _checkedAt = now;
                _available = Find("yt-dlp") is not null;
            }
            return _available;
        }
    }

    /// <summary>
    /// Call before mpv starts: puts Nation's own copy on this process's PATH, where mpv's
    /// ytdl hook and the resolver look for it (also when it's downloaded later in the session).
    /// </summary>
    public static void PrepareEnvironment()
    {
        SeedFromInstall();
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!path.Split(';').Contains(OwnDir, StringComparer.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PATH", OwnDir + ";" + path);
    }

    // The installer ships a copy next to Nation.exe. It goes to Nation's own folder (which
    // updates itself weekly) when there's none there yet, or when the installer's is newer.
    private static void SeedFromInstall()
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "yt-dlp");
        if (!File.Exists(Path.Combine(bundled, "yt-dlp.exe"))) return;
        try
        {
            string bundledVersion = ReadVersion(Path.Combine(bundled, "nation-version.txt"));
            if (File.Exists(OwnExe) && string.CompareOrdinal(bundledVersion, ReadVersion(VersionFile)) <= 0) return;
            if (Directory.Exists(OwnDir)) Directory.Delete(OwnDir, recursive: true);
            CopyDirectory(bundled, OwnDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // yt-dlp may be running (another Nation open); next start tries again.
        }
    }

    // Release tags are dates (2026.10.10), so ordinal order is release order.
    private static string ReadVersion(string file) => File.Exists(file) ? File.ReadAllText(file).Trim() : "";

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    /// <summary>
    /// yt-dlp only enables Deno for YouTube's JavaScript challenges by default. Node is far
    /// more common on developer machines, so it's the fallback when Deno isn't there.
    /// </summary>
    public static string? JsRuntimeOption() =>
        Find("deno") is null && Find("node") is not null ? "js-runtimes=node" : null;

    private static string? Find(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), name + ".exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { } // malformed PATH entry
        }
        return null;
    }

    /// <summary>Downloads the latest official build into Nation's bin folder. Returns an error message, or null.</summary>
    public static async Task<string?> DownloadAsync(CancellationToken ct)
    {
        string staging = OwnDir + ".new";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Nation (terminal music player)");
            string? version = await LatestVersionAsync(http, ct);
            var zip = await http.GetByteArrayAsync(Releases + "/download/yt-dlp_win.zip", ct);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            using (var archive = new ZipArchive(new MemoryStream(zip)))
                archive.ExtractToDirectory(staging);
            // The zip may hold the files at its root or inside one folder.
            string root = File.Exists(Path.Combine(staging, "yt-dlp.exe")) ? staging
                : Directory.GetDirectories(staging).FirstOrDefault(d => File.Exists(Path.Combine(d, "yt-dlp.exe")))
                  ?? throw new IOException("el zip no trae yt-dlp.exe");
            if (version is not null) await File.WriteAllTextAsync(Path.Combine(root, "nation-version.txt"), version, ct);
            if (Directory.Exists(OwnDir)) Directory.Delete(OwnDir, recursive: true);
            Directory.Move(root, OwnDir);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            _checkedAt = -1_000_000; // look again right away
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or InvalidDataException)
        {
            return "no se pudo descargar yt-dlp: " + e.Message;
        }
    }

    /// <summary>The latest release's tag, read from where GitHub's "latest" link redirects.</summary>
    private static async Task<string?> LatestVersionAsync(HttpClient http, CancellationToken ct)
    {
        using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await noRedirect.GetAsync(Releases, ct);
        string? location = response.Headers.Location?.ToString();
        return location is null ? null : location[(location.LastIndexOf('/') + 1)..];
    }

    /// <summary>Once a week: re-downloads Nation's own copy when GitHub has a newer release.</summary>
    public static void UpdateInBackground()
    {
        if (!File.Exists(OwnExe)) return;
        try
        {
            if (File.Exists(VersionFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(VersionFile) < TimeSpan.FromDays(7)) return;
        }
        catch (IOException) { return; }
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient();
                string? latest = await LatestVersionAsync(http, CancellationToken.None);
                string current = File.Exists(VersionFile) ? (await File.ReadAllTextAsync(VersionFile)).Trim() : "";
                if (latest is not null && latest != current) await DownloadAsync(CancellationToken.None);
                // Same version: just restart the week.
                else if (File.Exists(VersionFile)) File.SetLastWriteTimeUtc(VersionFile, DateTime.UtcNow);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
            {
                // Try again next start.
            }
        });
    }
}
