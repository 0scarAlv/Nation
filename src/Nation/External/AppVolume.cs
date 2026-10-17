using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace Nation.External;

/// <summary>
/// Per-app volume in the Windows mixer (the same slider as the volume mixer),
/// so +/- and mute act on the followed app instead of Nation's own mpv.
/// </summary>
internal sealed class AppVolume : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    // Process names by id; audio sessions come and go, processes keep their name.
    private readonly Dictionary<uint, string> _names = new();

    /// <summary>
    /// Process name behind a media session's app id: "Spotify.exe", a packaged
    /// "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", or a browser's "Chrome"/"MSEdge".
    /// </summary>
    public static string ProcessNameFor(string appId)
    {
        // Firefox registers under an opaque hash.
        if (appId.Contains("308046B0AF4A39CB", StringComparison.OrdinalIgnoreCase)) return "firefox";
        var fragment = appId.Contains('!') ? appId[(appId.LastIndexOf('!') + 1)..] : appId;
        if (fragment.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) fragment = fragment[..^4];
        return fragment;
    }

    /// <summary>Volume (0–1) and mute of the process's first audio session; null when it has none.</summary>
    public (float Volume, bool Muted)? Read(string process)
    {
        (float, bool)? result = null;
        ForEach(process, v => result ??= (v.Volume, v.Mute));
        return result;
    }

    /// <summary>Applies a change to every audio session of the process (apps like Spotify may open several).</summary>
    public void Apply(string process, Action<SimpleAudioVolume> change) => ForEach(process, change);

    private void ForEach(string process, Action<SimpleAudioVolume> action)
    {
        try
        {
            // Every output, not just the default: an app keeps its stream on the device it
            // opened, so after a switch to headphones Spotify may still be on the speakers.
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        if (session.IsSystemSoundsSession) continue;
                        if (string.Equals(NameOf(session.GetProcessID), process, StringComparison.OrdinalIgnoreCase))
                            action(session.SimpleAudioVolume);
                    }
                }
            }
        }
        catch (Exception)
        {
            // No device or the session vanished mid-read; volume just shows as unknown.
        }
    }

    private string NameOf(uint pid)
    {
        lock (_names)
        {
            if (_names.TryGetValue(pid, out var name)) return name;
            try { name = Process.GetProcessById((int)pid).ProcessName; }
            catch (Exception) { name = ""; }
            return _names[pid] = name;
        }
    }

    public void Dispose() => _enumerator.Dispose();
}
