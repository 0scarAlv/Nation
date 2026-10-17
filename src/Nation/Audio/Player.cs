using Nation.Library;

namespace Nation.Audio;

/// <summary>
/// Playback engine backed by libmpv. The queue lives in mpv's own playlist so
/// gapless transitions and pre-buffering of the next track come for free.
/// </summary>
public sealed unsafe class Player : IDisposable
{
    private readonly IntPtr _mpv;
    private readonly List<Track> _queue = new();
    private readonly List<Track> _original = new();
    private bool _disposed;

    public bool Shuffled { get; private set; }

    public IReadOnlyList<Track> Queue => _queue;

    public Player()
    {
        _mpv = MpvNative.Create();
        if (_mpv == IntPtr.Zero)
            throw new InvalidOperationException("mpv_create failed (is libmpv-2.dll next to the exe?)");

        // Audio-only, no mpv terminal/OSD/keybindings: Nation owns the terminal.
        Opt("vid", "no");
        Opt("audio-display", "no");
        Opt("terminal", "no");
        Opt("input-default-bindings", "no");
        Opt("idle", "yes");
        // Load the first track but wait for the user to press play.
        Opt("pause", "yes");
        Opt("gapless-audio", "weak");
        Opt("volume-max", "100");

        Check(MpvNative.Initialize(_mpv), "mpv_initialize");
    }

    public void Enqueue(IEnumerable<Track> tracks)
    {
        var list = tracks.ToList();
        bool first = _queue.Count == 0;
        _queue.AddRange(list);
        _original.AddRange(list);
        // "replace" on an empty player starts the first entry (paused, per the pause option).
        LoadList(list, first ? "replace" : "append");
    }

    // One reusable temp playlist per process; deleted on Dispose.
    private readonly string _listPath = Path.Combine(Path.GetTempPath(), $"nation-{Environment.ProcessId}.m3u8");

    /// <summary>
    /// Hands mpv many files in a single loadlist command. One loadfile per track
    /// costs ~10 ms each inside mpv, which made a 300-track queue take ~3 s to
    /// load and froze the UI for the same time on every shuffle toggle.
    /// </summary>
    private void LoadList(IReadOnlyList<Track> tracks, string mode)
    {
        if (tracks.Count == 0) return;
        File.WriteAllLines(_listPath, tracks.Select(t => t.Path), new System.Text.UTF8Encoding(false));
        Check(MpvNative.Command(_mpv, "loadlist", _listPath, mode), "loadlist");
    }

    // Playback state cached once per frame by Refresh(): the UI reads these
    // a dozen-plus times per frame and each live read is a call into mpv.
    private int _index = -1;
    private bool _paused = true, _muted, _idle;
    private double _position, _duration, _volume = 100;

    /// <summary>Re-reads playback state from mpv; call once at the start of each frame.</summary>
    public void Refresh()
    {
        _index = (int)GetInt64("playlist-pos", -1);
        _paused = GetFlag("pause");
        _muted = GetFlag("mute");
        _idle = GetFlag("idle-active");
        _position = GetDouble("time-pos", 0);
        _duration = GetDouble("duration", 0);
        _volume = GetDouble("volume", 100);
    }

    public int CurrentIndex => _index;
    public Track? Current => _index >= 0 && _index < _queue.Count ? _queue[_index] : null;

    public bool Paused => _paused;
    public bool Muted => _muted;
    public bool Idle => _idle;
    public bool Playing => !_paused && !_idle;
    public double Position => _position;
    public double Duration => _duration;
    public double Volume => _volume;

    public void TogglePause() => Cmd("cycle", "pause");
    public void SetPaused(bool paused) => MpvNative.SetPropertyString(_mpv, "pause", paused ? "yes" : "no");
    public void ToggleMute() => Cmd("cycle", "mute");
    public void Next() => Cmd("playlist-next", "force");
    public void Previous()
    {
        // Same convention as most players: restart the track unless we are near its start.
        if (Position > 3) Cmd("seek", "0", "absolute");
        else Cmd("playlist-prev", "force");
    }

