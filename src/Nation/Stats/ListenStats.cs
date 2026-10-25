namespace Nation.Stats;

public enum StatsPeriod { Hoy, Semana, Mes, Año, Todo }

/// <summary>One ranked row: an artist, genre or song with its listening time.</summary>
public sealed record StatsEntry(string Name, string? Detail, double Hours, int Plays);

public sealed record StatsSummary(
    StatsPeriod Period, double Hours, int Listens, int Plays,
    IReadOnlyList<StatsEntry> Artists, IReadOnlyList<StatsEntry> Genres, IReadOnlyList<StatsEntry> Songs,
    double[] ByHour)
{
    public static readonly StatsSummary Empty = new(StatsPeriod.Semana, 0, 0, 0, [], [], [], new double[24]);
}

/// <summary>
/// Reads the journal for one period and ranks it. Rules about what a "play" is live
/// here, at query time, so they can change without touching stored listens.
/// </summary>
public static class ListenStats
{
    /// <summary>Last.fm's rule: half the song or four minutes; songs without a length need 30 s.</summary>
    public static bool IsPlay(long playedMs, long durationMs) =>
        durationMs > 0 ? playedMs >= Math.Min(durationMs / 2, 240_000) : playedMs >= 30_000;

    public static DateTime PeriodStart(StatsPeriod period, DateTime nowLocal) => period switch
    {
        StatsPeriod.Hoy => nowLocal.Date,
        // Weeks start on Monday.
        StatsPeriod.Semana => nowLocal.Date.AddDays(-(((int)nowLocal.DayOfWeek + 6) % 7)),
        StatsPeriod.Mes => new DateTime(nowLocal.Year, nowLocal.Month, 1),
        StatsPeriod.Año => new DateTime(nowLocal.Year, 1, 1),
        _ => DateTime.MinValue,
    };

    /// <param name="includeNonMusic">Also count browser videos and other things flagged as probably not music.</param>
    public static StatsSummary Query(ListenLog log, StatsPeriod period, bool includeNonMusic, int top = 50)
    {
        var start = PeriodStart(period, DateTime.Now);
        long since = start == DateTime.MinValue ? 0 : new DateTimeOffset(start).ToUnixTimeMilliseconds();

        using var cmd = log.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT artist, title, genre, played_ms, duration_ms, started_at, track_key
            FROM listen WHERE started_at >= $since AND (music = 1 OR $all = 1)
            """;
        cmd.Parameters.AddWithValue("$since", since);
        cmd.Parameters.AddWithValue("$all", includeNonMusic ? 1 : 0);

        var artists = new Dictionary<string, Acc>(StringComparer.OrdinalIgnoreCase);
        var genres = new Dictionary<string, Acc>(StringComparer.OrdinalIgnoreCase);
        var songs = new Dictionary<string, Acc>();
        var byHour = new double[24];
        double totalMs = 0;
        int listens = 0, plays = 0;

        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                string artist = r.GetString(0), title = r.GetString(1);
                string? genre = r.IsDBNull(2) ? null : r.GetString(2);
                long played = r.GetInt64(3), duration = r.GetInt64(4);
                bool play = IsPlay(played, duration);
                listens++;
                if (play) plays++;
                totalMs += played;
                var hour = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)).LocalDateTime.Hour;
                byHour[hour] += played / 3_600_000.0;

                Add(artists, artist, null, played, play);
                // A song tagged "Alternativo, Indie Rock" counts in full for each of its genres.
                foreach (var g in SplitGenres(genre)) Add(genres, g, null, played, play);
                Add(songs, r.GetString(6), (title, artist), played, play);
            }
        }

        return new StatsSummary(period, totalMs / 3_600_000.0, listens, plays,
            Rank(artists, top), Rank(genres, top), Rank(songs, top), byHour);
    }

    private sealed class Acc
    {
        public string Name = "";
        public string? Detail;
        public double Ms;
        public int Plays;
    }

    // Songs are keyed by track_key but shown by title; the first spelling seen wins.
    private static void Add(Dictionary<string, Acc> map, string key, (string Name, string Detail)? label, long ms, bool play)
    {
        if (!map.TryGetValue(key, out var acc))
            map[key] = acc = new Acc { Name = label?.Name ?? key, Detail = label?.Detail };
        acc.Ms += ms;
        if (play) acc.Plays++;
    }

    private static List<StatsEntry> Rank(Dictionary<string, Acc> map, int top) => map.Values
        .OrderByDescending(a => a.Ms)
        .Take(top)
        .Select(a => new StatsEntry(a.Name, a.Detail, a.Ms / 3_600_000.0, a.Plays))
        .ToList();

    // Deezer separates genres with commas; "Rap/Hip Hop" is one genre, so '/' is not a separator.
    public static IEnumerable<string> SplitGenres(string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre)) yield break;
        foreach (var part in genre.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return RepairGenre(part);
    }

    // SpotiFLAC writes some Deezer genres with U+FFFD where the accent was ("M�sica Asi�tica").
    // A broken word is matched against known spellings that agree on every other letter.
    private static readonly string[] Accented =
    [
        "Música", "Asiática", "Películas", "Clásica", "Electrónica", "Brasileña", "Africana", "Árabe",
        "Latinoamericana", "Sudafricana", "Japonesa", "Coreana", "Infantil", "Canción", "Bandas", "Sonoras",
    ];

    public static string RepairGenre(string genre)
    {
        if (!genre.Contains('�')) return genre;
        var words = genre.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (!w.Contains('�')) continue;
            var fix = Accented.FirstOrDefault(a => a.Length == w.Length &&
                a.Zip(w).All(p => p.Second == '�' || char.ToLowerInvariant(p.First) == char.ToLowerInvariant(p.Second)));
            words[i] = fix ?? w.Replace('�', '?');
        }
        return string.Join(' ', words);
    }
}
