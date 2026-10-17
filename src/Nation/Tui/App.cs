using System.Diagnostics;
using System.Text;
using Nation.Art;
using Nation.Audio;
using Nation.External;
using Nation.Library;
using Nation.Lyrics;
using Nation.Remote;

namespace Nation.Tui;

/// <summary>Screen layout, animation state and key handling. Playback state is always read from Player.</summary>
public sealed class App
{
    private readonly Player _player;
    private readonly SpectrumAnalyzer _spectrum;
    private readonly LyricsProvider _lyricsProvider;

    /// <summary>Tauon-compatible API for Shade; null when disabled.</summary>
    public TauonCompatServer? Remote { get; init; }
    /// <summary>Windows media session (media keys, volume flyout); null when unavailable.</summary>
    public MediaSession? Media { get; init; }
    /// <summary>Whether to keep session.json up to date (off for --snapshot).</summary>
    public bool PersistSession { get; init; }
    /// <summary>Other apps' media sessions (Spotify, YouTube Music…), for skin mode; null when off.</summary>
    public ExternalPlayer? External { get; init; }
    /// <summary>--skin: follow another app from the start, even if it's paused.</summary>
    public bool StartAsSkin { get; init; }
    /// <summary>Sources panel settings (auto-follow, pinned app); saved on every change.</summary>
    public SourceSettings Sources { get; set; } = new();

    // Sources panel (`o`): takes the lyrics' place, like the equalizer.
    private bool _sourcesOpen;
    private int _sourceRow;

    /// <summary>Layout preferences (library sidebar on/off); saved on every change.</summary>
    public UiSettings Ui
    {
        get => _ui;
        set
        {
            _ui = value;
            _visual = (Visual)Math.Clamp(value.Visual, 0, Enum.GetValues<Visual>().Length - 1);
        }
    }
    private UiSettings _ui = new();
    /// <summary>The library sidebar: hidden at will, and always while following another app (it has no local queue).</summary>
    private bool QueueVisible => !_externalMode && Ui.ShowQueue;

    // Command menu (`?`): every action in one place instead of a footer full of hints.
    private bool _menuOpen;
    private int _menuRow;
    private bool _quit;

    // More sound-reactive bits: sparks thrown off the spectrum on beats and a tempo readout.
    private readonly List<(double X, double Y, double Born)> _sparks = new();
    private readonly List<double> _beatGaps = new();
    private double _bpm;

    // Skin mode (`o`): the player panel, lyrics and transport keys follow another app.
    // The queue, equalizer and saved session always stay on Nation's own library.
    private bool _externalMode;
    private bool _extWasPlaying, _localWasPlaying, _skinApplied;
    private double _sourceChangedAt = double.NegativeInfinity;
    private string? _sourceNotice;

    /// <summary>What the player panel shows and the transport keys control.</summary>
    private IPlayback Playback => _externalMode && External is { } ext ? ext : _player;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrame;

    private int _cursor;
    private int _scroll;
    private DateTime _lastManualMove = DateTime.MinValue;

    // Animated palette: drawn colors ease toward the target extracted from the cover.
    private ArtPalette _palette = ArtPalette.Default;
    private ArtPalette _targetPalette = ArtPalette.Default;
    private Track? _paletteFor;

    private int _lastIndex = -2;
    private double _trackChangedAt;

    // Cyberpunk effects (toggle with `f`): light-up text, glitch bursts, border runner.
    private bool _fx = true;
    // The cover's other dominant colors, and a 16-step gradient running through all four.
    private Rgb _second = ArtPalette.Default.Second, _third = ArtPalette.Default.Third, _fourth = ArtPalette.Default.Fourth;
    private readonly Rgb[] _wheel = new Rgb[16];
    private double _glitchAt, _glitchUntil, _nextGlitch = 6;
    private int _glitchSeed;
    private double _bass;
    private const double DecodeTime = 0.7;

    // Beat detection: bass-weighted spectral flux against an adaptive threshold.
    private readonly float[] _prevBands = new float[SpectrumAnalyzer.BandCount];
    private DateTime _lastAnalyzerFrame;
    private double _fluxAvg, _beat, _lastBeatAt = double.NegativeInfinity, _lastBeatGlitch = double.NegativeInfinity;
    private readonly List<double> _ripples = new();

    // Visualizer style (`v`) and the moment it changed, for the title notice.
    private enum Visual { Barras, Espejo, Onda, Lluvia, Circulo, Osciloscopio, Cascada, Reflejo }

    private string VisualName => _visual switch
    {
        Visual.Circulo => "círculo",
        _ => _visual.ToString().ToLowerInvariant(),
    };

    /// <summary>Next visualizer (`v`); remembered in ui.json.</summary>
    private void CycleVisual()
    {
        _visual = (Visual)(((int)_visual + 1) % Enum.GetValues<Visual>().Length);
        _visualChangedAt = Now;
        Ui = Ui with { Visual = (int)_visual };
        Ui.Save();
    }
    private Visual _visual = Visual.Barras;
    private double _visualChangedAt = double.NegativeInfinity;
    private double[] _rainY = Array.Empty<double>();
    private int[] _rainSeed = Array.Empty<int>();

    // Small general-purpose transitions.
    private double _volShown = -1, _volTarget = -1, _volChangedAt = double.NegativeInfinity;
    private string _stateText = "";
    private double _stateChangedAt;
    private int _lastCursor = -1, _prevCursor = -1;
    private double _cursorMovedAt = double.NegativeInfinity;
    private double _level, _levelPeak;
    private double _dt;

    // Equalizer panel (`e`): takes the lyrics' place while open.
    private readonly Equalizer _eq;
    private bool _eqOpen;
    private int _eqBand;
    private readonly double[] _eqShown = new double[Equalizer.Frequencies.Length];
    private double _eqChangedAt = double.NegativeInfinity;

    // Resume: the saved track is selected (paused) on start, then seeked once mpv has loaded it.
    private int _resumeIndex = -1;
    private double _resumePosition;
    private double _lastSessionSave;
    private string? _savedPath;
    private const double BootTime = 0.9;

    private readonly float[] _bars = new float[SpectrumAnalyzer.BandCount];
    private readonly float[] _peaks = new float[SpectrumAnalyzer.BandCount];

    // Lyrics: looked up in the background per track; a skip cancels the pending lookup.
    private bool _showLyrics = true;
    private Track? _lyricsFor;
    private LyricSheet? _lyrics;
    private bool _lyricsLoading;
    private CancellationTokenSource? _lyricsCts;
    private double _lyricOffset;
    private double _lyricOffsetChangedAt = double.NegativeInfinity;
    private double _lyricScroll;
    private string? _lyricsText;
    private int _activeLyric = -1;
    private double _activeLyricAt;
    private (LyricSheet? Sheet, int Width, List<(int Line, string Text)> Rows) _wrapCache;
    private const string Spinner = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

    // After this long without the user moving the cursor, it follows the playing track again.
    private static readonly TimeSpan FollowDelay = TimeSpan.FromSeconds(5);
    private const string Eighths = " ▁▂▃▄▅▆▇█";

