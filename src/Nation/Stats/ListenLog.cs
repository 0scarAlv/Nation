using Microsoft.Data.Sqlite;

namespace Nation.Stats;

/// <summary>
/// One stretch of listening to one song on one device. Append-only and keyed by a
/// random id, so journals from other devices (Shade's phone player) can be merged
/// in any order and any number of times without double counting.
/// </summary>
public sealed record Listen(
    string Id, string Listener, string Device, string Source,
    long StartedAt, long EndedAt, long PlayedMs, long DurationMs,
    string Title, string Artist, string Album, string? Genre, string? Isrc, string? Path,
    string TrackKey, bool Music);

/// <summary>
/// The listening journal: %LOCALAPPDATA%\Nation\listens.db. Nation is its source of
/// truth; every stat (time per artist, genre, hour…) is a query over these rows, so
/// rules like "what counts as a play" can change later without rewriting history.
/// </summary>
public sealed class ListenLog : IDisposable
{
    public static readonly string DefaultPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nation", "listens.db");

    private const int SchemaVersion = 2;
    private readonly SqliteConnection _db;

    /// <summary>The person this journal belongs to, and this PC. Only their listens are merged in.</summary>
    public Profile Profile { get; }

    public ListenLog(string? path = null, string? profilePath = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        SQLitePCL.Batteries_V2.Init();
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Migrate();
        Profile = Profile.LoadOrCreate(GetMeta("owner"), ThisMachineDevice(), profilePath);
        Adopt(Profile);
    }

    public SqliteConnection Connection => _db;

    private void Migrate()
    {
        long version = Scalar("PRAGMA user_version");
        if (version >= SchemaVersion) return;
        // Times are Unix milliseconds (UTC). updated_at lets a device ask "what changed since my last sync".
        if (version < 1) Exec("""
            CREATE TABLE IF NOT EXISTS listen (
                id          TEXT PRIMARY KEY,
                device      TEXT NOT NULL,
                source      TEXT NOT NULL,
                started_at  INTEGER NOT NULL,
                ended_at    INTEGER NOT NULL,
                played_ms   INTEGER NOT NULL,
                duration_ms INTEGER NOT NULL,
                title       TEXT NOT NULL,
                artist      TEXT NOT NULL,
                album       TEXT NOT NULL,
                genre       TEXT,
                isrc        TEXT,
                path        TEXT,
                track_key   TEXT NOT NULL,
                music       INTEGER NOT NULL DEFAULT 1,
                updated_at  INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS listen_started ON listen(started_at);
            CREATE INDEX IF NOT EXISTS listen_track ON listen(track_key);
            CREATE INDEX IF NOT EXISTS listen_updated ON listen(updated_at);
            PRAGMA user_version = 1;
            """);
        // v2: whose listen it is (one id per person across devices), the devices seen, and the journal's owner.
        // Rows from v1 get an empty listener here and are claimed by Adopt().
        if (version < 2) Exec("""
            BEGIN;
            ALTER TABLE listen ADD COLUMN listener TEXT NOT NULL DEFAULT '';
            CREATE INDEX IF NOT EXISTS listen_listener ON listen(listener);
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS device (
                id        TEXT PRIMARY KEY,
                listener  TEXT NOT NULL,
                name      TEXT NOT NULL,
                kind      TEXT NOT NULL,
                added_at  INTEGER NOT NULL
            );
            PRAGMA user_version = 2;
            COMMIT;
            """);
    }

    /// <summary>
    /// Records the owner and this PC, and hands them the listens written before ids existed
    /// (v1 stored the device as "pc:" + machine name). The journal belongs to whoever
    /// profile.json names: importing a profile from another PC moves this history to that person.
    /// </summary>
    private void Adopt(Profile p)
    {
        string? previous = GetMeta("owner");
        using var tx = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE listen SET device = $device WHERE listener = '' AND device LIKE 'pc:%';
            UPDATE listen SET listener = $listener WHERE listener = '' OR listener = $previous;
            UPDATE device SET listener = $listener WHERE listener = $previous;
            INSERT INTO meta (key, value) VALUES ('owner', $listener) ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            INSERT INTO device (id, listener, name, kind, added_at) VALUES ($device, $listener, $name, 'pc', $now)
                ON CONFLICT(id) DO UPDATE SET name = excluded.name, listener = excluded.listener;
            """;
        cmd.Parameters.AddWithValue("$previous", previous ?? "");
        cmd.Parameters.AddWithValue("$listener", p.ListenerId);
        cmd.Parameters.AddWithValue("$device", p.DeviceId);
        cmd.Parameters.AddWithValue("$name", p.DeviceName);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    // The id this PC was given before, in case profile.json was lost.
    private string? ThisMachineDevice()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id FROM device WHERE kind = 'pc' AND name = $name COLLATE NOCASE ORDER BY added_at LIMIT 1";
        cmd.Parameters.AddWithValue("$name", Environment.MachineName);
        return cmd.ExecuteScalar() as string;
    }

    private string? GetMeta(string key)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Inserts or refreshes a listen (an open one is saved every few seconds while it grows).</summary>
    public void Upsert(Listen l)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO listen (id, listener, device, source, started_at, ended_at, played_ms, duration_ms,
                                title, artist, album, genre, isrc, path, track_key, music, updated_at)
            VALUES ($id, $listener, $device, $source, $started, $ended, $played, $duration,
                    $title, $artist, $album, $genre, $isrc, $path, $key, $music, $updated)
            ON CONFLICT(id) DO UPDATE SET
                ended_at = excluded.ended_at, played_ms = excluded.played_ms, updated_at = excluded.updated_at
            """;
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
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Dispose() => _db.Dispose();
}
