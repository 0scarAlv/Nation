using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nation.Library;

namespace Nation.Streaming;

/// <summary>A playlist in the user's YouTube Music library ("VL…" browse id).</summary>
public sealed record YtPlaylist(string BrowseId, string Title, string Detail);

public sealed class YouTubeMusicException(string message) : Exception(message);

/// <summary>
/// YouTube Music through its own web API (InnerTube, the "WEB_REMIX" client the site
/// uses), the same way ytmusicapi does. There is no public API for it: search works
/// without an account; the library needs the browser's session cookie, which is kept
/// encrypted for the current Windows user (DPAPI) in %LOCALAPPDATA%\Nation\youtube.cookie.
/// Responses are read by walking the JSON for the item renderers instead of fixed paths,
/// so small layout changes on Google's side don't break it.
/// </summary>
public sealed partial class YouTubeMusic : IDisposable
{
    private const string Origin = "https://music.youtube.com";
    private const string ApiBase = Origin + "/youtubei/v1/";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    // Search filter "songs" (ytmusicapi's params for filter="songs").
    private const string SongsFilter = "EgWKAQIIAWoMEA4QChADEAQQCRAF";

    private static readonly string CookieFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nation", "youtube.cookie");

    private readonly HttpClient _http;
    private string? _cookie, _sapisid;

    public bool SignedIn => _sapisid is not null;

    public YouTubeMusic()
    {
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        LoadCookie();
    }

    // ── session ────────────────────────────────────────────────────────

    /// <summary>
    /// Accepts the cookie as copied from the browser's dev tools: the bare value, a
    /// "cookie: …" header line, or a whole block of copied request headers. Checks it
    /// against the library before keeping it. Returns an error message, or null when signed in.
    /// </summary>
    public async Task<string?> SignInAsync(string pasted, CancellationToken ct)
    {
        string? cookie = ExtractCookie(pasted);
        if (cookie is null) return "eso no parece la cookie de music.youtube.com (falta __Secure-3PAPISID)";
        var (oldCookie, oldSapisid) = (_cookie, _sapisid);
        _cookie = cookie;
        _sapisid = SapisidOf(cookie);
        try
        {
            await LibraryAsync(ct);
        }
        catch (YouTubeMusicException e)
        {
            (_cookie, _sapisid) = (oldCookie, oldSapisid);
            return e.Message;
        }
        SaveCookie();
        return null;
    }

    public void SignOut()
    {
        _cookie = _sapisid = null;
        try { File.Delete(CookieFile); } catch (IOException) { }
    }

