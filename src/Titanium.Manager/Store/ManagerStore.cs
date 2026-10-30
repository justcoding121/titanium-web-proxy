using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Titanium.Manager.Auth;

namespace Titanium.Manager.Store;

internal sealed class ManagerStore : IDisposable
{
    private const string RolloutsTable = "rollouts";
    private const string TextNullTypedef = "TEXT NULL";
    private const string StateRunning = "running";
    private const string StatePaused = "paused";

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
              previous_revision_id INTEGER NULL,
              state TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              pause_reason TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS rollout_steps (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              rollout_id INTEGER NOT NULL REFERENCES rollouts(id),
              ordinal INTEGER NOT NULL,
              label_selector_json TEXT NOT NULL DEFAULT '{}',
              max_unavailable INTEGER NOT NULL DEFAULT 0,
              bake_seconds INTEGER NOT NULL DEFAULT 0,
              state TEXT NOT NULL,
              started_at TEXT NULL,
              finished_at TEXT NULL
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

        // Additive columns for DBs created before staged rollouts.
        EnsureColumn(RolloutsTable, "previous_revision_id", "INTEGER NULL");
        EnsureColumn(RolloutsTable, "updated_at", TextNullTypedef);
        EnsureColumn(RolloutsTable, "pause_reason", TextNullTypedef);
    }

