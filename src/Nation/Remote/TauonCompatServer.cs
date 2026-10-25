using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Nation.Art;
using Nation.Library;
using Nation.Stats;

namespace Nation.Remote;

/// <summary>What the remote API reports; rebuilt by the UI thread every frame.</summary>
public sealed record RemoteSnapshot(
    string Status,          // "playing" | "paused" | "stopped", as Tauon reports it
    bool Shuffle,
    bool Repeat,
    long ProgressMs,
    Track? Track,
    string? LyricsText)     // LRC (synced) or plain text, null when none/not loaded yet
{
    public static readonly RemoteSnapshot Empty = new("stopped", false, false, 0, null, null);
}

/// <summary>
/// Speaks the subset of Tauon Music Box's local remote API (t_webserve.py, /api1/*)
/// that Shade's TauonBridge uses, so Shade drives Nation with zero changes.
/// Bound to loopback only. Commands are queued and executed on the UI thread,
/// because the queue/shuffle state is owned there.
/// </summary>
public sealed class TauonCompatServer : IDisposable
{
    public const int DefaultPort = 7814;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    // Only the current track's cover is ever requested; keep just that one.
    // Keyed by cover too: another app's cover can arrive after its title.
    private (string Key, (byte[] Bytes, string Mime)? Art) _lastArt = ("", null);
    private readonly object _artLock = new();

    public ConcurrentQueue<Action> Commands { get; } = new();

    /// <summary>Set when a command is queued so the UI loop wakes up instead of waiting for its next frame.</summary>
    public AutoResetEvent Wake { get; } = new(false);
    public RemoteSnapshot Snapshot { get; set; } = RemoteSnapshot.Empty;
    public int Port { get; }
    public bool Running { get; private set; }
    public string? Error { get; private set; }
    public DateTime LastRequestUtc { get; private set; }

    /// <summary>The journal's sync endpoints (/nation/*); null while stats are off.</summary>
    public ListenSync? Sync { get; set; }

    /// <summary>Called on the UI thread to run a command: play, pause, next, back, seek, shuffle, repeat.</summary>
    public Action<string, long>? OnCommand { get; set; }

