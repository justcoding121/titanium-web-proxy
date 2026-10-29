namespace Titanium.Web.Proxy.Abstractions.Plugins;

/// <summary>Config identity and last reload outcome, as the host reports it to Plus.</summary>
public sealed class PlusConfigStatus
{
    /// <summary>The <c>schemaVersion</c> of the running config document.</summary>
    public string? SchemaVersion { get; init; }

    /// <summary>Hash of the config file the running config came from (hex, no secret material).</summary>
    public string? ConfigHash { get; init; }

    /// <summary>When the first config was applied.</summary>
    public DateTimeOffset? LoadedUtc { get; init; }

    /// <summary>When the last reload finished, or null when none has run.</summary>
    public DateTimeOffset? LastReloadUtc { get; init; }

    /// <summary><c>ok</c> or <c>failed</c>, or null when no reload has run.</summary>
    public string? LastReloadResult { get; init; }

    /// <summary>The failure text of the last reload, with secrets masked. Null after a success.</summary>
    public string? LastReloadError { get; init; }

    /// <summary>True when this node is enrolled with a Plus manager (<c>plus.manager</c> / managed mode).</summary>
    public bool Managed { get; init; }

    /// <summary>True when the running config hash differs from the manager's desired revision.</summary>
    public bool Drifted { get; init; }
}

/// <summary>Outcome of a host config reload requested from the dashboard.</summary>
public sealed class PlusReloadResult
{
    public required bool Ok { get; init; }

    /// <summary>Failure text with secrets masked. Null on success.</summary>
    public string? Error { get; init; }

    /// <summary>Config hash after a successful reload, when the host reports one.</summary>
    public string? ConfigHash { get; init; }
}

/// <summary>One certificate the host serves or presents.</summary>
public sealed class PlusCertificateInfo
{
    /// <summary>A short label such as a file name.</summary>
    public required string Name { get; init; }

    /// <summary><c>listener</c> for material clients see, <c>upstream</c> for material presented to or trusted for origins.</summary>
    public required string Role { get; init; }

    public string? Subject { get; init; }

    public string? Issuer { get; init; }

    public DateTimeOffset? NotAfterUtc { get; init; }

    /// <summary>Set when the file could not be read or parsed. Never contains key material.</summary>
    public string? Error { get; init; }
}

/// <summary>What the CLI host exposes to Plus beyond the proxy itself: reload status and certificate inventory.</summary>
public interface IPlusHostStatus
{
    PlusConfigStatus GetConfigStatus();

    IReadOnlyList<PlusCertificateInfo> GetCertificates();

    /// <summary>
    /// Updates manager enrollment and drift flags shown on the dashboard Config page.
    /// Default no-op for hosts that do not track manager state.
    /// </summary>
    void SetManagedState(bool managed, bool drifted)
    {
    }
}

/// <summary>A destination ejection, recovery, or failed probe. Holds ids and short reasons, never a credential.</summary>
public sealed class DestinationHealthEvent
{
    public required DateTimeOffset Time { get; init; }

    public required string DestinationId { get; init; }

    /// <summary><c>ejection</c>, <c>recovery</c>, or <c>probe-failure</c>.</summary>
    public required string Kind { get; init; }

    /// <summary><c>passive</c>, <c>active-probe</c>, or <c>circuit-breaker</c>.</summary>
    public required string Source { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
///     Implemented by a cluster manager that publishes destination health events. Plus keeps the newest in a
///     bounded ring for the dashboard.
/// </summary>
public interface IDestinationHealthEvents
{
    event Action<DestinationHealthEvent>? HealthEvent;

    /// <summary>Publishes an event on behalf of a component that decides health outside the manager (a circuit breaker).</summary>
    void ReportHealthEvent(DestinationHealthEvent healthEvent);
}
