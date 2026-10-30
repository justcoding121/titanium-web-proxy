namespace Titanium.Web.Proxy.Abstractions.Plugins;

/// <summary>
/// Host seam the Plus manager agent uses to apply a fleet config revision (write + atomic reload).
/// Implemented by the CLI; null when the host cannot apply manager-pushed config.
/// </summary>
public interface IConfigApplier
{
    /// <summary>Writes <paramref name="yaml"/> and reloads so the running process matches it.</summary>
    Task<ConfigApplyResult> ApplyAsync(string yaml, string configHash, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of <see cref="IConfigApplier.ApplyAsync"/>.</summary>
public sealed class ConfigApplyResult
{
    public bool Ok { get; init; }

    public string? Error { get; init; }

    /// <summary>Hash after a successful apply, when the host reports one.</summary>
    public string? AppliedHash { get; init; }
}
