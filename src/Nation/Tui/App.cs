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
    private double _glitchAt, _glitchUntil, _nextGlitch = 6;
    private int _glitchSeed;
    private double _bass;
    private const double DecodeTime = 0.7;

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
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>Returns false when the app should quit.</summary>
    public bool HandleKey(ConsoleKeyInfo key)
    {
        var queue = _player.Queue;
        bool shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);
        // Offset keys by character so they work on both US and Spanish layouts.
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
        UpdateFx(now);

        DrawHeader(c);
        int bodyTop = 1, bodyHeight = c.Height - 2;
        int leftWidth = c.Width < 70 ? c.Width / 2 : Math.Clamp(c.Width * 42 / 100, 30, 64);
        DrawQueue(c, 0, bodyTop, leftWidth, bodyHeight, current);

        // Right column: player on top, lyrics below; on short terminals lyrics take it all.
        int rx = leftWidth, rw = c.Width - leftWidth;
        const int playerHeight = 16;
        if (!_showLyrics)
            DrawNowPlaying(c, rx, bodyTop, rw, bodyHeight);
        else if (bodyHeight - playerHeight >= 6)
        {
            DrawNowPlaying(c, rx, bodyTop, rw, playerHeight);
            DrawLyrics(c, rx, bodyTop + playerHeight, rw, bodyHeight - playerHeight, dt);
        }
        else
            DrawLyrics(c, rx, bodyTop, rw, bodyHeight, dt);
        DrawFooter(c);
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
        _bass = Math.Max(Sample(_bars, 0.5), Sample(_bars, 2));
        if (!_fx) return;
        // Ambient bursts only while playing (the loop is at 30 fps then anyway); paused stays idle.
        if (_player.Playing && now >= _nextGlitch)
        {
            TriggerGlitch(now, 0.08 + Fx.Noise(_glitchSeed, 7) * 0.14);
            _nextGlitch = now + 5 + Fx.Noise(_glitchSeed, 11) * 9;
        }
        if (now < _glitchUntil) _animFast = true;
    }

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

    private void DrawHeader(Canvas c)
    {
        // Logo glows with the bass and stutters during glitch bursts.
        var logo = _fx ? Rgb.Lerp(Accent, Fx.White, _bass * 0.75) : Accent;
        int x = c.Text(1, 0, "◆ NATION", logo, bold: true) + 3;
        if (Glitching)
        {
            int tick = GlitchTick;
            for (int i = 3; i < 9; i++)
                if (Fx.Noise(tick, i) < 0.35) c.Set(1 + i, 0, Fx.Glyph(tick, i + 50), Fx.Noise(tick, i + 90) < 0.5 ? _neon : Fx.White, bold: true);
        }
        string state = _player.Idle ? "■ detenido" : _player.Paused ? "❚❚ pausa" : "▶ reproduciendo";
        x += c.Text(x, 0, state, Theme.Muted) + 3;
        if (_player.Shuffled) x += c.Text(x, 0, "⤮ aleatorio", Accent) + 3;
        if (_player.Repeat) c.Text(x, 0, "⟳ repetir", Accent);

        string count = $"{_player.Queue.Count} pistas";
        int right = c.Width - count.Length - 1;
        c.Text(right, 0, count, Theme.Muted);

        // Shade polls every 500 ms while connected, so a quiet API means it's gone.
        if (Remote is { } api)
        {
            var (badge, color) = api.Error is { } err ? ($"API ✗ {err}", Theme.Muted)
                : DateTime.UtcNow - api.LastRequestUtc < TimeSpan.FromSeconds(4) ? ("⇄ shade", Accent)
                : ($"API :{api.Port}", Theme.Muted);
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
        int numWidth = Math.Max(2, queue.Count.ToString().Length);
        for (int row = 0; row < rows; row++)
        {
            int i = _scroll + row;
            if (i >= queue.Count) break;
            var t = queue[i];
            int ry = y + 1 + row, rx = x + 1;
            bool playing = i == current, selected = i == _cursor;

            if (selected) c.FillRow(rx, ry, inner, selection);
            if (selected && _fx) c.Set(rx, ry, '▌', _neon, selection);
            if (playing) DrawMiniEq(c, rx + 1, ry);
            c.Text(rx + 5, ry, (i + 1).ToString().PadLeft(numWidth), Theme.Muted);

            string dur = FormatTime(t.Duration.TotalSeconds);
            int textX = rx + 5 + numWidth + 2;
            int textMax = inner - (textX - rx) - dur.Length - 2;
            int used = c.Text(textX, ry, t.Title, playing ? Accent : Theme.Text, bold: playing, maxWidth: textMax);
            if (used + 3 < textMax)
                c.Text(textX + used, ry, " · " + t.Artist, Theme.Muted, maxWidth: textMax - used);
            if (playing && _fx && _player.Playing)
            {
                // A light band sweeps across the playing title every few seconds.
                double sweep = Now % 4.0 / 0.9 * (used + 6) - 3;
                for (int col = Math.Max(0, (int)sweep - 2); col <= sweep + 2 && col < used; col++)
                    c.Recolor(textX + col, ry, Rgb.Lerp(Accent, Fx.White, 1 - Math.Abs(col - sweep) / 3));
            }
            c.Text(rx + inner - dur.Length - 1, ry, dur, Theme.Muted);
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
        c.Box(x, y, w, h, "Reproduciendo", PanelBorder, Theme.Muted);
        if (_fx && _player.Playing) DrawBorderRunner(c, x, y, w, h);
        int ix = x + 3, iw = w - 6;
        var t = _player.Current;
        if (t is null)
        {
            c.Text(ix, y + 2, "Nada en reproducción", Theme.Muted, maxWidth: iw);
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
        if (!DrawDecoded(c, ix, y + 4, iw, album, Theme.Muted, false, age, row: 2))
            c.Text(ix, y + 4, album, Rgb.Lerp(from, Theme.Muted, fade), maxWidth: iw);
        if (!DrawDecoded(c, ix, y + 5, iw, t.QualityTag, Theme.Muted, false, age, row: 3))
            c.Text(ix, y + 5, t.QualityTag, Rgb.Lerp(from, Theme.Muted, fade), maxWidth: iw);
        if (Glitching)
        {
            GlitchRow(c, ix, y + 2, iw, 0);
            GlitchRow(c, ix, y + 3, iw, 1);
        }

        // Transport block sits at the bottom; the spectrum fills what's between.
        int by = Math.Max(y + h - 5, y + 9);
        int specTop = y + 7, specBottom = by - 2;
        if (specBottom - specTop >= 1) DrawSpectrum(c, ix, specTop, iw, specBottom - specTop + 1);

        double pos = _player.Position, dur = _player.Duration;
        DrawProgress(c, ix, by, iw, dur > 0 ? pos / dur : 0);
        c.Text(ix, by + 1, FormatTime(pos), Theme.Text);
        string total = FormatTime(dur);
        c.Text(ix + iw - total.Length, by + 1, total, Theme.Muted);

        string volLabel = _player.Muted ? "vol mute" : $"vol {_player.Volume,3:0}";
        c.Text(ix, by + 3, volLabel, Theme.Muted);
        int volW = Math.Min(20, iw - 9), volFill = _player.Muted ? 0 : (int)Math.Round(_player.Volume / 100 * volW);
        for (int i = 0; i < volW; i++)
            c.Set(ix + 9 + i, by + 3, i < volFill ? '■' : '·', i < volFill ? Accent : Theme.Track);
    }

    private Rgb PanelBorder => _fx ? Rgb.Lerp(Theme.Border, Accent, 0.3) : Theme.Border;

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
    /// Draws text resolving out of scrambled glyphs, left to right with jitter, each character
    /// flashing white as it locks in. Returns false once the effect is over (caller draws normally).
    /// </summary>
    private bool DrawDecoded(Canvas c, int x, int y, int w, string text, Rgb fg, bool bold, double age, int row)
    {
        if (!_fx || age >= DecodeTime) return false;
        var runes = text.EnumerateRunes().ToArray();
        int n = Math.Max(1, runes.Length - 1), tick = (int)(Now * 20), col = 0;
        double rowDelay = row * 0.05;
        var noiseColor = Rgb.Lerp(_palette.Backdrop, _neon, 0.75);
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
            else if (age >= appearAt && runes[i].Value != ' ')
                for (int k = 0; k < rw; k++)
                    c.Set(x + col + k, y, Fx.Glyph(i * 31 + k + row * 997, tick), noiseColor, bold: bold);
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
        c.Box(x, y, w, h, title, PanelBorder, Theme.Muted);

        int ix = x + 2, iw = w - 4, top = y + 1, rows = h - 2;
        if (rows <= 0 || iw <= 4) return;

        if (sheet is null)
        {
            string msg = _lyricsLoading
                ? $"{Spinner[(int)(Now * 12) % Spinner.Length]} buscando letra…"
                : _player.Current is null ? "" : "sin letra para esta canción";
            CenterText(c, ix, top + rows / 2, iw, msg, Theme.Muted);
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
                for (int col = 0; col < rowFill; col++)
                    c.Recolor(cx + col, ry, Accent);
                if (_fx)
                {
                    // White-hot edge where the karaoke fill is advancing.
                    if (rowFill > 0 && rowFill < width)
                    {
                        c.Recolor(cx + rowFill - 1, ry, Fx.White);
                        if (rowFill > 1) c.Recolor(cx + rowFill - 2, ry, Rgb.Lerp(Accent, Fx.White, 0.5));
                    }
                    if (lineAge < 0.3) ScrambleIn(c, cx, ry, text, width, lineAge / 0.3, wi);
                }
            }
            else
            {
                // Fade with distance from the active line; sung lines a little dimmer.
                int distance = Math.Abs(line - Math.Max(active, 0));
                var baseColor = line < active ? Rgb.Lerp(Theme.Muted, _palette.Backdrop, 0.35) : Theme.Muted;
                var color = Rgb.Lerp(baseColor, _palette.Backdrop, Math.Min(distance, 6) / 9.0);
                CenterText(c, ix, ry, iw, text.Length == 0 ? "♪" : text, color);
            }
        }
    }

    /// <summary>Overlays noise on the not-yet-resolved tail of a freshly activated lyric row.</summary>
    private void ScrambleIn(Canvas c, int x, int y, string text, int width, double progress, int seed)
    {
        _animFast = true;
        int resolved = (int)(progress * width), col = 0, tick = (int)(Now * 20);
        foreach (var rune in text.EnumerateRunes())
        {
            int rw = TextWidth.Of(rune);
            if (rw == 0) continue;
            if (col + rw > width) break;
            if (col >= resolved && rune.Value != ' ')
                for (int k = 0; k < rw; k++)
                    c.Set(x + col + k, y, Fx.Glyph(col + k + seed * 53, tick), _neon, bold: true);
            col += rw;
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
                    c.Set(cx, cy, Eighths[fill], SpectrumColor(low, h == 1 ? 1 : r / (double)(h - 1)));
                else if (r == peakRow && peakRow > 0)
                    c.Set(cx, cy, '▁', peakColor);
            }
        }
    }

    /// <summary>Bottom-to-top bar gradient: backdrop to accent, then (fx) on into the neon counterpart.</summary>
    private Rgb SpectrumColor(Rgb low, double t)
    {
        if (!_fx) return Rgb.Lerp(low, Accent, t);
        return t < 0.6 ? Rgb.Lerp(low, Accent, t / 0.6) : Rgb.Lerp(Accent, _neon, (t - 0.6) / 0.4);
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
            else c.Set(x + i, y, '─', Theme.Track);
        }
    }

    /// <summary>Accent-to-neon gradient with a white-hot head and a light pulse running along the filled part.</summary>
    private void DrawNeonProgress(Canvas c, int x, int y, int w, double fraction)
    {
        double cells = Math.Clamp(fraction, 0, 1) * w;
        int full = (int)cells;
        bool half = cells - full >= 0.5;
        int end = half ? full : full - 1; // last lit column
        double pulse = _player.Playing ? Now % 2.4 / 2.4 * (end + 10) - 5 : double.NaN;
        for (int i = 0; i < w; i++)
        {
            if (i > end) { c.Set(x + i, y, '─', Theme.Track); continue; }
            var color = Rgb.Lerp(Accent, _neon, w <= 1 ? 0 : i / (double)(w - 1));
            double d = Math.Abs(i - pulse);
            if (d < 3) color = Rgb.Lerp(color, Fx.White, (1 - d / 3) * 0.7);
            if (i == end) color = Fx.White;
            c.Set(x + i, y, i == full ? '╸' : '━', color);
        }
    }

    private static readonly (string Key, string Label)[] Hints =
    {
        ("espacio", "play"), ("n/p", "sig/ant"), ("←/→", "±5s"), ("s", "aleatorio"), ("r", "repetir"), ("l", "letra"), ("f", "fx"),
        ("+/-", "vol"), ("m", "mute"), ("enter", "reproducir"), (",/.", "desfase"), ("c", "actual"), ("j/k", "mover"),
    };

    private void DrawFooter(Canvas c)
    {
        int x = 1, y = c.Height - 1;
        // Quit hint is pinned to the right so it never gets cut off.
        int quitX = c.Width - "q salir".Length - 1;
        c.Text(quitX, y, "q", Accent);
        c.Text(quitX + 2, y, "salir", Theme.Muted);
        foreach (var (key, label) in Hints)
        {
            if (x + key.Length + label.Length + 3 > quitX) break;
            x += c.Text(x, y, key, Accent) + 1;
            x += c.Text(x, y, label, Theme.Muted) + 2;
        }
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
