using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Abstractions.Plugins;

/// <summary>Validates and applies dashboard or control-plane route/cluster edits (Plus + CLI).</summary>
public interface IConfigEditor
{
    /// <summary>
    ///     Validates a candidate routes/clusters snapshot against the last applied configuration.
    ///     Either list may be null when unchanged.
    /// </summary>
    IReadOnlyList<string> ValidateCandidate(IReadOnlyList<RouteConfig>? routes, IReadOnlyList<ClusterConfig>? clusters);

    /// <summary>Applies one overlay edit after validation. Does nothing when validation fails.</summary>
    Task<ConfigEditResult> ApplyEditAsync(ConfigEdit edit, string actor, CancellationToken cancellationToken = default);

    /// <summary>Removes the overlay and reloads the base configuration.</summary>
    Task<ConfigEditResult> ClearOverlayAsync(string actor, CancellationToken cancellationToken = default);

    /// <summary>Current overlay state for the dashboard banner.</summary>
    OverlayStatus GetOverlayStatus();
}

/// <summary>One route or cluster change in the persisted overlay.</summary>
public enum ConfigEditKind
{
    UpsertRoute,
    RemoveRoute,
    UpsertCluster,
    RemoveCluster,
}

/// <summary>Payload for <see cref="IConfigEditor.ApplyEditAsync"/>.</summary>
public sealed class ConfigEdit
{
    public required ConfigEditKind Kind { get; init; }

    public RouteConfig? Route { get; init; }

    public ClusterConfig? Cluster { get; init; }

    /// <summary>Route or cluster id for remove operations.</summary>
    public string? Id { get; init; }
}

/// <summary>Outcome of an edit or overlay clear.</summary>
public sealed class ConfigEditResult
{
    public bool Applied { get; init; }

    public IReadOnlyList<string>? Errors { get; init; }

    public string? OverlayDigest { get; init; }

    public static ConfigEditResult Rejected(IReadOnlyList<string> errors) =>
        new() { Applied = false, Errors = errors };

    public static ConfigEditResult Ok(string? overlayDigest = null) =>
        new() { Applied = true, OverlayDigest = overlayDigest };
}

/// <summary>Overlay summary for operators.</summary>
public sealed class OverlayStatus
{
    public bool HasOverlay { get; init; }

    public bool Suspended { get; init; }

    public IReadOnlyList<string>? SuspendedReasons { get; init; }

    public int EditCount { get; init; }

    public IReadOnlyList<string>? Orphans { get; init; }

    public DateTimeOffset? LastEditUtc { get; init; }
}
