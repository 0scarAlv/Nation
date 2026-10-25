using Nation.Stats;

namespace Nation.Tui;

// Listening stats (`t`): takes the lyrics' place, like the equalizer and the sources panel.
// Everything comes from the journal in listens.db; the panel re-reads it every few seconds while open.
public sealed partial class App
{
    private enum StatsView { Artistas, Generos, Canciones, Horas }

    private bool _statsOpen;
    private StatsView _statsView = StatsView.Artistas;
    private StatsPeriod _statsPeriod = StatsPeriod.Semana;
    private bool _statsAll; // include things flagged as probably not music (browser videos)
    private int _statsScroll;
    private StatsSummary _stats = StatsSummary.Empty;
    private string? _statsError;
    private double _statsReadAt = double.NegativeInfinity;

    private void OpenStats()
    {
        _statsOpen = true;
        _eqOpen = _sourcesOpen = false;
        _statsReadAt = double.NegativeInfinity;
    }

    private void ReadStats()
    {
        _statsReadAt = Now;
        if (_listens is null) { _statsError = "estadísticas desactivadas (no se pudo abrir listens.db)"; return; }
        // The song playing now shows up once its listen is first saved (after 30 s of it).
        try
        {
            _stats = ListenStats.Query(_listens, _statsPeriod, _statsAll);
            _statsError = null;
        }
        catch (Exception e)
        {
            _statsError = "no se pudo leer listens.db: " + e.Message;
        }
    }

    private bool HandleStatsKey(ConsoleKeyInfo key, bool shift)
    {
        int periods = Enum.GetValues<StatsPeriod>().Length;
        switch (key.Key)
        {
            case ConsoleKey.T or ConsoleKey.Escape: _statsOpen = false; return true;
            case ConsoleKey.Tab:
                _statsPeriod = (StatsPeriod)(((int)_statsPeriod + (shift ? periods - 1 : 1)) % periods);
                break;
            case ConsoleKey.A: _statsView = StatsView.Artistas; break;
            case ConsoleKey.G: _statsView = StatsView.Generos; break;
            case ConsoleKey.C: _statsView = StatsView.Canciones; break;
            case ConsoleKey.H: _statsView = StatsView.Horas; break;
            case ConsoleKey.I: _statsAll = !_statsAll; break;
            case ConsoleKey.J or ConsoleKey.DownArrow: _statsScroll++; return true;
            case ConsoleKey.K or ConsoleKey.UpArrow: _statsScroll = Math.Max(0, _statsScroll - 1); return true;
            default: return false;
        }
        _statsScroll = 0;
        ReadStats();
        return true;
    }

    private IEnumerable<MenuItem> StatsMenuItems()
    {
        yield return new("Estadísticas", "tab", "periodo siguiente (shift: anterior)", () => HandleStatsKey(new('\t', ConsoleKey.Tab, false, false, false), false), PeriodName(_statsPeriod));
        yield return new("Estadísticas", "a g c h", "artistas · géneros · canciones · horas", () => { });
        yield return new("Estadísticas", "i", "incluir videos que no parecen música", () => { _statsAll = !_statsAll; ReadStats(); }, YesNo(_statsAll));
        yield return new("Estadísticas", "esc", "cerrar estadísticas", () => _statsOpen = false);
    }

    private static string PeriodName(StatsPeriod p) => p switch
    {
        StatsPeriod.Hoy => "hoy",
        StatsPeriod.Semana => "semana",
        StatsPeriod.Mes => "mes",
        StatsPeriod.Año => "año",
        _ => "todo",
    };

    private static string StatsViewName(StatsView v) => v switch
    {
        StatsView.Artistas => "artistas",
        StatsView.Generos => "géneros",
        StatsView.Canciones => "canciones",
        _ => "horas",
    };

    /// <summary>"42m", "9h 40m", "123h".</summary>
    private static string FormatHours(double hours)
    {
        var minutes = (int)Math.Round(hours * 60);
        if (minutes < 60) return $"{minutes}m";
        if (hours >= 100) return $"{(int)hours}h";
        return $"{minutes / 60}h {minutes % 60:00}m";
    }

