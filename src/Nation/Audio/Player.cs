using System.Runtime.InteropServices;
using Nation.Library;

namespace Nation.Audio;

/// <summary>
/// Playback engine backed by libmpv. The queue lives in mpv's own playlist so
/// gapless transitions and pre-buffering of the next track come for free.
/// </summary>
public sealed unsafe class Player : IPlayback, IDisposable
{
    private readonly IntPtr _mpv;
    // Mirror of mpv's playlist, one entry per playlist item (same order, same indices).
    private readonly List<Entry> _entries = new();
    private readonly QueueView _view;
    // Everything scanned, and what the queue is built from (the library or one playlist) in its own order.
    private readonly List<Track> _library = new();
    private List<Track> _context = new();
    private bool _disposed;

    public bool Shuffled { get; private set; }

    /// <summary>The play order exactly as mpv will play it, user-queued entries included.</summary>
    public IReadOnlyList<Track> Queue => _view;
    public IReadOnlyList<Track> Library => _library;
    /// <summary>Playlist the queue was built from, or null for the whole library.</summary>
    public string? ContextName { get; private set; }
    /// <summary>Bumped on every change to the queue's order or content (for UI caches and session saves).</summary>
    public int Version { get; private set; }

    public Player()
    {
        _view = new QueueView(_entries);
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
        bool first = _entries.Count == 0;
        _library.AddRange(list);
        _context.AddRange(list);
        _entries.AddRange(list.Select(t => new Entry(t)));
        Version++;
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
        if (_index != _prunedAt) Prune();
        _paused = GetFlag("pause");
        _muted = GetFlag("mute");
        _idle = GetFlag("idle-active");
        _position = GetDouble("time-pos", 0);
        _duration = GetDouble("duration", 0);
        _volume = GetDouble("volume", 100);
    }

    public int CurrentIndex => _index;
    public Track? Current => _index >= 0 && _index < _entries.Count ? _entries[_index].Track : null;

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

    /// <summary>Switches to a queue entry without changing pause state (used to restore the last session).</summary>
    public void JumpTo(int index)
    {
        if (index < 0 || index >= _entries.Count) return;
        Cmd("playlist-play-index", index.ToString());
        SyncIndex();
    }

    // ── queue editing ──────────────────────────────────────────────────
    // Every edit goes through mpv's own playlist commands (insert, move, remove) so
    // the playing song never stops and the next one stays pre-buffered for gapless.

    private sealed class Entry(Track track, bool queued = false)
    {
        public Track Track { get; } = track;
        /// <summary>Added by the user with "a continuación" (x), as opposed to coming from the list being played.</summary>
        public bool Queued { get; set; } = queued;
    }

    /// <summary>Read-only Track view over the entries, so callers index the queue like a list of tracks.</summary>
    private sealed class QueueView(List<Entry> entries) : IReadOnlyList<Track>
    {
        public Track this[int index] => entries[index].Track;
        public int Count => entries.Count;
        public IEnumerator<Track> GetEnumerator() => entries.Select(e => e.Track).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public bool IsQueued(int index) => index >= 0 && index < _entries.Count && _entries[index].Queued;

    /// <summary>User-queued entries waiting right after the current one; new ones go after them.</summary>
    public int UpNextCount
    {
        get
        {
            int n = 0;
            for (int i = _index + 1; i < _entries.Count && _entries[i].Queued; i++) n++;
            return n;
        }
    }

    public IEnumerable<Track> UpNext => Enumerable.Range(_index + 1, UpNextCount).Select(i => _entries[i].Track);

    private int _prunedAt = -2;

    /// <summary>
    /// Queued songs are one-offs: once playback is two entries past one, it leaves the
    /// queue (the one just before the current stays, so "previous" can still go back to it).
    /// </summary>
    private void Prune()
    {
        bool removed = false;
        for (int i = _index - 2; i >= 0; i--)
            if (_entries[i].Queued)
            {
                Cmd("playlist-remove", i.ToString());
                _entries.RemoveAt(i);
                removed = true;
            }
        if (removed) { SyncIndex(); Version++; }
        _prunedAt = _index;
    }

    /// <summary>Live playlist position: the cached one is stale right after a command.</summary>
    private void SyncIndex() => _index = (int)GetInt64("playlist-pos", -1);

