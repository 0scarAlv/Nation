using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Nation.Art;
using Nation.Library;

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
                var path = await ReadRequestPathAsync(stream);
                if (path is null) return;
                LastRequestUtc = DateTime.UtcNow;
                var (status, mime, body) = Route(path);
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

    /// <summary>Reads up to the end of the headers and returns the request path (GET only).</summary>
    private static async Task<string?> ReadRequestPathAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read));
            if (n == 0) break;
            read += n;
            if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n")) break;
        }
        var requestLine = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n")[0].Split(' ');
        return requestLine.Length >= 2 ? requestLine[1] : null;
    }

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
