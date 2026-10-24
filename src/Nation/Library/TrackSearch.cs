using System.Runtime.CompilerServices;

namespace Nation.Library;

/// <summary>
/// Type-to-search over titles, artists and albums. Every word typed must appear
/// somewhere (so each extra word narrows the results), case- and accent-insensitive;
/// results are ranked so word starts and title hits come first. Only the shown list
/// is filtered: the queue never shrinks to the results.
/// </summary>
public static class TrackSearch
{
    private sealed record Keys(string Title, string Artist, string Album);

    // Folded once per track; the library doesn't change while Nation runs.
    private static readonly ConditionalWeakTable<Track, Keys> Cache = new();

    private static Keys KeysOf(Track t) => Cache.GetValue(t, x => new Keys(Fold(x.Title), Fold(x.Artist), Fold(x.Album)));

    /// <summary>Indices of the matching tracks, best first (ties keep list order).</summary>
    public static List<int> Rank(IReadOnlyList<Track> tracks, string query)
    {
        var tokens = Tokens(query);
        var hits = new List<(int Index, int Score)>();
        if (tokens.Length == 0)
        {
            for (int i = 0; i < tracks.Count; i++) hits.Add((i, 0));
            return hits.Select(h => h.Index).ToList();
        }
        string whole = string.Join(' ', tokens);
        for (int i = 0; i < tracks.Count; i++)
        {
            var k = KeysOf(tracks[i]);
            int score = 0;
            foreach (var token in tokens)
            {
                int best = Math.Max(FieldScore(k.Title, token) * 3, Math.Max(FieldScore(k.Artist, token) * 2, FieldScore(k.Album, token)));
                if (best == 0) { score = -1; break; }
                score += best;
            }
            if (score < 0) continue;
            // The whole query as typed, inside the title, beats the same words scattered around.
            if (k.Title == whole) score += 60;
            else if (k.Title.StartsWith(whole, StringComparison.Ordinal)) score += 35;
            else if (k.Title.Contains(whole, StringComparison.Ordinal)) score += 15;
            hits.Add((i, score));
        }
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Index).Select(h => h.Index).ToList();
    }

    /// <summary>Indices of the matching names (playlists), best first.</summary>
    public static List<int> RankNames(IReadOnlyList<string> names, string query)
    {
        var tokens = Tokens(query);
        var hits = new List<(int Index, int Score)>();
        for (int i = 0; i < names.Count; i++)
        {
            string name = Fold(names[i]);
            int score = 0;
            foreach (var token in tokens)
            {
                int s = FieldScore(name, token);
                if (s == 0) { score = -1; break; }
                score += s;
            }
            if (score >= 0) hits.Add((i, score));
        }
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Index).Select(h => h.Index).ToList();
    }

    /// <summary>10 when the token starts a word, 5 anywhere else inside, 0 when absent.</summary>
    private static int FieldScore(string field, string token)
    {
        int best = 0;
        for (int at = field.IndexOf(token, StringComparison.Ordinal); at >= 0; at = field.IndexOf(token, at + 1, StringComparison.Ordinal))
        {
            if (at == 0 || !char.IsLetterOrDigit(field[at - 1])) return 10;
            best = 5;
        }
        return best;
    }

    private static string[] Tokens(string query) => Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Which characters of `text` match a word of the query, for highlighting. Folding is
    /// one char to one char, so positions line up with the original string.
    /// </summary>
    public static bool[] Matches(string text, string query)
    {
        var mask = new bool[text.Length];
        string folded = Fold(text);
        foreach (var token in Tokens(query))
            for (int at = folded.IndexOf(token, StringComparison.Ordinal); at >= 0; at = folded.IndexOf(token, at + 1, StringComparison.Ordinal))
                for (int i = at; i < at + token.Length; i++) mask[i] = true;
        return mask;
    }

    /// <summary>Lowercase without accents ("Canción" → "cancion"), same length as the input.</summary>
    public static string Fold(string s) => string.Create(s.Length, s, (span, src) =>
    {
        for (int i = 0; i < src.Length; i++) span[i] = FoldChar(char.ToLowerInvariant(src[i]));
    });

    private static char FoldChar(char c) => c switch
    {
        'à' or 'á' or 'â' or 'ã' or 'ä' or 'å' or 'ā' or 'ă' or 'ą' => 'a',
        'ç' or 'ć' or 'č' => 'c',
        'è' or 'é' or 'ê' or 'ë' or 'ē' or 'ę' or 'ě' => 'e',
        'ì' or 'í' or 'î' or 'ï' or 'ī' => 'i',
        'ñ' or 'ń' or 'ň' => 'n',
        'ò' or 'ó' or 'ô' or 'õ' or 'ö' or 'ø' or 'ō' or 'ő' => 'o',
        'ù' or 'ú' or 'û' or 'ü' or 'ū' or 'ů' or 'ű' => 'u',
        'ý' or 'ÿ' => 'y',
        'š' or 'ś' => 's',
        'ž' or 'ź' or 'ż' => 'z',
        'ł' => 'l',
        'ř' => 'r',
        // Typographic apostrophes and dashes match the plain ones people type.
        '’' or '‘' or '´' or '`' => '\'',
        '–' or '—' => '-',
        _ => c,
    };
}