    private static string? ExtractCookie(string pasted)
    {
        string text = pasted.Trim().Trim('"', '\'');
        // A block of headers: keep only the cookie line.
        foreach (var line in text.Split('\n'))
        {
            var l = line.Trim();
            if (l.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase)) { text = l[7..].Trim(); break; }
        }
        if (text.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase)) text = text[7..].Trim();
        text = text.Replace("\r", "").Replace("\n", "");
        return SapisidOf(text) is null ? null : text;
    }

    private static string? SapisidOf(string cookie)
    {
        foreach (var name in new[] { "__Secure-3PAPISID", "SAPISID" })
        {
            var m = Regex.Match(cookie, @"(?:^|;\s*)" + Regex.Escape(name) + @"=([^;]+)");
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return null;
    }

    private void LoadCookie()
    {
        try
        {
            if (!File.Exists(CookieFile)) return;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(CookieFile), null, DataProtectionScope.CurrentUser);
            var cookie = Encoding.UTF8.GetString(plain);
            if (SapisidOf(cookie) is { } sapisid) (_cookie, _sapisid) = (cookie, sapisid);
        }
        catch (Exception e) when (e is IOException or CryptographicException or UnauthorizedAccessException)
        {
            // Unreadable (another user's, or corrupt): stay signed out.
        }
    }

    private void SaveCookie()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CookieFile)!);
            File.WriteAllBytes(CookieFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(_cookie!), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Signed in for this run only.
        }
    }

    // ── requests ───────────────────────────────────────────────────────

    private async Task<JsonNode> PostAsync(string endpoint, JsonObject body, CancellationToken ct, string query = "")
    {
        body["context"] = new JsonObject
        {
            ["client"] = new JsonObject
            {
                ["clientName"] = "WEB_REMIX",
                ["clientVersion"] = "1." + DateTime.UtcNow.ToString("yyyyMMdd") + ".01.00",
                ["hl"] = "es",
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + endpoint + "?prettyPrint=false" + query)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Origin", Origin);
        request.Headers.TryAddWithoutValidation("X-Origin", Origin);
        if (_cookie is not null && _sapisid is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", _cookie);
            request.Headers.TryAddWithoutValidation("Authorization", SapisidHash(_sapisid));
            request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            throw new YouTubeMusicException("sin conexión con YouTube Music");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new YouTubeMusicException("YouTube Music no respondió");
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new YouTubeMusicException("la sesión no es válida o caducó: vuelve a pegar la cookie (i)");
            if (!response.IsSuccessStatusCode)
                throw new YouTubeMusicException($"YouTube Music respondió {(int)response.StatusCode}");
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) ?? new JsonObject();
        }
    }

    /// <summary>The header the site sends with a session: SHA-1 of "time SAPISID origin".</summary>
    private static string SapisidHash(string sapisid)
    {
        string now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{now} {sapisid} {Origin}"));
        return $"SAPISIDHASH {now}_{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    // ── catalog ────────────────────────────────────────────────────────

    /// <summary>Songs matching the query (YouTube Music's own ranking), first page.</summary>
    public async Task<List<Track>> SearchAsync(string query, CancellationToken ct)
    {
        var root = await PostAsync("search", new JsonObject { ["query"] = query, ["params"] = SongsFilter }, ct);
        return Songs(root);
    }

    /// <summary>The song's radio (what YouTube Music plays after it), without the song itself.</summary>
    public async Task<List<Track>> RadioAsync(string videoId, CancellationToken ct)
    {
        var root = await PostAsync("next", new JsonObject
        {
            ["videoId"] = videoId,
            ["playlistId"] = "RDAMVM" + videoId,
            ["isAudioOnly"] = true,
        }, ct);
        var tracks = new List<Track>();
        foreach (var item in Find(root, "playlistPanelVideoRenderer"))
            if (PanelSong(item) is { } t && !t.Path.EndsWith(videoId, StringComparison.Ordinal) && !tracks.Contains(t)) tracks.Add(t);
        return tracks;
    }

    /// <summary>"Me gusta" plus the playlists saved in the library. Needs a session.</summary>
    public async Task<List<YtPlaylist>> LibraryAsync(CancellationToken ct)
    {
        if (!SignedIn) throw new YouTubeMusicException("sin sesión");
        var lists = new List<YtPlaylist> { new("VLLM", "Me gusta", "tus canciones con ♥") };
        var body = new JsonObject { ["browseId"] = "FEmusic_liked_playlists" };
        bool first = true;
        await foreach (var page in PagesAsync(body, legacy: true, ct))
        {
            // A cookie Google doesn't accept still gets an answer, just a signed-out one.
            if (first && !LoggedIn(page)) throw new YouTubeMusicException("YouTube Music no reconoció la sesión: copia la cookie otra vez");
            first = false;
            foreach (var item in Find(page, "musicTwoRowItemRenderer"))
            {
                string? id = Str(item, "navigationEndpoint", "browseEndpoint", "browseId");
                if (id is null || !id.StartsWith("VL", StringComparison.Ordinal) || id == "VLLM") continue;
                if (lists.Any(l => l.BrowseId == id)) continue;
                lists.Add(new YtPlaylist(id, Runs(item["title"]), Runs(item["subtitle"])));
            }
        }
        return lists;
    }

    /// <summary>InnerTube reports the session in responseContext.serviceTrackingParams (logged_in = 1).</summary>
    private static bool LoggedIn(JsonNode page) =>
        Find(page, "params").OfType<JsonArray>().SelectMany(a => a)
            .Any(p => Str(p, "key") == "logged_in" && Str(p, "value") == "1");

    /// <summary>Every song in a playlist ("VL…" browse id), following the pages (capped for huge lists).</summary>
    public async Task<List<Track>> PlaylistAsync(string browseId, CancellationToken ct)
    {
        var tracks = new List<Track>();
        await foreach (var page in PagesAsync(new JsonObject { ["browseId"] = browseId }, legacy: false, ct))
        {
            tracks.AddRange(Songs(page));
            if (tracks.Count >= 5000) break;
        }
        return tracks;
    }

    /// <summary>
    /// The first response and its continuations. Current pages end in a
    /// continuationItemRenderer token; the library grid still uses the older
    /// nextContinuationData (legacy), which on playlist pages would load suggestions instead.
    /// </summary>
    private async IAsyncEnumerable<JsonNode> PagesAsync(JsonObject body, bool legacy, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var page = await PostAsync("browse", body, ct);
        var seen = new HashSet<string>();
        for (int n = 0; n < 200; n++)
        {
            yield return page;
            string? token = Find(page, "continuationItemRenderer")
                .Select(c => Str(c, "continuationEndpoint", "continuationCommand", "token")
                    ?? (c["continuationEndpoint"]?["commandExecutorCommand"]?["commands"] as JsonArray)?
                        .Select(cmd => Str(cmd, "continuationCommand", "token")).FirstOrDefault(t => t is not null))
                .LastOrDefault(t => t is not null);
            if (token is not null && seen.Add(token))
            {
                page = await PostAsync("browse", new JsonObject { ["continuation"] = token }, ct);
                continue;
            }
            string? legacyToken = legacy ? Find(page, "nextContinuationData").Select(c => Str(c, "continuation")).LastOrDefault(t => t is not null) : null;
            if (legacyToken is null || !seen.Add(legacyToken)) yield break;
            string q = "&ctoken=" + Uri.EscapeDataString(legacyToken) + "&continuation=" + Uri.EscapeDataString(legacyToken) + "&type=next";
            page = await PostAsync("browse", (JsonObject)body.DeepClone(), ct, q);
        }
    }

    // ── parsing ────────────────────────────────────────────────────────

    private static List<Track> Songs(JsonNode root)
    {
        var tracks = new List<Track>();
        foreach (var item in Find(root, "musicResponsiveListItemRenderer"))
            if (ListSong(item) is { } t) tracks.Add(t);
        return tracks;
    }

    /// <summary>A song row (search results, playlists): title column, then artist/album/length runs.</summary>
    private static Track? ListSong(JsonNode item)
    {
        if (Str(item, "musicItemRendererDisplayPolicy") == "MUSIC_ITEM_RENDERER_DISPLAY_POLICY_GREY_OUT") return null;
        var columns = item["flexColumns"] as JsonArray;
        if (columns is null || columns.Count == 0) return null;
        JsonNode? Column(int i) => columns[i]?["musicResponsiveListItemFlexColumnRenderer"]?["text"];

        string? videoId = Str(item, "overlay", "musicItemThumbnailOverlayRenderer", "content", "musicPlayButtonRenderer", "playNavigationEndpoint", "watchEndpoint", "videoId")
            ?? Str(Column(0), "runs", 0, "navigationEndpoint", "watchEndpoint", "videoId")
            ?? Str(item, "playlistItemData", "videoId");
        if (videoId is null) return null;

        string title = Runs(Column(0));
        var artists = new List<string>();
        string album = "", duration = Runs(item["fixedColumns"]?[0]?["musicResponsiveListItemFixedColumnRenderer"]?["text"]);
        string? fallbackArtist = null;
        for (int i = 1; i < columns.Count; i++)
        {
            if (Column(i)?["runs"] is not JsonArray runs) continue;
            foreach (var run in runs)
            {
                string text = Str(run, "text") ?? "";
                string? page = Str(run, "navigationEndpoint", "browseEndpoint", "browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig", "pageType");
                if (page is "MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_UNKNOWN") artists.Add(text);
                else if (page == "MUSIC_PAGE_TYPE_ALBUM") album = text;
                else if (DurationPattern().IsMatch(text)) duration = text;
                else if (i == 1 && fallbackArtist is null && text.Trim() is { Length: > 1 } t && t != "•") fallbackArtist = t;
            }
        }
        string artist = artists.Count > 0 ? string.Join(", ", artists) : fallbackArtist ?? "";
        return StreamTracks.Get(videoId, title, artist, album, ParseDuration(duration), Cover(item["thumbnail"]?["musicThumbnailRenderer"]?["thumbnail"]));
    }

    /// <summary>A song in a radio/queue panel: title, "artist • album • year" byline, length.</summary>
    private static Track? PanelSong(JsonNode item)
    {
        if (Str(item, "videoId") is not { } videoId) return null;
        var artists = new List<string>();
        string album = "";
        if (item["longBylineText"]?["runs"] is JsonArray runs)
            foreach (var run in runs)
            {
                string? page = Str(run, "navigationEndpoint", "browseEndpoint", "browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig", "pageType");
                if (page is "MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_UNKNOWN") artists.Add(Str(run, "text") ?? "");
                else if (page == "MUSIC_PAGE_TYPE_ALBUM") album = Str(run, "text") ?? "";
            }
        if (artists.Count == 0) artists.Add(Str(item, "shortBylineText", "runs", 0, "text") ?? "");
        return StreamTracks.Get(videoId, Runs(item["title"]), string.Join(", ", artists), album,
            ParseDuration(Runs(item["lengthText"])), Cover(item["thumbnail"]));
    }

    /// <summary>The largest thumbnail, asked for at 400 px (enough for the palette and the media flyout).</summary>
    private static string? Cover(JsonNode? thumbnail)
    {
        if (thumbnail?["thumbnails"] is not JsonArray list || list.Count == 0) return null;
        string? url = Str(list[^1], "url");
        return url is null ? null : SizePattern().Replace(url, "=w400-h400");
    }

    private static TimeSpan ParseDuration(string text)
    {
        var parts = text.Trim().Split(':');
        int seconds = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out int v)) return TimeSpan.Zero;
            seconds = seconds * 60 + v;
        }
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Concatenated text of a "runs" node.</summary>
    private static string Runs(JsonNode? node) =>
        node?["runs"] is JsonArray runs ? string.Concat(runs.Select(r => Str(r, "text") ?? "")) : Str(node, "simpleText") ?? "";

    private static string? Str(JsonNode? node, params object[] path)
    {
        foreach (var key in path)
        {
            node = key switch
            {
                string name => (node as JsonObject)?[name],
                int index => node is JsonArray a && index < a.Count ? a[index] : null,
                _ => null,
            };
            if (node is null) return null;
        }
        return node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    }

    /// <summary>Every object stored under `key` anywhere in the tree, in document order.</summary>
    private static IEnumerable<JsonNode> Find(JsonNode? node, string key)
    {
        var stack = new Stack<JsonNode>();
        if (node is not null) stack.Push(node);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is JsonObject o)
            {
                // Push children in reverse so they pop in document order.
                foreach (var (k, v) in o.Reverse())
                {
                    if (v is null) continue;
                    if (k == key) { yield return v; continue; }
                    stack.Push(v);
                }
            }
            else if (n is JsonArray a)
                for (int i = a.Count - 1; i >= 0; i--)
                    if (a[i] is { } child) stack.Push(child);
        }
    }

    [GeneratedRegex(@"^\d{1,2}(:\d{2}){1,2}$")]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"=w\d+-h\d+")]
    private static partial Regex SizePattern();

    public void Dispose() => _http.Dispose();
}