    public App(Player player, SpectrumAnalyzer spectrum, LyricsProvider lyrics)
    {
        _player = player;
        _spectrum = spectrum;
        _lyricsProvider = lyrics;
        _eq = Equalizer.Load();
        _eq.Gains.CopyTo(_eqShown, 0);
        _player.ApplyEqualizer(_eq);
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>
    /// Restores the last session's track and position (and volume unless the
    /// command line set one). Call once after the queue is loaded.
    /// </summary>
    public void RestoreSession(bool volume)
    {
        if (Session.Load() is not { } session) return;
        int index = -1;
        for (int i = 0; i < _player.Queue.Count; i++)
            if (string.Equals(_player.Queue[i].Path, session.Path, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        if (volume) _player.SetVolume((int)Math.Round(session.Volume));
        if (index < 0) return;
        _resumeIndex = index;
        _resumePosition = session.Position;
        if (index > 0) _player.JumpTo(index);
    }

    /// <summary>Writes the current track/position; also called by Program on exit.</summary>
    public void SaveSession()
    {
        if (_player.Current is not { } track || _resumeIndex >= 0) return;
        new Session(track.Path, _player.Position, _player.Volume).Save();
        _savedPath = track.Path;
    }

    /// <summary>Returns false when the app should quit.</summary>
    public bool HandleKey(ConsoleKeyInfo key)
    {
        var queue = _player.Queue;
        bool shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);
        if (_menuOpen)
        {
            if (HandleMenuKey(key, out bool handled) is false) return false;
            if (handled) return true;
        }
        if (key.KeyChar == '?' || key.Key == ConsoleKey.F1) { OpenMenu(); return true; }
        // Offset keys by character so they work on both US and Spanish layouts.
        if (_sourcesOpen && HandleSourcesKey(key)) return true;
        if (_eqOpen && HandleEqKey(key, shift)) return true;
        switch (key.KeyChar)
        {
            case ',': NudgeLyrics(-0.25); return true;
            case '.': NudgeLyrics(+0.25); return true;
        }
        switch (key.Key)
        {
            case ConsoleKey.Q:
            case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                return false;
            case ConsoleKey.Spacebar: Playback.TogglePause(); break;
            case ConsoleKey.N: Playback.Next(); break;
            case ConsoleKey.P: Playback.Previous(); break;
            case ConsoleKey.S: Playback.ToggleShuffle(); JumpToCurrent(); break;
            case ConsoleKey.C: JumpToCurrent(); break;
            case ConsoleKey.B: ToggleSidebar(); break;
            case ConsoleKey.L: _showLyrics = !_showLyrics; break;
            case ConsoleKey.F: _fx = !_fx; break;
            case ConsoleKey.E: _eqOpen = true; _sourcesOpen = false; break;
            case ConsoleKey.V: CycleVisual(); break;
            case ConsoleKey.R: Playback.ToggleRepeat(); break;
            case ConsoleKey.RightArrow: Playback.SeekRelative(shift ? 30 : 5); break;
            case ConsoleKey.LeftArrow: Playback.SeekRelative(shift ? -30 : -5); break;
            case ConsoleKey.M: Playback.ToggleMute(); break;
            case ConsoleKey.OemPlus or ConsoleKey.Add: Playback.AddVolume(5); break;
            case ConsoleKey.OemMinus or ConsoleKey.Subtract: Playback.AddVolume(-5); break;
            // Queue navigation only while the sidebar is on screen.
            case ConsoleKey.J or ConsoleKey.DownArrow when QueueVisible: MoveCursor(1); break;
            case ConsoleKey.K or ConsoleKey.UpArrow when QueueVisible: MoveCursor(-1); break;
            case ConsoleKey.PageDown when QueueVisible: MoveCursor(10); break;
            case ConsoleKey.PageUp when QueueVisible: MoveCursor(-10); break;
            case ConsoleKey.G when shift && QueueVisible: MoveCursor(queue.Count); break;
            case ConsoleKey.G or ConsoleKey.Home when QueueVisible: MoveCursor(-queue.Count); break;
            case ConsoleKey.End when QueueVisible: MoveCursor(queue.Count); break;
            case ConsoleKey.O: OpenSources(); break;
            case ConsoleKey.Enter when QueueVisible:
                // Picking a song from the library brings Nation back to its own player.
                SetSource(external: false);
                _player.PlayIndex(_cursor);
                _lastManualMove = DateTime.MinValue;
                break;
        }
        return true;
    }

    /// <summary>Keys while the equalizer is open; returns false to let the normal bindings handle it.</summary>
    private bool HandleEqKey(ConsoleKeyInfo key, bool shift)
    {
        int bands = Equalizer.Frequencies.Length;
        switch (key.Key)
        {
            case ConsoleKey.E or ConsoleKey.Escape: _eqOpen = false; return true;
            case ConsoleKey.LeftArrow or ConsoleKey.H: _eqBand = (_eqBand - 1 + bands) % bands; return true;
            case ConsoleKey.RightArrow: _eqBand = (_eqBand + 1) % bands; return true;
            case ConsoleKey.UpArrow or ConsoleKey.K: ChangeEq(() => _eq.SetGain(_eqBand, _eq.Gains[_eqBand] + (shift ? 3 : 1))); return true;
            case ConsoleKey.DownArrow or ConsoleKey.J: ChangeEq(() => _eq.SetGain(_eqBand, _eq.Gains[_eqBand] - (shift ? 3 : 1))); return true;
            case ConsoleKey.D0 or ConsoleKey.NumPad0: ChangeEq(() => _eq.SetGain(_eqBand, 0)); return true;
            case ConsoleKey.Tab: ChangeEq(() => _eq.CyclePreset(shift ? -1 : 1)); return true;
            case ConsoleKey.B: ChangeEq(_eq.ToggleEnabled); return true;
        }
        return false;
    }

    private void ChangeEq(Action change)
    {
        change();
        _player.ApplyEqualizer(_eq);
        _eqChangedAt = Now;
    }

    private void MoveCursor(int delta)
    {
        _cursor = Math.Clamp(_cursor + delta, 0, Math.Max(0, _player.Queue.Count - 1));
        _lastManualMove = DateTime.UtcNow;
    }

    private void JumpToCurrent() => _lastManualMove = DateTime.MinValue;

    /// <summary>Remote/media-key commands (UI thread): play, pause, stop, next, back, seek (ms), shuffle, repeat.</summary>
    public void RunCommand(string command, long arg)
    {
        var p = Playback;
        switch (command)
        {
            case "play": p.SetPaused(false); break;
            case "pause" or "stop": p.SetPaused(true); break;
            case "next": p.Next(); break;
            case "back": p.Previous(); break;
            case "seek": p.SeekAbsolute(arg / 1000.0); break;
            case "shuffle": p.ToggleShuffle(); break;
            case "repeat": p.ToggleRepeat(); break;
        }
    }

    /// <summary>
    /// Follows another app on its own when it starts playing music while Nation is quiet
    /// (or when Nation is paused while it plays), and falls back when its session closes.
    /// </summary>
    private void UpdateSource()
    {
        if (External is not { } ext) return;
        if (StartAsSkin && !_skinApplied && ext.HasSession)
        {
            _skinApplied = true;
            SetSource(external: true);
        }
        bool extPlaying = ext.Playing && ext.LooksLikeMusic, localPlaying = _player.Playing;
        if (Sources.AutoFollow && !_externalMode && extPlaying && !localPlaying && (!_extWasPlaying || _localWasPlaying))
            SetSource(external: true);
        else if (_externalMode && !ext.HasSession)
            SetSource(external: false, notice: "la otra app se cerró");
        _extWasPlaying = extPlaying;
        _localWasPlaying = localPlaying;
    }

    private void ToggleSidebar()
    {
        if (_externalMode)
        {
            Notice($"{External?.AppName} no tiene lista local; vuelve a la biblioteca con o");
            return;
        }
        Ui = Ui with { ShowQueue = !Ui.ShowQueue };
        Ui.Save();
    }

    private void OpenSources()
    {
        _sourcesOpen = true;
        _eqOpen = false;
        External?.RefreshSessions();
        // Start on whatever is being followed now.
        int active = SourceRows().FindIndex(r => r.Active);
        _sourceRow = Math.Max(0, active);
    }

    private enum SourceKind { Local, Auto, App }

    private sealed record SourceRow(SourceKind Kind, string Name, string Detail, string Status, bool Active, bool Pinned, string? AppId);

    /// <summary>Library, automatic, then one row per other app's media session.</summary>
    private List<SourceRow> SourceRows()
    {
        var ext = External;
        bool auto = _externalMode && ext?.PinnedAppId is null;
        var rows = new List<SourceRow>
        {
            new(SourceKind.Local, "Biblioteca local", $"{_player.Queue.Count} pistas", "", !_externalMode, false, null),
            new(SourceKind.Auto, "Automático", "la que esté sonando (prefiere apps de música)", "", auto, false, null),
        };
        if (ext is null) return rows;
        var sessions = ext.Sessions.ToList();
        // A pinned app that isn't open right now still gets a row, so it can be unpinned.
        if (ext.PinnedAppId is { } pin && sessions.All(si => si.AppId != pin))
            sessions.Add(new ExternalPlayer.SessionInfo(pin, Sources.PinnedName ?? pin, "cerrada", false, "", ""));
        foreach (var si in sessions)
        {
            string detail = si.Title.Length == 0 ? "" : si.Artist.Length == 0 ? si.Title : $"{si.Title} · {si.Artist}";
            bool active = _externalMode && ext.PinnedAppId is not null && ext.CurrentAppId == si.AppId;
            rows.Add(new(SourceKind.App, si.AppName, detail, si.Status, active, ext.PinnedAppId == si.AppId, si.AppId));
        }
        return rows;
    }

    private bool HandleSourcesKey(ConsoleKeyInfo key)
    {
        var rows = SourceRows();
        switch (key.Key)
        {
            case ConsoleKey.O or ConsoleKey.Escape: _sourcesOpen = false; return true;
            case ConsoleKey.J or ConsoleKey.DownArrow: _sourceRow = Math.Min(rows.Count - 1, _sourceRow + 1); return true;
            case ConsoleKey.K or ConsoleKey.UpArrow: _sourceRow = Math.Max(0, _sourceRow - 1); return true;
            case ConsoleKey.A:
                Sources = Sources with { AutoFollow = !Sources.AutoFollow };
                Sources.Save();
                return true;
            case ConsoleKey.Enter:
                ChooseSource(rows[Math.Clamp(_sourceRow, 0, rows.Count - 1)]);
                return true;
        }
        return false;
    }

    private void ChooseSource(SourceRow row)
    {
        var ext = External;
        switch (row.Kind)
        {
            case SourceKind.Local:
                SetSource(external: false);
                return;
            case SourceKind.Auto:
                if (ext is null) return;
                ext.Pin(null);
                Sources = Sources with { PinnedAppId = null, PinnedName = null };
                Sources.Save();
                if (ext.Sessions.Count == 0) { Notice("ninguna otra app tiene sesión de medios"); return; }
                if (!_externalMode) SetSource(external: true, notice: "automático");
                else Notice("automático");
                return;
            case SourceKind.App when ext is not null:
                ext.Pin(row.AppId);
                Sources = Sources with { PinnedAppId = row.AppId, PinnedName = row.Name };
                Sources.Save();
                if (!_externalMode) SetSource(external: true, notice: $"siguiendo {row.Name}");
                else Notice($"siguiendo {row.Name}");
                return;
        }
    }

    private void Notice(string text)
    {
        _sourceNotice = text;
        _sourceChangedAt = Now;
    }

    /// <summary>Switches what the player shows; whichever side is left behind is paused so they never talk over each other.</summary>
    private void SetSource(bool external, string? notice = null)
    {
        if (external == _externalMode) return;
        if (external && _player.Playing) _player.SetPaused(true);
        if (!external && External is { Playing: true } ext && notice is null) ext.SetPaused(true);
        _externalMode = external;
        _sourceChangedAt = Now;
        _sourceNotice = notice ?? (external ? $"siguiendo {External?.AppName}" : "biblioteca local");
        _lastManualMove = DateTime.MinValue;
    }

    private void NudgeLyrics(double delta)
    {
        _lyricOffsetChangedAt = Now;
        // Plain lyrics have no timestamps to shift; the title shows why nothing happened.
        if (_lyrics is not { Synced: true }) return;
        _lyricOffset = Math.Clamp(Math.Round(_lyricOffset + delta, 2), -30, 30);
    }

    /// <summary>
    /// How long the main loop may sleep before the next frame. Input and remote
    /// commands wake it early, so this only bounds animation smoothness.
    /// </summary>
    public TimeSpan NextFrameDelay { get; private set; }

    private static readonly TimeSpan FastFrame = TimeSpan.FromMilliseconds(33);   // ~30 fps: spectrum, transitions
    private static readonly TimeSpan SlowFrame = TimeSpan.FromMilliseconds(125);  // ~8 fps: marquee, spinner
    private static readonly TimeSpan IdleFrame = TimeSpan.FromMilliseconds(1000); // nothing moving

    // Set by any drawing code that is mid-animation during this frame.
    private bool _animFast, _animSlow;

    public void Draw(Canvas c)
    {
        double now = Now, dt = Math.Clamp(now - _lastFrame, 0, 0.1);
        _lastFrame = now;
        _dt = dt;
        _animFast = _animSlow = false;
        _player.Refresh();
        External?.Refresh();
        UpdateSource();

        int current = _player.CurrentIndex;
        // Track ids are unique across the library and other apps' tracks.
        int shown = Playback.Current?.Id ?? -1;
        if (shown != _lastIndex)
        {
            _lastIndex = shown;
            _trackChangedAt = now;
            TriggerGlitch(now, 0.22);
        }
        if (current >= 0 && DateTime.UtcNow - _lastManualMove > FollowDelay)
            _cursor = current;

        UpdatePalette(dt);
        UpdateSpectrum(dt);
        UpdateLyrics();
        // Seek the restored track once mpv knows its duration (seeking earlier is ignored).
        if (_resumeIndex >= 0 && _player.CurrentIndex == _resumeIndex && _player.Duration > 0)
        {
            if (_resumePosition > 1 && _resumePosition < _player.Duration - 2) _player.SeekAbsolute(_resumePosition);
            _resumeIndex = -1;
            _lastManualMove = DateTime.MinValue;
        }
        else if (_resumeIndex >= 0) _animFast = true;
        // Save on track change and every 5 s while playing.
        if (PersistSession && (_player.Current?.Path != _savedPath || (_player.Playing && now - _lastSessionSave > 5)))
        {
            _lastSessionSave = now;
            SaveSession();
        }
        UpdateBeat(now, dt);
        UpdateFx(now);

        DrawHeader(c);
        int bodyTop = 1, bodyHeight = c.Height - 2;
        int leftWidth = !QueueVisible ? 0 : c.Width < 70 ? c.Width / 2 : Math.Clamp(c.Width * 42 / 100, 30, 64);
        if (QueueVisible) DrawQueue(c, 0, bodyTop, leftWidth, bodyHeight, current);

        // Right column: player on top, lyrics below; on short terminals lyrics take it all.
        int rx = leftWidth, rw = c.Width - leftWidth;
        // Taller terminals give the visualizer more rows.
        int playerHeight = Math.Clamp(bodyHeight / 2 + 2, 16, 24);
        // The EQ draws its own spectrum, so the player gives up rows for taller sliders.
        if (_eqOpen) playerHeight = 14;
        if (!_showLyrics && !_eqOpen && !_sourcesOpen)
            DrawNowPlaying(c, rx, bodyTop, rw, bodyHeight);
        else if (bodyHeight - playerHeight >= 8)
        {
            DrawNowPlaying(c, rx, bodyTop, rw, playerHeight);
            DrawLower(c, rx, bodyTop + playerHeight, rw, bodyHeight - playerHeight, dt);
        }
        else
            DrawLower(c, rx, bodyTop, rw, bodyHeight, dt);
        DrawFooter(c);
        if (_menuOpen) DrawMenu(c);
        if (_fx && now < BootTime) DrawBoot(c, now / BootTime);
        PublishRemote();
        // While following another app, Nation's own media session steps aside (it would show a paused duplicate).
        if (_externalMode) Media?.Update(null, true, true, false, false, 0, 0);
        else Media?.Update(_player.Current, _player.Idle, _player.Paused, _player.Shuffled, _player.Repeat, _player.Position, _player.Duration);

        // Playing always animates (spectrum, progress, karaoke); otherwise only while something is settling.
        NextFrameDelay = Playback.Playing || _animFast ? FastFrame : _animSlow ? SlowFrame : IdleFrame;
    }

    // ── animation state ────────────────────────────────────────────────

    private void UpdatePalette(double dt)
    {
        var track = Playback.Current;
        // Same id + cover = same palette (another app's track is rebuilt when its duration or cover lands).
        if (track?.Id != _paletteFor?.Id || track?.CoverKey != _paletteFor?.CoverKey)
        {
            _paletteFor = track;
            if (track is null) _targetPalette = ArtPalette.Default;
            else
            {
                var requested = track;
                _ = Task.Run(async () =>
                {
                    var p = await CoverArt.PaletteForAsync(requested);
                    // Ignore results that arrive after the user already skipped ahead.
                    if (ReferenceEquals(_paletteFor, requested)) _targetPalette = p;
                });
            }
        }

        // Exponential ease (~0.6 s to settle), frame-rate independent.
        double k = 1 - Math.Exp(-dt / 0.15);
        _palette = new ArtPalette(
            Rgb.Lerp(_palette.Accent, _targetPalette.Accent, k),
            Rgb.Lerp(_palette.Backdrop, _targetPalette.Backdrop, k),
            Rgb.Lerp(_palette.Second, _targetPalette.Second, k),
            Rgb.Lerp(_palette.Third, _targetPalette.Third, k),
            Rgb.Lerp(_palette.Fourth, _targetPalette.Fourth, k));
        // Byte steps round to zero near the target; snap once the gap is imperceptible.
        if (Close(_palette.Accent, _targetPalette.Accent) && Close(_palette.Backdrop, _targetPalette.Backdrop)
            && Close(_palette.Second, _targetPalette.Second) && Close(_palette.Third, _targetPalette.Third) && Close(_palette.Fourth, _targetPalette.Fourth))
            _palette = _targetPalette;
        else
            _animFast = true;
    }

    private static bool Close(Rgb a, Rgb b) =>
        Math.Abs(a.R - b.R) <= 6 && Math.Abs(a.G - b.G) <= 6 && Math.Abs(a.B - b.B) <= 6;

    private void TriggerGlitch(double now, double length)
    {
        _glitchAt = now;
        _glitchUntil = now + length;
        _glitchSeed++;
    }

    private bool Glitching => _fx && Now < _glitchUntil;

    /// <summary>Glitch sub-frame: changes ~25 times a second so a burst stutters instead of sliding.</summary>
    private int GlitchTick => (int)((Now - _glitchAt) * 25) + _glitchSeed * 1000;

    private void UpdateFx(double now)
    {
        _second = _palette.Second;
        _third = _palette.Third;
        _fourth = _palette.Fourth;
        Rgb[] stops = { Accent, _second, _third, _fourth };
        for (int i = 0; i < _wheel.Length; i++)
        {
            double t = i / (double)(_wheel.Length - 1) * (stops.Length - 1);
            int a = Math.Min((int)t, stops.Length - 2);
            _wheel[i] = Rgb.Lerp(stops[a], stops[a + 1], t - a);
        }
        _bass = Math.Max(Sample(_bars, 0.5), Sample(_bars, 2));
        if (!_fx) return;
        // Ambient bursts only while playing (the loop is at 30 fps then anyway); paused stays idle.
        if (Playback.Playing && now >= _nextGlitch)
        {
            TriggerGlitch(now, 0.08 + Fx.Noise(_glitchSeed, 7) * 0.14);
            _nextGlitch = now + 10 + Fx.Noise(_glitchSeed, 11) * 10;
        }
        if (now < _glitchUntil) _animFast = true;
    }

    private void UpdateBeat(double now, double dt)
    {
        bool live = Playback.Playing && DateTime.UtcNow - _spectrum.LastFrameUtc < TimeSpan.FromMilliseconds(250);
        var bands = _spectrum.Latest;
        if (live && _spectrum.LastFrameUtc != _lastAnalyzerFrame)
        {
            _lastAnalyzerFrame = _spectrum.LastFrameUtc;
            // Rising energy only, weighted toward the kick/bass bands.
            double flux = 0;
            for (int i = 0; i < 8; i++)
                flux += Math.Max(0, bands[i] - _prevBands[i]) * (i < 4 ? 1.0 : 0.5);
            Array.Copy(bands, _prevBands, bands.Length);
            _fluxAvg += (flux - _fluxAvg) * 0.08;
            if (flux > _fluxAvg * 1.6 + 0.08 && now - _lastBeatAt > 0.22)
            {
                double strength = Math.Clamp((flux - _fluxAvg) / 0.6, 0.35, 1);
                double gap = now - _lastBeatAt;
                _lastBeatAt = now;
                _beat = Math.Max(_beat, strength);
                if (_ripples.Count < 4) _ripples.Add(now);
                TrackTempo(gap);
                if (_fx) SpawnSparks(now, strength);
                // Hard hits glitch the metadata, with a cooldown so it never turns into strobe.
                if (_fx && strength > 0.65 && now - _lastBeatGlitch > 3.5)
                {
                    _lastBeatGlitch = now;
                    TriggerGlitch(now, 0.1);
                }
            }
        }
        _beat *= Math.Exp(-dt / 0.14);
        if (_beat < 0.02) _beat = 0;
        else _animFast = true;

        // Overall loudness for the level meter: fast rise, slow fall, peak hold.
        double target = 0;
        foreach (var b in _bars) target += b;
        target /= _bars.Length;
        _level = target > _level ? target : Math.Max(target, _level - dt * 0.8);
        _levelPeak = _level >= _levelPeak ? _level : Math.Max(0, _levelPeak - dt * 0.25);
        if (_levelPeak > 0) _animFast = true;
    }

    /// <summary>
    /// Tempo from the median gap between recent beats, folded into 70–180 BPM
    /// (the detector often catches only every other kick, or the hi-hats too).
    /// </summary>
    private void TrackTempo(double gap)
    {
        if (gap < 0.25 || gap > 1.6) { if (gap > 3) _beatGaps.Clear(); return; }
        _beatGaps.Add(gap);
        if (_beatGaps.Count > 12) _beatGaps.RemoveAt(0);
        if (_beatGaps.Count < 5) return;
        var sorted = _beatGaps.OrderBy(g => g).ToList();
        double bpm = 60 / sorted[sorted.Count / 2];
        while (bpm < 70) bpm *= 2;
        while (bpm > 180) bpm /= 2;
        _bpm = _bpm == 0 ? bpm : _bpm + (bpm - _bpm) * 0.3;
    }

    /// <summary>A beat throws a few sparks off the loudest bars; they rise and fade.</summary>
    private void SpawnSparks(double now, double strength)
    {
        int count = strength > 0.7 ? 3 : 2;
        for (int k = 0; k < count && _sparks.Count < 30; k++)
        {
            // Pick among the louder bands, biased toward the bass that triggered the beat.
            int band = (int)(Math.Pow(Random.Shared.NextDouble(), 1.6) * SpectrumAnalyzer.BandCount);
            float level = _bars[Math.Clamp(band, 0, _bars.Length - 1)];
            if (level < 0.2) continue;
            _sparks.Add((band / (double)(SpectrumAnalyzer.BandCount - 1), level, now));
        }
    }

    private const double SparkLife = 0.8;

    /// <summary>Sparks above the visualizer: x in [0,1] across it, rising from the bar top they left.</summary>
    private void DrawSparks(Canvas c, int x, int y, int w, int h, Func<double, int> column)
    {
        _sparks.RemoveAll(sp => Now - sp.Born > SparkLife);
        foreach (var (sx, sy, born) in _sparks)
        {
            double age = Now - born, t = age / SparkLife;
            double height = sy + age * 0.9 + 0.05;
            int row = y + h - 1 - (int)(height * h);
            if (row < y) continue;
            char glyph = t < 0.3 ? '•' : t < 0.6 ? '∙' : '·';
            c.Set(column(sx), row, glyph, Rgb.Lerp(Fx.White, Wheel(sx), t));
        }
    }

    /// <summary>Beat envelope stepped into a few levels so pulsing borders don't repaint every row every frame.</summary>
    private double BeatStep => Math.Round(_beat * 4) / 4;
    private double BassStep => Math.Round(_bass * 4) / 4;

    /// <summary>--demo-spectrum (snapshots): synthetic moving bands so visualizers can be checked without audio.</summary>
    public bool DemoSpectrum { get; init; }

    private float[] DemoFine()
    {
        var fine = new float[SpectrumAnalyzer.FineCount];
        var coarse = DemoBands();
        for (int i = 0; i < fine.Length; i++)
        {
            double v = Sample(coarse, i / (double)(fine.Length - 1) * (SpectrumAnalyzer.BandCount - 1));
            // A few moving "harmonics" so the demo has lines to resolve.
            double line = Math.Max(0, 1 - Math.Abs(i - (30 + 20 * Math.Sin(Now * 1.3))) / 2.0);
            fine[i] = (float)Math.Clamp(v * 0.7 + line * 0.6, 0, 1);
        }
        return fine;
    }

    private float[] DemoBands()
    {
        var bands = new float[SpectrumAnalyzer.BandCount];
        for (int i = 0; i < bands.Length; i++)
            bands[i] = (float)Math.Clamp(0.7 * Math.Exp(-i / 9.0) + 0.3 * (0.5 + 0.5 * Math.Sin(Now * 3 + i * 0.7)), 0, 1);
        return bands;
    }

    private void UpdateSpectrum(double dt)
    {
        // Loopback hears every app; only capture while Nation itself is playing.
        bool playing = Playback.Playing;
        if (playing) _spectrum.Start();
        bool live = DemoSpectrum || playing && DateTime.UtcNow - _spectrum.LastFrameUtc < TimeSpan.FromMilliseconds(250);
        var target = DemoSpectrum ? DemoBands() : _spectrum.Latest;
        // The spectrogram wants the analyzer's fine frames as they are, not the falling bars.
        if (live) Array.Copy(DemoSpectrum ? DemoFine() : _spectrum.LatestFine, _rawFine, _rawFine.Length);
        else Array.Clear(_rawFine);
        bool moving = false;
        for (int i = 0; i < _bars.Length; i++)
        {
            float t = live ? target[i] : 0;
            // Fast attack, gravity-like release.
            _bars[i] = t > _bars[i] ? _bars[i] + (t - _bars[i]) * 0.6f : Math.Max(t, _bars[i] - (float)(1.6 * dt));
            _peaks[i] = _bars[i] >= _peaks[i] ? _bars[i] : Math.Max(0, _peaks[i] - (float)(0.45 * dt));
            moving |= _peaks[i] > 0;
        }
        // Keep animating until the bars have fully fallen, then release the capture.
        if (moving) _animFast = true;
        else if (!playing) _spectrum.Stop();
    }

    /// <summary>Bar value at fractional band position, linearly interpolated.</summary>
    private static float Sample(float[] bands, double pos)
    {
        int i = (int)pos;
        if (i >= bands.Length - 1) return bands[^1];
        float f = (float)(pos - i);
        return bands[i] * (1 - f) + bands[i + 1] * f;
    }

    // ── drawing ────────────────────────────────────────────────────────

    private Rgb Accent => _palette.Accent;
    private Rgb Muted => _fx ? Rgb.Lerp(Theme.Muted, Accent, 0.3) : Theme.Muted;
    private Rgb Track => _fx ? Rgb.Lerp(Theme.Track, Accent, 0.25) : Theme.Track;

    /// <summary>Color from the gradient through the cover's four colors, x in [0, 1].</summary>
    private Rgb Wheel(double x) => _wheel[Math.Clamp((int)Math.Round(x * (_wheel.Length - 1)), 0, _wheel.Length - 1)];

    private void DrawHeader(Canvas c)
    {
        // Logo glows with the bass and stutters during glitch bursts.
        var logo = _fx ? Rgb.Lerp(Accent, Fx.White, Math.Max(_bass * 0.6, _beat)) : Accent;
        int x = c.Text(1, 0, _fx && _beat > 0.5 ? "◈ NATION" : "◆ NATION", logo, bold: true) + 3;
        if (Glitching)
        {
            int tick = GlitchTick;
            for (int i = 3; i < 9; i++)
                if (Fx.Noise(tick, i) < 0.35) c.Recolor(1 + i, 0, Fx.Noise(tick, i + 90) < 0.5 ? _second : _fourth);
        }
        string state = Playback.Idle ? "■ detenido" : Playback.Paused ? "❚❚ pausa" : "▶ reproduciendo";
        if (state != _stateText) { _stateText = state; _stateChangedAt = Now; }
        double stateAge = Now - _stateChangedAt;
        if (stateAge < DecodeTime) _animFast = true;
        var stateColor = !_fx ? Muted : Playback.Idle ? Muted : Playback.Paused ? _fourth : _second;
        if (!DrawDecoded(c, x, 0, TextWidth.Of(state), state, stateColor, false, stateAge, row: 5)) c.Text(x, 0, state, stateColor);
        x += TextWidth.Of(state) + 3;
        if (_externalMode) x += c.Text(x, 0, $"◉ {External?.AppName}", _fx ? _third : Accent, bold: true) + 3;
        if (Playback.Shuffled) x += c.Text(x, 0, "⤮ aleatorio", Accent) + 3;
        if (Playback.Repeat) c.Text(x, 0, "⟳ repetir", _fx ? _third : Accent);

        string count = _externalMode ? "" : $"{_player.Queue.Count} pistas";
        int right = c.Width - count.Length - 1;
        c.Text(right, 0, count, _fx ? Rgb.Lerp(Muted, _third, 0.5) : Muted);
        // Tempo: the note flashes on every detected beat; hidden when the beat is lost.
        if (_fx && Playback.Playing && _bpm > 0 && Now - _lastBeatAt < 3)
        {
            string bpm = $"{_bpm:0} bpm";
            int bx = right - TextWidth.Of(bpm) - 2 - (count.Length > 0 ? 3 : 0);
            c.Set(bx - 2, 0, _beat > 0.4 ? '♪' : '♩', Rgb.Lerp(_second, Fx.White, _beat));
            c.Text(bx, 0, bpm, Rgb.Lerp(Muted, _second, 0.5));
            right = bx - 2;
        }

        // Shade polls every 500 ms while connected, so a quiet API means it's gone.
        if (Remote is { } api)
        {
            var (badge, color) = api.Error is { } err ? ($"API ✗ {err}", Muted)
                : DateTime.UtcNow - api.LastRequestUtc < TimeSpan.FromSeconds(4) ? ("⇄ shade", Accent)
                : ($"API :{api.Port}", Muted);
            c.Text(right - TextWidth.Of(badge) - 3, 0, badge, color);
        }
    }

    /// <summary>Hands the remote API a consistent copy of the current state (read from other threads).</summary>
    private void PublishRemote()
    {
        if (Remote is null) return;
        var track = Playback.Current;
        string status = Playback.Idle || track is null ? "stopped" : Playback.Paused ? "paused" : "playing";
        Remote.Snapshot = new RemoteSnapshot(
            status, Playback.Shuffled, Playback.Repeat,
            (long)(Playback.Position * 1000), track,
            SameLyrics(_lyricsFor, track) ? _lyricsText : null);
    }

    /// <summary>Serializes back to LRC so remote clients (Shade) get the same lyrics Nation found.</summary>
    private static string ToLrc(LyricSheet sheet)
    {
        if (!sheet.Synced) return string.Join('\n', sheet.Lines.Select(l => l.Text));
        return string.Join('\n', sheet.Lines.Select(l =>
        {
            var t = TimeSpan.FromSeconds(l.Time);
            return $"[{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}]{l.Text}";
        }));
    }

    private void DrawQueue(Canvas c, int x, int y, int w, int h, int current)
    {
        var queue = _player.Queue;
        Panel(c, x, y, w, h, _player.Shuffled ? "Cola ⤮" : "Cola", Accent, Accent);

        int rows = h - 2, inner = w - 2;
        if (rows <= 0) return;

        if (_cursor < _scroll) _scroll = _cursor;
        if (_cursor >= _scroll + rows) _scroll = _cursor - rows + 1;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, queue.Count - rows));

        var selection = Rgb.Lerp(_palette.Backdrop, Theme.Selection, 0.25);
        if (_cursor != _lastCursor)
        {
            if (_lastCursor >= 0) { _prevCursor = _lastCursor; _cursorMovedAt = Now; }
            _lastCursor = _cursor;
        }
        double trail = (Now - _cursorMovedAt) / 0.3;
        if (_fx && trail < 1) _animFast = true;
        int numWidth = Math.Max(2, queue.Count.ToString().Length);
        for (int row = 0; row < rows; row++)
        {
            int i = _scroll + row;
            if (i >= queue.Count) break;
            var t = queue[i];
            int ry = y + 1 + row, rx = x + 1;
            bool playing = i == current, selected = i == _cursor;

            // Playing row: a band of the cover's backdrop that brightens with the bass.
            if (playing && !selected && _fx)
                c.FillRow(rx, ry, inner, Rgb.Lerp(_palette.Backdrop, Accent, _player.Playing ? BassStep * 0.22 : 0));
            if (selected) c.FillRow(rx, ry, inner, selection);
            if (selected && _fx) c.Set(rx, ry, '▌', _second, selection);
            else if (_fx && i == _prevCursor && trail < 1) c.Set(rx, ry, '▌', Rgb.Lerp(_second, _palette.Backdrop, trail));
            if (playing) DrawMiniEq(c, rx + 1, ry);
            c.Text(rx + 5, ry, (i + 1).ToString().PadLeft(numWidth), _fx ? Rgb.Lerp(Muted, _second, 0.35) : Muted);

            string dur = FormatTime(t.Duration.TotalSeconds);
            int textX = rx + 5 + numWidth + 2;
            int textMax = inner - (textX - rx) - dur.Length - 2;
            int used = c.Text(textX, ry, t.Title, playing ? Accent : Theme.Text, bold: playing, maxWidth: textMax);
            if (used + 3 < textMax)
                c.Text(textX + used, ry, " · " + t.Artist, _fx ? Rgb.Lerp(Muted, _third, 0.5) : Muted, maxWidth: textMax - used);
            if (playing && _fx && _player.Playing)
            {
                // A light band sweeps across the playing title every few seconds.
                double sweep = Now % 4.0 / 0.9 * (used + 6) - 3;
                for (int col = Math.Max(0, (int)sweep - 2); col <= sweep + 2 && col < used; col++)
                    c.Recolor(textX + col, ry, Rgb.Lerp(Accent, Fx.White, 1 - Math.Abs(col - sweep) / 3));
            }
            c.Text(rx + inner - dur.Length - 1, ry, dur, _fx ? Rgb.Lerp(Muted, _second, 0.25) : Muted);
        }
    }