    private void EnsureColumn(string table, string column, string typedef)
    {
        // ALTER TABLE cannot bind identifiers; keep migration DDL as compile-time constants.
        var alterSql = (table, column, typedef) switch
        {
            (RolloutsTable, "previous_revision_id", "INTEGER NULL") =>
                "ALTER TABLE rollouts ADD COLUMN previous_revision_id INTEGER NULL;",
            (RolloutsTable, "updated_at", TextNullTypedef) =>
                "ALTER TABLE rollouts ADD COLUMN updated_at TEXT NULL;",
            (RolloutsTable, "pause_reason", TextNullTypedef) =>
                "ALTER TABLE rollouts ADD COLUMN pause_reason TEXT NULL;",
            _ => throw new ArgumentException($"unsupported schema migration: {table}.{column} {typedef}"),
        };

        using var info = _db.CreateCommand();
        info.CommandText = "SELECT 1 FROM pragma_table_info($table) WHERE name = $column LIMIT 1;";
        info.Parameters.AddWithValue("$table", table);
        info.Parameters.AddWithValue("$column", column);
        if (info.ExecuteScalar() is not null)
        {
            return;
        }

        using var alter = _db.CreateCommand();
        alter.CommandText = alterSql;
        alter.ExecuteNonQuery();
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

    /// <summary>
    /// Publish a revision. When <paramref name="waves"/> is null/empty, exposes the revision to all
    /// agents immediately (P4-compatible). Otherwise starts a staged rollout.
    /// </summary>
    public RolloutRow Publish(
        long fleetId,
        long revisionId,
        IReadOnlyList<PublishWaveSpec>? waves = null,
        int? defaultBakeSeconds = null)
    {
        lock (_gate)
        {
            var rev = GetRevisionUnlocked(revisionId)
                      ?? throw new KeyNotFoundException($"revision {revisionId} not found");
            if (rev.FleetId != fleetId)
            {
                throw new KeyNotFoundException($"revision {revisionId} not in fleet {fleetId}");
            }

            var running = GetActiveRolloutUnlocked(fleetId);
            if (running is not null)
            {
                throw new InvalidOperationException(
                    $"fleet {fleetId} already has an active rollout {running.Id} ({running.State})");
            }

            var previous = GetPublishedUnlocked(fleetId);
            var previousId = previous?.RevisionId;
            var now = UtcNow();
            var staged = waves is { Count: > 0 };

            using var tx = _db.BeginTransaction();

            SetFleetPublishedUnlocked(fleetId, revisionId, tx);

            long rolloutId;
            if (!staged)
            {
                // Immediate: all agents get the new desired revision; rollout succeeds.
                using var setAll = _db.CreateCommand();
                setAll.Transaction = tx;
                setAll.CommandText = "UPDATE agents SET desired_revision_id = $r WHERE fleet_id = $f;";
                setAll.Parameters.AddWithValue("$r", revisionId);
                setAll.Parameters.AddWithValue("$f", fleetId);
                setAll.ExecuteNonQuery();

                rolloutId = InsertRolloutUnlocked(
                    fleetId, revisionId, previousId, "succeeded", now, now, null, tx);

                var bake = defaultBakeSeconds ?? 0;
                InsertStepUnlocked(
                    rolloutId, 0, "{}", int.MaxValue, bake, "succeeded", now, now, tx);
            }
            else
            {
                rolloutId = InsertRolloutUnlocked(
                    fleetId, revisionId, previousId, StateRunning, now, now, null, tx);

                for (var i = 0; i < waves!.Count; i++)
                {
                    var w = waves[i];
                    var selector = SerializeLabels(w.MatchLabels);
                    var bake = w.BakeSeconds >= 0
                        ? w.BakeSeconds
                        : defaultBakeSeconds ?? 0;
                    var state = i == 0 ? StateRunning : "pending";
                    var started = i == 0 ? now : null;
                    InsertStepUnlocked(
                        rolloutId,
                        i,
                        selector,
                        Math.Max(0, w.MaxUnavailable),
                        Math.Max(0, bake),
                        state,
                        started,
                        null,
                        tx);
                }

                // Wave 0: expose new revision only to matching agents; others keep previous.
                ExposeWaveUnlocked(fleetId, revisionId, previousId, waves[0].MatchLabels, tx);
            }

            tx.Commit();
            return GetRolloutUnlocked(rolloutId)
                   ?? throw new InvalidOperationException("rollout missing after insert");
        }
    }

    public RolloutRow? GetRollout(long fleetId, long rolloutId)
    {
        lock (_gate)
        {
            var row = GetRolloutUnlocked(rolloutId);
            return row is not null && row.FleetId == fleetId ? row : null;
        }
    }

    public IReadOnlyList<RolloutRow> ListRollouts(long fleetId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, fleet_id, revision_id, previous_revision_id, state, created_at, updated_at, pause_reason
                FROM rollouts WHERE fleet_id = $f ORDER BY id DESC;
                """;
            cmd.Parameters.AddWithValue("$f", fleetId);
            using var reader = cmd.ExecuteReader();
            var list = new List<RolloutRow>();
            while (reader.Read())
            {
                list.Add(ReadRollout(reader));
            }

            return list;
        }
    }

    public IReadOnlyList<RolloutStepRow> ListRolloutSteps(long rolloutId)
    {
        lock (_gate)
        {
            return ListStepsUnlocked(rolloutId);
        }
    }

    public RolloutRow PauseRollout(long fleetId, long rolloutId, string reason)
    {
        lock (_gate)
        {
            return SetRolloutStateUnlocked(fleetId, rolloutId, StatePaused, reason);
        }
    }

    public RolloutRow ResumeRollout(long fleetId, long rolloutId)
    {
        lock (_gate)
        {
            var row = GetRolloutUnlocked(rolloutId)
                      ?? throw new KeyNotFoundException($"rollout {rolloutId} not found");
            if (row.FleetId != fleetId)
            {
                throw new KeyNotFoundException($"rollout {rolloutId} not in fleet {fleetId}");
            }

            if (!string.Equals(row.State, StatePaused, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"rollout {rolloutId} is {row.State}, not paused");
            }

            return SetRolloutStateUnlocked(fleetId, rolloutId, StateRunning, null);
        }
    }

    public RolloutRow RollbackRollout(long fleetId, long rolloutId)
    {
        lock (_gate)
        {
            var row = GetRolloutUnlocked(rolloutId)
                      ?? throw new KeyNotFoundException($"rollout {rolloutId} not found");
            if (row.FleetId != fleetId)
            {
                throw new KeyNotFoundException($"rollout {rolloutId} not in fleet {fleetId}");
            }

            if (string.Equals(row.State, "rolled_back", StringComparison.OrdinalIgnoreCase))
            {
                return row;
            }

            var target = row.PreviousRevisionId
                         ?? throw new InvalidOperationException(
                             $"rollout {rolloutId} has no previous revision to roll back to");

            var now = UtcNow();
            using var tx = _db.BeginTransaction();
            SetFleetPublishedUnlocked(fleetId, target, tx);

            using var setAgents = _db.CreateCommand();
            setAgents.Transaction = tx;
            setAgents.CommandText = "UPDATE agents SET desired_revision_id = $r WHERE fleet_id = $f;";
            setAgents.Parameters.AddWithValue("$r", target);
            setAgents.Parameters.AddWithValue("$f", fleetId);
            setAgents.ExecuteNonQuery();

            using var finishSteps = _db.CreateCommand();
            finishSteps.Transaction = tx;
            finishSteps.CommandText =
                """
                UPDATE rollout_steps SET state = 'rolled_back', finished_at = COALESCE(finished_at, $t)
                WHERE rollout_id = $id AND state IN ('pending', 'running', 'paused');
                """;
            finishSteps.Parameters.AddWithValue("$t", now);
            finishSteps.Parameters.AddWithValue("$id", rolloutId);
            finishSteps.ExecuteNonQuery();

            using var upd = _db.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText =
                """
                UPDATE rollouts SET state = 'rolled_back', updated_at = $t, pause_reason = NULL
                WHERE id = $id;
                """;
            upd.Parameters.AddWithValue("$t", now);
            upd.Parameters.AddWithValue("$id", rolloutId);
            upd.ExecuteNonQuery();
            tx.Commit();

            return GetRolloutUnlocked(rolloutId)
                   ?? throw new InvalidOperationException("rollout missing after rollback");
        }
    }

    /// <summary>
    /// Advance running rollouts when wave readiness + bake time allow. Returns fleet ids whose
    /// desired config exposure changed (callers should wake long-polls).
    /// </summary>
    public IReadOnlyList<long> TickRollouts()
    {
        lock (_gate)
        {
            var changed = new HashSet<long>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, fleet_id, revision_id, previous_revision_id, state, created_at, updated_at, pause_reason
                FROM rollouts WHERE state = 'running';
                """;
            using var reader = cmd.ExecuteReader();
            var running = new List<RolloutRow>();
            while (reader.Read())
            {
                running.Add(ReadRollout(reader));
            }

            reader.Close();

            foreach (var rollout in running)
            {
                if (AdvanceRolloutUnlocked(rollout))
                {
                    changed.Add(rollout.FleetId);
                }
            }

            return changed.ToList();
        }
    }

