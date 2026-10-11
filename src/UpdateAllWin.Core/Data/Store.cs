using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UpdateAllWin.Contracts;

namespace UpdateAllWin.Core.Data;

public interface IHistoryStore
{
    void Add(HistoryEntry entry);
    IReadOnlyList<HistoryEntry> Query(int limit = 500, HistoryKind? kind = null);
    void Prune(TimeSpan olderThan);
}

public interface IStateStore
{
    string? Get(string key);
    void Set(string key, string? value);
    T? GetJson<T>(string key);
    void SetJson<T>(string key, T? value);
}

/// <summary>SQLite-backed history and small key/value state (one database file).</summary>
public sealed class SqliteStore : IHistoryStore, IStateStore
{
    private readonly string _connectionString;

    public SqliteStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 15,
        }.ToString();

        using var db = Open();
        Execute(db, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS history (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                time_utc    TEXT NOT NULL,
                kind        TEXT NOT NULL,
                trigger     TEXT,
                title       TEXT NOT NULL,
                result      TEXT NOT NULL,
                success     INTEGER NOT NULL,
                adapter_id  TEXT,
                update_key  TEXT,
                category    TEXT,
                from_ver    TEXT,
                to_ver      TEXT,
                exit_code   INTEGER,
                details     TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_history_time ON history(time_utc);
            CREATE TABLE IF NOT EXISTS state (
                key   TEXT PRIMARY KEY,
                value TEXT
            );
            """);
    }

    public void Add(HistoryEntry e)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO history (time_utc, kind, trigger, title, result, success, adapter_id, update_key, category, from_ver, to_ver, exit_code, details)
            VALUES ($time, $kind, $trigger, $title, $result, $success, $adapter, $key, $category, $from, $to, $exit, $details)
            """;
        cmd.Parameters.AddWithValue("$time", (e.TimeUtc == default ? DateTime.UtcNow : e.TimeUtc).ToString("O"));
        cmd.Parameters.AddWithValue("$kind", e.Kind.ToString());
        cmd.Parameters.AddWithValue("$trigger", (object?)e.Trigger?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", e.Title);
        cmd.Parameters.AddWithValue("$result", e.Result);
        cmd.Parameters.AddWithValue("$success", e.Success ? 1 : 0);
        cmd.Parameters.AddWithValue("$adapter", (object?)e.AdapterId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$key", (object?)e.UpdateKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$category", (object?)e.Category?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$from", (object?)e.FromVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to", (object?)e.ToVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exit", (object?)e.ExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$details", (object?)e.Details ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<HistoryEntry> Query(int limit = 500, HistoryKind? kind = null)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, time_utc, kind, trigger, title, result, success, adapter_id, update_key, category, from_ver, to_ver, exit_code, details FROM history"
                          + (kind is null ? "" : " WHERE kind = $kind")
                          + " ORDER BY id DESC LIMIT $limit";
        if (kind is not null) cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10_000));

        var list = new List<HistoryEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new HistoryEntry
            {
                Id = r.GetInt64(0),
                TimeUtc = DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Kind = Enum.Parse<HistoryKind>(r.GetString(2)),
                Trigger = r.IsDBNull(3) ? null : Enum.Parse<OperationTrigger>(r.GetString(3)),
                Title = r.GetString(4),
                Result = r.GetString(5),
                Success = r.GetInt32(6) != 0,
                AdapterId = r.IsDBNull(7) ? null : r.GetString(7),
                UpdateKey = r.IsDBNull(8) ? null : r.GetString(8),
                Category = r.IsDBNull(9) ? null : Enum.Parse<UpdateCategory>(r.GetString(9)),
                FromVersion = r.IsDBNull(10) ? null : r.GetString(10),
                ToVersion = r.IsDBNull(11) ? null : r.GetString(11),
                ExitCode = r.IsDBNull(12) ? null : r.GetInt32(12),
                Details = r.IsDBNull(13) ? null : r.GetString(13),
            });
        }
        return list;
    }

    public void Prune(TimeSpan olderThan)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM history WHERE time_utc < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", (DateTime.UtcNow - olderThan).ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public string? Get(string key)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM state WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string key, string? value)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        if (value is null)
        {
            cmd.CommandText = "DELETE FROM state WHERE key = $key";
        }
        else
        {
            cmd.CommandText = "INSERT INTO state (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            cmd.Parameters.AddWithValue("$value", value);
        }
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
    }

    public T? GetJson<T>(string key)
    {
        var json = Get(key);
        if (json is null) return default;
        try { return JsonSerializer.Deserialize<T>(json, IpcProtocol.Json); }
        catch (JsonException) { return default; }
    }

    public void SetJson<T>(string key, T? value) =>
        Set(key, value is null ? null : JsonSerializer.Serialize(value, IpcProtocol.Json));

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
