using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Nation.Stats;

/// <summary>A listen as it travels between devices (camelCase JSON, same fields as the journal row).</summary>
public sealed record ListenDto(
    string Id, string Listener, string Device, string Source,
    long StartedAt, long EndedAt, long PlayedMs, long DurationMs,
    string Title, string Artist, string Album, string? Genre, string? Isrc, string? Path,
    string TrackKey, bool Music, long UpdatedAt);

public sealed record DeviceDto(string Id, string Name, string Kind);

/// <summary>
/// The journal's side of syncing with other devices (Shade's phone player, through
/// the Shade agent): who owns it, what changed since a point, and merging in rows
/// sent by a paired device. Runs on the API's threads, so every call opens its own
/// connection instead of sharing the UI thread's (WAL lets them coexist).
/// </summary>
public sealed class ListenSync
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ListenLog _log;
    private readonly string _path;
    public Profile Owner => _log.Profile;

    public ListenSync(ListenLog log)
    {
        _log = log;
        _path = log.Path;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 3000";
        cmd.ExecuteNonQuery();
        return db;
    }

    /// <summary>
    /// The owner's listens changed after the cursor (updated_at on Nation's clock, then id), oldest
    /// first. The id breaks ties: a merge writes many rows with the same updated_at, and a page may
    /// end in the middle of them.
    /// </summary>
    public (List<ListenDto> Listens, bool More) Changed(long since, string sinceId, int limit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, listener, device, source, started_at, ended_at, played_ms, duration_ms,
                   title, artist, album, genre, isrc, path, track_key, music, updated_at
            FROM listen WHERE listener = $listener AND (updated_at > $since OR (updated_at = $since AND id > $sinceId))
            ORDER BY updated_at, id LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$listener", Owner.ListenerId);
        cmd.Parameters.AddWithValue("$since", since);
        cmd.Parameters.AddWithValue("$sinceId", sinceId);
        cmd.Parameters.AddWithValue("$limit", limit + 1);
        var rows = new List<ListenDto>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                rows.Add(new ListenDto(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7),
                    r.GetString(8), r.GetString(9), r.GetString(10),
                    r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13),
                    r.GetString(14), r.GetInt64(15) != 0, r.GetInt64(16)));
            }
        }
        bool more = rows.Count > limit;
        if (more) rows.RemoveAt(rows.Count - 1);
        return (rows, more);
    }

    /// <summary>
    /// A paired phone brings its owner here (their second PC): this journal, its history and
    /// everything it records from now on become that person's. Refused once any phone is
    /// linked, so a friend's phone plugged into this PC can't take it over.
    /// </summary>
    public string? TakeIdentity(string listener, string name)
    {
        if (string.IsNullOrWhiteSpace(listener)) return "falta la identidad";
        if (listener == Owner.ListenerId) return null;
        using var db = Open();
        using (var count = db.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM device WHERE kind = 'phone'";
            if (Convert.ToInt64(count.ExecuteScalar()) > 0) return "este Nation ya tiene un teléfono vinculado";
        }
        string previous = Owner.ListenerId;
        using (var tx = db.BeginTransaction())
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            // updated_at moves too, so the phone pulls this history under its new owner.
            cmd.CommandText = """
                UPDATE listen SET listener = $new, updated_at = $now WHERE listener = $old OR listener = '';
                UPDATE device SET listener = $new WHERE listener = $old;
                INSERT INTO meta (key, value) VALUES ('owner', $new) ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            cmd.Parameters.AddWithValue("$new", listener);
            cmd.Parameters.AddWithValue("$old", previous);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
        var profile = Owner with { ListenerId = listener, Name = string.IsNullOrWhiteSpace(name) ? Owner.Name : name };
        profile.Save(_log.ProfilePath);
        _log.Profile = profile;
        return null;
    }

    /// <summary>
    /// Merges listens sent by a device paired to this owner; anyone else's are ignored.
    /// Idempotent: a row seen again only grows (an open listen keeps being resent while it plays).
    /// </summary>
    public int Merge(DeviceDto device, IReadOnlyList<ListenDto> listens)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using (var dev = db.CreateCommand())
        {
            dev.Transaction = tx;
            dev.CommandText = """
                INSERT INTO device (id, listener, name, kind, added_at) VALUES ($id, $listener, $name, $kind, $now)
                ON CONFLICT(id) DO UPDATE SET name = excluded.name, listener = excluded.listener
                """;
            dev.Parameters.AddWithValue("$id", device.Id);
            dev.Parameters.AddWithValue("$listener", Owner.ListenerId);
            dev.Parameters.AddWithValue("$name", device.Name);
            dev.Parameters.AddWithValue("$kind", device.Kind);
            dev.Parameters.AddWithValue("$now", now);
            dev.ExecuteNonQuery();
        }

        int accepted = 0;
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO listen (id, listener, device, source, started_at, ended_at, played_ms, duration_ms,
                                title, artist, album, genre, isrc, path, track_key, music, updated_at)
            VALUES ($id, $listener, $device, $source, $started, $ended, $played, $duration,
                    $title, $artist, $album, $genre, $isrc, $path, $key, $music, $now)
            ON CONFLICT(id) DO UPDATE SET
                ended_at = MAX(ended_at, excluded.ended_at), played_ms = MAX(played_ms, excluded.played_ms), updated_at = excluded.updated_at
            WHERE excluded.played_ms > listen.played_ms OR excluded.ended_at > listen.ended_at
            """;
        foreach (var l in listens)
        {
            if (l.Listener != Owner.ListenerId || string.IsNullOrEmpty(l.Id)) continue;
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", l.Id);
            cmd.Parameters.AddWithValue("$listener", l.Listener);
            cmd.Parameters.AddWithValue("$device", l.Device);
            cmd.Parameters.AddWithValue("$source", l.Source);
            cmd.Parameters.AddWithValue("$started", l.StartedAt);
            cmd.Parameters.AddWithValue("$ended", l.EndedAt);
            cmd.Parameters.AddWithValue("$played", l.PlayedMs);
            cmd.Parameters.AddWithValue("$duration", l.DurationMs);
            cmd.Parameters.AddWithValue("$title", l.Title);
            cmd.Parameters.AddWithValue("$artist", l.Artist);
            cmd.Parameters.AddWithValue("$album", l.Album);
            cmd.Parameters.AddWithValue("$genre", (object?)l.Genre ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$isrc", (object?)l.Isrc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$path", (object?)l.Path ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$key", l.TrackKey);
            cmd.Parameters.AddWithValue("$music", l.Music ? 1 : 0);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
            accepted++;
        }
        tx.Commit();
        return accepted;
    }
}
