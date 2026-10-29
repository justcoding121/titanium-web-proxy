using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Titanium.Manager.Auth;

namespace Titanium.Manager.Store;

internal sealed class ManagerStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public ManagerStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        _db.Open();
        Migrate();
    }

    public void Dispose() => _db.Dispose();

    private void Migrate()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText =
            """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS fleets (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              name TEXT NOT NULL UNIQUE,
              labels_json TEXT NOT NULL DEFAULT '{}',
              created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS agents (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              fleet_id INTEGER NOT NULL REFERENCES fleets(id),
              node_id TEXT NOT NULL,
              labels_json TEXT NOT NULL DEFAULT '{}',
              last_seen TEXT NULL,
              config_hash TEXT NULL,
              health_json TEXT NULL,
              status TEXT NOT NULL DEFAULT 'unknown',
              desired_revision_id INTEGER NULL,
              UNIQUE(fleet_id, node_id)
            );
            CREATE TABLE IF NOT EXISTS revisions (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              fleet_id INTEGER NOT NULL REFERENCES fleets(id),
              content_hash TEXT NOT NULL,
              yaml_text TEXT NOT NULL,
              author TEXT NOT NULL,
              parent_id INTEGER NULL,
              created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS drafts (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              fleet_id INTEGER NOT NULL UNIQUE REFERENCES fleets(id),
              yaml_text TEXT NOT NULL,
              author TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS rollouts (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              fleet_id INTEGER NOT NULL REFERENCES fleets(id),
              revision_id INTEGER NOT NULL REFERENCES revisions(id),
              state TEXT NOT NULL,
              created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS agent_tokens (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              cluster_id INTEGER NOT NULL REFERENCES fleets(id),
              name TEXT NOT NULL,
              token_hash TEXT NOT NULL UNIQUE,
              created_at TEXT NOT NULL,
              revoked_at TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS admin_tokens (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              name TEXT NOT NULL,
              token_hash TEXT NOT NULL UNIQUE,
              role TEXT NOT NULL,
              created_at TEXT NOT NULL,
              revoked_at TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS audit (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              time TEXT NOT NULL,
              actor TEXT NOT NULL,
              action TEXT NOT NULL,
              target TEXT NOT NULL,
              result TEXT NOT NULL,
              detail TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS fleet_published (
              fleet_id INTEGER PRIMARY KEY REFERENCES fleets(id),
              revision_id INTEGER NOT NULL REFERENCES revisions(id)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void EnsureBootstrapAdminToken(string plainToken, string name = "bootstrap")
    {
        if (string.IsNullOrWhiteSpace(plainToken))
        {
            return;
        }

        lock (_gate)
        {
            var hash = TokenHasher.Hash(plainToken);
            using var find = _db.CreateCommand();
            find.CommandText = "SELECT id FROM admin_tokens WHERE token_hash = $h LIMIT 1;";
            find.Parameters.AddWithValue("$h", hash);
            if (find.ExecuteScalar() is not null)
            {
                return;
            }

            using var insert = _db.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO admin_tokens (name, token_hash, role, created_at)
                VALUES ($n, $h, 'admin', $t);
                """;
            insert.Parameters.AddWithValue("$n", name);
            insert.Parameters.AddWithValue("$h", hash);
            insert.Parameters.AddWithValue("$t", UtcNow());
            insert.ExecuteNonQuery();
        }
    }

    public AdminTokenRow? FindAdminByToken(string plainToken)
    {
        if (string.IsNullOrEmpty(plainToken))
        {
            return null;
        }

        lock (_gate)
        {
            var hash = TokenHasher.Hash(plainToken);
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, name, token_hash, role, created_at, revoked_at
                FROM admin_tokens WHERE token_hash = $h LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$h", hash);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var row = ReadAdmin(reader);
            return row.RevokedAt is null ? row : null;
        }
    }

    public AgentTokenRow? FindAgentByToken(string plainToken)
    {
        if (string.IsNullOrEmpty(plainToken))
        {
            return null;
        }

        lock (_gate)
        {
            var hash = TokenHasher.Hash(plainToken);
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, cluster_id, name, token_hash, created_at, revoked_at
                FROM agent_tokens WHERE token_hash = $h LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$h", hash);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var row = ReadAgentToken(reader);
            return row.RevokedAt is null ? row : null;
        }
    }

    public FleetRow CreateFleet(string name, string labelsJson = "{}")
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO fleets (name, labels_json, created_at)
                VALUES ($n, $l, $t);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$l", string.IsNullOrWhiteSpace(labelsJson) ? "{}" : labelsJson);
            cmd.Parameters.AddWithValue("$t", UtcNow());
            var id = (long)(cmd.ExecuteScalar() ?? throw new InvalidOperationException("fleet insert failed"));
            return GetFleet(id) ?? throw new InvalidOperationException("fleet missing after insert");
        }
    }

    public IReadOnlyList<FleetRow> ListFleets()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, name, labels_json, created_at FROM fleets ORDER BY id;";
            using var reader = cmd.ExecuteReader();
            var list = new List<FleetRow>();
            while (reader.Read())
            {
                list.Add(ReadFleet(reader));
            }

            return list;
        }
    }

    public FleetRow? GetFleet(long id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, name, labels_json, created_at FROM fleets WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadFleet(reader) : null;
        }
    }

    public RevisionRow CreateRevision(long fleetId, string yamlText, string author, long? parentId = null)
    {
        lock (_gate)
        {
            if (GetFleetUnlocked(fleetId) is null)
            {
                throw new KeyNotFoundException($"fleet {fleetId} not found");
            }

            var hash = ContentHash(yamlText);
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO revisions (fleet_id, content_hash, yaml_text, author, parent_id, created_at)
                VALUES ($f, $h, $y, $a, $p, $t);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$f", fleetId);
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$y", yamlText);
            cmd.Parameters.AddWithValue("$a", author);
            cmd.Parameters.AddWithValue("$p", (object?)parentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", UtcNow());
            var id = (long)(cmd.ExecuteScalar() ?? throw new InvalidOperationException("revision insert failed"));
            return GetRevisionUnlocked(id) ?? throw new InvalidOperationException("revision missing after insert");
        }
    }

    public IReadOnlyList<RevisionRow> ListRevisions(long fleetId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, fleet_id, content_hash, yaml_text, author, parent_id, created_at
                FROM revisions WHERE fleet_id = $f ORDER BY id;
                """;
            cmd.Parameters.AddWithValue("$f", fleetId);
            using var reader = cmd.ExecuteReader();
            var list = new List<RevisionRow>();
            while (reader.Read())
            {
                list.Add(ReadRevision(reader));
            }

            return list;
        }
    }

    public RevisionRow? GetRevision(long fleetId, long revisionId)
    {
        lock (_gate)
        {
            var row = GetRevisionUnlocked(revisionId);
            return row is not null && row.FleetId == fleetId ? row : null;
        }
    }

    public RolloutRow Publish(long fleetId, long revisionId)
    {
        lock (_gate)
        {
            var rev = GetRevisionUnlocked(revisionId)
                      ?? throw new KeyNotFoundException($"revision {revisionId} not found");
            if (rev.FleetId != fleetId)
            {
                throw new KeyNotFoundException($"revision {revisionId} not in fleet {fleetId}");
            }

            using var upsert = _db.CreateCommand();
            upsert.CommandText =
                """
                INSERT INTO fleet_published (fleet_id, revision_id) VALUES ($f, $r)
                ON CONFLICT(fleet_id) DO UPDATE SET revision_id = excluded.revision_id;
                UPDATE agents SET desired_revision_id = $r WHERE fleet_id = $f;
                INSERT INTO rollouts (fleet_id, revision_id, state, created_at)
                VALUES ($f, $r, 'complete', $t);
                SELECT last_insert_rowid();
                """;
            upsert.Parameters.AddWithValue("$f", fleetId);
            upsert.Parameters.AddWithValue("$r", revisionId);
            upsert.Parameters.AddWithValue("$t", UtcNow());
            var id = (long)(upsert.ExecuteScalar() ?? throw new InvalidOperationException("rollout insert failed"));

            using var get = _db.CreateCommand();
            get.CommandText =
                "SELECT id, fleet_id, revision_id, state, created_at FROM rollouts WHERE id = $id;";
            get.Parameters.AddWithValue("$id", id);
            using var reader = get.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("rollout missing after insert");
            }

            return ReadRollout(reader);
        }
    }

    public PublishedConfig? GetPublished(long fleetId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                """
                SELECT r.id, r.content_hash, r.yaml_text
                FROM fleet_published p
                JOIN revisions r ON r.id = p.revision_id
                WHERE p.fleet_id = $f;
                """;
            cmd.Parameters.AddWithValue("$f", fleetId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new PublishedConfig(reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
        }
    }

    public IReadOnlyList<AgentRow> ListAgents(long fleetId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, fleet_id, node_id, labels_json, last_seen, config_hash, health_json, status, desired_revision_id
                FROM agents WHERE fleet_id = $f ORDER BY node_id;
                """;
            cmd.Parameters.AddWithValue("$f", fleetId);
            using var reader = cmd.ExecuteReader();
            var list = new List<AgentRow>();
            while (reader.Read())
            {
                list.Add(ReadAgent(reader));
            }

            return list;
        }
    }

    public AgentRow UpsertAgentAck(
        long fleetId,
        string nodeId,
        string? configHash,
        bool ok,
        string? error,
        string? healthJson)
    {
        lock (_gate)
        {
            var status = ok ? "ok" : "error";
            var now = UtcNow();
            var health = healthJson;
            if (string.IsNullOrEmpty(health) && !string.IsNullOrEmpty(error))
            {
                health = $"{{\"error\":{System.Text.Json.JsonSerializer.Serialize(error)}}}";
            }

            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO agents (fleet_id, node_id, labels_json, last_seen, config_hash, health_json, status)
                VALUES ($f, $n, '{}', $t, $h, $health, $s)
                ON CONFLICT(fleet_id, node_id) DO UPDATE SET
                  last_seen = excluded.last_seen,
                  config_hash = excluded.config_hash,
                  health_json = excluded.health_json,
                  status = excluded.status;
                SELECT id, fleet_id, node_id, labels_json, last_seen, config_hash, health_json, status, desired_revision_id
                FROM agents WHERE fleet_id = $f AND node_id = $n;
                """;
            cmd.Parameters.AddWithValue("$f", fleetId);
            cmd.Parameters.AddWithValue("$n", nodeId);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.Parameters.AddWithValue("$h", (object?)configHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$health", (object?)health ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$s", status);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("agent upsert failed");
            }

            return ReadAgent(reader);
        }
    }

    public (AgentTokenRow Row, string PlainToken) CreateAgentToken(long fleetId, string name)
    {
        lock (_gate)
        {
            if (GetFleetUnlocked(fleetId) is null)
            {
                throw new KeyNotFoundException($"fleet {fleetId} not found");
            }

            var plain = TokenHasher.NewToken();
            var hash = TokenHasher.Hash(plain);
            var created = UtcNow();
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO agent_tokens (cluster_id, name, token_hash, created_at)
                VALUES ($c, $n, $h, $t);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$c", fleetId);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$t", created);
            var id = (long)(cmd.ExecuteScalar() ?? throw new InvalidOperationException("token insert failed"));
            return (new AgentTokenRow(id, fleetId, name, hash, DateTimeOffset.Parse(created), null), plain);
        }
    }

    public bool RevokeAgentToken(long tokenId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                """
                UPDATE agent_tokens SET revoked_at = $t
                WHERE id = $id AND revoked_at IS NULL;
                """;
            cmd.Parameters.AddWithValue("$t", UtcNow());
            cmd.Parameters.AddWithValue("$id", tokenId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public void Audit(string actor, string action, string target, string result, string? detail = null)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO audit (time, actor, action, target, result, detail)
                VALUES ($t, $a, $act, $tgt, $r, $d);
                """;
            cmd.Parameters.AddWithValue("$t", UtcNow());
            cmd.Parameters.AddWithValue("$a", actor);
            cmd.Parameters.AddWithValue("$act", action);
            cmd.Parameters.AddWithValue("$tgt", target);
            cmd.Parameters.AddWithValue("$r", result);
            cmd.Parameters.AddWithValue("$d", (object?)detail ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<AuditRow> ListAudit(int limit = 200)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, time, actor, action, target, result, detail
                FROM audit ORDER BY id DESC LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 1000));
            using var reader = cmd.ExecuteReader();
            var list = new List<AuditRow>();
            while (reader.Read())
            {
                list.Add(new AuditRow(
                    reader.GetInt64(0),
                    DateTimeOffset.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }

            return list;
        }
    }

    public static string ContentHash(string yamlText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(yamlText))).ToLowerInvariant();

    private FleetRow? GetFleetUnlocked(long id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, name, labels_json, created_at FROM fleets WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadFleet(reader) : null;
    }

    private RevisionRow? GetRevisionUnlocked(long id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, fleet_id, content_hash, yaml_text, author, parent_id, created_at
            FROM revisions WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRevision(reader) : null;
    }

    private static string UtcNow() => DateTimeOffset.UtcNow.ToString("O");

    private static FleetRow ReadFleet(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), DateTimeOffset.Parse(r.GetString(3)));

    private static RevisionRow ReadRevision(SqliteDataReader r) =>
        new(
            r.GetInt64(0),
            r.GetInt64(1),
            r.GetString(2),
            r.GetString(3),
            r.GetString(4),
            r.IsDBNull(5) ? null : r.GetInt64(5),
            DateTimeOffset.Parse(r.GetString(6)));

    private static AgentRow ReadAgent(SqliteDataReader r) =>
        new(
            r.GetInt64(0),
            r.GetInt64(1),
            r.GetString(2),
            r.GetString(3),
            r.IsDBNull(4) ? null : DateTimeOffset.Parse(r.GetString(4)),
            r.IsDBNull(5) ? null : r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            r.GetString(7),
            r.IsDBNull(8) ? null : r.GetInt64(8));

    private static RolloutRow ReadRollout(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), DateTimeOffset.Parse(r.GetString(4)));

    private static AgentTokenRow ReadAgentToken(SqliteDataReader r) =>
        new(
            r.GetInt64(0),
            r.GetInt64(1),
            r.GetString(2),
            r.GetString(3),
            DateTimeOffset.Parse(r.GetString(4)),
            r.IsDBNull(5) ? null : DateTimeOffset.Parse(r.GetString(5)));

    private static AdminTokenRow ReadAdmin(SqliteDataReader r) =>
        new(
            r.GetInt64(0),
            r.GetString(1),
            r.GetString(2),
            r.GetString(3),
            DateTimeOffset.Parse(r.GetString(4)),
            r.IsDBNull(5) ? null : DateTimeOffset.Parse(r.GetString(5)));
}
