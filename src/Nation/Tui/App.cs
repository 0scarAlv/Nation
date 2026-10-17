using System.Diagnostics;
using System.Text;
using Nation.Art;
using Nation.Audio;
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

    // Cyberpunk effects (toggle with `f`): decode-in text, glitch bursts, neon border runner.
    private bool _fx = true;
    private Rgb _neon = Fx.Neon(ArtPalette.Default.Accent);
    // Third hue (accent rotated -90°) and a hue wheel anchored on the cover, refreshed per frame.
    private Rgb _third = Fx.Shift(ArtPalette.Default.Accent, 270);
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
    private enum Visual { Barras, Espejo, Onda, Lluvia }
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
        // Offset keys by character so they work on both US and Spanish layouts.
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
            case ConsoleKey.Spacebar: _player.TogglePause(); break;
            case ConsoleKey.N: _player.Next(); break;
            case ConsoleKey.P: _player.Previous(); break;
            case ConsoleKey.S: _player.ToggleShuffle(); JumpToCurrent(); break;
            case ConsoleKey.C: JumpToCurrent(); break;
            case ConsoleKey.L: _showLyrics = !_showLyrics; break;
            case ConsoleKey.F: _fx = !_fx; break;
            case ConsoleKey.E: _eqOpen = true; break;
            case ConsoleKey.V:
                _visual = (Visual)(((int)_visual + 1) % Enum.GetValues<Visual>().Length);
                _visualChangedAt = Now;
                break;
            case ConsoleKey.R: _player.ToggleRepeat(); break;
            case ConsoleKey.RightArrow: _player.SeekRelative(shift ? 30 : 5); break;
            case ConsoleKey.LeftArrow: _player.SeekRelative(shift ? -30 : -5); break;
            case ConsoleKey.M: _player.ToggleMute(); break;
            case ConsoleKey.OemPlus or ConsoleKey.Add: _player.AddVolume(5); break;
            case ConsoleKey.OemMinus or ConsoleKey.Subtract: _player.AddVolume(-5); break;
            case ConsoleKey.J or ConsoleKey.DownArrow: MoveCursor(1); break;
            case ConsoleKey.K or ConsoleKey.UpArrow: MoveCursor(-1); break;
            case ConsoleKey.PageDown: MoveCursor(10); break;
            case ConsoleKey.PageUp: MoveCursor(-10); break;
            case ConsoleKey.G when shift: MoveCursor(queue.Count); break;
            case ConsoleKey.G or ConsoleKey.Home: MoveCursor(-queue.Count); break;
            case ConsoleKey.End: MoveCursor(queue.Count); break;
            case ConsoleKey.Enter:
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

        int current = _player.CurrentIndex;
        if (current != _lastIndex)
        {
            _lastIndex = current;
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
        int leftWidth = c.Width < 70 ? c.Width / 2 : Math.Clamp(c.Width * 42 / 100, 30, 64);
        DrawQueue(c, 0, bodyTop, leftWidth, bodyHeight, current);

        // Right column: player on top, lyrics below; on short terminals lyrics take it all.
        int rx = leftWidth, rw = c.Width - leftWidth;
        // Taller terminals give the visualizer more rows.
        int playerHeight = Math.Clamp(bodyHeight / 2 + 2, 16, 24);
        // The EQ draws its own spectrum, so the player gives up rows for taller sliders.
        if (_eqOpen) playerHeight = 14;
        if (!_showLyrics && !_eqOpen)
            DrawNowPlaying(c, rx, bodyTop, rw, bodyHeight);
        else if (bodyHeight - playerHeight >= 8)
        {
            DrawNowPlaying(c, rx, bodyTop, rw, playerHeight);
            DrawLower(c, rx, bodyTop + playerHeight, rw, bodyHeight - playerHeight, dt);
        }
        else
            DrawLower(c, rx, bodyTop, rw, bodyHeight, dt);
        DrawFooter(c);
        if (_fx && now < BootTime) DrawBoot(c, now / BootTime);
        PublishRemote();
        Media?.Update(_player.Current, _player.Idle, _player.Paused, _player.Shuffled, _player.Repeat, _player.Position, _player.Duration);

        // Playing always animates (spectrum, progress, karaoke); otherwise only while something is settling.
        NextFrameDelay = _player.Playing || _animFast ? FastFrame : _animSlow ? SlowFrame : IdleFrame;
    }

    // ── animation state ────────────────────────────────────────────────

    private void UpdatePalette(double dt)
    {
        var track = _player.Current;
        if (!ReferenceEquals(track, _paletteFor))
        {
            _paletteFor = track;
            if (track is null) _targetPalette = ArtPalette.Default;
            else
            {
                var requested = track;
                _ = Task.Run(async () =>
                {
                    var p = await CoverArt.PaletteForAsync(requested.Path);
                    // Ignore results that arrive after the user already skipped ahead.
                    if (ReferenceEquals(_paletteFor, requested)) _targetPalette = p;
                });
            }
        }

        // Exponential ease (~0.6 s to settle), frame-rate independent.
        double k = 1 - Math.Exp(-dt / 0.15);
        _palette = new ArtPalette(
            Rgb.Lerp(_palette.Accent, _targetPalette.Accent, k),
            Rgb.Lerp(_palette.Backdrop, _targetPalette.Backdrop, k));
        // Byte steps round to zero near the target; snap once the gap is imperceptible.
        if (Close(_palette.Accent, _targetPalette.Accent) && Close(_palette.Backdrop, _targetPalette.Backdrop))
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
        _neon = Fx.Neon(Accent);
        _third = Fx.Shift(Accent, 270);
        for (int i = 0; i < _wheel.Length; i++) _wheel[i] = Fx.Shift(Accent, i * 300.0 / (_wheel.Length - 1));
        _bass = Math.Max(Sample(_bars, 0.5), Sample(_bars, 2));
        if (!_fx) return;
        // Ambient bursts only while playing (the loop is at 30 fps then anyway); paused stays idle.
        if (_player.Playing && now >= _nextGlitch)
        {
            TriggerGlitch(now, 0.08 + Fx.Noise(_glitchSeed, 7) * 0.14);
            _nextGlitch = now + 10 + Fx.Noise(_glitchSeed, 11) * 10;
        }
        if (now < _glitchUntil) _animFast = true;
    }

    private void UpdateBeat(double now, double dt)
    {
        bool live = _player.Playing && DateTime.UtcNow - _spectrum.LastFrameUtc < TimeSpan.FromMilliseconds(250);
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
                _lastBeatAt = now;
                _beat = Math.Max(_beat, strength);
                if (_ripples.Count < 4) _ripples.Add(now);
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

    /// <summary>Beat envelope stepped into a few levels so pulsing borders don't repaint every row every frame.</summary>
    private double BeatStep => Math.Round(_beat * 4) / 4;
    private double BassStep => Math.Round(_bass * 4) / 4;

    private void UpdateSpectrum(double dt)
    {
        // Loopback hears every app; only capture while Nation itself is playing.
        bool playing = _player.Playing;
        if (playing) _spectrum.Start();
        bool live = playing && DateTime.UtcNow - _spectrum.LastFrameUtc < TimeSpan.FromMilliseconds(250);
        var target = _spectrum.Latest;
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

    /// <summary>Color from the cover-anchored hue wheel, x in [0, 1].</summary>
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
                if (Fx.Noise(tick, i) < 0.35) c.Recolor(1 + i, 0, Fx.Noise(tick, i + 90) < 0.5 ? _neon : Fx.Cyan);
        }
        string state = _player.Idle ? "■ detenido" : _player.Paused ? "❚❚ pausa" : "▶ reproduciendo";
        if (state != _stateText) { _stateText = state; _stateChangedAt = Now; }
        double stateAge = Now - _stateChangedAt;
        if (stateAge < DecodeTime) _animFast = true;
        var stateColor = !_fx ? Muted : _player.Idle ? Muted : _player.Paused ? Fx.Amber : _neon;
        if (!DrawDecoded(c, x, 0, TextWidth.Of(state), state, stateColor, false, stateAge, row: 5)) c.Text(x, 0, state, stateColor);
        x += TextWidth.Of(state) + 3;
        if (_player.Shuffled) x += c.Text(x, 0, "⤮ aleatorio", Accent) + 3;
        if (_player.Repeat) c.Text(x, 0, "⟳ repetir", _fx ? _third : Accent);

        string count = $"{_player.Queue.Count} pistas";
        int right = c.Width - count.Length - 1;
        c.Text(right, 0, count, _fx ? Rgb.Lerp(Muted, _third, 0.5) : Muted);

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
        var track = _player.Current;
        string status = _player.Idle || track is null ? "stopped" : _player.Paused ? "paused" : "playing";
        Remote.Snapshot = new RemoteSnapshot(
            status, _player.Shuffled, _player.Repeat,
            (long)(_player.Position * 1000), track,
            ReferenceEquals(_lyricsFor, track) ? _lyricsText : null);
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
        c.Box(x, y, w, h, _player.Shuffled ? "Cola ⤮" : "Cola", Accent, Accent);

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
            if (selected && _fx) c.Set(rx, ry, '▌', _neon, selection);
            else if (_fx && i == _prevCursor && trail < 1) c.Set(rx, ry, '▌', Rgb.Lerp(_neon, _palette.Backdrop, trail));
            if (playing) DrawMiniEq(c, rx + 1, ry);
            c.Text(rx + 5, ry, (i + 1).ToString().PadLeft(numWidth), _fx ? Rgb.Lerp(Muted, _neon, 0.35) : Muted);

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
            c.Text(rx + inner - dur.Length - 1, ry, dur, _fx ? Rgb.Lerp(Muted, _neon, 0.25) : Muted);
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
        string title = notice ? $"Reproduciendo · visual: {_visual.ToString().ToLowerInvariant()}" : "Reproduciendo";
        var border = _fx ? Rgb.Lerp(BorderIn(_neon), _neon, BeatStep * 0.8) : PanelBorder;
        c.Box(x, y, w, h, title, border, notice ? Accent : Muted);
        if (_fx && _player.Playing) DrawBorderRunner(c, x, y, w, h);
        int ix = x + 3, iw = w - 6;
        var t = _player.Current;
        if (t is null)
        {
            c.Text(ix, y + 2, "Nada en reproducción", Muted, maxWidth: iw);
            return;
        }

        // Metadata enters on every track change: decoded from noise (fx) or faded in from the backdrop tint.
        double age = Now - _trackChangedAt;
        double fade = _fx ? 1 : Math.Clamp(age / 0.45, 0, 1);
        if (fade < 1 || (_fx && age < DecodeTime + 0.1)) _animFast = true;
        fade = 1 - Math.Pow(1 - fade, 3); // ease-out
        var from = _palette.Backdrop;

        DrawMarquee(c, ix, y + 2, iw, t.Title, Rgb.Lerp(from, Theme.Text, fade), bold: true, age, row: 0);
        DrawMarquee(c, ix, y + 3, iw, t.Artist, Rgb.Lerp(from, Accent, fade), bold: false, age, row: 1);
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

        double pos = _player.Position, dur = _player.Duration;
        DrawProgress(c, ix, by, iw, dur > 0 ? pos / dur : 0);
        c.Text(ix, by + 1, FormatTime(pos), Theme.Text);
        string total = FormatTime(dur);
        c.Text(ix + iw - total.Length, by + 1, total, Muted);

        string volLabel = _player.Muted ? "vol mute" : $"vol {_player.Volume,3:0}";
        c.Text(ix, by + 3, volLabel, Muted);
        // The bar glides to the new level and flashes after a change.
        double vol = _player.Muted ? 0 : _player.Volume;
        if (vol != _volTarget) { if (_volTarget >= 0) _volChangedAt = Now; _volTarget = vol; }
        _volShown = _volShown < 0 ? vol : _volShown + (vol - _volShown) * 0.35;
        if (Math.Abs(_volShown - vol) < 0.5) _volShown = vol; else _animFast = true;
        double flash = Math.Clamp(1 - (Now - _volChangedAt) / 0.4, 0, 1);
        if (flash > 0) _animFast = true;
        int volW = Math.Min(20, iw - 9), volFill = (int)Math.Round(_volShown / 100 * volW);
        var volColor = Rgb.Lerp(Accent, Fx.White, flash * 0.8);
        for (int i = 0; i < volW; i++)
            c.Set(ix + 9 + i, by + 3, i < volFill ? '■' : '·', i >= volFill ? Track : _fx ? Rgb.Lerp(Rgb.Lerp(Accent, _neon, i / (double)Math.Max(1, volW - 1)), Fx.White, flash * 0.8) : volColor);
        DrawLevelMeter(c, ix + 9 + volW + 3, by + 3, iw - (9 + volW + 3));
    }

    private const string Horizontal = " ▏▎▍▌▋▊▉█";

    /// <summary>Loudness meter with eighth-cell resolution, accent→neon→white, and a falling peak marker.</summary>
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
            var color = t < 0.7 ? Rgb.Lerp(Accent, _neon, t / 0.7) : Rgb.Lerp(_neon, Fx.White, (t - 0.7) / 0.3);
            int fill = Math.Clamp((int)Math.Round((cells - i) * 8), 0, 8);
            if (fill > 0) c.Set(mx + i, y, Horizontal[fill], color);
            else if (i == peak && _levelPeak > 0.01) c.Set(mx + i, y, '▏', Fx.White);
            else c.Set(mx + i, y, '·', Track);
        }
    }

    private Rgb PanelBorder => _fx ? Rgb.Lerp(Theme.Border, Accent, 0.3) : Theme.Border;
    private Rgb BorderIn(Rgb hue) => _fx ? Rgb.Lerp(Theme.Border, hue, 0.4) : Theme.Border;

    /// <summary>A bright packet with a fading tail that circles the panel border.</summary>
    private void DrawBorderRunner(Canvas c, int x, int y, int w, int h)
    {
        int perimeter = 2 * (w - 1) + 2 * (h - 1);
        if (perimeter <= 0) return;
        const double lap = 4.0; // seconds per loop
        const int tail = 12;
        int head = (int)(Now % lap / lap * perimeter);
        for (int k = 0; k < tail; k++)
        {
            int p = ((head - k) % perimeter + perimeter) % perimeter;
            var (cx, cy) =
                p < w - 1 ? (x + p, y) :
                p < w - 1 + h - 1 ? (x + w - 1, y + p - (w - 1)) :
                p < 2 * (w - 1) + h - 1 ? (x + w - 1 - (p - (w - 1 + h - 1)), y + h - 1) :
                (x, y + h - 1 - (p - (2 * (w - 1) + h - 1)));
            double t = 1 - k / (double)tail;
            c.Recolor(cx, cy, k == 0 ? Fx.White : Rgb.Lerp(PanelBorder, _neon, t * t));
        }
    }

    /// <summary>
    /// Draws text lighting up left to right with jitter: each character appears as a dim neon
    /// ghost, then flashes white as it locks in. Returns false once the effect is over (caller draws normally).
    /// </summary>
    private bool DrawDecoded(Canvas c, int x, int y, int w, string text, Rgb fg, bool bold, double age, int row)
    {
        if (!_fx || age >= DecodeTime) return false;
        var runes = text.EnumerateRunes().ToArray();
        int n = Math.Max(1, runes.Length - 1), col = 0;
        double rowDelay = row * 0.05;
        var ghost = Rgb.Lerp(_palette.Backdrop, _neon, 0.55);
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

    /// <summary>Glitch burst on one row: horizontal tear plus a few neon-tinted cells.</summary>
    private void GlitchRow(Canvas c, int x, int y, int w, int row)
    {
        int tick = GlitchTick;
        if (Fx.Noise(tick, row) < 0.6)
            c.ShiftRow(x, y, w, (int)(Fx.Noise(tick, row + 13) * 5) - 2);
        for (int col = 0; col < w; col++)
        {
            double r = Fx.Noise(tick, col + row * 131);
            if (r < 0.06) c.Recolor(x + col, y, _neon);
            else if (r < 0.10) c.Recolor(x + col, y, Fx.Cyan);
        }
    }

    private void DrawLower(Canvas c, int x, int y, int w, int h, double dt)
    {
        if (_eqOpen) DrawEqualizer(c, x, y, w, h, dt);
        else DrawLyrics(c, x, y, w, h, dt);
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
        var border = _fx ? Rgb.Lerp(BorderIn(_third), _neon, Math.Clamp(1 - (Now - _eqChangedAt) / 0.3, 0, 1) * 0.8) : PanelBorder;
        c.Box(x, y, w, h, title, border, on ? Accent : Muted);

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
                var color = !on ? Muted : u == halves && selected ? Fx.White : Rgb.Lerp(Accent, _neon, t);
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
        var track = _player.Current;
        if (ReferenceEquals(track, _lyricsFor)) return;

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
        c.Box(x, y, w, h, title, BorderIn(_third), Muted);

        int ix = x + 2, iw = w - 4, top = y + 1, rows = h - 2;
        if (rows <= 0 || iw <= 4) return;

        if (sheet is null)
        {
            string msg = _lyricsLoading
                ? $"{Spinner[(int)(Now * 12) % Spinner.Length]} buscando letra…"
                : _player.Current is null ? "" : "sin letra para esta canción";
            CenterText(c, ix, top + rows / 2, iw, msg, Muted);
            return;
        }

        var wrapped = Wrap(sheet, iw);
        if (!sheet.Synced)
        {
            // No timing: scroll proportionally to playback so the text roughly keeps up.
            double dur = _player.Duration;
            int maxScroll = Math.Max(0, wrapped.Count - rows);
            double target = dur > 0 ? Math.Clamp(_player.Position / dur, 0, 1) * maxScroll : 0;
            if (Math.Abs(target - _lyricScroll) > 0.02) _animFast = true;
            _lyricScroll += (target - _lyricScroll) * (1 - Math.Exp(-dt / 0.3));
            int first = Math.Clamp((int)Math.Round(_lyricScroll), 0, maxScroll);
            for (int r = 0; r < rows && first + r < wrapped.Count; r++)
                CenterText(c, ix, top + r, iw, wrapped[first + r].Text, Theme.Text);
            return;
        }

        double pos = _player.Position + _lyricOffset;
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
                    c.Recolor(cx + col, ry, _fx ? Rgb.Lerp(Rgb.Lerp(Accent, _neon, col / (double)Math.Max(1, width - 1)), Fx.White, _beat * 0.4) : sung);
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
        var ghost = Rgb.Lerp(_palette.Backdrop, _neon, 0.4);
        for (int col = revealed; col < width; col++) c.Recolor(x + col, y, ghost);
        if (revealed < width) c.Recolor(x + revealed, y, Fx.White);
    }

    /// <summary>Startup: a diagonal CRT wipe reveals the UI, its leading edge a band of white-to-neon light.</summary>
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
                else if (d > front - 6) c.Recolor(x, y, Rgb.Lerp(Fx.White, _neon, (front - d) / 6));
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
            if (_player.Playing) _rainY[col] += (2 + v * 28) * dt;
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
                var color = k == 0 ? Fx.White : Rgb.Lerp(v > 0.5 ? _neon : Accent, _palette.Backdrop, t);
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
        var peakColor = _fx ? Rgb.Lerp(_neon, Fx.White, 0.6) : Rgb.Lerp(Accent, Theme.Text, 0.5);

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
                if (fill > 0)
                    c.Set(cx, cy, Eighths[fill], SpectrumColor(low, h == 1 ? 1 : r / (double)(h - 1), b / (double)Math.Max(1, count - 1)));
                else if (r == peakRow && peakRow > 0)
                    c.Set(cx, cy, '▁', peakColor);
            }
        }
    }

    /// <summary>Bar color: backdrop to accent bottom-to-top; with fx, a cover-anchored hue sweep across the width.</summary>
    private Rgb SpectrumColor(Rgb low, double t, double x)
    {
        if (!_fx) return Rgb.Lerp(low, Accent, t);
        // Hue sweeps across the width (cover hue at the left/center), brightening toward the top.
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

    /// <summary>Accent-to-neon gradient with a white-hot head and a light pulse running along the filled part.</summary>
    private void DrawNeonProgress(Canvas c, int x, int y, int w, double fraction)
    {
        double cells = Math.Clamp(fraction, 0, 1) * w;
        int full = (int)cells;
        bool half = cells - full >= 0.5;
        int end = half ? full : full - 1; // last lit column
        bool beating = Now - _lastBeatAt < 3;
        double pulse = _player.Playing && !beating ? Now % 2.4 / 2.4 * (end + 10) - 5 : double.NaN;
        _ripples.RemoveAll(t0 => (Now - t0) * 45 > end + 4);
        for (int i = 0; i < w; i++)
        {
            if (i > end) { c.Set(x + i, y, '─', Track); continue; }
            var color = Rgb.Lerp(Accent, _neon, w <= 1 ? 0 : i / (double)(w - 1));
            double d = Math.Abs(i - pulse);
            foreach (var t0 in _ripples) d = Math.Min(d, Math.Abs(i - (end - (Now - t0) * 45)));
            if (d < 3) color = Rgb.Lerp(color, Fx.White, (1 - d / 3) * 0.7);
            if (i == end) color = Fx.White;
            c.Set(x + i, y, i == full ? '╸' : '━', color);
        }
    }

    private static readonly (string Key, string Label)[] EqHints =
    {
        ("←/→", "banda"), ("↑/↓", "ganancia"), ("shift", "±3 dB"), ("0", "plano"), ("tab", "preset"), ("b", "on/off"),
        ("e", "cerrar"), ("espacio", "play"), ("n/p", "sig/ant"),
    };

    private static readonly (string Key, string Label)[] Hints =
    {
        ("espacio", "play"), ("n/p", "sig/ant"), ("←/→", "±5s"), ("s", "aleatorio"), ("r", "repetir"), ("l", "letra"), ("e", "ecualizador"), ("f", "fx"), ("v", "visual"),
        ("+/-", "vol"), ("m", "mute"), ("enter", "reproducir"), (",/.", "desfase"), ("c", "actual"), ("j/k", "mover"),
    };

    private void DrawFooter(Canvas c)
    {
        int x = 1, y = c.Height - 1;
        // Quit hint is pinned to the right so it never gets cut off.
        int quitX = c.Width - "q salir".Length - 1;
        c.Text(quitX, y, "q", Accent);
        c.Text(quitX + 2, y, "salir", Muted);
        int hint = 0;
        foreach (var (key, label) in _eqOpen ? EqHints : Hints)
        {
            if (x + key.Length + label.Length + 3 > quitX) break;
            var keyColor = !_fx ? Accent : (hint++ % 3) switch { 0 => Accent, 1 => _neon, _ => _third };
            x += c.Text(x, y, key, keyColor) + 1;
            x += c.Text(x, y, label, Muted) + 2;
        }
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
