using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus.Manager;

/// <summary>
/// Outbound Plus manager agent: long-polls <c>GET /v1/agent/config</c>, applies via
/// <see cref="IConfigApplier"/> in managed mode (ack/nack), reports drift in advisory mode,
/// and keeps a last-known-good yaml+hash under the state directory.
/// Default-off: <see cref="TryStart"/> returns null without <c>manager.url</c>.
/// </summary>
public sealed class ManagerAgent : IDisposable
{
    /// <summary>Live agent poll loops. Guardrail tests assert this stays 0 without <c>manager.url</c>.</summary>
    public static int LiveLoops => Volatile.Read(ref _liveLoops);

    private static int _liveLoops;

    public const string LastKnownGoodYamlFileName = "last-known-good.yaml";
    public const string LastKnownGoodHashFileName = "last-known-good.hash";

    private static readonly TimeSpan DefaultApplyDebounce = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly PlusActivationContext _context;
    private readonly string _baseUrl;
    private readonly string _token;
    private readonly string _fleet;
    private readonly string _nodeId;
    private readonly string? _labelsJson;
    private readonly bool _managed;
    private readonly string _stateDir;
    private readonly TimeSpan _applyDebounce;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _applyGate = new();
    private Task? _loop;
    /// <summary>Local applied / file hash (drives drift).</summary>
    private string _haveHash = "";
    /// <summary>Hash sent as <c>have=</c> on long-poll so identical manager revisions are not re-delivered.</summary>
    private string _pollHave = "";
    private string? _desiredHash;
    private DateTimeOffset _lastApplyUtc = DateTimeOffset.MinValue;
    private PendingRevision? _pending;
    private bool _disposed;

    private ManagerAgent(
        PlusActivationContext context,
        string baseUrl,
        string token,
        string fleet,
        string nodeId,
        string? labelsJson,
        bool managed,
        string stateDir,
        TimeSpan applyDebounce,
        HttpClient http)
    {
        _context = context;
        _baseUrl = baseUrl;
        _token = token;
        _fleet = fleet;
        _nodeId = nodeId;
        _labelsJson = labelsJson;
        _managed = managed;
        _stateDir = stateDir;
        _applyDebounce = applyDebounce;
        _http = http;
        Interlocked.Increment(ref _liveLoops);
    }

    /// <summary>
    /// Starts the agent when <c>manager.url</c> is set. Returns null (and leaves <see cref="LiveLoops"/> unchanged)
    /// when manager enrollment is not configured.
    /// </summary>
    public static ManagerAgent? TryStart(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        if (!TryGet(options, out var url, "manager.url") || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var token = ReadToken(options);
        if (string.IsNullOrWhiteSpace(token))
        {
            PlusLog.Warn(context, "Plus ManagerAgent: manager.url set but manager.tokenFile is missing or empty — agent not started.");
            return null;
        }

        var fleet = First(options, "manager.fleet", "manager.fleetId") ?? "";
        var nodeId = First(options, "manager.nodeId", "manager.node_id") ?? Environment.MachineName;
        var labelsJson = ParseLabels(First(options, "manager.labels"));
        var mode = (First(options, "manager.mode") ?? "advisory").Trim();
        var managed = mode.Equals("managed", StringComparison.OrdinalIgnoreCase);

        if (managed && HasGatewayController(options))
        {
            PlusLog.Warn(context,
                "Plus ManagerAgent: manager.mode=managed conflicts with gateway controller options — forcing advisory.");
            managed = false;
        }

        var stateDir = First(options, "manager.stateDir", "manager.stateDirectory")
                       ?? context.StateDirectory
                       ?? Path.Combine(Environment.CurrentDirectory, "state");
        stateDir = Path.GetFullPath(stateDir);

        var debounce = DefaultApplyDebounce;
        if (int.TryParse(First(options, "manager.applyDebounceMs"), out var ms) && ms >= 0)
        {
            debounce = TimeSpan.FromMilliseconds(ms);
        }
        else if (int.TryParse(First(options, "manager.applyDebounceSeconds"), out var sec) && sec >= 0)
        {
            debounce = TimeSpan.FromSeconds(sec);
        }

        var baseUrl = url!.Trim().TrimEnd('/') + "/";
        var insecure = IsTrue(First(options, "manager.tls.insecure", "manager.tls"));
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (insecure)
        {
            // Explicit operator opt-in: manager.tls.insecure disables upstream cert validation (lab/dev only).
            handler.SslOptions.RemoteCertificateValidationCallback =
                static (_, _, _, _) => true; // NOSONAR S4830 -- manager.tls.insecure is an explicit opt-in.
        }

        var http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            BaseAddress = new Uri(baseUrl),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!);

        var agent = new ManagerAgent(
            context, baseUrl, token!, fleet, nodeId, labelsJson, managed, stateDir, debounce, http);
        var localHash = context.HostStatus?.GetConfigStatus().ConfigHash;
        if (!string.IsNullOrWhiteSpace(localHash))
        {
            agent._haveHash = localHash!;
        }

        context.HostStatus?.SetManagedState(true, drifted: false);
        PlusLog.Info(context,
            $"Plus ManagerAgent: url={baseUrl} fleet={fleet} mode={(managed ? "managed" : "advisory")} state={stateDir}");
        agent._loop = Task.Run(agent.RunLoopAsync, agent._cts.Token);
        return agent;
    }

