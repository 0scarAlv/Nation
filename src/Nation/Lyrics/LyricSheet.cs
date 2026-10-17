using System.Globalization;
using System.Text.RegularExpressions;

namespace Nation.Lyrics;

public sealed record LyricLine(double Time, string Text);

public enum LyricsSource { Embedded, Sidecar, Online }

/// <summary>Either synced (timed lines) or plain text split into lines.</summary>
public sealed class LyricSheet
{
    public IReadOnlyList<LyricLine> Lines { get; }
    public bool Synced { get; }
    public LyricsSource Source { get; }

    private LyricSheet(IReadOnlyList<LyricLine> lines, bool synced, LyricsSource source)
    {
        Lines = lines;
        Synced = synced;
        Source = source;
    }

    private static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    private static readonly Regex OffsetTag = new(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Enhanced LRC per-word stamps: <00:12.34>
    private static readonly Regex WordTag = new(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);

    /// <summary>Parses LRC if it has timestamps, otherwise treats the text as plain lyrics.</summary>
    public static LyricSheet? Parse(string? raw, LyricsSource source)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var synced = new List<LyricLine>();
        double offset = 0;
        foreach (var rawLine in raw.Replace("\r", "").Split('\n'))
        {
            var line = rawLine.Trim();
            if (OffsetTag.Match(line) is { Success: true } om)
            {
                // LRC spec: positive offset means lyrics appear earlier.
                offset = -int.Parse(om.Groups[1].Value, CultureInfo.InvariantCulture) / 1000.0;
                continue;
            }

            // A line may carry several stamps: [00:10.00][01:20.00]Chorus
            var stamps = new List<double>();
            int pos = 0;
            Match m;
            while ((m = TimeTag.Match(line, pos)).Success && m.Index == pos)
            {
                stamps.Add(ToSeconds(m));
                pos += m.Length;
            }
            if (stamps.Count == 0) continue;

            var text = WordTag.Replace(line[pos..], "").Trim();
            foreach (var t in stamps) synced.Add(new LyricLine(t, text));
        }

        if (synced.Count > 0)
        {
            var lines = synced
                .Select(l => l with { Time = Math.Max(0, l.Time + offset) })
                .OrderBy(l => l.Time)
                .ToList();
            // Drop leading blank stamps (common "[00:00.00]" header lines).
            while (lines.Count > 0 && lines[0].Text.Length == 0) lines.RemoveAt(0);
            return lines.Count > 0 ? new LyricSheet(lines, true, source) : null;
        }

        // Plain text: skip metadata-looking tag lines like [ar:Artist].
        var plain = StripGeniusChrome(raw.Replace("\r", "").Split('\n'))
            .Select(l => l.TrimEnd())
            .Where(l => !Regex.IsMatch(l, @"^\[[a-z]+:.*\]$", RegexOptions.IgnoreCase))
            .Select(l => new LyricLine(0, l))
            .ToList();
        while (plain.Count > 0 && plain[^1].Text.Length == 0) plain.RemoveAt(plain.Count - 1);
        while (plain.Count > 0 && plain[0].Text.Length == 0) plain.RemoveAt(0);
        return plain.Count > 0 ? new LyricSheet(plain, false, source) : null;
    }

    /// <summary>
    /// Downloaders that scrape Genius embed its page chrome: "N Contributors",
    /// translation language links, the song blurb ending in "Read More", and
    /// "You might also like"/"Embed" leftovers. Drop all of that.
    /// </summary>
    private static IEnumerable<string> StripGeniusChrome(string[] lines)
    {
        int start = 0;
        int readMore = Array.FindIndex(lines, 0, Math.Min(lines.Length, 25),
            l => l.TrimEnd().EndsWith("Read More", StringComparison.OrdinalIgnoreCase));
        if (readMore >= 0) start = readMore + 1;
        else if (lines.Length > 0 && Regex.IsMatch(lines[0], @"^\d+\s*Contributors", RegexOptions.IgnoreCase)) start = 1;

        foreach (var line in lines.Skip(start))
        {
            if (line.Trim().Equals("You might also like", StringComparison.OrdinalIgnoreCase)) continue;
            yield return Regex.Replace(line, @"\d*Embed$", "");
        }
    }

    public static LyricSheet FromSynced(IEnumerable<LyricLine> lines, LyricsSource source) =>
        new(lines.OrderBy(l => l.Time).ToList(), true, source);

    private static double ToSeconds(Match m)
    {
        int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        double frac = 0;
        if (m.Groups[3].Success)
        {
            var f = m.Groups[3].Value;
            frac = int.Parse(f, CultureInfo.InvariantCulture) / Math.Pow(10, f.Length);
        }
        return min * 60 + sec + frac;
    }

    /// <summary>Index of the line active at time t (-1 before the first line).</summary>
    public int IndexAt(double t)
    {
        if (!Synced) return -1;
        int lo = 0, hi = Lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Lines[mid].Time <= t) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }
}