    /// <summary>Three tiny bars driven by bass/mid/treble; static pause glyph otherwise.</summary>
    private void DrawMiniEq(Canvas c, int x, int y)
    {
        if (_player.Paused) { c.Text(x, y, "❚❚", Accent); return; }
        double[] at = { 2, 10, 20 };
        for (int i = 0; i < at.Length; i++)
        {
            int level = Math.Clamp((int)Math.Round(Sample(_bars, at[i]) * 7) + 1, 1, 8);
            c.Set(x + i, y, Eighths[level], Accent);
        }
    }

    private void DrawNowPlaying(Canvas c, int x, int y, int w, int h)
    {
        bool notice = Now - _visualChangedAt < 2;
        if (notice) _animSlow = true;
        bool sourceNotice = Now - _sourceChangedAt < 2.5 && _sourceNotice is not null;
        if (sourceNotice) _animSlow = true;
        string title = notice ? $"Reproduciendo · visual: {VisualName}"
            : sourceNotice ? $"Reproduciendo · {_sourceNotice}"
            : _externalMode ? $"Reproduciendo · {External?.AppName}" : "Reproduciendo";
        notice |= sourceNotice;
        var border = _fx ? Rgb.Lerp(BorderIn(_second), _second, BeatStep * 0.8) : PanelBorder;
        Panel(c, x, y, w, h, title, border, notice ? Accent : Muted);
        int ix = x + 3, iw = w - 6;
        var t = Playback.Current;
        if (t is null)
        {
            string empty = _externalMode ? $"{External?.AppName} no está mostrando ninguna canción"
                : _player.Queue.Count == 0 ? "No hay música en tu carpeta Música: abre Spotify, YouTube Music u otra app, o pasa una carpeta (nation \"D:\\Musica\")"
                : "Nada en reproducción";
            c.Text(ix, y + 2, empty, Muted, maxWidth: iw);
            return;
        }

        // Metadata enters on every track change: decoded from noise (fx) or faded in from the backdrop tint.
        double age = Now - _trackChangedAt;
        double fade = _fx ? 1 : Math.Clamp(age / 0.45, 0, 1);
        if (fade < 1 || (_fx && age < DecodeTime + 0.1)) _animFast = true;
        fade = 1 - Math.Pow(1 - fade, 3); // ease-out
        var from = _palette.Backdrop;

        DrawMarquee(c, ix, y + 2, iw, t.Title, Rgb.Lerp(from, Theme.Text, fade), bold: true, age, row: 0);
        var artistColor = Rgb.Lerp(from, Accent, fade);
        if (_fx && Playback.Playing) artistColor = Rgb.Lerp(artistColor, Fx.White, BeatStep * 0.4);
        DrawMarquee(c, ix, y + 3, iw, t.Artist, artistColor, bold: false, age, row: 1);
        string album = t.Year > 0 ? $"{t.Album} · {t.Year}" : t.Album;
        if (!DrawDecoded(c, ix, y + 4, iw, album, Muted, false, age, row: 2))
            c.Text(ix, y + 4, album, Rgb.Lerp(from, Muted, fade), maxWidth: iw);
        if (!DrawDecoded(c, ix, y + 5, iw, t.QualityTag, Muted, false, age, row: 3))
            c.Text(ix, y + 5, t.QualityTag, Rgb.Lerp(from, Muted, fade), maxWidth: iw);
        if (Glitching)
        {
            GlitchRow(c, ix, y + 2, iw, 0);
            GlitchRow(c, ix, y + 3, iw, 1);
        }

        // Transport block sits at the bottom; the spectrum fills what's between.
        int by = Math.Max(y + h - 5, y + 9);
        int specTop = y + 7, specBottom = by - 2;
        if (specBottom - specTop >= 1) DrawVisual(c, ix, specTop, iw, specBottom - specTop + 1);

        double pos = Playback.Position, dur = Playback.Duration;
        DrawProgress(c, ix, by, iw, dur > 0 ? pos / dur : 0);
        c.Text(ix, by + 1, FormatTime(pos), Theme.Text);
        string total = FormatTime(dur);
        c.Text(ix + iw - total.Length, by + 1, total, Muted);

        // Another app's volume is its slider in the Windows mixer; unknown until read.
        double appVol = Playback.Volume;
        string volLabel = Playback.Muted ? "vol mute" : double.IsNaN(appVol) ? "vol  — " : $"vol {appVol,3:0}";
        c.Text(ix, by + 3, volLabel, Muted);
        // The bar glides to the new level and flashes after a change.
        double vol = Playback.Muted || double.IsNaN(appVol) ? 0 : appVol;
        if (vol != _volTarget) { if (_volTarget >= 0) _volChangedAt = Now; _volTarget = vol; }
        _volShown = _volShown < 0 ? vol : _volShown + (vol - _volShown) * 0.35;
        if (Math.Abs(_volShown - vol) < 0.5) _volShown = vol; else _animFast = true;
        double flash = Math.Clamp(1 - (Now - _volChangedAt) / 0.4, 0, 1);
        if (flash > 0) _animFast = true;
        int volW = Math.Min(20, iw - 9), volFill = (int)Math.Round(_volShown / 100 * volW);
        var volColor = Rgb.Lerp(Accent, Fx.White, flash * 0.8);
        for (int i = 0; i < volW; i++)
            c.Set(ix + 9 + i, by + 3, i < volFill ? '■' : '·', i >= volFill ? Track : _fx ? Rgb.Lerp(Rgb.Lerp(Accent, _second, i / (double)Math.Max(1, volW - 1)), Fx.White, flash * 0.8) : volColor);
        DrawLevelMeter(c, ix + 9 + volW + 3, by + 3, iw - (9 + volW + 3));
    }

