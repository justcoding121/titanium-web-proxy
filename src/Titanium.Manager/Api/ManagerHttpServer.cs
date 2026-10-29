using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Titanium.Manager.Auth;
using Titanium.Manager.Store;
using Titanium.Manager.Ui;
using Titanium.Manager.Validation;

namespace Titanium.Manager.Api;

internal sealed class ManagerHttpServer : IAsyncDisposable
{
    /// <summary>Matches Plus <c>ControlHttpServer.MaxBodyBytes</c> — admin and agent request bodies.</summary>
    internal const int MaxBodyBytes = ManagerRequestBody.MaxBodyBytes;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ManagerStore _store;
    private readonly ConfigWaitHub _waitHub = new();
    private readonly ManagerUiHost _ui;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private Task? _ticker;

    public ManagerHttpServer(ManagerStore store, string listenPrefix, TimeProvider? timeProvider = null)
    {
        _store = store;
        _ui = new ManagerUiHost(store, _waitHub, timeProvider);
        var prefix = listenPrefix.EndsWith('/') ? listenPrefix : listenPrefix + "/";
        _listener.Prefixes.Add(prefix);
        BaseUrl = prefix;
    }

    public string BaseUrl { get; }

    /// <summary>Browser sessions for the HTML UI (tests assert login/CSRF).</summary>
    internal ManagerSessionStore Sessions => _ui.Sessions;

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync, _stop.Token);
        _ticker = Task.Run(RolloutTickLoopAsync, _stop.Token);
    }

    private async Task RolloutTickLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var changed = _store.TickRollouts();
                foreach (var fleetId in changed)
                {
                    _waitHub.NotifyFleet(fleetId);
                }
            }
            catch
            {
                // keep ticker alive
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException or OperationCanceledException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context), _stop.Token);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";
            if (path.Length == 0)
            {
                path = "/";
            }

            var method = context.Request.HttpMethod.ToUpperInvariant();
            if (method == "GET" && path.Equals("/v1/healthz", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context, 200, new { status = "ok" }).ConfigureAwait(false);
                return;
            }

            if (await _ui.TryHandleAsync(context, method, path).ConfigureAwait(false))
            {
                return;
            }

            if (path.StartsWith("/v1/agent/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleAgentAsync(context, method, path).ConfigureAwait(false);
                return;
            }

            try
            {
                await HandleAdminAsync(context, method, path).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await WriteTextAsync(context, 403, "forbidden").ConfigureAwait(false);
            }
            catch (RequestTooLargeException)
            {
                await WriteTextAsync(context, 413, "request too large").ConfigureAwait(false);
            }
        }
        catch (RequestTooLargeException)
        {
            try
            {
                await WriteTextAsync(context, 413, "request too large").ConfigureAwait(false);
            }
            catch
            {
                /* ignore */
            }
        }
        catch (Exception)
        {
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
                /* ignore */
            }
        }
    }

    private async Task HandleAgentAsync(HttpListenerContext context, string method, string path)
    {
        var token = ReadBearer(context.Request);
        var agent = token is null ? null : _store.FindAgentByToken(token);
        if (agent is null)
        {
            await WriteTextAsync(context, 401, "unauthorized").ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path.Equals("/v1/agent/config", StringComparison.OrdinalIgnoreCase))
        {
            await LongPollConfigAsync(context, agent).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path.Equals("/v1/agent/ack", StringComparison.OrdinalIgnoreCase))
        {
            await AgentAckAsync(context, agent).ConfigureAwait(false);
            return;
        }

        await WriteTextAsync(context, 404, "not found").ConfigureAwait(false);
    }

    private async Task LongPollConfigAsync(HttpListenerContext context, AgentTokenRow agent)
    {
        var qs = context.Request.QueryString;
        var fleetId = ParseFleetQuery(qs, agent.ClusterId);
        if (fleetId is null)
        {
            await WriteTextAsync(context, 400, "fleet required").ConfigureAwait(false);
            return;
        }

        if (fleetId.Value != agent.ClusterId)
        {
            await WriteTextAsync(context, 403, "token not scoped to fleet").ConfigureAwait(false);
            return;
        }

        var have = qs["have"] ?? "";
        var nodeId = qs["node_id"] ?? qs["nodeId"];
        var waitSec = 30;
        if (int.TryParse(qs["wait"], out var w))
        {
            waitSec = Math.Clamp(w, 0, 120);
        }

        var published = _store.GetConfigForAgent(fleetId.Value, nodeId);
        if (published is not null && !string.Equals(published.ContentHash, have, StringComparison.OrdinalIgnoreCase))
        {
            await WriteConfigAsync(context, published).ConfigureAwait(false);
            return;
        }

        if (waitSec > 0)
        {
            await _waitHub.WaitAsync(fleetId.Value, TimeSpan.FromSeconds(waitSec), _stop.Token).ConfigureAwait(false);
            published = _store.GetConfigForAgent(fleetId.Value, nodeId);
            if (published is not null && !string.Equals(published.ContentHash, have, StringComparison.OrdinalIgnoreCase))
            {
                await WriteConfigAsync(context, published).ConfigureAwait(false);
                return;
            }
        }

        context.Response.StatusCode = 204;
        context.Response.Close();
    }

    private async Task AgentAckAsync(HttpListenerContext context, AgentTokenRow agent)
    {
        var body = await ReadBodyBytesAsync(context).ConfigureAwait(false);
        if (!AgentAckPayload.TryParse(body, out var ack, out var parseError) || ack is null)
        {
            await WriteTextAsync(context, 400, parseError ?? "invalid ack body").ConfigureAwait(false);
            return;
        }

        var fleet = ack.Fleet ?? agent.ClusterId;
        if (fleet != agent.ClusterId)
        {
            await WriteTextAsync(context, 403, "token not scoped to fleet").ConfigureAwait(false);
            return;
        }

        var nodeId = ack.NodeId;
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            await WriteTextAsync(context, 400, "node_id required").ConfigureAwait(false);
            return;
        }

        var configHash = ack.ConfigHash;
        var ok = ack.Ok;
        var error = ack.Error;
        var healthJson = ack.HealthJson;
        var labelsJson = ack.LabelsJson;

        var (row, paused) = _store.UpsertAgentAck(fleet, nodeId!, configHash, ok, error, healthJson, labelsJson);
        if (paused)
        {
            _waitHub.NotifyFleet(fleet);
        }
        else
        {
            foreach (var fleetId in _store.TickRollouts())
            {
                _waitHub.NotifyFleet(fleetId);
            }
        }

        await WriteJsonAsync(context, 200, new
        {
            id = row.Id,
            fleetId = row.FleetId,
            nodeId = row.NodeId,
            status = row.Status,
            configHash = row.ConfigHash,
            lastSeen = row.LastSeen,
            rolloutPaused = paused,
        }).ConfigureAwait(false);
    }

    private async Task HandleAdminAsync(HttpListenerContext context, string method, string path)
    {
        if (!TryAuthenticateAdmin(context, method, out var role, out var actor))
        {
            return;
        }

        if (method == "GET" && path.Equals("/v1/fleets", StringComparison.OrdinalIgnoreCase))
        {
            Require(role, AdminRole.Viewer);
            var fleets = _store.ListFleets().Select(f => new
            {
                id = f.Id,
                name = f.Name,
                labelsJson = f.LabelsJson,
                createdAt = f.CreatedAt,
            });
            await WriteJsonAsync(context, 200, fleets).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path.Equals("/v1/fleets", StringComparison.OrdinalIgnoreCase))
        {
            Require(role, AdminRole.Editor);
            using var doc = await ReadJsonAsync(context).ConfigureAwait(false);
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                await WriteTextAsync(context, 400, "name required").ConfigureAwait(false);
                return;
            }

            var labels = doc.RootElement.TryGetProperty("labelsJson", out var l) ? l.GetString()
                : doc.RootElement.TryGetProperty("labels", out var l2) ? l2.GetRawText() : "{}";
            var fleet = _store.CreateFleet(name!, labels ?? "{}");
            _store.Audit(actor, "fleet.create", $"fleet:{fleet.Id}", "ok", fleet.Name);
            await WriteJsonAsync(context, 201, new
            {
                id = fleet.Id,
                name = fleet.Name,
                labelsJson = fleet.LabelsJson,
                createdAt = fleet.CreatedAt,
            }).ConfigureAwait(false);
            return;
        }

        if (TryMatch(path, "/v1/fleets/", out var fleetRest))
        {
            await HandleFleetRoutesAsync(context, method, fleetRest, role, actor).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path.Equals("/v1/agent-tokens", StringComparison.OrdinalIgnoreCase))
        {
            Require(role, AdminRole.Editor);
            using var doc = await ReadJsonAsync(context).ConfigureAwait(false);
            var fleetId = ReadLong(doc.RootElement, "fleetId", "fleet_id", "clusterId", "cluster_id");
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : "agent";
            if (fleetId is null)
            {
                await WriteTextAsync(context, 400, "fleetId required").ConfigureAwait(false);
                return;
            }

            try
            {
                var (row, plain) = _store.CreateAgentToken(fleetId.Value, name ?? "agent");
                _store.Audit(actor, "agent-token.create", $"token:{row.Id}", "ok", $"fleet:{fleetId}");
                await WriteJsonAsync(context, 201, new
                {
                    id = row.Id,
                    fleetId = row.ClusterId,
                    clusterId = row.ClusterId,
                    name = row.Name,
                    token = plain,
                    createdAt = row.CreatedAt,
                }).ConfigureAwait(false);
            }
            catch (KeyNotFoundException ex)
            {
                await WriteTextAsync(context, 404, ex.Message).ConfigureAwait(false);
            }

            return;
        }

        if (method == "POST" && TryMatch(path, "/v1/agent-tokens/", out var tokenRest) &&
            tokenRest.EndsWith("/revoke", StringComparison.OrdinalIgnoreCase))
        {
            Require(role, AdminRole.Admin);
            var idPart = tokenRest[..^"/revoke".Length].Trim('/');
            if (!long.TryParse(idPart, out var tokenId))
            {
                await WriteTextAsync(context, 400, "invalid token id").ConfigureAwait(false);
                return;
            }

            var revoked = _store.RevokeAgentToken(tokenId);
            _store.Audit(actor, "agent-token.revoke", $"token:{tokenId}", revoked ? "ok" : "missing");
            await WriteJsonAsync(context, revoked ? 200 : 404, new { id = tokenId, revoked }).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path.Equals("/v1/audit", StringComparison.OrdinalIgnoreCase))
        {
            Require(role, AdminRole.Viewer);
            var rows = _store.ListAudit().Select(a => new
            {
                id = a.Id,
                time = a.Time,
                actor = a.Actor,
                action = a.Action,
                target = a.Target,
                result = a.Result,
                detail = a.Detail,
            });
            await WriteJsonAsync(context, 200, rows).ConfigureAwait(false);
            return;
        }

        await WriteTextAsync(context, 404, "not found").ConfigureAwait(false);
    }

    private async Task HandleFleetRoutesAsync(
        HttpListenerContext context,
        string method,
        string rest,
        AdminRole role,
        string actor)
    {
        var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || !long.TryParse(segments[0], out var fleetId))
        {
            await WriteTextAsync(context, 400, "invalid fleet id").ConfigureAwait(false);
            return;
        }

        if (_store.GetFleet(fleetId) is null)
        {
            await WriteTextAsync(context, 404, "fleet not found").ConfigureAwait(false);
            return;
        }

        if (segments.Length == 1)
        {
            if (method != "GET")
            {
                await WriteTextAsync(context, 405, "method not allowed").ConfigureAwait(false);
                return;
            }

            Require(role, AdminRole.Viewer);
            var fleet = _store.GetFleet(fleetId)!;
            var published = _store.GetPublished(fleetId);
            await WriteJsonAsync(context, 200, new
            {
                id = fleet.Id,
                name = fleet.Name,
                labelsJson = fleet.LabelsJson,
                createdAt = fleet.CreatedAt,
                publishedRevisionId = published?.RevisionId,
                publishedHash = published?.ContentHash,
            }).ConfigureAwait(false);
            return;
        }

        var action = segments[1];

        if (action.Equals("validate", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            Require(role, AdminRole.Editor);
            var yaml = await ReadYamlBodyAsync(context).ConfigureAwait(false);
            var errors = ConfigRevisionValidator.ValidateYaml(yaml);
            await WriteJsonAsync(context, errors.Count == 0 ? 200 : 400, new { ok = errors.Count == 0, errors }).ConfigureAwait(false);
            return;
        }

        if (action.Equals("revisions", StringComparison.OrdinalIgnoreCase))
        {
            await HandleRevisionsAsync(context, method, fleetId, segments, role, actor).ConfigureAwait(false);
            return;
        }

        if (action.Equals("rollouts", StringComparison.OrdinalIgnoreCase))
        {
            await HandleRolloutsAsync(context, method, fleetId, segments, role, actor).ConfigureAwait(false);
            return;
        }

        if (action.Equals("agents", StringComparison.OrdinalIgnoreCase) && method == "GET" && segments.Length == 2)
        {
            Require(role, AdminRole.Viewer);
            var agents = _store.ListAgents(fleetId).Select(a => new
            {
                id = a.Id,
                fleetId = a.FleetId,
                nodeId = a.NodeId,
                labelsJson = a.LabelsJson,
                lastSeen = a.LastSeen,
                configHash = a.ConfigHash,
                healthJson = a.HealthJson,
                status = a.Status,
                desiredRevisionId = a.DesiredRevisionId,
            });
            await WriteJsonAsync(context, 200, agents).ConfigureAwait(false);
            return;
        }

        if (action.Equals("diff", StringComparison.OrdinalIgnoreCase) && method == "GET" && segments.Length == 2)
        {
            Require(role, AdminRole.Viewer);
            var qs = context.Request.QueryString;
            if (!long.TryParse(qs["from"], out var fromId) || !long.TryParse(qs["to"], out var toId))
            {
                await WriteTextAsync(context, 400, "from and to revision ids required").ConfigureAwait(false);
                return;
            }

            var from = _store.GetRevision(fleetId, fromId);
            var to = _store.GetRevision(fleetId, toId);
            if (from is null || to is null)
            {
                await WriteTextAsync(context, 404, "revision not found").ConfigureAwait(false);
                return;
            }

            var format = qs["format"] ?? "unified";
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context, 200, new
                {
                    from = fromId,
                    to = toId,
                    fromHash = from.ContentHash,
                    toHash = to.ContentHash,
                    unified = YamlDiff.Unified(from.YamlText, to.YamlText, $"rev-{fromId}", $"rev-{toId}"),
                }).ConfigureAwait(false);
                return;
            }

            var text = YamlDiff.Unified(from.YamlText, to.YamlText, $"rev-{fromId}", $"rev-{toId}");
            await WriteTextAsync(context, 200, text, "text/plain; charset=utf-8").ConfigureAwait(false);
            return;
        }

        await WriteTextAsync(context, 404, "not found").ConfigureAwait(false);
    }

    private async Task HandleRolloutsAsync(
        HttpListenerContext context,
        string method,
        long fleetId,
        string[] segments,
        AdminRole role,
        string actor)
    {
        if (segments.Length == 2 && method == "GET")
        {
            Require(role, AdminRole.Viewer);
            var list = _store.ListRollouts(fleetId).Select(r => new
            {
                id = r.Id,
                fleetId = r.FleetId,
                revisionId = r.RevisionId,
                previousRevisionId = r.PreviousRevisionId,
                state = r.State,
                createdAt = r.CreatedAt,
                updatedAt = r.UpdatedAt,
                pauseReason = r.PauseReason,
            });
            await WriteJsonAsync(context, 200, list).ConfigureAwait(false);
            return;
        }

        if (segments.Length >= 3 && long.TryParse(segments[2], out var rolloutId))
        {
            if (segments.Length == 3 && method == "GET")
            {
                Require(role, AdminRole.Viewer);
                var rollout = _store.GetRollout(fleetId, rolloutId);
                if (rollout is null)
                {
                    await WriteTextAsync(context, 404, "rollout not found").ConfigureAwait(false);
                    return;
                }

                var steps = _store.ListRolloutSteps(rolloutId).Select(s => new
                {
                    id = s.Id,
                    ordinal = s.Ordinal,
                    labelSelectorJson = s.LabelSelectorJson,
                    maxUnavailable = s.MaxUnavailable,
                    bakeSeconds = s.BakeSeconds,
                    state = s.State,
                    startedAt = s.StartedAt,
                    finishedAt = s.FinishedAt,
                });
                await WriteJsonAsync(context, 200, new
                {
                    id = rollout.Id,
                    fleetId = rollout.FleetId,
                    revisionId = rollout.RevisionId,
                    previousRevisionId = rollout.PreviousRevisionId,
                    state = rollout.State,
                    createdAt = rollout.CreatedAt,
                    updatedAt = rollout.UpdatedAt,
                    pauseReason = rollout.PauseReason,
                    steps,
                }).ConfigureAwait(false);
                return;
            }

            if (segments.Length == 4 && method == "POST")
            {
                var verb = segments[3];
                try
                {
                    if (verb.Equals("pause", StringComparison.OrdinalIgnoreCase))
                    {
                        Require(role, AdminRole.Editor);
                        var paused = _store.PauseRollout(fleetId, rolloutId, "manual");
                        _store.Audit(actor, "rollout.pause", $"rollout:{rolloutId}", "ok", paused.PauseReason);
                        await WriteRolloutAsync(context, paused).ConfigureAwait(false);
                        return;
                    }

                    if (verb.Equals("resume", StringComparison.OrdinalIgnoreCase))
                    {
                        Require(role, AdminRole.Editor);
                        var resumed = _store.ResumeRollout(fleetId, rolloutId);
                        foreach (var id in _store.TickRollouts())
                        {
                            _waitHub.NotifyFleet(id);
                        }

                        _store.Audit(actor, "rollout.resume", $"rollout:{rolloutId}", "ok");
                        await WriteRolloutAsync(context, resumed).ConfigureAwait(false);
                        return;
                    }

                    if (verb.Equals("rollback", StringComparison.OrdinalIgnoreCase))
                    {
                        Require(role, AdminRole.Editor);
                        var rolled = _store.RollbackRollout(fleetId, rolloutId);
                        _waitHub.NotifyFleet(fleetId);
                        _store.Audit(actor, "rollout.rollback", $"rollout:{rolloutId}", "ok",
                            $"previous:{rolled.PreviousRevisionId}");
                        await WriteRolloutAsync(context, rolled).ConfigureAwait(false);
                        return;
                    }
                }
                catch (KeyNotFoundException ex)
                {
                    await WriteTextAsync(context, 404, ex.Message).ConfigureAwait(false);
                    return;
                }
                catch (InvalidOperationException ex)
                {
                    await WriteTextAsync(context, 409, ex.Message).ConfigureAwait(false);
                    return;
                }
            }
        }

        await WriteTextAsync(context, 404, "not found").ConfigureAwait(false);
    }

    private async Task HandleRevisionsAsync(
        HttpListenerContext context,
        string method,
        long fleetId,
        string[] segments,
        AdminRole role,
        string actor)
    {
        if (segments.Length == 2 && method == "GET")
        {
            Require(role, AdminRole.Viewer);
            var list = _store.ListRevisions(fleetId).Select(r => new
            {
                id = r.Id,
                fleetId = r.FleetId,
                contentHash = r.ContentHash,
                author = r.Author,
                parentId = r.ParentId,
                createdAt = r.CreatedAt,
            });
            await WriteJsonAsync(context, 200, list).ConfigureAwait(false);
            return;
        }

        if (segments.Length == 2 && method == "POST")
        {
            Require(role, AdminRole.Editor);
            var body = await ManagerRequestBody.ReadUtf8Async(context).ConfigureAwait(false);
            var author = actor;
            var yaml = body;
            if (IsJsonRequest(context.Request))
            {
                try
                {
                    using var envelope = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    if (envelope.RootElement.TryGetProperty("yaml", out var y))
                    {
                        yaml = y.GetString() ?? "";
                    }

                    if (envelope.RootElement.TryGetProperty("author", out var a) &&
                        a.GetString() is { Length: > 0 } authorName)
                    {
                        author = authorName;
                    }
                }
                catch (JsonException)
                {
                    yaml = body;
                }
            }

            var errors = ConfigRevisionValidator.ValidateYaml(yaml);
            if (errors.Count > 0)
            {
                _store.Audit(actor, "revision.create", $"fleet:{fleetId}", "rejected", string.Join("; ", errors));
                await WriteJsonAsync(context, 400, new { ok = false, errors }).ConfigureAwait(false);
                return;
            }

            var rev = _store.CreateRevision(fleetId, yaml, author);
            _store.Audit(actor, "revision.create", $"revision:{rev.Id}", "ok", rev.ContentHash);
            await WriteJsonAsync(context, 201, new
            {
                id = rev.Id,
                fleetId = rev.FleetId,
                contentHash = rev.ContentHash,
                author = rev.Author,
                parentId = rev.ParentId,
                createdAt = rev.CreatedAt,
            }).ConfigureAwait(false);
            return;
        }

        if (segments.Length >= 3 && long.TryParse(segments[2], out var revId))
        {
            if (segments.Length == 3 && method == "GET")
            {
                Require(role, AdminRole.Viewer);
                var rev = _store.GetRevision(fleetId, revId);
                if (rev is null)
                {
                    await WriteTextAsync(context, 404, "revision not found").ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(context, 200, new
                {
                    id = rev.Id,
                    fleetId = rev.FleetId,
                    contentHash = rev.ContentHash,
                    yaml = rev.YamlText,
                    author = rev.Author,
                    parentId = rev.ParentId,
                    createdAt = rev.CreatedAt,
                }).ConfigureAwait(false);
                return;
            }

            if (segments.Length == 4 &&
                segments[3].Equals("publish", StringComparison.OrdinalIgnoreCase) &&
                method == "POST")
            {
                Require(role, AdminRole.Editor);
                try
                {
                    var (waves, bakeSeconds) = await ReadPublishBodyAsync(context).ConfigureAwait(false);
                    var rollout = _store.Publish(fleetId, revId, waves, bakeSeconds);
                    _waitHub.NotifyFleet(fleetId);
                    _store.Audit(actor, "revision.publish", $"revision:{revId}", "ok", $"rollout:{rollout.Id}");
                    var steps = _store.ListRolloutSteps(rollout.Id).Select(s => new
                    {
                        id = s.Id,
                        ordinal = s.Ordinal,
                        labelSelectorJson = s.LabelSelectorJson,
                        maxUnavailable = s.MaxUnavailable,
                        bakeSeconds = s.BakeSeconds,
                        state = s.State,
                        startedAt = s.StartedAt,
                        finishedAt = s.FinishedAt,
                    });
                    await WriteJsonAsync(context, 200, new
                    {
                        id = rollout.Id,
                        fleetId = rollout.FleetId,
                        revisionId = rollout.RevisionId,
                        previousRevisionId = rollout.PreviousRevisionId,
                        state = rollout.State,
                        createdAt = rollout.CreatedAt,
                        updatedAt = rollout.UpdatedAt,
                        pauseReason = rollout.PauseReason,
                        steps,
                    }).ConfigureAwait(false);
                }
                catch (KeyNotFoundException ex)
                {
                    await WriteTextAsync(context, 404, ex.Message).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    await WriteTextAsync(context, 409, ex.Message).ConfigureAwait(false);
                }

                return;
            }
        }

        await WriteTextAsync(context, 404, "not found").ConfigureAwait(false);
    }

    private static async Task<(IReadOnlyList<PublishWaveSpec>? Waves, int? BakeSeconds)> ReadPublishBodyAsync(
        HttpListenerContext context)
    {
        var body = await ManagerRequestBody.ReadUtf8Async(context).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            int? bakeSeconds = null;
            if (root.TryGetProperty("bakeSeconds", out var bake) && bake.TryGetInt32(out var b))
            {
                bakeSeconds = b;
            }

            if (!root.TryGetProperty("waves", out var wavesEl) || wavesEl.ValueKind != JsonValueKind.Array)
            {
                return (null, bakeSeconds);
            }

            var waves = new List<PublishWaveSpec>();
            foreach (var wave in wavesEl.EnumerateArray())
            {
                var labels = new Dictionary<string, string>(StringComparer.Ordinal);
                if (wave.TryGetProperty("matchLabels", out var ml) && ml.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in ml.EnumerateObject())
                    {
                        labels[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString() ?? ""
                            : prop.Value.ToString();
                    }
                }

                var maxUnavailable = 0;
                if (wave.TryGetProperty("maxUnavailable", out var mu) && mu.TryGetInt32(out var m))
                {
                    maxUnavailable = m;
                }

                var waveBake = bakeSeconds ?? 0;
                if (wave.TryGetProperty("bakeSeconds", out var wb) && wb.TryGetInt32(out var wbVal))
                {
                    waveBake = wbVal;
                }

                waves.Add(new PublishWaveSpec(labels, maxUnavailable, waveBake));
            }

            return (waves.Count == 0 ? null : waves, bakeSeconds);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private async Task WriteRolloutAsync(HttpListenerContext context, RolloutRow rollout)
    {
        var steps = _store.ListRolloutSteps(rollout.Id).Select(s => new
        {
            id = s.Id,
            ordinal = s.Ordinal,
            labelSelectorJson = s.LabelSelectorJson,
            maxUnavailable = s.MaxUnavailable,
            bakeSeconds = s.BakeSeconds,
            state = s.State,
            startedAt = s.StartedAt,
            finishedAt = s.FinishedAt,
        });
        await WriteJsonAsync(context, 200, new
        {
            id = rollout.Id,
            fleetId = rollout.FleetId,
            revisionId = rollout.RevisionId,
            previousRevisionId = rollout.PreviousRevisionId,
            state = rollout.State,
            createdAt = rollout.CreatedAt,
            updatedAt = rollout.UpdatedAt,
            pauseReason = rollout.PauseReason,
            steps,
        }).ConfigureAwait(false);
    }

    private static void Require(AdminRole have, AdminRole need)
    {
        if (have < need)
        {
            throw new UnauthorizedAccessException("insufficient role");
        }
    }

    /// <summary>
    ///     Bearer admin token (scripts) or UI session cookie. Session mutations require <see cref="ManagerUiHost.CsrfHeader"/>.
    /// </summary>
    private bool TryAuthenticateAdmin(
        HttpListenerContext context,
        string method,
        out AdminRole role,
        out string actor)
    {
        role = AdminRole.Viewer;
        actor = "";

        var token = ReadBearer(context.Request);
        var admin = token is null ? null : _store.FindAdminByToken(token);
        if (admin is not null && AdminRoleParser.TryParse(admin.Role, out role))
        {
            actor = $"admin:{admin.Name}:{AdminRoleParser.ToApi(role)}";
            return true;
        }

        var session = _ui.FindSession(context.Request, touch: true);
        if (session is null)
        {
            WriteTextAsync(context, 401, "unauthorized").GetAwaiter().GetResult();
            return false;
        }

        if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            !method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) &&
            !ManagerSessionStore.CsrfMatches(session, context.Request.Headers[ManagerUiHost.CsrfHeader]))
        {
            WriteTextAsync(context, 403, "csrf token missing or wrong").GetAwaiter().GetResult();
            return false;
        }

        role = session.Role;
        actor = session.Actor;
        return true;
    }

    private static long? ParseFleetQuery(NameValueCollection qs, long fallback)
    {
        var raw = qs["fleet"] ?? qs["fleetId"];
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        return long.TryParse(raw, out var id) ? id : null;
    }

    private static string? ReadBearer(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(header))
        {
            // Also accept X-Titanium-Admin-Token for admin convenience
            return request.Headers["X-Titanium-Admin-Token"] ?? request.Headers["X-Titanium-Agent-Token"];
        }

        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : header.Trim();
    }

    private static bool TryMatch(string path, string prefix, out string rest)
    {
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = path[prefix.Length..];
            return true;
        }

        rest = "";
        return false;
    }

    private static long? ReadLong(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var el))
            {
                continue;
            }

            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n))
            {
                return n;
            }

            if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var p))
            {
                return p;
            }
        }

        return null;
    }

    private static bool IsJsonRequest(HttpListenerRequest request) =>
        request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;

    private static async Task<string> ReadYamlBodyAsync(HttpListenerContext context)
    {
        var body = await ManagerRequestBody.ReadUtf8Async(context).ConfigureAwait(false);
        if (!IsJsonRequest(context.Request))
        {
            return body;
        }

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (doc.RootElement.TryGetProperty("yaml", out var y))
            {
                return y.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // fall through
        }

        return body;
    }

    private static Task<byte[]> ReadBodyBytesAsync(HttpListenerContext context) =>
        ManagerRequestBody.ReadBytesAsync(context);

    private static Task<JsonDocument> ReadJsonAsync(HttpListenerContext context) =>
        ManagerRequestBody.ReadJsonAsync(context);

    private static async Task WriteConfigAsync(HttpListenerContext context, PublishedConfig published)
    {
        await WriteJsonAsync(context, 200, new
        {
            revisionId = published.RevisionId,
            hash = published.ContentHash,
            configHash = published.ContentHash,
            yaml = published.YamlText,
        }).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int status, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static async Task WriteTextAsync(
        HttpListenerContext context,
        int status,
        string text,
        string contentType = "text/plain; charset=utf-8")
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            _listener.Stop();
        }
        catch
        {
            /* ignore */
        }

        try
        {
            _listener.Close();
        }
        catch
        {
            /* ignore */
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                /* ignore */
            }
        }

        if (_ticker is not null)
        {
            try
            {
                await _ticker.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                /* ignore */
            }
        }

        _stop.Dispose();
    }
}