    /// <summary>
    /// Reads last-known-good yaml+hash written by a previous successful managed apply.
    /// CLI / hosts may prefer this when <c>manager.url</c> is set and the manager is unreachable at start;
    /// the agent also applies LKG on first-poll failure in managed mode when <see cref="IConfigApplier"/> is set.
    /// </summary>
    public static bool TryReadLastKnownGood(string? stateDirectory, out string yaml, out string hash)
    {
        yaml = "";
        hash = "";
        if (string.IsNullOrWhiteSpace(stateDirectory))
        {
            return false;
        }

        try
        {
            var dir = Path.GetFullPath(stateDirectory);
            var yamlPath = Path.Combine(dir, LastKnownGoodYamlFileName);
            var hashPath = Path.Combine(dir, LastKnownGoodHashFileName);
            if (!File.Exists(yamlPath) || !File.Exists(hashPath))
            {
                return false;
            }

            yaml = File.ReadAllText(yamlPath);
            hash = File.ReadAllText(hashPath).Trim();
            return !string.IsNullOrWhiteSpace(yaml) && !string.IsNullOrWhiteSpace(hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task RunLoopAsync()
    {
        var ct = _cts.Token;
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var wait = first ? 0 : 30;
                first = false;
                var revision = await LongPollAsync(wait, ct).ConfigureAwait(false);
                if (revision is null)
                {
                    if (_haveHash.Length == 0)
                    {
                        await TryApplyLastKnownGoodAsync(ct).ConfigureAwait(false);
                    }

                    continue;
                }

                await HandleRevisionAsync(revision, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                PlusLog.Warn(_context, $"Plus ManagerAgent: poll failed ({ex.GetType().Name}: {ex.Message})");
                if (_haveHash.Length == 0)
                {
                    await TryApplyLastKnownGoodAsync(ct).ConfigureAwait(false);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task TryApplyLastKnownGoodAsync(CancellationToken ct)
    {
        if (!_managed || _context.ConfigApplier is null)
        {
            return;
        }

        if (!TryReadLastKnownGood(_stateDir, out var yaml, out var hash))
        {
            return;
        }

        if (string.Equals(_haveHash, hash, StringComparison.Ordinal))
        {
            return;
        }

        PlusLog.Warn(_context, "Plus ManagerAgent: manager unreachable — applying last-known-good config.");
        await ApplyRevisionAsync(new AgentRevision(hash, yaml, hash), fromLkg: true, ct).ConfigureAwait(false);
    }

    private async Task HandleRevisionAsync(AgentRevision revision, CancellationToken ct)
    {
        // Identical to what we already told the manager we have → no-op (long-poll should 204).
        if (string.Equals(revision.ConfigHash, _pollHave, StringComparison.Ordinal) &&
            string.Equals(revision.ConfigHash, _haveHash, StringComparison.Ordinal))
        {
            UpdateDrift(desired: revision.ConfigHash);
            return;
        }

        _desiredHash = revision.ConfigHash;
        var validationError = ValidateYamlLocally(revision.Yaml);
        if (validationError is not null)
        {
            _pollHave = revision.ConfigHash;
            await AckAsync(revision.ConfigHash, ok: false, validationError, ct).ConfigureAwait(false);
            UpdateDrift(desired: revision.ConfigHash);
            return;
        }

        if (!_managed || _context.ConfigApplier is null)
        {
            // Advisory: report drift only; do not apply. Advance poll have so we wait for the next revision.
            _pollHave = revision.ConfigHash;
            UpdateDrift(desired: revision.ConfigHash);
            PlusLog.Info(_context,
                $"Plus ManagerAgent: advisory drift desired={revision.ConfigHash} have={(_haveHash.Length == 0 ? "(none)" : _haveHash)}");
            return;
        }

        await QueueOrApplyAsync(revision, ct).ConfigureAwait(false);
    }

    private async Task QueueOrApplyAsync(AgentRevision revision, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        TimeSpan wait;
        lock (_applyGate)
        {
            _pending = new PendingRevision(revision);
            var since = now - _lastApplyUtc;
            if (since >= _applyDebounce)
            {
                wait = TimeSpan.Zero;
            }
            else
            {
                wait = _applyDebounce - since;
            }
        }

        if (wait > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }

        AgentRevision? toApply = null;
        lock (_applyGate)
        {
            if (_pending is { } pending)
            {
                toApply = pending.Revision;
                _pending = null;
                _lastApplyUtc = DateTimeOffset.UtcNow;
            }
        }

        if (toApply is not null)
        {
            await ApplyRevisionAsync(toApply, fromLkg: false, ct).ConfigureAwait(false);
        }
    }

    private async Task ApplyRevisionAsync(AgentRevision revision, bool fromLkg, CancellationToken ct)
    {
        var applier = _context.ConfigApplier;
        if (applier is null)
        {
            return;
        }

        ConfigApplyResult result;
        try
        {
            result = await applier.ApplyAsync(revision.Yaml, revision.ConfigHash, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new ConfigApplyResult { Ok = false, Error = ex.Message };
        }

        if (result.Ok)
        {
            _haveHash = result.AppliedHash ?? revision.ConfigHash;
            _pollHave = revision.ConfigHash;
            WriteLastKnownGood(revision.Yaml, _haveHash);
            if (!fromLkg)
            {
                await AckAsync(revision.ConfigHash, ok: true, error: null, ct).ConfigureAwait(false);
            }

            UpdateDrift(desired: _desiredHash ?? _haveHash);
            PlusLog.Info(_context, $"Plus ManagerAgent: applied config hash={_haveHash}");
            return;
        }

        // Failed apply: keep prior config; still advance poll have so we do not tight-loop the same revision.
        _pollHave = revision.ConfigHash;
        var error = result.Error ?? "apply failed";
        if (!fromLkg)
        {
            await AckAsync(revision.ConfigHash, ok: false, error, ct).ConfigureAwait(false);
        }

        UpdateDrift(desired: revision.ConfigHash);
        PlusLog.Warn(_context, $"Plus ManagerAgent: apply failed: {error}");
    }

    private async Task<AgentRevision?> LongPollAsync(int waitSeconds, CancellationToken ct)
    {
        var fleetQ = string.IsNullOrEmpty(_fleet) ? "" : $"fleet={Uri.EscapeDataString(_fleet)}&";
        var nodeQ = string.IsNullOrEmpty(_nodeId) ? "" : $"node_id={Uri.EscapeDataString(_nodeId)}&";
        var path = $"v1/agent/config?{fleetQ}{nodeQ}have={Uri.EscapeDataString(_pollHave)}&wait={waitSeconds}";
        using var response = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            PlusLog.Warn(_context, "Plus ManagerAgent: unauthorized (check manager.tokenFile).");
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"manager returned {(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
        if (!AgentConfigPayload.TryParse(ms.ToArray(), out var parsed, out var parseError) || parsed is null)
        {
            throw new InvalidOperationException(parseError ?? "manager config response missing hash or yaml");
        }

        return new AgentRevision(parsed.ConfigHash, parsed.Yaml, parsed.RevisionId);
    }

    private async Task AckAsync(string configHash, bool ok, string? error, CancellationToken ct)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["fleet"] = string.IsNullOrEmpty(_fleet) ? null : _fleet,
                ["node_id"] = _nodeId,
                ["config_hash"] = configHash,
                ["ok"] = ok,
                ["error"] = ok ? null : error,
            };
            if (!string.IsNullOrEmpty(_labelsJson))
            {
                payload["health"] = new Dictionary<string, object?> { ["labels"] = _labelsJson };
            }

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync("v1/agent/ack", content, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NoContent)
            {
                PlusLog.Warn(_context, $"Plus ManagerAgent: ack returned {(int)response.StatusCode}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PlusLog.Warn(_context, $"Plus ManagerAgent: ack failed ({ex.GetType().Name})");
        }
    }

    private void WriteLastKnownGood(string yaml, string hash)
    {
        try
        {
            Directory.CreateDirectory(_stateDir);
            var yamlPath = Path.Combine(_stateDir, LastKnownGoodYamlFileName);
            var hashPath = Path.Combine(_stateDir, LastKnownGoodHashFileName);
            var tmpYaml = yamlPath + "." + Environment.ProcessId + ".tmp";
            var tmpHash = hashPath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmpYaml, yaml);
            File.WriteAllText(tmpHash, hash);
            File.Move(tmpYaml, yamlPath, overwrite: true);
            File.Move(tmpHash, hashPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PlusLog.Warn(_context, $"Plus ManagerAgent: failed to write last-known-good ({ex.GetType().Name})");
        }
    }

    private void UpdateDrift(string? desired)
    {
        _desiredHash = desired ?? _desiredHash;
        var drifted = !string.IsNullOrEmpty(_desiredHash) &&
                      !string.Equals(_haveHash, _desiredHash, StringComparison.Ordinal);
        // In managed mode after a successful apply, have == desired. In advisory, have stays local.
        if (_managed && string.Equals(_haveHash, _desiredHash, StringComparison.Ordinal))
        {
            drifted = false;
        }

        _context.HostStatus?.SetManagedState(true, drifted);
    }

    private static string? ValidateYamlLocally(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return "empty config yaml";
        }

        return null;
    }

    private static bool HasGatewayController(IReadOnlyDictionary<string, string> options)
    {
        if (IsTrue(First(options, "gateway.enabled", "gateway.controller")))
        {
            return true;
        }

        foreach (var key in options.Keys)
        {
            if (key.StartsWith("gateway.", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(options[key]))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadToken(IReadOnlyDictionary<string, string> options)
    {
        var file = First(options, "manager.tokenFile", "manager.token_file");
        if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
        {
            try
            {
                return File.ReadAllText(file).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return First(options, "manager.token");
    }

    private static string? ParseLabels(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('{'))
        {
            return trimmed;
        }

        // comma-separated key=value → JSON object
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            map[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }

        return map.Count == 0 ? null : JsonSerializer.Serialize(map);
    }

    private static bool TryGet(IReadOnlyDictionary<string, string> options, out string? value, params string[] keys)
    {
        value = First(options, keys);
        return value is not null;
    }

    private static string? First(IReadOnlyDictionary<string, string> options, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static bool IsTrue(string? value) =>
        value is not null &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _cts.Cancel();
        }
        catch
        {
            /* ignore */
        }

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(3), CancellationToken.None);
        }
        catch
        {
            /* ignore */
        }

        _http.Dispose();
        _cts.Dispose();
        Interlocked.Decrement(ref _liveLoops);
        try
        {
            _context.HostStatus?.SetManagedState(false, drifted: false);
        }
        catch
        {
            /* ignore */
        }
    }

    private sealed record AgentRevision(string ConfigHash, string Yaml, string RevisionId);

    private sealed record PendingRevision(AgentRevision Revision);
}
