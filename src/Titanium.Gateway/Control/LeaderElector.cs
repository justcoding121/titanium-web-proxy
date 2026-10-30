using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Titanium.Gateway.Kube;

namespace Titanium.Gateway.Control;

/// <summary>Timing and identity of the leader election.</summary>
public sealed record LeaderElectionOptions
{
    public required string Namespace { get; init; }

    public string Name { get; init; } = "titanium-gateway";

    /// <summary>Unique per pod (the pod name).</summary>
    public required string Identity { get; init; }

    /// <summary>How long a lease stays valid without a renewal. A follower waits this long before it takes over.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The leader steps down when it could not renew for this long.</summary>
    public TimeSpan RenewDeadline { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan RetryPeriod { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
///     Leader election on a <c>coordination.k8s.io/v1</c> Lease, the same protocol client-go uses. Only the leader
///     writes the data-plane ConfigMap, the certificate Secret, and resource status; the replicas keep their caches
///     warm so a failover needs no relist.
/// </summary>
public sealed class LeaderElector
{
    private const string HolderIdentityKey = "holderIdentity";
    private const string LeaseTransitionsKey = "leaseTransitions";
    private const string MetadataKey = "metadata";

    private readonly IKubeApi _api;
    private readonly LeaderElectionOptions _options;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private string? _observedHolder;
    private string? _observedRenewTime;
    private DateTimeOffset _observedAt;
    private DateTimeOffset _lastRenewal;
    private volatile bool _isLeader;

    public LeaderElector(IKubeApi api, LeaderElectionOptions options, ILogger log, TimeProvider? time = null)
    {
        _api = api;
        _options = options;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public bool IsLeader => _isLeader;

    /// <summary>Raised when this process gains or loses the lease.</summary>
    public event Action<bool>? LeadershipChanged;

    private string LeasePath => $"/apis/coordination.k8s.io/v1/namespaces/{Uri.EscapeDataString(_options.Namespace)}/leases/{Uri.EscapeDataString(_options.Name)}";

    private string LeaseCollectionPath => $"/apis/coordination.k8s.io/v1/namespaces/{Uri.EscapeDataString(_options.Namespace)}/leases";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await StepAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(_options.RetryPeriod, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // RunAsync was cancelled.
        }

        await ReleaseAsync().ConfigureAwait(false);
    }

    /// <summary>One acquire or renew attempt. Public so tests can drive the election without waiting.</summary>
    public async Task StepAsync(CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            acquired = await TryAcquireOrRenewAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or KubeApiException or IOException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Leader election could not reach the API server: {Message}", ex.Message);
        }

        var now = _time.GetUtcNow();
        if (acquired)
        {
            _lastRenewal = now;
            SetLeader(true);
        }
        else if (_isLeader && now - _lastRenewal > _options.RenewDeadline)
        {
            _log.LogWarning("Could not renew the lease within {Seconds}s. Stepping down.", (int)_options.RenewDeadline.TotalSeconds);
            SetLeader(false);
        }
        else if (!_isLeader)
        {
            SetLeader(false);
        }
    }

    private async Task<bool> TryAcquireOrRenewAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var response = await _api.SendAsync(HttpMethod.Get, LeasePath, null, null, cancellationToken).ConfigureAwait(false);
        if (response.IsNotFound)
        {
            var created = await _api.SendAsync(HttpMethod.Post, LeaseCollectionPath, NewLease(null, now, 0, now), "application/json", cancellationToken).ConfigureAwait(false);
            return created.IsSuccess;
        }

        if (!response.IsSuccess)
        {
            throw new KubeApiException(response.Status, response.Body);
        }

        using var document = JsonDocument.Parse(response.Body);
        var root = document.RootElement;
        var holder = root.GetString("spec", HolderIdentityKey) ?? string.Empty;
        var renew = root.GetString("spec", "renewTime");
        var acquire = root.GetString("spec", "acquireTime");
        var transitions = root.GetInt("spec", LeaseTransitionsKey) ?? 0;
        var duration = TimeSpan.FromSeconds(root.GetInt("spec", "leaseDurationSeconds") ?? (int)_options.LeaseDuration.TotalSeconds);
        var resourceVersion = root.GetString(MetadataKey, "resourceVersion");

        // Expiry is judged on the local clock from when this process last saw the record change, so clock skew
        // between nodes cannot make two replicas both think the lease is theirs.
        if (!string.Equals(holder, _observedHolder, StringComparison.Ordinal) || !string.Equals(renew, _observedRenewTime, StringComparison.Ordinal))
        {
            _observedHolder = holder;
            _observedRenewTime = renew;
            _observedAt = now;
        }

        var mine = string.Equals(holder, _options.Identity, StringComparison.Ordinal);
        var expired = string.IsNullOrEmpty(holder) || now - _observedAt > duration;
        if (!mine && !expired)
        {
            return false;
        }

        string body;
        if (mine)
        {
            body = NewLease(resourceVersion, ParseTime(acquire) ?? now, transitions, now);
        }
        else
        {
            var nextTransitions = string.IsNullOrEmpty(holder) ? transitions : transitions + 1;
            body = NewLease(resourceVersion, now, nextTransitions, now);
        }
        var updated = await _api.SendAsync(HttpMethod.Put, LeasePath, body, "application/json", cancellationToken).ConfigureAwait(false);
        if (updated.IsSuccess)
        {
            _observedHolder = _options.Identity;
            _observedRenewTime = null;
            _observedAt = now;
            return true;
        }

        if (!updated.IsConflict)
        {
            throw new KubeApiException(updated.Status, updated.Body);
        }

        return false;
    }

    private async Task ReleaseAsync()
    {
        if (!_isLeader)
        {
            return;
        }

        SetLeader(false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await _api.SendAsync(HttpMethod.Get, LeasePath, null, null, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                return;
            }

            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            if (root.GetString("spec", HolderIdentityKey) != _options.Identity)
            {
                return;
            }

            var now = _time.GetUtcNow();
            var spec = new JsonObject
            {
                [HolderIdentityKey] = string.Empty,
                ["leaseDurationSeconds"] = 1,
                ["renewTime"] = GatewayJson.FormatMicroTime(now),
                [LeaseTransitionsKey] = root.GetInt("spec", LeaseTransitionsKey) ?? 0,
            };
            var patch = new JsonObject
            {
                [MetadataKey] = new JsonObject { ["resourceVersion"] = root.GetString(MetadataKey, "resourceVersion") },
                ["spec"] = spec,
            };
            await _api.SendAsync(HttpMethod.Patch, LeasePath, patch.ToJsonString(), "application/merge-patch+json", cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or KubeApiException or OperationCanceledException or JsonException)
        {
            _log.LogDebug(ex, "Could not release the lease: {Message}", ex.Message);
        }
    }

    private string NewLease(string? resourceVersion, DateTimeOffset acquireTime, int transitions, DateTimeOffset renewTime)
    {
        var metadataObject = new JsonObject { ["name"] = _options.Name, ["namespace"] = _options.Namespace };
        if (resourceVersion is not null)
        {
            metadataObject["resourceVersion"] = resourceVersion;
        }

        var lease = new JsonObject
        {
            ["apiVersion"] = "coordination.k8s.io/v1",
            ["kind"] = "Lease",
            [MetadataKey] = metadataObject,
            ["spec"] = new JsonObject
            {
                [HolderIdentityKey] = _options.Identity,
                ["leaseDurationSeconds"] = (int)_options.LeaseDuration.TotalSeconds,
                ["acquireTime"] = GatewayJson.FormatMicroTime(acquireTime),
                ["renewTime"] = GatewayJson.FormatMicroTime(renewTime),
                [LeaseTransitionsKey] = transitions,
            },
        };
        return lease.ToJsonString();
    }

    private static DateTimeOffset? ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var value) ? value : null;

    private void SetLeader(bool leader)
    {
        if (_isLeader == leader)
        {
            return;
        }

        _isLeader = leader;
        if (_log.IsEnabled(LogLevel.Information))
        {
            if (leader)
            {
                _log.LogInformation("{Identity} became the leader.", _options.Identity);
            }
            else
            {
                _log.LogInformation("{Identity} is no longer the leader.", _options.Identity);
            }
        }

        LeadershipChanged?.Invoke(leader);
    }
}