    public TauonCompatServer(int port)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start()
    {
        try
        {
            _listener.Start();
            Running = true;
            _ = AcceptLoopAsync();
        }
        catch (SocketException ex)
        {
            // Usually Tauon itself is open with its API on the same port.
            Error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse ? $"puerto {Port} ocupado" : ex.Message;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { break; }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 2000;
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream);
                if (request is not { } req) return;
                LastRequestUtc = DateTime.UtcNow;
                var (status, mime, body) = req.Path.StartsWith("/nation/") ? RouteNation(req.Method, req.Path, req.Body) : Route(req.Path);
                var header = $"HTTP/1.1 {status}\r\nContent-Type: {mime}\r\nContent-Length: {body.Length}\r\n" +
                             "Access-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                await stream.WriteAsync(body);
            }
            catch (Exception)
            {
                // Client went away mid-request; nothing to recover.
            }
        }
    }

    /// <summary>Reads the request line, headers and (for POST) a Content-Length body.</summary>
    private static async Task<(string Method, string Path, byte[] Body)?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int headerEnd = -1;
        while (headerEnd < 0 && buffer.Length < 64 * 1024)
        {
            int n = await stream.ReadAsync(chunk);
            if (n == 0) break;
            buffer.Write(chunk, 0, n);
            headerEnd = IndexOfHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
        }
        if (headerEnd < 0) return null;

        var all = buffer.ToArray();
        var head = Encoding.ASCII.GetString(all, 0, headerEnd).Split("\r\n");
        var requestLine = head[0].Split(' ');
        if (requestLine.Length < 2) return null;
        int length = 0;
        bool chunked = false;
        foreach (var line in head.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out length);
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && value.Contains("chunked", StringComparison.OrdinalIgnoreCase)) chunked = true;
        }

        // Whatever already arrived past the headers, then the rest from the socket.
        var rest = new MemoryStream();
        rest.Write(all, headerEnd + 4, all.Length - (headerEnd + 4));
        if (chunked)
        {
            var body = await ReadChunkedAsync(stream, rest);
            return body is null ? null : (requestLine[0], requestLine[1], body);
        }
        if (length is < 0 or > MaxBodyBytes) return null;
        while (rest.Length < length)
        {
            int n = await stream.ReadAsync(chunk);
            if (n == 0) return null;
            rest.Write(chunk, 0, n);
        }
        return (requestLine[0], requestLine[1], rest.ToArray().AsSpan(0, length).ToArray());
    }

    // Transfer-Encoding: chunked (what HttpClient sends for JSON content of unknown length):
    // hex size line, data, CRLF … until a zero-size chunk.
    private static async Task<byte[]?> ReadChunkedAsync(NetworkStream stream, MemoryStream received)
    {
        var raw = received;
        var chunk = new byte[8192];
        var body = new MemoryStream();
        int pos = 0;
        while (true)
        {
            int lineEnd;
            while ((lineEnd = IndexOfCrLf(raw.GetBuffer(), pos, (int)raw.Length)) < 0)
                if (!await ReadMore()) return null;
            var sizeText = Encoding.ASCII.GetString(raw.GetBuffer(), pos, lineEnd - pos).Split(';')[0].Trim();
            if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber, null, out int size) || size < 0) return null;
            pos = lineEnd + 2;
            if (size == 0) return body.ToArray();
            if (body.Length + size > MaxBodyBytes) return null;
            while (raw.Length < pos + size + 2)
                if (!await ReadMore()) return null;
            body.Write(raw.GetBuffer(), pos, size);
            pos += size + 2;
        }

        async Task<bool> ReadMore()
        {
            int n = await stream.ReadAsync(chunk);
            if (n == 0) return false;
            raw.Position = raw.Length;
            raw.Write(chunk, 0, n);
            return true;
        }
    }

    private static int IndexOfCrLf(byte[] data, int from, int count)
    {
        for (int i = from + 1; i < count; i++)
            if (data[i - 1] == '\r' && data[i] == '\n') return i - 1;
        return -1;
    }

    private const int MaxBodyBytes = 32 * 1024 * 1024;

    private static int IndexOfHeaderEnd(byte[] data, int count)
    {
        for (int i = 3; i < count; i++)
            if (data[i - 3] == '\r' && data[i - 2] == '\n' && data[i - 1] == '\r' && data[i] == '\n') return i - 3;
        return -1;
    }

    /// <summary>
    /// Listening-journal sync for Shade's phone player (relayed by the Shade agent):
    /// GET /nation/profile, GET /nation/listens?since=&amp;sinceId=&amp;limit=, POST /nation/listens.
    /// </summary>
    private (string, string, byte[]) RouteNation(string method, string path, byte[] body)
    {
        var sync = Sync;
        if (sync is null) return ("503 Service Unavailable", "text/plain", Encoding.ASCII.GetBytes("stats off"));
        var route = path.Split('?')[0].TrimEnd('/');
        try
        {
            switch (method, route)
            {
                case ("GET", "/nation/profile"):
                    return NationJson(new { listener = sync.Owner.ListenerId, name = sync.Owner.Name, device = sync.Owner.DeviceId });
                case ("GET", "/nation/listens"):
                {
                    var query = ParseQuery(path);
                    long since = long.TryParse(query.GetValueOrDefault("since"), out var s) ? s : 0;
                    int limit = int.TryParse(query.GetValueOrDefault("limit"), out var l) ? Math.Clamp(l, 1, 5000) : 1000;
                    var (listens, more) = sync.Changed(since, query.GetValueOrDefault("sinceId") ?? "", limit);
                    return NationJson(new { listener = sync.Owner.ListenerId, listens, more });
                }
                case ("POST", "/nation/listens"):
                {
                    var push = JsonSerializer.Deserialize<ListenPush>(body, ListenSync.Json);
                    if (push?.Device is null) return ("400 Bad Request", "text/plain", Encoding.ASCII.GetBytes("device required"));
                    int accepted = sync.Merge(push.Device, push.Listens ?? []);
                    return NationJson(new { accepted });
                }
            }
        }
        catch (Exception e) when (e is JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            return ("500 Internal Server Error", "text/plain", Encoding.UTF8.GetBytes(e.Message));
        }
        return NotFound();
    }

    private sealed record ListenPush(DeviceDto? Device, List<ListenDto>? Listens);

    private static Dictionary<string, string> ParseQuery(string path)
    {
        var result = new Dictionary<string, string>();
        int q = path.IndexOf('?');
        if (q < 0) return result;
        foreach (var pair in path[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0) result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return result;
    }

    private static (string, string, byte[]) NationJson(object value) =>
        ("200 OK", "application/json", JsonSerializer.SerializeToUtf8Bytes(value, ListenSync.Json));

    private (string Status, string Mime, byte[] Body) Route(string path)
    {
        var parts = path.Split('?')[0].Trim('/').Split('/');
        if (parts.Length < 2 || parts[0] != "api1") return NotFound();

        var snap = Snapshot;
        switch (parts[1])
        {
            case "status":
                return Json(new
                {
                    status = snap.Status,
                    shuffle = snap.Shuffle,
                    repeat = snap.Repeat,
                    progress = snap.ProgressMs,
                    id = snap.Track?.Id,
                    track = snap.Track is { } t
                        ? new
                        {
                            id = t.Id,
                            title = t.Title,
                            artist = t.Artist,
                            album = t.Album,
                            duration = (long)t.Duration.TotalMilliseconds,
                            has_lyrics = snap.LyricsText is not null,
                        }
                        : null,
                });

            case "pic" when parts.Length >= 4 && int.TryParse(parts[3], out var picId):
                return Art(picId, snap);

            case "lyrics" when parts.Length >= 3 && int.TryParse(parts[2], out var lyrId):
                return Json(new { lyrics_text = snap.Track?.Id == lyrId ? snap.LyricsText ?? "" : "" });

            case "play" or "pause" or "next" or "back" or "shuffle" or "repeat":
                Enqueue(parts[1], 0);
                return Json(new { ok = true });

            case "seek" when parts.Length >= 3 && long.TryParse(parts[2], out var ms):
                Enqueue("seek", ms);
                return Json(new { ok = true });
        }
        return NotFound();
    }

    private void Enqueue(string command, long arg)
    {
        var handler = OnCommand;
        if (handler is null) return;
        Commands.Enqueue(() => handler(command, arg));
        Wake.Set();
    }

    private (string, string, byte[]) Art(int id, RemoteSnapshot snap)
    {
        if (snap.Track is not { } t || t.Id != id) return NotFound();
        (byte[] Bytes, string Mime)? art;
        // Requests run concurrently; the two-field cache must not be read half-written.
        lock (_artLock)
        {
            string key = $"{id}:{t.CoverKey}";
            if (_lastArt.Key != key) _lastArt = (key, LoadArt());
            art = _lastArt.Art;
        }
        return art is { } a ? ("200 OK", a.Mime, a.Bytes) : NotFound();

        (byte[] Bytes, string Mime)? LoadArt()
        {
            try
            {
                var (_, bytes) = CoverArt.FindCover(t);
                if (bytes is null) return null;
                bool png = bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50;
                return (bytes, png ? "image/png" : "image/jpeg");
            }
            catch (Exception) { return null; }
        }
    }

    private static (string, string, byte[]) Json(object value) =>
        ("200 OK", "application/json", JsonSerializer.SerializeToUtf8Bytes(value));

    private static (string, string, byte[]) NotFound() =>
        ("404 Not Found", "text/plain", Encoding.ASCII.GetBytes("not found"));

    public void Dispose()
    {
        _cts.Cancel();
        if (Running) _listener.Stop();
        Wake.Dispose();
    }
}
