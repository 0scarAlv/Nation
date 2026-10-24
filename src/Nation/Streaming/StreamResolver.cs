using System.Collections.Concurrent;
using System.Diagnostics;

namespace Nation.Streaming;

/// <summary>
/// Turns a YouTube Music link into its direct audio URL ahead of time. mpv's ytdl hook
/// would do the same when the song starts, but yt-dlp takes ~5 s, which is 5 s of
/// silence between songs; resolving the next song while the current one plays makes
/// the change near-instant. Direct URLs expire (Google puts the time in them), so
/// they're cached only until shortly before that.
/// </summary>
public sealed class StreamResolver
{
    private readonly ConcurrentDictionary<string, (string Url, DateTime Expires)> _resolved = new();
    private readonly ConcurrentDictionary<string, byte> _pending = new();
    private readonly ConcurrentDictionary<string, DateTime> _failed = new();

    /// <summary>A still-valid direct URL for the page (and when to stop using it), or null.</summary>
    public string? TryGet(string pageUrl, out DateTime expires)
    {
        expires = default;
        if (!_resolved.TryGetValue(pageUrl, out var hit) || hit.Expires <= DateTime.UtcNow) return null;
        expires = hit.Expires;
        return hit.Url;
    }

    public string? TryGet(string pageUrl) => TryGet(pageUrl, out _);

    public bool IsPending(string pageUrl) => _pending.ContainsKey(pageUrl);

    /// <summary>Starts resolving in the background (no-op when already done, running, or recently failed).</summary>
    public void Request(string pageUrl)
    {
        if (TryGet(pageUrl) is not null || !YtDlp.Available) return;
        if (_failed.TryGetValue(pageUrl, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(2)) return;
        if (!_pending.TryAdd(pageUrl, 0)) return;
        _ = Task.Run(() =>
        {
            try
            {
                // Without a JavaScript runtime it's ~3 s faster, but YouTube may demand one: then retry with it.
                if ((Resolve(pageUrl, null) ?? (YtDlp.JsRuntimeOption() is { } js ? Resolve(pageUrl, js) : null)) is { } url) _resolved[pageUrl] = (url, ExpiryOf(url));
                else _failed[pageUrl] = DateTime.UtcNow;
            }
            finally { _pending.TryRemove(pageUrl, out _); }
        });
    }

    /// <summary>Runs yt-dlp -g; `js` is an option like "js-runtimes=node", or null for none.</summary>
    private static string? Resolve(string pageUrl, string? js)
    {
        var args = new List<string> { "--no-warnings", "--no-playlist", "-f", "bestaudio/best", "-g" };
        if (js is not null) { args.Add("--" + js.Split('=')[0]); args.Add(js.Split('=')[1]); }
        args.Add("--");
        args.Add(pageUrl);
        var info = new ProcessStartInfo("yt-dlp") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            process.ErrorDataReceived += (_, _) => { }; // drain so a chatty stderr can't block it
            process.BeginErrorReadLine();
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromSeconds(60))) { process.Kill(); return null; }
            string? url = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return process.ExitCode == 0 && url is not null && url.StartsWith("http", StringComparison.Ordinal) ? url : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The "expire=" Unix time in the URL, minus a margin; one hour when absent.</summary>
    private static DateTime ExpiryOf(string url)
    {
        int at = url.IndexOf("expire=", StringComparison.Ordinal);
        if (at >= 0)
        {
            var digits = new string(url.Skip(at + 7).TakeWhile(char.IsDigit).ToArray());
            if (long.TryParse(digits, out long unix)) return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime - TimeSpan.FromMinutes(15);
        }
        return DateTime.UtcNow + TimeSpan.FromHours(1);
    }
}