    public void SeekRelative(double seconds) => Cmd("seek", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "relative");
    public void SeekAbsolute(double seconds) => Cmd("seek", Math.Max(0, seconds).ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");

    /// <summary>Repeat = loop the whole queue (same on/off model as Tauon).</summary>
    public bool Repeat { get; private set; }
    public void ToggleRepeat()
    {
        Repeat = !Repeat;
        MpvNative.SetPropertyString(_mpv, "loop-playlist", Repeat ? "inf" : "no");
    }

    public void AddVolume(int delta) => Cmd("add", "volume", delta.ToString());
    public void SetVolume(int volume) => MpvNative.SetPropertyString(_mpv, "volume", volume.ToString());

    // Equalizer chain: one labeled mpv filter per band plus a preamp, installed the
    // first time a non-flat curve is applied (so an unused EQ costs nothing). After
    // that, gains change through af-command, which doesn't rebuild the audio chain.
    private bool _eqInstalled;

    /// <summary>Last mpv error from the equalizer, or null when it is working.</summary>
    public string? EqualizerError { get; private set; }

    public void ApplyEqualizer(Equalizer eq)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double Gain(int i) => eq.Enabled ? eq.Gains[i] : 0;
        if (_eqInstalled)
        {
            bool ok = true;
            for (int i = 0; i < Equalizer.Frequencies.Length && ok; i++)
                ok = MpvNative.Command(_mpv, "af-command", $"eq{i}", "g", Gain(i).ToString(inv)) >= 0;
            if (ok) ok = MpvNative.Command(_mpv, "af-command", "eqpre", "volume", eq.Preamp.ToString(inv) + "dB") >= 0;
            if (ok) { EqualizerError = null; return; }
            // e.g. no audio chain yet (nothing loaded): fall back to setting the whole chain.
        }
        else if (eq.IsFlat) return;

        var filters = Equalizer.Frequencies.Select((f, i) =>
            string.Format(inv, "@eq{0}:lavfi=[equalizer=f={1}:t=o:w=1:g={2}]", i, f, Gain(i)));
        string chain = string.Join(",", filters) + string.Format(inv, ",@eqpre:lavfi=[volume=volume={0}dB]", eq.Preamp);
        int code = MpvNative.SetPropertyString(_mpv, "af", chain);
        EqualizerError = code < 0 ? MpvNative.Error(code) : null;
        _eqInstalled = code >= 0;
    }

    public void PlayIndex(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        Cmd("playlist-play-index", index.ToString());
        MpvNative.SetPropertyString(_mpv, "pause", "no");
    }

    /// <summary>
    /// Shuffle on: playing track first, rest randomized. Off: back to the original order.
    /// Rebuilt around mpv's playlist-clear (which keeps the playing entry) so the
    /// current song never stops or restarts.
    /// </summary>
    public void ToggleShuffle()
    {
        // Live read: the cached index may be stale if another command ran this frame.
        int cur = (int)GetInt64("playlist-pos", -1);
        var playing = cur >= 0 && cur < _queue.Count ? _queue[cur] : null;
        Shuffled = !Shuffled;

        List<Track> order;
        if (Shuffled)
        {
            var rest = _original.Where(t => !ReferenceEquals(t, playing)).ToList();
            Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rest));
            order = playing is null ? rest : rest.Prepend(playing).ToList();
        }
        else order = _original.ToList();

        if (playing is null)
        {
            Cmd("playlist-clear");
            LoadList(order, "append");
        }
        else
        {
            Cmd("playlist-clear"); // playing entry survives at index 0
            int at = order.IndexOf(playing);
            if (at > 0)
            {
                // [cur] → [cur, before…] → move cur behind them → [before…, cur]; playback isn't interrupted.
                LoadList(order.GetRange(0, at), "append");
                Cmd("playlist-move", "0", (at + 1).ToString());
            }
            LoadList(order.GetRange(at + 1, order.Count - at - 1), "append");
        }

        _queue.Clear();
        _queue.AddRange(order);
        _index = (int)GetInt64("playlist-pos", -1);
    }

    private void Opt(string name, string value) =>
        Check(MpvNative.SetOptionString(_mpv, name, value), $"option {name}");

    private void Cmd(params string[] args) => MpvNative.Command(_mpv, args);

    private double GetDouble(string name, double fallback)
    {
        double v;
        return MpvNative.GetProperty(_mpv, name, MpvNative.Format.Double, &v) >= 0 ? v : fallback;
    }

    private long GetInt64(string name, long fallback)
    {
        long v;
        return MpvNative.GetProperty(_mpv, name, MpvNative.Format.Int64, &v) >= 0 ? v : fallback;
    }

    private bool GetFlag(string name)
    {
        int v;
        return MpvNative.GetProperty(_mpv, name, MpvNative.Format.Flag, &v) >= 0 && v != 0;
    }

    private static void Check(int code, string what)
    {
        if (code < 0) throw new InvalidOperationException($"{what}: {MpvNative.Error(code)}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        MpvNative.TerminateDestroy(_mpv);
        try { File.Delete(_listPath); } catch (IOException) { }
    }
}