    private const string Horizontal = " ▏▎▍▌▋▊▉█";

    /// <summary>Loudness meter with eighth-cell resolution, through the cover colors to white, and a falling peak marker.</summary>
    private void DrawLevelMeter(Canvas c, int x, int y, int w)
    {
        if (w < 10) return;
        c.Text(x, y, "nivel", Muted);
        int mx = x + 6, mw = Math.Min(24, w - 6);
        double cells = Math.Clamp(_level * 1.6, 0, 1) * mw;
        int peak = (int)Math.Min(mw - 1, Math.Clamp(_levelPeak * 1.6, 0, 1) * mw);
        for (int i = 0; i < mw; i++)
        {
            double t = mw <= 1 ? 0 : i / (double)(mw - 1);
            var color = t < 0.7 ? Rgb.Lerp(Accent, _second, t / 0.7) : Rgb.Lerp(_second, Fx.White, (t - 0.7) / 0.3);
            int fill = Math.Clamp((int)Math.Round((cells - i) * 8), 0, 8);
            if (fill > 0) c.Set(mx + i, y, Horizontal[fill], color);
            else if (i == peak && _levelPeak > 0.01) c.Set(mx + i, y, '▏', Fx.White);
            else c.Set(mx + i, y, '·', Track);
        }
    }

    private Rgb PanelBorder => _fx ? Rgb.Lerp(Theme.Border, Accent, 0.3) : Theme.Border;
    private Rgb BorderIn(Rgb hue) => _fx ? Rgb.Lerp(Theme.Border, hue, 0.4) : Theme.Border;

    /// <summary>
    /// Panel frame; with fx and music playing it reacts to the bass like Shade's
    /// display border: brighter with each beat, and thick (it "grows") on strong bass.
    /// </summary>
    private void Panel(Canvas c, int x, int y, int w, int h, string title, Rgb border, Rgb titleColor)
    {
        bool live = _fx && Playback.Playing;
        double level = live ? Math.Max(BeatStep, BassStep * 0.85) : 0;
        if (level > 0) border = Rgb.Lerp(border, Fx.White, level * 0.35);
        c.Box(x, y, w, h, title, border, titleColor, heavy: level >= 0.5);
    }

    /// <summary>
    /// Draws text lighting up left to right with jitter: each character appears as a dim
    /// ghost, then flashes white as it locks in. Returns false once the effect is over (caller draws normally).
    /// </summary>
    private bool DrawDecoded(Canvas c, int x, int y, int w, string text, Rgb fg, bool bold, double age, int row)
    {
        if (!_fx || age >= DecodeTime) return false;
        var runes = text.EnumerateRunes().ToArray();
        int n = Math.Max(1, runes.Length - 1), col = 0;
        double rowDelay = row * 0.05;
        var ghost = Rgb.Lerp(_palette.Backdrop, _second, 0.55);
        for (int i = 0; i < runes.Length; i++)
        {
            int rw = TextWidth.Of(runes[i]);
            if (rw == 0) continue;
            if (col + rw > w) break;
            double appearAt = rowDelay + i / (double)n * 0.2;
            double lockAt = Math.Min(DecodeTime - 0.05, rowDelay + 0.12 + i / (double)n * 0.35 + Fx.Noise(i, row + _lastIndex * 7) * 0.15);
            if (age >= lockAt)
            {
                var color = age - lockAt < 0.07 ? Fx.White : fg;
                c.Set(x + col, y, runes[i], color, bold: bold);
                if (rw == 2) c.Set(x + col + 1, y, default(Rune), color, bold: bold);
            }
            else if (age >= appearAt)
            {
                c.Set(x + col, y, runes[i], ghost, bold: bold);
                if (rw == 2) c.Set(x + col + 1, y, default(Rune), ghost, bold: bold);
            }
            col += rw;
        }
        return true;
    }

