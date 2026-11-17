using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CATIA_CFD_Clean;

/// <summary>
/// The SQLite file kept next to a CATPart (Silencer.CATPart -> Silencer.sqlite3). It holds:
/// <list type="bullet">
/// <item><c>state</c>: what the app holds for this part (O-I slots, lock, plugs). Written after
/// every operation that changes it, so it survives restarts. Source of truth for the app.</item>
/// <item><c>catalogue</c> + <c>entity</c>: the last Re-catalogue, a snapshot of facts read from
/// CATIA (bodies today; faces, edges, ... later). Replaced on every Re-catalogue; no history.</item>
/// </list>
/// The schema is deliberately loose while ideas settle: rows have a kind, a key and a JSON
/// <c>meta</c> column for everything else.
/// </summary>
internal static class PartDb
{
    private const int SchemaVersion = 2;

    public sealed record Entity(string Kind, string Key, string? ParentKey, object Meta);
    public sealed record StateRow(string Kind, string Key, string Meta);

    // The .sqlite3 path for the active document. Only saved CATParts have one.
    public static string PathFor(object document)
    {
        string path = (string)((dynamic)document).FullName;
        if (!Path.IsPathRooted(path) || !path.EndsWith(".CATPart", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The active document must be a saved .CATPart; the app keeps its state in a file next to it.");
        return Path.ChangeExtension(path, ".sqlite3");
    }

    // Replaces the catalogue and its entities in a single transaction.
    public static void SaveCatalogue(string dbPath, object meta, IReadOnlyList<Entity> entities)
    {
        using var conn = Open(dbPath);
        using var tx = conn.BeginTransaction();

        Exec(conn, tx, "DELETE FROM entity; DELETE FROM catalogue;");

        using var head = conn.CreateCommand();
        head.Transaction = tx;
        head.CommandText = "INSERT INTO catalogue (taken_at, meta) VALUES ($taken, $meta) RETURNING id";
        head.Parameters.AddWithValue("$taken", DateTime.UtcNow.ToString("o"));
        head.Parameters.AddWithValue("$meta", JsonSerializer.Serialize(meta));
        long id = (long)head.ExecuteScalar()!;

        using var row = conn.CreateCommand();
        row.Transaction = tx;
        row.CommandText = "INSERT INTO entity (catalogue_id, kind, key, parent_key, meta) " +
                          "VALUES ($cat, $kind, $key, $parent, $meta)";
        row.Parameters.AddWithValue("$cat", id);
        var kind = row.Parameters.Add("$kind", SqliteType.Text);
        var key = row.Parameters.Add("$key", SqliteType.Text);
        var parent = row.Parameters.Add("$parent", SqliteType.Text);
        var json = row.Parameters.Add("$meta", SqliteType.Text);
        foreach (var e in entities)
        {
            kind.Value = e.Kind;
            key.Value = e.Key;
            parent.Value = (object?)e.ParentKey ?? DBNull.Value;
            json.Value = JsonSerializer.Serialize(e.Meta);
            row.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // Reads all state rows in the order they were written.
    public static List<StateRow> LoadState(string dbPath)
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT kind, key, meta FROM state ORDER BY rowid";
        using var reader = cmd.ExecuteReader();
        var rows = new List<StateRow>();
        while (reader.Read())
            rows.Add(new StateRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    // Replaces all state rows in a single transaction.
    public static void SaveState(string dbPath, IEnumerable<StateRow> rows)
    {
        using var conn = Open(dbPath);
        using var tx = conn.BeginTransaction();

        Exec(conn, tx, "DELETE FROM state;");

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO state (kind, key, meta) VALUES ($kind, $key, $meta)";
        var kind = cmd.Parameters.Add("$kind", SqliteType.Text);
        var key = cmd.Parameters.Add("$key", SqliteType.Text);
        var meta = cmd.Parameters.Add("$meta", SqliteType.Text);
        foreach (var r in rows)
        {
            kind.Value = r.Kind;
            key.Value = r.Key;
            meta.Value = r.Meta;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // Opens the file (creating it if needed) with the schema up to date.
    // No pooling: the file is released as soon as the connection closes.
    private static SqliteConnection Open(string dbPath)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false, ForeignKeys = true };
        var conn = new SqliteConnection(cs.ToString());
        try
        {
            conn.Open();
            EnsureSchema(conn, dbPath);
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    private static void EnsureSchema(SqliteConnection conn, string dbPath)
    {
        long version;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version";
            version = (long)cmd.ExecuteScalar()!;
        }
        if (version == SchemaVersion)
            return;
        if (version > SchemaVersion)
            throw new InvalidOperationException(
                $"{Path.GetFileName(dbPath)} was written by a newer version of this app (schema {version}).");

        using var tx = conn.BeginTransaction();
        if (version < 1)
        {
            Exec(conn, tx, """
                CREATE TABLE catalogue (
                    id       INTEGER PRIMARY KEY,
                    taken_at TEXT NOT NULL,                          -- UTC, ISO 8601
                    meta     TEXT NOT NULL CHECK (json_valid(meta))
                );
                CREATE TABLE entity (
                    id           INTEGER PRIMARY KEY,
                    catalogue_id INTEGER NOT NULL REFERENCES catalogue(id) ON DELETE CASCADE,
                    kind         TEXT NOT NULL,                      -- 'body' (later: 'face', 'edge', ...)
                    key          TEXT NOT NULL,                      -- body name (CFD_ key for app bodies)
                    parent_key   TEXT,                               -- owning entity, for sub-entities
                    meta         TEXT NOT NULL CHECK (json_valid(meta))
                );
                CREATE INDEX entity_lookup ON entity (catalogue_id, kind, key);
                """);
        }
        if (version < 2)
        {
            Exec(conn, tx, """
                DROP VIEW IF EXISTS latest_entity;
                CREATE TABLE state (
                    kind TEXT NOT NULL,                              -- 'oi', 'lock', 'plug'
                    key  TEXT NOT NULL,                              -- O-I slot or body name
                    meta TEXT NOT NULL CHECK (json_valid(meta)),
                    PRIMARY KEY (kind, key)
                );
                """);
        }
        Exec(conn, tx, $"PRAGMA user_version = {SchemaVersion};");
        tx.Commit();
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