    public PublishedConfig? GetPublished(long fleetId)
    {
        lock (_gate)
        {
            return GetPublishedUnlocked(fleetId);
        }
    }

    /// <summary>
    /// Config the agent should see. With <paramref name="nodeId"/>, uses that agent's
    /// <c>desired_revision_id</c> (staged exposure). Without it, returns fleet published target.
    /// </summary>
    public PublishedConfig? GetConfigForAgent(long fleetId, string? nodeId)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(nodeId))
            {
                var agent = GetAgentUnlocked(fleetId, nodeId);
                if (agent?.DesiredRevisionId is long desiredId)
                {
                    var rev = GetRevisionUnlocked(desiredId);
                    if (rev is not null)
                    {
                        return new PublishedConfig(rev.Id, rev.ContentHash, rev.YamlText);
                    }
                }

                // Known agent with no desired yet: during a staged rollout keep previous;
                // otherwise fall through to fleet published.
                var active = GetActiveRolloutUnlocked(fleetId);
                if (active is not null &&
                    active.PreviousRevisionId is long prevId &&
                    (string.Equals(active.State, StateRunning, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(active.State, StatePaused, StringComparison.OrdinalIgnoreCase)))
                {
                    var prev = GetRevisionUnlocked(prevId);
                    if (prev is not null)
                    {
                        return new PublishedConfig(prev.Id, prev.ContentHash, prev.YamlText);
                    }

                    return null;
                }
            }

            return GetPublishedUnlocked(fleetId);
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

    public AgentRow EnsureAgent(long fleetId, string nodeId, string labelsJson = "{}")
    {
        lock (_gate)
        {
            if (GetFleetUnlocked(fleetId) is null)
            {
                throw new KeyNotFoundException($"fleet {fleetId} not found");
            }

            var labels = string.IsNullOrWhiteSpace(labelsJson) ? "{}" : labelsJson;
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO agents (fleet_id, node_id, labels_json, status)
                VALUES ($f, $n, $l, 'unknown')
                ON CONFLICT(fleet_id, node_id) DO UPDATE SET
                  labels_json = excluded.labels_json;
                SELECT id, fleet_id, node_id, labels_json, last_seen, config_hash, health_json, status, desired_revision_id
                FROM agents WHERE fleet_id = $f AND node_id = $n;
                """;
            cmd.Parameters.AddWithValue("$f", fleetId);
            cmd.Parameters.AddWithValue("$n", nodeId);
            cmd.Parameters.AddWithValue("$l", labels);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("agent ensure failed");
            }

            return ReadAgent(reader);
        }
    }

    /// <summary>
    /// Upsert ack. Returns the agent row and whether a running rollout was auto-paused.
    /// </summary>
    public (AgentRow Agent, bool RolloutPaused) UpsertAgentAck(
        long fleetId,
        string nodeId,
        string? configHash,
        bool ok,
        string? error,
        string? healthJson,
        string? labelsJson = null)
    {
        lock (_gate)
        {
            var previous = GetAgentUnlocked(fleetId, nodeId);
            var status = ok ? "ok" : "error";
            var now = UtcNow();
            var health = healthJson;
            if (string.IsNullOrEmpty(health) && !string.IsNullOrEmpty(error))
            {
                health = $"{{\"error\":{JsonSerializer.Serialize(error)}}}";
            }

            var labels = string.IsNullOrWhiteSpace(labelsJson)
                ? previous?.LabelsJson ?? "{}"
                : labelsJson;

            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO agents (fleet_id, node_id, labels_json, last_seen, config_hash, health_json, status)
                VALUES ($f, $n, $l, $t, $h, $health, $s)
                ON CONFLICT(fleet_id, node_id) DO UPDATE SET
                  labels_json = excluded.labels_json,
                  last_seen = excluded.last_seen,
                  config_hash = excluded.config_hash,
                  health_json = excluded.health_json,
                  status = excluded.status;
                SELECT id, fleet_id, node_id, labels_json, last_seen, config_hash, health_json, status, desired_revision_id
                FROM agents WHERE fleet_id = $f AND node_id = $n;
                """;
            cmd.Parameters.AddWithValue("$f", fleetId);
            cmd.Parameters.AddWithValue("$n", nodeId);
            cmd.Parameters.AddWithValue("$l", labels);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.Parameters.AddWithValue("$h", (object?)configHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$health", (object?)health ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$s", status);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("agent upsert failed");
            }

            var row = ReadAgent(reader);
            reader.Close();

            var paused = false;
            var active = GetActiveRolloutUnlocked(fleetId);
            if (active is not null &&
                string.Equals(active.State, StateRunning, StringComparison.OrdinalIgnoreCase))
            {
                if (!ok)
                {
                    SetRolloutStateUnlocked(fleetId, active.Id, StatePaused, $"nack:{nodeId}");
                    paused = true;
                }
                else if (IsHealthRegression(previous?.HealthJson, health))
                {
                    SetRolloutStateUnlocked(fleetId, active.Id, StatePaused, $"health:{nodeId}");
                    paused = true;
                }
                else
                {
                    // Ready agents may allow the current wave to advance.
                    _ = AdvanceRolloutUnlocked(active);
                }
            }

            return (row, paused);
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
            return (new AgentTokenRow(id, fleetId, name, hash, ParseUtc(created), null), plain);
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
                    ParseUtc(reader.GetString(1)),
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

    public static bool LabelsMatch(string agentLabelsJson, IReadOnlyDictionary<string, string> selector)
    {
        if (selector.Count == 0)
        {
            return true;
        }

        var agent = ParseLabels(agentLabelsJson);
        foreach (var (key, value) in selector)
        {
            if (!agent.TryGetValue(key, out var have) ||
                !string.Equals(have, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public static bool LabelsMatchJson(string agentLabelsJson, string selectorJson) =>
        LabelsMatch(agentLabelsJson, ParseLabels(selectorJson));

    private bool AdvanceRolloutUnlocked(RolloutRow rollout)
    {
        if (!string.Equals(rollout.State, StateRunning, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var steps = ListStepsUnlocked(rollout.Id);
        var current = steps.FirstOrDefault(s =>
            string.Equals(s.State, StateRunning, StringComparison.OrdinalIgnoreCase));
        if (current is null)
        {
            return false;
        }

        var rev = GetRevisionUnlocked(rollout.RevisionId);
        if (rev is null)
        {
            return false;
        }

        var selector = ParseLabels(current.LabelSelectorJson);
        var agents = ListAgentsUnlocked(rollout.FleetId)
            .Where(a => LabelsMatch(a.LabelsJson, selector))
            .ToList();

        var ready = agents.Count(a =>
            a.DesiredRevisionId == rollout.RevisionId &&
            string.Equals(a.Status, "ok", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.ConfigHash, rev.ContentHash, StringComparison.OrdinalIgnoreCase));

        var unavailable = agents.Count - ready;
        if (unavailable > current.MaxUnavailable)
        {
            return false;
        }

        if (current.StartedAt is null)
        {
            return false;
        }

        var bakeElapsed = DateTimeOffset.UtcNow - current.StartedAt.Value;
        if (bakeElapsed < TimeSpan.FromSeconds(current.BakeSeconds))
        {
            return false;
        }

        var now = UtcNow();
        using var tx = _db.BeginTransaction();

        using var finish = _db.CreateCommand();
        finish.Transaction = tx;
        finish.CommandText =
            """
            UPDATE rollout_steps SET state = 'succeeded', finished_at = $t WHERE id = $id;
            """;
        finish.Parameters.AddWithValue("$t", now);
        finish.Parameters.AddWithValue("$id", current.Id);
        finish.ExecuteNonQuery();

        var next = steps.FirstOrDefault(s => s.Ordinal == current.Ordinal + 1);
        var changed = false;
        if (next is not null)
        {
            using var start = _db.CreateCommand();
            start.Transaction = tx;
            start.CommandText =
                """
                UPDATE rollout_steps SET state = 'running', started_at = $t WHERE id = $id;
                """;
            start.Parameters.AddWithValue("$t", now);
            start.Parameters.AddWithValue("$id", next.Id);
            start.ExecuteNonQuery();

            ExposeWaveUnlocked(
                rollout.FleetId,
                rollout.RevisionId,
                rollout.PreviousRevisionId,
                ParseLabels(next.LabelSelectorJson),
                tx);
            changed = true;
        }
        else
        {
            // All waves done and ready: mark succeeded. Ensure every agent has the new desired.
            using var setAll = _db.CreateCommand();
            setAll.Transaction = tx;
            setAll.CommandText = "UPDATE agents SET desired_revision_id = $r WHERE fleet_id = $f;";
            setAll.Parameters.AddWithValue("$r", rollout.RevisionId);
            setAll.Parameters.AddWithValue("$f", rollout.FleetId);
            setAll.ExecuteNonQuery();

            using var done = _db.CreateCommand();
            done.Transaction = tx;
            done.CommandText =
                """
                UPDATE rollouts SET state = 'succeeded', updated_at = $t, pause_reason = NULL WHERE id = $id;
                """;
            done.Parameters.AddWithValue("$t", now);
            done.Parameters.AddWithValue("$id", rollout.Id);
            done.ExecuteNonQuery();
            changed = true;
        }

        using var touch = _db.CreateCommand();
        touch.Transaction = tx;
        touch.CommandText = "UPDATE rollouts SET updated_at = $t WHERE id = $id;";
        touch.Parameters.AddWithValue("$t", now);
        touch.Parameters.AddWithValue("$id", rollout.Id);
        touch.ExecuteNonQuery();

        tx.Commit();
        return changed;
    }

    private void ExposeWaveUnlocked(
        long fleetId,
        long revisionId,
        long? previousRevisionId,
        IReadOnlyDictionary<string, string> matchLabels,
        SqliteTransaction tx)
    {
        _ = previousRevisionId; // non-matching agents keep their existing desired revision
        foreach (var agent in ListAgentsUnlocked(fleetId))
        {
            if (!LabelsMatch(agent.LabelsJson, matchLabels))
            {
                continue;
            }

            using var upd = _db.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE agents SET desired_revision_id = $r WHERE id = $id;";
            upd.Parameters.AddWithValue("$r", revisionId);
            upd.Parameters.AddWithValue("$id", agent.Id);
            upd.ExecuteNonQuery();
        }
    }

    private RolloutRow SetRolloutStateUnlocked(
        long fleetId,
        long rolloutId,
        string state,
        string? pauseReason)
    {
        var row = GetRolloutUnlocked(rolloutId)
                  ?? throw new KeyNotFoundException($"rollout {rolloutId} not found");
        if (row.FleetId != fleetId)
        {
            throw new KeyNotFoundException($"rollout {rolloutId} not in fleet {fleetId}");
        }

        using var cmd = _db.CreateCommand();
        cmd.CommandText =
            """
            UPDATE rollouts SET state = $s, updated_at = $t, pause_reason = $p WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$s", state);
        cmd.Parameters.AddWithValue("$t", UtcNow());
        cmd.Parameters.AddWithValue("$p", (object?)pauseReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", rolloutId);
        cmd.ExecuteNonQuery();
        return GetRolloutUnlocked(rolloutId)
               ?? throw new InvalidOperationException("rollout missing after state change");
    }

    private long InsertRolloutUnlocked( // NOSONAR S107 -- SQLite insert keeps column wiring explicit without a parameter bag.
        long fleetId,
        long revisionId,
        long? previousRevisionId,
        string state,
        string createdAt,
        string updatedAt,
        string? pauseReason,
        SqliteTransaction tx)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO rollouts (fleet_id, revision_id, previous_revision_id, state, created_at, updated_at, pause_reason)
            VALUES ($f, $r, $p, $s, $c, $u, $pr);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$f", fleetId);
        cmd.Parameters.AddWithValue("$r", revisionId);
        cmd.Parameters.AddWithValue("$p", (object?)previousRevisionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", state);
        cmd.Parameters.AddWithValue("$c", createdAt);
        cmd.Parameters.AddWithValue("$u", updatedAt);
        cmd.Parameters.AddWithValue("$pr", (object?)pauseReason ?? DBNull.Value);
        return (long)(cmd.ExecuteScalar() ?? throw new InvalidOperationException("rollout insert failed"));
    }

    private void InsertStepUnlocked( // NOSONAR S107 -- SQLite insert keeps column wiring explicit without a parameter bag.
        long rolloutId,
        int ordinal,
        string labelSelectorJson,
        int maxUnavailable,
        int bakeSeconds,
        string state,
        string? startedAt,
        string? finishedAt,
        SqliteTransaction tx)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO rollout_steps
              (rollout_id, ordinal, label_selector_json, max_unavailable, bake_seconds, state, started_at, finished_at)
            VALUES ($r, $o, $l, $m, $b, $s, $st, $fi);
            """;
        cmd.Parameters.AddWithValue("$r", rolloutId);
        cmd.Parameters.AddWithValue("$o", ordinal);
        cmd.Parameters.AddWithValue("$l", labelSelectorJson);
        cmd.Parameters.AddWithValue("$m", maxUnavailable);
        cmd.Parameters.AddWithValue("$b", bakeSeconds);
        cmd.Parameters.AddWithValue("$s", state);
        cmd.Parameters.AddWithValue("$st", (object?)startedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fi", (object?)finishedAt ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void SetFleetPublishedUnlocked(long fleetId, long revisionId, SqliteTransaction tx)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO fleet_published (fleet_id, revision_id) VALUES ($f, $r)
            ON CONFLICT(fleet_id) DO UPDATE SET revision_id = excluded.revision_id;
            """;
        cmd.Parameters.AddWithValue("$f", fleetId);
        cmd.Parameters.AddWithValue("$r", revisionId);
        cmd.ExecuteNonQuery();
    }

    private PublishedConfig? GetPublishedUnlocked(long fleetId)
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

    private RolloutRow? GetActiveRolloutUnlocked(long fleetId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, fleet_id, revision_id, previous_revision_id, state, created_at, updated_at, pause_reason
            FROM rollouts
            WHERE fleet_id = $f AND state IN ('pending', 'running', 'paused')
            ORDER BY id DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$f", fleetId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRollout(reader) : null;
    }

    private RolloutRow? GetRolloutUnlocked(long id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, fleet_id, revision_id, previous_revision_id, state, created_at, updated_at, pause_reason
            FROM rollouts WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRollout(reader) : null;
    }

    private List<RolloutStepRow> ListStepsUnlocked(long rolloutId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, rollout_id, ordinal, label_selector_json, max_unavailable, bake_seconds, state, started_at, finished_at
            FROM rollout_steps WHERE rollout_id = $r ORDER BY ordinal;
            """;
        cmd.Parameters.AddWithValue("$r", rolloutId);
        using var reader = cmd.ExecuteReader();
        var list = new List<RolloutStepRow>();
        while (reader.Read())
        {
            list.Add(ReadStep(reader));
        }

        return list;
    }

    private List<AgentRow> ListAgentsUnlocked(long fleetId)
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

    private AgentRow? GetAgentUnlocked(long fleetId, string nodeId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, fleet_id, node_id, labels_json, last_seen, config_hash, health_json, status, desired_revision_id
            FROM agents WHERE fleet_id = $f AND node_id = $n;
            """;
        cmd.Parameters.AddWithValue("$f", fleetId);
        cmd.Parameters.AddWithValue("$n", nodeId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadAgent(reader) : null;
    }

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

    private static bool IsHealthRegression(string? previousHealthJson, string? newHealthJson)
    {
        var before = ClassifyHealth(previousHealthJson);
        var after = ClassifyHealth(newHealthJson);
        return before == HealthClass.Healthy && after == HealthClass.Unhealthy;
    }

    private static HealthClass ClassifyHealth(string? healthJson)
    {
        if (string.IsNullOrWhiteSpace(healthJson))
        {
            return HealthClass.Unknown;
        }

        try
        {
            using var doc = JsonDocument.Parse(healthJson);
            if (doc.RootElement.TryGetProperty("status", out var status))
            {
                var s = status.ValueKind == JsonValueKind.String
                    ? status.GetString()
                    : status.ToString();
                return ClassifyHealthStatus(s);
            }
        }
        catch (JsonException)
        {
            return HealthClass.Unknown;
        }

        return HealthClass.Unknown;
    }

    private static HealthClass ClassifyHealthStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return HealthClass.Unknown;
        }

        if (status.Equals("ok", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("healthy", StringComparison.OrdinalIgnoreCase))
        {
            return HealthClass.Healthy;
        }

        if (status.Equals("failing", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("unhealthy", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("error", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("failed", StringComparison.OrdinalIgnoreCase))
        {
            return HealthClass.Unhealthy;
        }

        return HealthClass.Unknown;
    }

    private static Dictionary<string, string> ParseLabels(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.ToString();
            }
        }
        catch (JsonException)
        {
            // treat as empty
        }

        return result;
    }

    private static string SerializeLabels(IReadOnlyDictionary<string, string> labels) =>
        JsonSerializer.Serialize(labels);

    private static string UtcNow() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static FleetRow ReadFleet(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), ParseUtc(r.GetString(3)));

    private static RevisionRow ReadRevision(SqliteDataReader r) =>
        new(
            r.GetInt64(0),
            r.GetInt64(1),
            r.GetString(2),
            r.GetString(3),
            r.GetString(4),
            r.IsDBNull(5) ? null : r.GetInt64(5),
            ParseUtc(r.GetString(6)));

    private static AgentRow ReadAgent(SqliteDataReader r) =>
        new(
            r.GetInt64(0),
            r.GetInt64(1),
            r.GetString(2),
            r.GetString(3),
            r.IsDBNull(4) ? null : ParseUtc(r.GetString(4)),
            r.IsDBNull(5) ? null : r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            r.GetString(7),
            r.IsDBNull(8) ? null : r.GetInt64(8));

    private static RolloutRow ReadRollout(SqliteDataReader r) =>
        new(
            r.GetInt64(0),
            r.GetInt64(1),
            r.GetInt64(2),
            r.IsDBNull(3) ? null : r.GetInt64(3),
            r.GetString(4),
            ParseUtc(r.GetString(5)),
            r.IsDBNull(6) || string.IsNullOrEmpty(r.GetString(6))
                ? ParseUtc(r.GetString(5))
                : ParseUtc(r.GetString(6)),
            r.IsDBNull(7) ? null : r.GetString(7));

    private static RolloutStepRow ReadStep(SqliteDataReader r) =>
        new(
            r.GetInt64(0),
            r.GetInt64(1),
            r.GetInt32(2),
            r.GetString(3),
            r.GetInt32(4),
            r.GetInt32(5),
            r.GetString(6),
            r.IsDBNull(7) ? null : ParseUtc(r.GetString(7)),
            r.IsDBNull(8) ? null : ParseUtc(r.GetString(8)));

    private static AgentTokenRow ReadAgentToken(SqliteDataReader r) =>
        new(
            r.GetInt64(0),
            r.GetInt64(1),
            r.GetString(2),
            r.GetString(3),
            ParseUtc(r.GetString(4)),
            r.IsDBNull(5) ? null : ParseUtc(r.GetString(5)));

    private static AdminTokenRow ReadAdmin(SqliteDataReader r) =>
        new(
            r.GetInt64(0),
            r.GetString(1),
            r.GetString(2),
            r.GetString(3),
            ParseUtc(r.GetString(4)),
            r.IsDBNull(5) ? null : ParseUtc(r.GetString(5)));

    private enum HealthClass
    {
        Unknown,
        Healthy,
        Unhealthy,
    }
}