    /// <summary>Glitch burst on one row: horizontal tear plus a few cover-tinted cells.</summary>
    private void GlitchRow(Canvas c, int x, int y, int w, int row)
    {
        int tick = GlitchTick;
        if (Fx.Noise(tick, row) < 0.6)
            c.ShiftRow(x, y, w, (int)(Fx.Noise(tick, row + 13) * 5) - 2);
        for (int col = 0; col < w; col++)
        {
            double r = Fx.Noise(tick, col + row * 131);
            if (r < 0.06) c.Recolor(x + col, y, _second);
            else if (r < 0.10) c.Recolor(x + col, y, _fourth);
        }
    }

    private void DrawLower(Canvas c, int x, int y, int w, int h, double dt)
    {
        if (_sourcesOpen) DrawSources(c, x, y, w, h);
        else if (_eqOpen) DrawEqualizer(c, x, y, w, h, dt);
        else DrawLyrics(c, x, y, w, h, dt);
    }

    // ── command menu ───────────────────────────────────────────────────

    private sealed record MenuItem(string Section, string Key, string Label, Action Run, string? State = null);

    /// <summary>Shift tapped on its own: open or close the command menu. Returns true (keep running).</summary>
    public bool ToggleMenu()
    {
        if (_menuOpen) _menuOpen = false;
        else OpenMenu();
        return true;
    }

    private void OpenMenu()
    {
        _menuOpen = true;
        _menuRow = 0;
    }

    private static string YesNo(bool on) => on ? "sí" : "no";

    /// <summary>Every command, grouped; the open panel's own keys come first.</summary>
    private List<MenuItem> MenuItems()
    {
        var p = Playback;
        var items = new List<MenuItem>();
        int bands = Equalizer.Frequencies.Length;
        if (_eqOpen)
        {
            items.Add(new("Ecualizador", "← →", "banda anterior / siguiente", () => _eqBand = (_eqBand + 1) % bands, $"{Equalizer.Labels[_eqBand]} Hz"));
            items.Add(new("Ecualizador", "↑ ↓", "ganancia ±1 dB (shift ±3)", () => ChangeEq(() => _eq.SetGain(_eqBand, _eq.Gains[_eqBand] + 1)), $"{_eq.Gains[_eqBand]:+0;-0;0} dB"));
            items.Add(new("Ecualizador", "0", "banda plana", () => ChangeEq(() => _eq.SetGain(_eqBand, 0))));
            items.Add(new("Ecualizador", "tab", "siguiente preset", () => ChangeEq(() => _eq.CyclePreset(1)), _eq.PresetName));
            items.Add(new("Ecualizador", "b", "ecualizador on/off", () => ChangeEq(_eq.ToggleEnabled), YesNo(_eq.Enabled)));
            items.Add(new("Ecualizador", "esc", "cerrar ecualizador", () => _eqOpen = false));
        }
        if (_sourcesOpen)
        {
            items.Add(new("Fuentes", "j k", "mover · enter usar la fuente", () => { }));
            items.Add(new("Fuentes", "a", "auto-seguir otras apps", () => { Sources = Sources with { AutoFollow = !Sources.AutoFollow }; Sources.Save(); }, YesNo(Sources.AutoFollow)));
            items.Add(new("Fuentes", "esc", "cerrar fuentes", () => _sourcesOpen = false));
        }

        items.Add(new("Reproducción", "espacio", "play / pausa", p.TogglePause, p.Playing ? "sonando" : "pausa"));
        items.Add(new("Reproducción", "n", "siguiente", p.Next));
        items.Add(new("Reproducción", "p", "anterior", p.Previous));
        items.Add(new("Reproducción", "→", "adelantar 5 s (shift 30 s)", () => p.SeekRelative(5)));
        items.Add(new("Reproducción", "←", "retroceder 5 s (shift 30 s)", () => p.SeekRelative(-5)));
        items.Add(new("Reproducción", "+", "subir volumen", () => p.AddVolume(5), double.IsNaN(p.Volume) ? null : $"{p.Volume:0}"));
        items.Add(new("Reproducción", "-", "bajar volumen", () => p.AddVolume(-5)));
        items.Add(new("Reproducción", "m", "silenciar", p.ToggleMute, YesNo(p.Muted)));
        items.Add(new("Reproducción", "s", "aleatorio", () => { p.ToggleShuffle(); JumpToCurrent(); }, YesNo(p.Shuffled)));
        items.Add(new("Reproducción", "r", "repetir", p.ToggleRepeat, YesNo(p.Repeat)));

        items.Add(new("Vista", "o", "fuentes: biblioteca, Spotify, navegador…", OpenSources, _externalMode ? External?.AppName : "biblioteca"));
        if (!_externalMode) items.Add(new("Vista", "b", "lista de canciones (barra lateral)", ToggleSidebar, Ui.ShowQueue ? "visible" : "oculta"));
        items.Add(new("Vista", "l", "letra", () => _showLyrics = !_showLyrics, YesNo(_showLyrics)));
        if (!_externalMode) items.Add(new("Vista", "e", "ecualizador", () => { _eqOpen = true; _sourcesOpen = false; }));
        items.Add(new("Vista", "v", "visualizador", CycleVisual, VisualName));
        items.Add(new("Vista", "f", "efectos", () => _fx = !_fx, YesNo(_fx)));

        items.Add(new("Letra", ",", "desfase −0.25 s", () => NudgeLyrics(-0.25)));
        items.Add(new("Letra", ".", "desfase +0.25 s", () => NudgeLyrics(+0.25)));

        if (QueueVisible)
        {
            items.Add(new("Lista", "j k", "mover · g/G inicio/final · enter reproducir", () => { }));
            items.Add(new("Lista", "c", "ir a la canción actual", JumpToCurrent));
        }
        items.Add(new("General", "q", "salir", () => _quit = true));
        return items;
    }

    /// <summary>Navigation inside the menu. Any other key closes it and runs as a normal shortcut (handled = false).</summary>
    private bool HandleMenuKey(ConsoleKeyInfo key, out bool handled)
    {
        handled = true;
        var items = MenuItems();
        switch (key.Key)
        {
            case ConsoleKey.Escape: _menuOpen = false; return true;
            case ConsoleKey.J or ConsoleKey.DownArrow: _menuRow = (_menuRow + 1) % items.Count; return true;
            case ConsoleKey.K or ConsoleKey.UpArrow: _menuRow = (_menuRow - 1 + items.Count) % items.Count; return true;
            case ConsoleKey.Enter:
                _menuOpen = false;
                items[Math.Clamp(_menuRow, 0, items.Count - 1)].Run();
                return !_quit;
        }
        if (key.KeyChar == '?' || key.Key == ConsoleKey.F1) { _menuOpen = false; return true; }
        _menuOpen = false;
        handled = false;
        return true;
    }