    /// <summary>Moves an entry so it ends up at index `to` (mpv's playlist-move takes the entry to insert before).</summary>
    private void Move(int from, int to)
    {
        if (from == to) return;
        Cmd("playlist-move", from.ToString(), (from < to ? to + 1 : to).ToString());
        var e = _entries[from];
        _entries.RemoveAt(from);
        _entries.Insert(to, e);
    }

    /// <summary>
    /// "A continuación": plays these after the current song and after anything queued
    /// before them (front = right after the current song instead). The rest of the
    /// queue keeps going afterwards.
    /// </summary>
    public void AddToUpNext(IReadOnlyList<Track> tracks, bool front = false)
    {
        if (tracks.Count == 0) return;
        SyncIndex();
        int at = _index < 0 ? _entries.Count : _index + 1 + (front ? 0 : UpNextCount);
        int end = _entries.Count;
        LoadList(tracks, "append");
        for (int k = 0; k < tracks.Count; k++)
        {
            _entries.Add(new Entry(tracks[k], queued: true));
            Move(end + k, at + k);
        }
        SyncIndex();
        Version++;
    }

    /// <summary>An upcoming entry joins "a continuación" (moved, not copied); anything else is queued as a copy.</summary>
    public void QueueEntry(int index, bool front = false)
    {
        SyncIndex();
        if (index < 0 || index >= _entries.Count) return;
        if (index <= _index || _entries[index].Queued)
        {
            AddToUpNext(new[] { _entries[index].Track }, front);
            return;
        }
        int to = _index + 1 + (front ? 0 : UpNextCount);
        Move(index, to);
        _entries[to].Queued = true;
        SyncIndex();
        Version++;
    }

    /// <summary>Swaps an upcoming entry with its neighbor; returns its new index (the current song can't be passed).</summary>
    public int MoveEntry(int index, int delta)
    {
        SyncIndex();
        int to = index + delta;
        if (index <= _index || to <= _index || to >= _entries.Count || index >= _entries.Count) return index;
        Move(index, to);
        SyncIndex();
        Version++;
        return to;
    }

    /// <summary>Drops an entry from the queue (not the one playing). Returns false when it can't.</summary>
    public bool RemoveEntry(int index)
    {
        SyncIndex();
        if (index < 0 || index >= _entries.Count || index == _index) return false;
        Cmd("playlist-remove", index.ToString());
        _entries.RemoveAt(index);
        SyncIndex();
        Version++;
        return true;
    }

    /// <summary>Plays a queue entry; songs waiting in "a continuación" move along to play right after it.</summary>
    public void PlayEntry(int index)
    {
        SyncIndex();
        if (index < 0 || index >= _entries.Count) return;
        var target = _entries[index];
        var pending = PendingEntries().Where(e => !ReferenceEquals(e, target)).ToList();
        for (int k = 0; k < pending.Count; k++)
        {
            int from = _entries.IndexOf(pending[k]), t = _entries.IndexOf(target);
            Move(from, (from < t ? t : t + 1) + k);
        }
        Cmd("playlist-play-index", _entries.IndexOf(target).ToString());
        MpvNative.SetPropertyString(_mpv, "pause", "no");
        SyncIndex();
        Version++;
    }

    private List<Entry> PendingEntries() => _entries.Skip(_index + 1).Take(UpNextCount).ToList();

    /// <summary>
    /// Plays a song from a list (the library, or the playlist called `name`): the queue becomes
    /// that list, in its order (or shuffled), and "a continuación" survives the switch.
    /// </summary>
    public void PlayFrom(IReadOnlyList<Track> list, string? name, int index, bool play = true)
    {
        if (index < 0 || index >= list.Count) return;
        SyncIndex();
        if (name == ContextName && SameContext(list))
        {
            // Same list already queued: jump to that occurrence of the song instead of rebuilding.
            int occurrence = list.Take(index).Count(t => ReferenceEquals(t, list[index]));
            int at = -1;
            for (int i = 0, seen = 0; i < _entries.Count; i++)
                if (!_entries[i].Queued && ReferenceEquals(_entries[i].Track, list[index]) && seen++ == occurrence) { at = i; break; }
            if (at >= 0)
            {
                if (play) PlayEntry(at); else JumpTo(at);
                return;
            }
        }

        var pending = PendingEntries();
        var start = list[index];
        Cmd("loadfile", start.Path, "replace"); // the playlist is now just this song, already current
        if (play) MpvNative.SetPropertyString(_mpv, "pause", "no");
        _context = list.ToList();
        ContextName = name;
        _entries.Clear();
        _entries.Add(new Entry(start));
        if (Shuffled)
            Rebuild(new List<Entry>(), pending.Concat(Shuffle(_context.Where((_, i) => i != index))).ToList());
        else
            Rebuild(_context.Take(index).Select(t => new Entry(t)).ToList(),
                pending.Concat(_context.Skip(index + 1).Select(t => new Entry(t))).ToList());
    }