    private void DrawStats(Canvas c, int x, int y, int w, int h)
    {
        if (Now - _statsReadAt > 15) ReadStats();
        var s = _stats;
        string title = $"Estadísticas · {PeriodName(_statsPeriod)}" + (_statsAll ? " · con videos" : "");
        Panel(c, x, y, w, h, title, BorderIn(_fourth), Accent);
        int ix = x + 2, iw = w - 4, top = y + 1, rows = h - 2;
        if (rows <= 0 || iw <= 16) return;

        if (_statsError is { } err)
        {
            c.Text(ix, top, err, Muted, maxWidth: iw);
            return;
        }

        // Summary: total time, then the favorites of the period.
        int sx = ix;
        sx += c.Text(sx, top, FormatHours(s.Hours), Accent, bold: true);
        sx += c.Text(sx, top, $"  ·  {s.Plays} reproducciones", Theme.Text, maxWidth: ix + iw - sx);
        if (s.Artists.Count > 0 && ix + iw - sx > 8) sx += c.Text(sx, top, "  ·  " + s.Artists[0].Name, _second, maxWidth: ix + iw - sx);
        if (s.Genres.Count > 0 && ix + iw - sx > 8) c.Text(sx, top, "  ·  " + s.Genres[0].Name, _third, maxWidth: ix + iw - sx);
        if (rows < 2) return;

        // View tabs, and the period tabs on the right.
        int tx = ix;
        foreach (var v in Enum.GetValues<StatsView>())
        {
            bool on = v == _statsView;
            tx += c.Text(tx, top + 1, StatsViewName(v), on ? Accent : Muted, bold: on) + 2;
        }
        string periods = string.Join(" ", Enum.GetValues<StatsPeriod>().Select(PeriodName));
        int px = ix + iw - periods.Length;
        if (px > tx + 2)
            foreach (var p in Enum.GetValues<StatsPeriod>())
            {
                bool on = p == _statsPeriod;
                px += c.Text(px, top + 1, PeriodName(p), on ? _second : Muted, bold: on) + 1;
            }

        int bodyTop = top + 2, bodyRows = rows - 2;
        if (bodyRows <= 0) return;
        if (s.Listens == 0)
        {
            c.Text(ix, bodyTop, _statsPeriod == StatsPeriod.Todo
                ? "todavía no hay escuchas: lo que suene a partir de ahora se va guardando"
                : "sin escuchas en este periodo (tab cambia el periodo)", Muted, maxWidth: iw);
            return;
        }
        if (_statsView == StatsView.Horas) DrawStatsHours(c, ix, bodyTop, iw, bodyRows, s.ByHour);
        else DrawStatsList(c, ix, bodyTop, iw, bodyRows, _statsView switch
        {
            StatsView.Artistas => s.Artists,
            StatsView.Generos => s.Genres,
            _ => s.Songs,
        });
    }

    private void DrawStatsList(Canvas c, int x, int y, int w, int rows, IReadOnlyList<StatsEntry> list)
    {
        _statsScroll = Math.Clamp(_statsScroll, 0, Math.Max(0, list.Count - rows));
        double max = list.Count > 0 ? list[0].Hours : 1;
        int rankW = list.Count >= 10 ? 3 : 2, timeW = 7, playsW = 5;
        int nameW = Math.Clamp(w * 2 / 5, 10, 34);
        int barW = w - rankW - 1 - nameW - 1 - 1 - timeW - playsW;
        for (int r = 0; r < rows && _statsScroll + r < list.Count; r++)
        {
            int i = _statsScroll + r, ry = y + r;
            var e = list[i];
            c.Text(x, ry, $"{i + 1}".PadLeft(rankW - 1), Muted);
            int nx = x + rankW;
            string name = e.Detail is { } detail ? $"{e.Name} · {detail}" : e.Name;
            c.Text(nx, ry, name, i == 0 ? Accent : Theme.Text, bold: i == 0, maxWidth: nameW);
            int bx = nx + nameW + 1;
            if (barW >= 4) DrawStatsBar(c, bx, ry, barW, e.Hours / max, _wheel[i % _wheel.Length]);
            int rx = x + w - playsW - timeW;
            c.Text(rx, ry, FormatHours(e.Hours).PadLeft(timeW - 1), Theme.Text);
            c.Text(rx + timeW, ry, $"{e.Plays}×".PadLeft(playsW - 1), Muted);
        }
    }

    // Horizontal bar with eighth-cell resolution.
    private static void DrawStatsBar(Canvas c, int x, int y, int w, double fraction, Rgb color)
    {
        const string eighths = " ▏▎▍▌▋▊▉";
        int units = (int)Math.Round(Math.Clamp(fraction, 0, 1) * w * 8);
        if (units == 0) units = 1;
        int full = units / 8;
        for (int i = 0; i < full; i++) c.Set(x + i, y, '█', color);
        if (units % 8 > 0 && full < w) c.Set(x + full, y, eighths[units % 8], color);
    }

    /// <summary>Time listened per hour of the day, as vertical bars with eighth-row resolution.</summary>
    private void DrawStatsHours(Canvas c, int x, int y, int w, int rows, double[] byHour)
    {
        const string eighths = " ▁▂▃▄▅▆▇";
        int chartRows = Math.Max(1, rows - 1);
        int colW = Math.Max(1, Math.Min(3, w / 24));
        int cx0 = x + Math.Max(0, (w - colW * 24) / 2);
        double max = Math.Max(byHour.Max(), 1e-9);
        int peak = Array.IndexOf(byHour, byHour.Max());
        for (int hour = 0; hour < 24; hour++)
        {
            int units = (int)Math.Round(byHour[hour] / max * chartRows * 8);
            var color = hour == peak ? Accent : Rgb.Lerp(_third, _second, hour / 23.0);
            for (int k = 0; k < colW; k++)
            {
                int cx = cx0 + hour * colW + k;
                if (colW > 1 && k == colW - 1) continue; // gap between columns
                for (int r = 0; r < chartRows; r++)
                {
                    int cellUnits = units - r * 8;
                    if (cellUnits <= 0) break;
                    c.Set(cx, y + chartRows - 1 - r, cellUnits >= 8 ? '█' : eighths[cellUnits], color);
                }
            }
        }
        if (rows < 2) return;
        int ly = y + chartRows;
        foreach (int hour in new[] { 0, 6, 12, 18, 23 })
            c.Text(cx0 + hour * colW, ly, $"{hour}h", hour == peak ? Accent : Muted);
        // The peak hour, spelled out at the right end of the labels row when there is room.
        string peakText = $"pico {peak}h · {FormatHours(byHour[peak])}";
        int ptx = x + w - peakText.Length;
        if (ptx > cx0 + 23 * colW + 4) c.Text(ptx, ly, peakText, Accent);
    }
}