    /// <summary>Centered overlay listing every command with its key and current state.</summary>
    private void DrawMenu(Canvas c)
    {
        var items = MenuItems();
        _menuRow = Math.Clamp(_menuRow, 0, items.Count - 1);
        // One line per item plus a header line per section.
        var lines = new List<(string? Section, int Item)>();
        string? section = null;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Section != section) { section = items[i].Section; lines.Add((section, -1)); }
            lines.Add((null, i));
        }

        int w = Math.Min(c.Width - 4, 62), h = Math.Min(c.Height - 2, lines.Count + 4);
        int x = (c.Width - w) / 2, y = Math.Max(1, (c.Height - h) / 2);
        var bg = Rgb.Lerp(_palette.Backdrop, Rgb.Hex(0x0A0A0C), 0.55);
        for (int row = y; row < y + h; row++)
            for (int col = x; col < x + w; col++) c.Set(col, row, ' ', null, bg);
        c.Box(x, y, w, h, "Comandos", _fx ? Rgb.Lerp(Accent, Fx.White, 0.2) : Accent, Accent);

        int rows = h - 4, top = y + 1;
        int selLine = lines.FindIndex(l => l.Item == _menuRow);
        int scroll = Math.Clamp(selLine - rows / 2, 0, Math.Max(0, lines.Count - rows));
        var selection = Rgb.Lerp(bg, Theme.Selection, 0.8);
        for (int r = 0; r < rows && scroll + r < lines.Count; r++)
        {
            var (head, index) = lines[scroll + r];
            int ry = top + r;
            if (head is not null)
            {
                c.Text(x + 2, ry, head, _fx ? _third : Muted, bg, bold: true);
                continue;
            }
            var item = items[index];
            bool selected = index == _menuRow;
            var rowBg = selected ? selection : bg;
            if (selected) for (int col = x + 1; col < x + w - 1; col++) c.Set(col, ry, ' ', null, rowBg);
            if (selected) c.Set(x + 1, ry, '▌', _second, rowBg);
            c.Text(x + 4, ry, item.Key, selected ? Fx.White : Accent, rowBg, bold: true, maxWidth: 9);
            int stateW = item.State is { } st ? TextWidth.Of(st) : 0;
            c.Text(x + 14, ry, item.Label, selected ? Theme.Text : Rgb.Lerp(Theme.Text, Muted, 0.35), rowBg, maxWidth: w - 16 - (stateW > 0 ? stateW + 2 : 0));
            if (item.State is { } state) c.Text(x + w - 2 - stateW, ry, state, _second, rowBg);
        }
        string help = "j/k mover · enter ejecutar · shift/esc cerrar · o pulsa su tecla";
        c.Text(x + 2, y + h - 2, help, Muted, bg, maxWidth: w - 4);
    }

    // ── sources ────────────────────────────────────────────────────────

    private double _sourcesListedAt = double.NegativeInfinity;

    /// <summary>Where the player takes its music from: library, automatic, or a specific app (pinned).</summary>
    private void DrawSources(Canvas c, int x, int y, int w, int h)
    {
        // Titles and statuses change under us; re-list about once a second while open.
        if (Now - _sourcesListedAt > 1) { _sourcesListedAt = Now; External?.RefreshSessions(); }
        string title = $"Fuentes · auto-seguir: {(Sources.AutoFollow ? "sí" : "no")}";
        Panel(c, x, y, w, h, title, BorderIn(_second), Accent);
        int ix = x + 2, iw = w - 4, top = y + 1, rows = h - 2;
        if (rows <= 0 || iw <= 10) return;

        var list = SourceRows();
        _sourceRow = Math.Clamp(_sourceRow, 0, list.Count - 1);
        var selection = Rgb.Lerp(_palette.Backdrop, Theme.Selection, 0.25);
        int nameW = Math.Clamp(iw / 3, 12, 18), statusW = 9;
        int r = 0;
        for (int i = 0; i < list.Count && r < rows - 2; i++, r++)
        {
            var row = list[i];
            int ry = top + r;
            bool selected = i == _sourceRow;
            if (selected) c.FillRow(x + 1, ry, w - 2, selection);
            if (row.Active) c.Text(ix, ry, "●", Accent);
            c.Text(ix + 2, ry, row.Name, row.Active ? Accent : Theme.Text, bold: row.Active || selected, maxWidth: nameW);
            int dx = ix + 2 + nameW + 1;
            if (row.Kind == SourceKind.App)
            {
                c.Text(dx, ry, row.Status, row.Status == "sonando" ? _second : Muted, maxWidth: statusW);
                dx += statusW + 1;
            }
            string pin = row.Pinned ? "◆ fijada" : "";
            int detailW = ix + iw - dx - (pin.Length > 0 ? pin.Length + 2 : 0);
            if (detailW > 3) c.Text(dx, ry, row.Detail, _fx ? Rgb.Lerp(Muted, _third, 0.4) : Muted, maxWidth: detailW);
            if (pin.Length > 0) c.Text(ix + iw - pin.Length, ry, pin, _third);
            // The sessions list arrives a moment after opening.
            if (i == 1 && list.Count == 2 && External is not null && r + 1 < rows - 2)
            {
                r++;
                c.Text(ix + 2, top + r, "ninguna otra app con sesión de medios (abre Spotify o YouTube Music)", Muted, maxWidth: iw - 2);
            }
        }
        string help = External is null
            ? "seguir otras apps está desactivado (--no-external)"
            : Sources.AutoFollow
                ? "auto-seguir: si otra app suena con Nation en pausa, la sigue"
                : "auto-seguir apagado: solo cambia si eliges aquí";
        c.Text(ix, top + rows - 1, help, Muted, maxWidth: iw);
    }

    // ── equalizer ──────────────────────────────────────────────────────

    /// <summary>
    /// Ten vertical sliders around a 0 dB line, half-cell resolution, with the live
    /// spectrum at each band's frequency glowing faintly behind them.
    /// </summary>
    private void DrawEqualizer(Canvas c, int x, int y, int w, int h, double dt)
    {
        bool on = _eq.Enabled;
        string title = $"Ecualizador · {_eq.PresetName}" + (on ? "" : " · apagado");
        if (_player.EqualizerError is { } err) title += $" · error: {err}";
        if (_externalMode) title += $" · solo música local, no {External?.AppName}";
        var border = _fx ? Rgb.Lerp(BorderIn(_third), _second, Math.Clamp(1 - (Now - _eqChangedAt) / 0.3, 0, 1) * 0.8) : PanelBorder;
        Panel(c, x, y, w, h, title, border, on ? Accent : Muted);

        int bands = Equalizer.Frequencies.Length;
        int ix = x + 2, iw = w - 4, top = y + 1, rows = h - 2;
        int sliderH = rows - 3; // gain values, sliders, frequency labels, preset strip
        if (sliderH % 2 == 0) sliderH--; // odd height keeps the 0 dB line centered
        if (sliderH < 3 || iw < bands * 3)
        {
            CenterText(c, ix, top + rows / 2, iw, "agranda la terminal para ver el ecualizador", Muted);
            return;
        }

        // Sliders glide to new values.
        double k = 1 - Math.Exp(-dt / 0.06);
        for (int i = 0; i < bands; i++)
        {
            double target = on ? _eq.Gains[i] : 0;
            _eqShown[i] += (target - _eqShown[i]) * k;
            if (Math.Abs(target - _eqShown[i]) < 0.05) _eqShown[i] = target; else _animFast = true;
        }
        if (Now - _eqChangedAt < 0.3) _animFast = true;

        int colW = iw / bands, ox = ix + (iw - colW * bands) / 2;
        int sliderTop = top + 1, centerRow = sliderTop + (sliderH - 1) / 2;
        int halfRange = Math.Max(1, Math.Min(centerRow - sliderTop, sliderTop + sliderH - 1 - centerRow));
        var selection = Rgb.Lerp(_palette.Backdrop, Theme.Selection, 0.25);
        var dim = on ? Track : Rgb.Lerp(Track, Theme.Border, 0.5);

        for (int i = 0; i < bands; i++)
        {
            bool selected = i == _eqBand;
            int cx = ox + i * colW, bx = cx + (colW - 2) / 2;
            if (selected)
                for (int r = top; r < top + rows - 1; r++) c.FillRow(cx, r, colW, selection);

            // Live spectrum at this band's frequency, as a faint column behind the slider.
            double level = Sample(_bars, BandPosition(Equalizer.Frequencies[i]));
            int lit = (int)Math.Round(level * sliderH);
            var glow = Rgb.Lerp(_palette.Backdrop, Accent, on ? 0.35 : 0.15);
            for (int r = 0; r < lit; r++)
            {
                int ry = sliderTop + sliderH - 1 - r;
                c.Set(bx - 1, ry, '░', glow, selected ? selection : null);
                c.Set(bx + 2, ry, '░', glow, selected ? selection : null);
            }

            // 0 dB line, then the fill from it toward the gain in half-cell steps.
            double g = _eqShown[i];
            var lineColor = !on ? dim : Math.Abs(g) < 0.5 ? Muted : Accent;
            c.Set(bx, centerRow, '━', lineColor);
            c.Set(bx + 1, centerRow, '━', lineColor);
            // Any non-zero gain shows at least half a cell, even on short terminals.
            int halves = (int)Math.Round(Math.Abs(g) / Equalizer.MaxGain * halfRange * 2, MidpointRounding.AwayFromZero);
            if (halves == 0 && Math.Abs(g) >= 0.5) halves = 1;
            for (int u = 1; u <= halves; u++)
            {
                int offset = (u + 1) / 2;
                bool half = u == halves && u % 2 == 1;
                int ry = g > 0 ? centerRow - offset : centerRow + offset;
                char ch = half ? (g > 0 ? '▄' : '▀') : '█';
                double t = offset / (double)halfRange;
                var color = !on ? Muted : u == halves && selected ? Fx.White : Rgb.Lerp(Accent, _second, t);
                c.Set(bx, ry, ch, color);
                c.Set(bx + 1, ry, ch, color);
            }

            string value = on ? (_eq.Gains[i] > 0 ? "+" : "") + _eq.Gains[i].ToString("0") : "·";
            CenterText(c, cx, top, colW, value, selected ? Accent : Muted);
            int labelX = cx + (colW - Equalizer.Labels[i].Length) / 2;
            c.Text(labelX, sliderTop + sliderH, Equalizer.Labels[i], selected ? Accent : Muted, bold: selected);
        }

        // Preset strip; the active one is highlighted, the preamp sits on the right.
        int py = top + rows - 1, px = ix;
        string preamp = on && _eq.Preamp < 0 ? $"pre {_eq.Preamp:0} dB" : "";
        int stripEnd = ix + iw - preamp.Length - 2;
        var names = Equalizer.Presets.Select(p => p.Name).Append(Equalizer.CustomName);
        foreach (var name in names)
        {
            bool active = name == _eq.PresetName && on;
            string label = active ? $"[{name}]" : name;
            if (px + TextWidth.Of(label) > stripEnd) break;
            px += c.Text(px, py, label, active ? Accent : Muted, bold: active) + 2;
        }
        if (preamp.Length > 0) c.Text(ix + iw - preamp.Length, py, preamp, Muted);
    }

    /// <summary>
    /// Fractional analyzer band for a frequency. Bands are spaced by the square of
    /// their index over half the sample rate (~48 kHz loopback), so invert that.
    /// </summary>
    private static double BandPosition(double hz) =>
        Math.Clamp(Math.Sqrt(hz / 24000) * SpectrumAnalyzer.BandCount - 0.5, 0, SpectrumAnalyzer.BandCount - 1);

    // ── lyrics ─────────────────────────────────────────────────────────

    private void UpdateLyrics()
    {
        var track = Playback.Current;
        if (SameLyrics(track, _lyricsFor)) return;
        // Another app's duration may land a moment after its title; LRCLIB matching needs it.
        if (track is { IsExternal: true, Duration.Ticks: 0 } && Now - _trackChangedAt < 2)
        {
            _lyricsCts?.Cancel();
            _lyricsFor = null;
            _lyrics = null;
            _lyricsText = null;
            _lyricsLoading = true;
            return;
        }

        _lyricsFor = track;
        _lyrics = null;
        _lyricsText = null;
        _lyricOffset = 0;
        _lyricScroll = 0;
        _lyricsCts?.Cancel();
        if (track is null) { _lyricsLoading = false; return; }

        var cts = _lyricsCts = new CancellationTokenSource();
        _lyricsLoading = true;
        var requested = track;
        _ = Task.Run(async () =>
        {
            var sheet = await _lyricsProvider.FindAsync(requested, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_lyricsFor, requested)) return;
            _lyricsText = sheet is null ? null : ToLrc(sheet);
            _lyrics = sheet;
            _lyricsLoading = false;
        });
    }

    /// <summary>Same song for lyrics purposes: same id and (whole-second) duration.</summary>
    private static bool SameLyrics(Track? a, Track? b) =>
        a is null ? b is null : b is not null && a.Id == b.Id && (int)a.Duration.TotalSeconds == (int)b.Duration.TotalSeconds;

    private void DrawLyrics(Canvas c, int x, int y, int w, int h, double dt)
    {
        var sheet = _lyrics;
        string title = "Letra";
        if (sheet is not null)
        {
            title += sheet.Source switch
            {
                LyricsSource.Embedded => " · archivo",
                LyricsSource.Sidecar => " · .lrc",
                _ => " · lrclib",
            };
            if (!sheet.Synced) title += " · sin sincronizar";
        }
        bool recentNudge = Now - _lyricOffsetChangedAt < 2;
        if (recentNudge || _lyricsLoading) _animSlow = true; // offset notice must clear, spinner spins
        if (sheet is { Synced: true } && (_lyricOffset != 0 || recentNudge))
            title += $" · desfase {_lyricOffset:+0.00;-0.00;0.00}s";
        else if (sheet is not null && !sheet.Synced && recentNudge)
            title += " · desfase no aplica";
        Panel(c, x, y, w, h, title, BorderIn(_third), Muted);

        int ix = x + 2, iw = w - 4, top = y + 1, rows = h - 2;
        if (rows <= 0 || iw <= 4) return;

        if (sheet is null)
        {
            string msg = _lyricsLoading
                ? $"{Spinner[(int)(Now * 12) % Spinner.Length]} buscando letra…"
                : Playback.Current is null ? "" : "sin letra para esta canción";
            CenterText(c, ix, top + rows / 2, iw, msg, Muted);
            return;
        }

        var wrapped = Wrap(sheet, iw);
        if (!sheet.Synced)
        {
            // No timing: scroll proportionally to playback so the text roughly keeps up.
            double dur = Playback.Duration;
            int maxScroll = Math.Max(0, wrapped.Count - rows);
            double target = dur > 0 ? Math.Clamp(Playback.Position / dur, 0, 1) * maxScroll : 0;
            if (Math.Abs(target - _lyricScroll) > 0.02) _animFast = true;
            _lyricScroll += (target - _lyricScroll) * (1 - Math.Exp(-dt / 0.3));
            int first = Math.Clamp((int)Math.Round(_lyricScroll), 0, maxScroll);
            for (int r = 0; r < rows && first + r < wrapped.Count; r++)
                CenterText(c, ix, top + r, iw, wrapped[first + r].Text, Theme.Text);
            return;
        }

        double pos = Playback.Position + _lyricOffset;
        int active = sheet.IndexAt(pos);
        if (active != _activeLyric) { _activeLyric = active; _activeLyricAt = Now; }
        double lineAge = Now - _activeLyricAt;
        int activeLine = Math.Max(active, 0), activeRow = 0;
        while (activeRow < wrapped.Count - 1 && wrapped[activeRow].Line < activeLine) activeRow++;
        // Active line sits a third from the top so more upcoming lines are visible.
        int anchor = rows / 3;
        if (Math.Abs(activeRow - anchor - _lyricScroll) > 0.02) _animFast = true;
        _lyricScroll += (activeRow - anchor - _lyricScroll) * (1 - Math.Exp(-dt / 0.12));
        int start = (int)Math.Round(_lyricScroll);

        // Karaoke-style fill across the active line, timed to the next line's stamp.
        double fill = 0;
        if (active >= 0)
        {
            double t0 = sheet.Lines[active].Time;
            double t1 = active + 1 < sheet.Lines.Count ? sheet.Lines[active + 1].Time : t0 + 4;
            fill = Math.Clamp((pos - t0) / Math.Max(0.1, t1 - t0), 0, 1);
        }
        int activeWidth = 0;
        for (int k = activeRow; k < wrapped.Count && wrapped[k].Line == active; k++) activeWidth += TextWidth.Of(wrapped[k].Text);
        int filledCols = (int)Math.Round(fill * activeWidth);

        for (int r = 0; r < rows; r++)
        {
            int wi = start + r;
            if (wi < 0 || wi >= wrapped.Count) continue;
            var (line, text) = wrapped[wi];
            int ry = top + r;

            if (line == active)
            {
                if (text.Length == 0) text = Instrumental();
                int width = Math.Min(TextWidth.Of(text), iw);
                int cx = ix + (iw - width) / 2;
                // Columns of this row already sung = total filled minus earlier rows of the same line.
                int before = 0;
                for (int k = wi - 1; k >= 0 && wrapped[k].Line == active; k--) before += TextWidth.Of(wrapped[k].Text);
                int rowFill = Math.Clamp(filledCols - before, 0, width);
                c.Text(cx, ry, text, Theme.Text, bold: true, maxWidth: iw);
                var sung = _fx ? Rgb.Lerp(Accent, Fx.White, _beat * 0.4) : Accent;
                for (int col = 0; col < rowFill; col++)
                    c.Recolor(cx + col, ry, _fx ? Rgb.Lerp(Rgb.Lerp(Accent, _second, col / (double)Math.Max(1, width - 1)), Fx.White, _beat * 0.4) : sung);
                if (_fx)
                {
                    // White-hot edge where the karaoke fill is advancing.
                    if (rowFill > 0 && rowFill < width)
                    {
                        c.Recolor(cx + rowFill - 1, ry, Fx.White);
                        if (rowFill > 1) c.Recolor(cx + rowFill - 2, ry, Rgb.Lerp(Accent, Fx.White, 0.5));
                    }
                    if (lineAge < 0.3) RevealIn(c, cx, ry, width, lineAge / 0.3);
                }
            }
            else
            {
                // Fade with distance from the active line; sung lines a little dimmer.
                int distance = Math.Abs(line - Math.Max(active, 0));
                var upcoming = _fx ? Rgb.Lerp(Muted, _third, 0.35) : Muted;
                var baseColor = line < active ? Rgb.Lerp(Muted, _palette.Backdrop, 0.35) : upcoming;
                var color = Rgb.Lerp(baseColor, _palette.Backdrop, Math.Min(distance, 6) / 9.0);
                CenterText(c, ix, ry, iw, text.Length == 0 ? "♪" : text, color);
            }
        }
    }

    /// <summary>Dims the not-yet-revealed tail of a freshly activated lyric row, with a white edge sweeping right.</summary>
    private void RevealIn(Canvas c, int x, int y, int width, double progress)
    {
        _animFast = true;
        int revealed = (int)(progress * width);
        var ghost = Rgb.Lerp(_palette.Backdrop, _second, 0.4);
        for (int col = revealed; col < width; col++) c.Recolor(x + col, y, ghost);
        if (revealed < width) c.Recolor(x + revealed, y, Fx.White);
    }

    /// <summary>Startup: a diagonal CRT wipe reveals the UI, its leading edge a band of white-to-cover-color light.</summary>
    private void DrawBoot(Canvas c, double progress)
    {
        _animFast = true;
        double eased = 1 - Math.Pow(1 - progress, 2);
        double span = c.Width + c.Height * 2 + 8;
        double front = eased * span;
        for (int y = 0; y < c.Height; y++)
            for (int x = 0; x < c.Width; x++)
            {
                double d = x + y * 2;
                if (d > front) c.Reset(x, y);
                else if (d > front - 6) c.Recolor(x, y, Rgb.Lerp(Fx.White, _second, (front - d) / 6));
            }
    }

    private string Instrumental()
    {
        int n = (int)(Now * 2.5) % 4;
        return "♪" + string.Concat(Enumerable.Repeat(" ♪", n));
    }

    private static void CenterText(Canvas c, int x, int y, int w, string text, Rgb fg)
    {
        int width = Math.Min(TextWidth.Of(text), w);
        c.Text(x + (w - width) / 2, y, text, fg, maxWidth: w);
    }

    /// <summary>Word-wraps every lyric line to the panel width; cached until the sheet or width changes.</summary>
    private List<(int Line, string Text)> Wrap(LyricSheet sheet, int width)
    {
        if (ReferenceEquals(_wrapCache.Sheet, sheet) && _wrapCache.Width == width) return _wrapCache.Rows;

        var rows = new List<(int, string)>();
        for (int i = 0; i < sheet.Lines.Count; i++)
        {
            var text = sheet.Lines[i].Text;
            if (text.Length == 0) { rows.Add((i, "")); continue; }

            var current = new System.Text.StringBuilder();
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (TextWidth.Of(candidate) <= width) { current.Clear().Append(candidate); continue; }
                if (current.Length > 0) rows.Add((i, current.ToString()));
                current.Clear().Append(word); // a single over-long word gets clipped when drawn
            }
            if (current.Length > 0) rows.Add((i, current.ToString()));
        }
        _wrapCache = (sheet, width, rows);
        return rows;
    }

    /// <summary>Scrolls text that doesn't fit: 2 s hold, scroll, loop.</summary>
    private void DrawMarquee(Canvas c, int x, int y, int w, string text, Rgb fg, bool bold, double age, int row)
    {
        if (DrawDecoded(c, x, y, w, text, fg, bold, age, row)) return;
        if (TextWidth.Of(text) <= w)
        {
            c.Text(x, y, text, fg, bold: bold, maxWidth: w);
            return;
        }
        _animSlow = true;
        const string gap = "     ";
        var runes = (text + gap).EnumerateRunes().ToArray();
        const double hold = 2.0, speed = 6.0; // seconds, columns/second
        double cycle = hold + runes.Length / speed;
        int offset = (int)(Math.Max(0, age % cycle - hold) * speed);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < runes.Length * 2 && TextWidth.Of(sb.ToString()) < w; i++)
            sb.Append(runes[(offset + i) % runes.Length].ToString());
        c.Text(x, y, sb.ToString(), fg, bold: bold, maxWidth: w, ellipsis: false);
    }

    private void DrawVisual(Canvas c, int x, int y, int w, int h)
    {
        switch (_visual)
        {
            case Visual.Espejo: DrawMirror(c, x, y, w, h); break;
            case Visual.Onda: DrawWave(c, x, y, w, h); break;
            case Visual.Lluvia: DrawRain(c, x, y, w, h); break;
            case Visual.Circulo: DrawCircle(c, x, y, w, h); break;
            case Visual.Osciloscopio: DrawScope(c, x, y, w, h); break;
            case Visual.Cascada: DrawCascade(c, x, y, w, h); break;
            case Visual.Reflejo: DrawReflection(c, x, y, w, h); break;
            default: DrawSpectrum(c, x, y, w, h); break;
        }
    }

    /// <summary>Bars grow up and down from the middle row, bass in the center, treble toward both edges.</summary>
    private void DrawMirror(Canvas c, int x, int y, int w, int h)
    {
        int count = Math.Max(1, (w + 1) / 2);
        int offsetX = x + (w - (count * 2 - 1)) / 2;
        int mid = h; // in half-rows: 2h half-rows, middle line between h-1 and h
        var low = Rgb.Lerp(_palette.Backdrop, Accent, 0.45);
        for (int b = 0; b < count; b++)
        {
            double fromCenter = Math.Abs(b - (count - 1) / 2.0) / Math.Max(1, (count - 1) / 2.0);
            double v = Sample(_bars, fromCenter * (SpectrumAnalyzer.BandCount - 1));
            int k = (int)Math.Round(v * h);
            if (k == 0) continue;
            int cx = offsetX + b * 2;
            for (int r = 0; r < h; r++)
            {
                bool top = 2 * r >= mid - k && 2 * r < mid + k;
                bool bottom = 2 * r + 1 >= mid - k && 2 * r + 1 < mid + k;
                if (!top && !bottom) continue;
                double dist = Math.Abs(r + 0.5 - h / 2.0) / Math.Max(1, h / 2.0);
                c.Set(cx, y + r, top && bottom ? '█' : top ? '▀' : '▄', SpectrumColor(low, dist, fromCenter));
            }
        }
    }

    private static readonly int[,] BrailleBits = { { 0x01, 0x08 }, { 0x02, 0x10 }, { 0x04, 0x20 }, { 0x40, 0x80 } };

    /// <summary>Filled waveform-like ridge in braille dots: 2×4 sub-cell resolution, interpolated across bands.</summary>
    private void DrawWave(Canvas c, int x, int y, int w, int h)
    {
        int dotsW = w * 2, dotsH = h * 4;
        var low = Rgb.Lerp(_palette.Backdrop, Accent, 0.45);
        Span<int> heights = stackalloc int[2];
        for (int cx = 0; cx < w; cx++)
        {
            for (int sub = 0; sub < 2; sub++)
            {
                double pos = (cx * 2 + sub) / (double)Math.Max(1, dotsW - 1) * (SpectrumAnalyzer.BandCount - 1);
                heights[sub] = (int)Math.Round(Sample(_bars, pos) * dotsH);
            }
            for (int r = 0; r < h; r++)
            {
                int bits = 0;
                for (int dy = 0; dy < 4; dy++)
                {
                    int fromBottom = dotsH - 1 - (r * 4 + dy);
                    for (int sub = 0; sub < 2; sub++)
                        // The top dot of each column is always lit so the ridge reads as a line.
                        if (fromBottom < heights[sub] || (fromBottom == 0 && heights[sub] == 0)) bits |= BrailleBits[dy, sub];
                }
                if (bits == 0) continue;
                double t = h == 1 ? 1 : (h - 1 - r) / (double)(h - 1);
                c.Set(x + cx, y + r, (char)(0x2800 + bits), SpectrumColor(low, t, cx / (double)Math.Max(1, w - 1)));
            }
        }
        if (_fx) DrawSparks(c, x, y, w, h, sx => x + (int)Math.Round(sx * (w - 1)));
    }

    // ── braille canvas shared by the dot-drawn visualizers ──

    private bool[] _dots = Array.Empty<bool>();
    private int _dotsW, _dotsH;

    private void BeginDots(int w, int h)
    {
        _dotsW = w * 2;
        _dotsH = h * 4;
        int n = _dotsW * _dotsH;
        if (_dots.Length < n) _dots = new bool[n];
        Array.Clear(_dots, 0, n);
    }

    private void Dot(int dx, int dy)
    {
        if (dx >= 0 && dy >= 0 && dx < _dotsW && dy < _dotsH) _dots[dy * _dotsW + dx] = true;
    }

    private void DotLine(double x0, double y0, double x1, double y1)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0))));
        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            Dot((int)Math.Round(x0 + (x1 - x0) * t), (int)Math.Round(y0 + (y1 - y0) * t));
        }
    }

    /// <summary>Turns the dot grid into braille cells; empty cells are left untouched (transparent).</summary>
    private void FlushDots(Canvas c, int x, int y, int w, int h, Func<int, int, Rgb> color)
    {
        for (int r = 0; r < h; r++)
            for (int cx = 0; cx < w; cx++)
            {
                int bits = 0;
                for (int dy = 0; dy < 4; dy++)
                    for (int sub = 0; sub < 2; sub++)
                        if (_dots[(r * 4 + dy) * _dotsW + cx * 2 + sub]) bits |= BrailleBits[dy, sub];
                if (bits != 0) c.Set(x + cx, y + r, (char)(0x2800 + bits), color(cx, r));
            }
    }

    /// <summary>
    /// The spectrum wrapped into a ring that breathes with the beat: bass at the top,
    /// mirrored down both sides to the treble at the bottom, over a dotted inner circle.
    /// </summary>
    private void DrawCircle(Canvas c, int x, int y, int w, int h)
    {
        BeginDots(w, h);
        double cx = _dotsW / 2.0, cy = _dotsH / 2.0;
        // Braille dots are close to square. The ring is round when there's room, and
        // stretches into an oval (up to 4:1) on the short, wide panel instead of shrinking.
        double ry = _dotsH / 2.0 - 1, rx = Math.Min(_dotsW / 2.0 - 1, ry * 4);
        if (ry < 3) { DrawSpectrum(c, x, y, w, h); return; }
        double maxR = 1, r0 = 0.4 + _beat * 0.08;
        int samples = (int)(rx * 12);
        double px = 0, py = 0;
        for (int i = 0; i <= samples; i++)
        {
            double a = i / (double)samples * 2 * Math.PI;
            double f = 1 - Math.Abs(a / Math.PI - 1);
            double r = r0 + Sample(_bars, f * (SpectrumAnalyzer.BandCount - 1)) * (maxR - r0);
            double dx = cx + r * rx * Math.Sin(a), dy = cy - r * ry * Math.Cos(a);
            if (i > 0) DotLine(px, py, dx, dy);
            px = dx; py = dy;
            if (i % 4 == 0) Dot((int)Math.Round(cx + r0 * 0.85 * rx * Math.Sin(a)), (int)Math.Round(cy - r0 * 0.85 * ry * Math.Cos(a)));
        }
        FlushDots(c, x, y, w, h, (col, row) =>
        {
            double ang = Math.Atan2((col * 2 + 1 - cx) / rx, (cy - (row * 4 + 2)) / ry);
            double around = 1 - Math.Abs(ang / Math.PI); // 1 at the top, 0 at the bottom: same axis as the bands
            return Rgb.Lerp(Wheel(1 - around), Fx.White, _beat * 0.35);
        });
    }

    private double _scopePhase;

    /// <summary>
    /// Oscilloscope-style trace. The analyzer has no raw samples, so the wave is
    /// rebuilt from eight band groups as sines whose loudness sets their amplitude.
    /// </summary>
    private void DrawScope(Canvas c, int x, int y, int w, int h)
    {
        BeginDots(w, h);
        double mid = (_dotsH - 1) / 2.0, amp = _dotsH / 2.0 - 1;
        if (Playback.Playing) _scopePhase += _dt;
        Span<double> level = stackalloc double[8];
        for (int k = 0; k < 8; k++) level[k] = Sample(_bars, k * (SpectrumAnalyzer.BandCount - 1) / 7.0);
        double prev = mid;
        for (int dx = 0; dx < _dotsW; dx++)
        {
            double u = dx / (double)Math.Max(1, _dotsW - 1), v = 0;
            for (int k = 0; k < 8; k++)
                v += level[k] * Math.Sin(2 * Math.PI * (1 + k * 1.7) * u * 2 + _scopePhase * (2.5 + k * 3.1) + k);
            // Taper toward the edges so the trace looks anchored, like a scope screen.
            v = Math.Clamp(v / 2.4, -1, 1) * Math.Sin(Math.PI * u);
            double py = mid - v * amp;
            if (dx > 0) DotLine(dx - 1, prev, dx, py); else Dot(0, (int)Math.Round(py));
            prev = py;
        }
        FlushDots(c, x, y, w, h, (col, row) => Rgb.Lerp(Wheel(col / (double)Math.Max(1, w - 1)), Fx.White, _beat * 0.4));
    }

    // Spectrogram history, oldest first; one braille dot column every CascadeStep seconds.
    private readonly List<float[]> _cascade = new();
    private readonly float[] _rawFine = new float[SpectrumAnalyzer.FineCount];
    private double _cascadeClock = double.NegativeInfinity;
    private const double CascadeStep = 0.03;

    // 4x4 ordered-dither thresholds: loudness shows as dot density inside each braille cell.
    private static readonly double[,] Bayer =
    {
        { 0, 8, 2, 10 }, { 12, 4, 14, 6 }, { 3, 11, 1, 9 }, { 15, 7, 13, 5 },
    };

    /// <summary>
    /// Horizontal spectrogram, like Spek or Audacity: time runs right to left, bass at the
    /// bottom, treble at the top. Drawn in braille (2 time × 4 frequency dots per cell) from
    /// 96 log-spaced bands; a cell holds one color, so loudness is shown by dot density
    /// (ordered dithering) and the cell takes the color of its loudest dot.
    /// Freezes while paused; silent cells stay unpainted (transparent).
    /// </summary>
    private void DrawCascade(Canvas c, int x, int y, int w, int h)
    {
        int columns = w * 2, rows = h * 4;
        bool running = Playback.Playing || DemoSpectrum;
        if (!running) _cascadeClock = Now;
        else if (_cascadeClock < Now - 0.5) _cascadeClock = Now; // resume without a burst of catch-up columns
        // Fixed time step regardless of frame rate, so the image scrolls evenly.
        while (running && _cascadeClock + CascadeStep <= Now)
        {
            _cascadeClock += CascadeStep;
            var column = _cascade.Count >= columns && _cascade.Count > 0 ? _cascade[0] : new float[SpectrumAnalyzer.FineCount];
            if (_cascade.Count >= columns && _cascade.Count > 0) _cascade.RemoveAt(0);
            Array.Copy(_rawFine, column, column.Length);
            _cascade.Add(column);
        }
        while (_cascade.Count > columns) _cascade.RemoveAt(0);

        // Value of every dot row for one column: fine bands resampled to the panel height.
        double Level(float[] frame, int dotRow)
        {
            double pos = (rows - 1 - dotRow) / (double)Math.Max(1, rows - 1) * (SpectrumAnalyzer.FineCount - 1);
            int i = (int)pos;
            double f = pos - i, v = i >= frame.Length - 1 ? frame[^1] : frame[i] * (1 - f) + frame[i + 1] * f;
            // Gamma lifts quiet detail; the floor keeps background hiss from speckling the panel.
            v = Math.Pow(Math.Clamp(v, 0, 1), 0.8);
            return v < 0.1 ? 0 : (v - 0.1) / 0.9;
        }

        int first = columns - _cascade.Count; // newest column sits at the right edge
        for (int cx = 0; cx < w; cx++)
            for (int r = 0; r < h; r++)
            {
                int bits = 0;
                double loudest = 0;
                for (int sub = 0; sub < 2; sub++)
                {
                    int col = cx * 2 + sub, index = col - first;
                    if (index < 0) continue;
                    var frame = _cascade[index];
                    for (int dy = 0; dy < 4; dy++)
                    {
                        int dotRow = r * 4 + dy;
                        double v = Level(frame, dotRow);
                        if (v <= 0) continue;
                        loudest = Math.Max(loudest, v);
                        if (v * 16 > Bayer[dotRow & 3, col & 3]) bits |= BrailleBits[dy, sub];
                    }
                }
                if (bits == 0) continue;
                var color = loudest < 0.4 ? Rgb.Lerp(Rgb.Lerp(_palette.Backdrop, Accent, 0.4), Accent, loudest / 0.4)
                    : loudest < 0.75 ? Rgb.Lerp(Accent, _second, (loudest - 0.4) / 0.35)
                    : Rgb.Lerp(_second, Fx.White, (loudest - 0.75) / 0.25 * 0.8);
                c.Set(x + cx, y + r, (char)(0x2800 + bits), color);
            }
    }

    /// <summary>Bars standing on water: the lower third mirrors them, dimmer, with a rippling shimmer.</summary>
    private void DrawReflection(Canvas c, int x, int y, int w, int h)
    {
        int barH = Math.Max(1, (int)Math.Round(h * 0.65)), refH = h - barH;
        DrawSpectrum(c, x, y, w, barH);
        if (refH <= 0) return;
        int count = Math.Max(1, (w + 1) / 2);
        int offsetX = x + (w - (count * 2 - 1)) / 2;
        var low = Rgb.Lerp(_palette.Backdrop, Accent, 0.45);
        int tick = (int)(Now * 7);
        for (int b = 0; b < count; b++)
        {
            double pos = count == 1 ? 0 : b * (SpectrumAnalyzer.BandCount - 1) / (double)(count - 1);
            // The reflection is half as tall as the bar, measured in eighths of a cell.
            int eighths = (int)Math.Round(Sample(_bars, pos) * barH * 8 * 0.5);
            for (int r = 0; r < refH; r++)
            {
                int fill = Math.Clamp(eighths - r * 8, 0, 8);
                if (fill == 0) break;
                if (Fx.Noise(tick + b * 3, r) < 0.16) continue; // ripples break the surface
                char glyph = fill >= 7 ? '█' : fill >= 4 ? '▀' : '▔';
                double depth = (r + 1) / (double)(refH + 1);
                var color = Rgb.Lerp(SpectrumColor(low, 1 - depth * 0.5, b / (double)Math.Max(1, count - 1)), _palette.Backdrop, 0.5 + depth * 0.35);
                c.Set(offsetX + b * 2, y + barH + r, glyph, color);
            }
        }
    }

    /// <summary>Katakana rain: one stream per column, falling faster and with longer tails where its band is loud.</summary>
    private void DrawRain(Canvas c, int x, int y, int w, int h)
    {
        if (_rainY.Length != w)
        {
            _rainY = new double[w];
            _rainSeed = new int[w];
            for (int i = 0; i < w; i++) { _rainY[i] = Fx.Noise(i, 3) * h * 2; _rainSeed[i] = i * 7919; }
        }
        double dt = _dt;
        int tick = (int)(Now * 14);
        for (int col = 0; col < w; col++)
        {
            double v = Sample(_bars, col / (double)Math.Max(1, w - 1) * (SpectrumAnalyzer.BandCount - 1));
            if (Playback.Playing) _rainY[col] += (2 + v * 28) * dt;
            int trail = 1 + (int)(v * h * 1.5);
            if (_rainY[col] - trail > h)
            {
                _rainY[col] = -Fx.Noise(_rainSeed[col], 1) * h;
                _rainSeed[col]++;
            }
            int head = (int)_rainY[col];
            for (int k = 0; k <= trail; k++)
            {
                int r = head - k;
                if (r < 0 || r >= h) continue;
                double t = k / (double)(trail + 1);
                var color = k == 0 ? Fx.White : Rgb.Lerp(v > 0.5 ? _second : Accent, _palette.Backdrop, t);
                c.Set(x + col, y + r, Fx.Glyph(col * 131 + r, k == 0 ? tick : _rainSeed[col]), color, bold: k == 0);
            }
        }
    }

    private void DrawSpectrum(Canvas c, int x, int y, int w, int h)
    {
        // One-column bars with one-column gaps, resampled from the 32 analyzer bands.
        int count = Math.Max(1, (w + 1) / 2);
        int offsetX = x + (w - (count * 2 - 1)) / 2;
        var low = Rgb.Lerp(_palette.Backdrop, Accent, 0.45);
        var peakColor = _fx ? Rgb.Lerp(_second, Fx.White, 0.6) : Rgb.Lerp(Accent, Theme.Text, 0.5);

        for (int b = 0; b < count; b++)
        {
            double pos = count == 1 ? 0 : b * (SpectrumAnalyzer.BandCount - 1) / (double)(count - 1);
            int eighths = (int)Math.Round(Sample(_bars, pos) * h * 8);
            int peakRow = Math.Min(h - 1, (int)(Sample(_peaks, pos) * h));
            int cx = offsetX + b * 2;

            for (int r = 0; r < h; r++)
            {
                int cy = y + h - 1 - r;
                int fill = Math.Clamp(eighths - r * 8, 0, 8);
                if (fill == 0 && r != peakRow) continue;
                if (fill > 0)
                    c.Set(cx, cy, Eighths[fill], SpectrumColor(low, h == 1 ? 1 : r / (double)(h - 1), b / (double)Math.Max(1, count - 1)));
                else if (r == peakRow && peakRow > 0)
                    c.Set(cx, cy, '▁', peakColor);
            }
        }
        if (_fx) DrawSparks(c, x, y, w, h, sx => offsetX + (int)Math.Round(sx * (count - 1)) * 2);
    }

    /// <summary>Bar color: backdrop to accent bottom-to-top; with fx, a gradient through the cover's colors across the width.</summary>
    private Rgb SpectrumColor(Rgb low, double t, double x)
    {
        if (!_fx) return Rgb.Lerp(low, Accent, t);
        // The cover's colors sweep across the width (dominant one at the left/center), brightening toward the top.
        var hue = Wheel(x);
        var color = t < 0.7 ? Rgb.Lerp(Rgb.Lerp(_palette.Backdrop, hue, 0.5), hue, t / 0.7) : Rgb.Lerp(hue, Fx.White, (t - 0.7) / 0.3 * 0.35);
        return Rgb.Lerp(color, Fx.White, _beat * 0.35);
    }

    private void DrawProgress(Canvas c, int x, int y, int w, double fraction)
    {
        if (_fx) { DrawNeonProgress(c, x, y, w, fraction); return; }
        // Half-cell resolution via the right-half heavy line glyph.
        double cells = Math.Clamp(fraction, 0, 1) * w;
        int full = (int)cells;
        for (int i = 0; i < w; i++)
        {
            if (i < full) c.Set(x + i, y, '━', Accent);
            else if (i == full && cells - full >= 0.5) c.Set(x + i, y, '╸', Accent);
            else c.Set(x + i, y, '─', Track);
        }
    }

    /// <summary>Accent-to-second-color gradient with a white-hot head and a light pulse running along the filled part.</summary>
    private void DrawNeonProgress(Canvas c, int x, int y, int w, double fraction)
    {
        double cells = Math.Clamp(fraction, 0, 1) * w;
        int full = (int)cells;
        bool half = cells - full >= 0.5;
        int end = half ? full : full - 1; // last lit column
        bool beating = Now - _lastBeatAt < 3;
        double pulse = Playback.Playing && !beating ? Now % 2.4 / 2.4 * (end + 10) - 5 : double.NaN;
        _ripples.RemoveAll(t0 => (Now - t0) * 45 > end + 4);
        for (int i = 0; i < w; i++)
        {
            if (i > end) { c.Set(x + i, y, '─', Track); continue; }
            var color = Rgb.Lerp(Accent, _second, w <= 1 ? 0 : i / (double)(w - 1));
            double d = Math.Abs(i - pulse);
            foreach (var t0 in _ripples) d = Math.Min(d, Math.Abs(i - (end - (Now - t0) * 45)));
            if (d < 3) color = Rgb.Lerp(color, Fx.White, (1 - d / 3) * 0.7);
            if (i == end) color = Fx.White;
            c.Set(x + i, y, i == full ? '╸' : '━', color);
        }
    }

    private void DrawFooter(Canvas c)
    {
        int x = 1, y = c.Height - 1;
        // Everything lives in the command menu; the footer only says how to open it.
        x += c.Text(x, y, "shift", Accent, bold: true) + 1;
        x += c.Text(x, y, "comandos", Muted) + 3;
        string? context = _menuOpen ? null : _eqOpen ? "esc cierra el ecualizador" : _sourcesOpen ? "esc cierra fuentes" : null;
        if (context is not null) c.Text(x, y, context, _fx ? Rgb.Lerp(Muted, _third, 0.4) : Muted);
        int quitX = c.Width - "q salir".Length - 1;
        c.Text(quitX, y, "q", Accent);
        c.Text(quitX + 2, y, "salir", Muted);
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