    /// <summary>Keeps the queue tied to its playlist after the playlist is renamed.</summary>
    public void RenameContext(string from, string to)
    {
        if (ContextName == from) ContextName = to;
    }

    private bool SameContext(IReadOnlyList<Track> list)
    {
        if (list.Count != _context.Count) return false;
        for (int i = 0; i < list.Count; i++)
            if (!ReferenceEquals(list[i], _context[i])) return false;
        return true;
    }

    private static IEnumerable<Entry> Shuffle(IEnumerable<Track> tracks)
    {
        var list = tracks.ToList();
        Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list));
        return list.Select(t => new Entry(t));
    }

    /// <summary>
    /// Lays `before` and `after` around the single entry left in the playlist (the playing
    /// one, after playlist-clear or loadfile replace) without interrupting it:
    /// [cur] → [cur, before…] → move cur behind them → [before…, cur] → [before…, cur, after…].
    /// </summary>
    private void Rebuild(List<Entry> before, List<Entry> after)
    {
        if (before.Count > 0)
        {
            LoadList(before.Select(e => e.Track).ToList(), "append");
            Cmd("playlist-move", "0", (before.Count + 1).ToString());
            _entries.InsertRange(0, before);
        }
        LoadList(after.Select(e => e.Track).ToList(), "append");
        _entries.AddRange(after);
        SyncIndex();
        _prunedAt = _index;
        Version++;
    }

    /// <summary>
    /// Shuffle on: playing song first, then "a continuación", then the rest of the list randomized.
    /// Off: back to the list's order, resuming where the playing song sits in it.
    /// The current song never stops or restarts.
    /// </summary>
    public void ToggleShuffle()
    {
        SyncIndex();
        var playing = _index >= 0 && _index < _entries.Count ? _entries[_index] : null;
        var pending = playing is null ? new List<Entry>() : PendingEntries();
        Shuffled = !Shuffled;

        if (playing is null)
        {
            Cmd("playlist-clear");
            _entries.Clear();
            var order = (Shuffled ? Shuffle(_context) : _context.Select(t => new Entry(t))).ToList();
            LoadList(order.Select(e => e.Track).ToList(), "append");
            _entries.AddRange(order);
            SyncIndex();
            Version++;
            return;
        }

        List<Entry> before, after;
        if (Shuffled)
        {
            int self = playing.Queued ? -1 : _context.FindIndex(t => ReferenceEquals(t, playing.Track));
            before = new();
            after = pending.Concat(Shuffle(_context.Where((_, i) => i != self))).ToList();
        }
        else
        {
            // A queued song isn't part of the list: resume at the first list song that was due after it.
            var anchor = playing.Queued ? _entries.Skip(_index + 1).FirstOrDefault(e => !e.Queued)?.Track : playing.Track;
            int at = anchor is null ? -1 : _context.FindIndex(t => ReferenceEquals(t, anchor));
            if (at < 0) at = _context.Count;
            int resume = playing.Queued ? at : at + 1;
            before = _context.Take(at).Select(t => new Entry(t)).ToList();
            after = pending.Concat(_context.Skip(resume).Select(t => new Entry(t))).ToList();
        }
        Cmd("playlist-clear"); // playing entry survives at index 0
        _entries.Clear();
        _entries.Add(playing);
        Rebuild(before, after);
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

    private string? GetString(string name)
    {
        byte* v;
        if (MpvNative.GetProperty(_mpv, name, MpvNative.Format.String, &v) < 0) return null;
        try { return Marshal.PtrToStringUTF8((IntPtr)v); }
        finally { MpvNative.Free(v); }
    }

    /// <summary>
    /// Self-check for --snapshot: entries whose file differs from mpv's own playlist
    /// at the same index (0 = the mirror is exact), or -1 when the lengths differ.
    /// </summary>
    public int MirrorMismatches()
    {
        if (GetInt64("playlist-count", -1) != _entries.Count) return -1;
        int bad = 0;
        for (int i = 0; i < _entries.Count; i++)
            if (!string.Equals(GetString($"playlist/{i}/filename"), _entries[i].Track.Path, StringComparison.OrdinalIgnoreCase)) bad++;
        return bad;
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
