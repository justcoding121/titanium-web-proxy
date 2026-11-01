using Titanium.Cli.Parsers;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Config;

/// <summary>CLI implementation of dashboard and control-plane config editing.</summary>
internal sealed class CliConfigEditor : IConfigEditor
{
    private readonly string _configPath;
    private readonly ConfigOverlayStore _overlayStore;
    private readonly SemaphoreSlim _gate;
    private readonly Func<TwpConfig?> _getLastApplied;
    private readonly Action<TwpConfig> _setLastApplied;
    private readonly Func<TwpConfig, CancellationToken, Task> _applyLiveAsync;
    private readonly Action<string, IReadOnlyList<string>?> _setOverlayBanner;

    private bool _suspended;
    private IReadOnlyList<string>? _suspendedReasons;
    private IReadOnlyList<string>? _lastOrphans;

    public CliConfigEditor(
        string configPath,
        ConfigOverlayStore overlayStore,
        SemaphoreSlim gate,
        Func<TwpConfig?> getLastApplied,
        Action<TwpConfig> setLastApplied,
        Func<TwpConfig, CancellationToken, Task> applyLiveAsync,
        Action<string, IReadOnlyList<string>?> setOverlayBanner)
    {
        _configPath = configPath;
        _overlayStore = overlayStore;
        _gate = gate;
        _getLastApplied = getLastApplied;
        _setLastApplied = setLastApplied;
        _applyLiveAsync = applyLiveAsync;
        _setOverlayBanner = setOverlayBanner;
    }

    public IReadOnlyList<string> ValidateCandidate(IReadOnlyList<RouteConfig>? routes, IReadOnlyList<ClusterConfig>? clusters)
    {
        var baseline = _getLastApplied() ?? ConfigLoader.Load(_configPath).Config;
        var candidate = CloneWithRoutesClusters(baseline, routes, clusters);
        return TwpConfigValidator.Validate(candidate);
    }

    public async Task<ConfigEditResult> ApplyEditAsync(ConfigEdit edit, string actor, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var baseBytes = await File.ReadAllBytesAsync(_configPath, cancellationToken).ConfigureAwait(false);
            var baseLoaded = ConfigLoader.Load(_configPath);
            var overlay = _overlayStore.Exists ? _overlayStore.LoadOrEmpty() : new ConfigOverlayDocument();
            overlay.BaseDigest ??= ConfigOverlay.ComputeDigest(baseBytes);

            overlay = edit.Kind switch
            {
                ConfigEditKind.UpsertRoute => ConfigOverlay.ApplyEdit(overlay, edit.Kind, edit.Route, null, null, actor),
                ConfigEditKind.RemoveRoute => ConfigOverlay.ApplyEdit(overlay, edit.Kind, null, null, edit.Id, actor),
                ConfigEditKind.UpsertCluster => ConfigOverlay.ApplyEdit(overlay, edit.Kind, null, edit.Cluster, null, actor),
                ConfigEditKind.RemoveCluster => ConfigOverlay.ApplyEdit(overlay, edit.Kind, null, null, edit.Id, actor),
                _ => throw new ArgumentOutOfRangeException(nameof(edit)),
            };

            var merged = ConfigOverlay.Merge(baseLoaded.Config, overlay);
            _lastOrphans = merged.Orphans;
            var errors = TwpConfigValidator.Validate(merged.Config);
            if (errors.Count > 0)
            {
                return ConfigEditResult.Rejected(errors);
            }

            var previous = _getLastApplied();
            try
            {
                await _applyLiveAsync(merged.Config, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (previous is not null)
                {
                    try
                    {
                        await _applyLiveAsync(previous, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best-effort rollback.
                    }
                }

                throw;
            }

            overlay.BaseDigest = ConfigOverlay.ComputeDigest(baseBytes);
            try
            {
                _overlayStore.Save(overlay);
            }
            catch
            {
                if (previous is not null)
                {
                    await _applyLiveAsync(previous, cancellationToken).ConfigureAwait(false);
                }

                throw;
            }

            _setLastApplied(merged.Config);
            _suspended = false;
            _suspendedReasons = null;
            _setOverlayBanner(overlay.BaseDigest, null);
            return ConfigEditResult.Ok(overlay.BaseDigest);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ConfigEditResult> ClearOverlayAsync(string actor, CancellationToken cancellationToken = default)
    {
        _ = actor;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _overlayStore.Delete();
            _suspended = false;
            _suspendedReasons = null;
            _lastOrphans = null;
            var baseLoaded = ConfigLoader.Load(_configPath);
            var errors = TwpConfigValidator.Validate(baseLoaded.Config);
            if (errors.Count > 0)
            {
                return ConfigEditResult.Rejected(errors);
            }

            await _applyLiveAsync(baseLoaded.Config, cancellationToken).ConfigureAwait(false);
            _setLastApplied(baseLoaded.Config);
            _setOverlayBanner(null, null);
            return ConfigEditResult.Ok();
        }
        finally
        {
            _gate.Release();
        }
    }

    public OverlayStatus GetOverlayStatus()
    {
        var overlay = _overlayStore.Exists ? _overlayStore.LoadOrEmpty() : null;
        var editCount = overlay is null
            ? 0
            : overlay.Routes.Upsert.Count + overlay.Routes.Remove.Count +
              overlay.Clusters.Upsert.Count + overlay.Clusters.Remove.Count;
        return new OverlayStatus
        {
            HasOverlay = overlay is not null && editCount > 0,
            Suspended = _suspended,
            SuspendedReasons = _suspendedReasons,
            EditCount = editCount,
            Orphans = _lastOrphans,
            LastEditUtc = overlay?.History.LastOrDefault()?.At,
        };
    }

    internal void MarkSuspended(IReadOnlyList<string> reasons)
    {
        _suspended = true;
        _suspendedReasons = reasons;
        _setOverlayBanner(null, reasons);
    }

    internal static TwpConfig ResolveEffectiveConfig(
        string configPath,
        ConfigOverlayStore? overlayStore,
        CliConfigEditor? editor)
    {
        var loaded = ConfigLoader.Load(configPath);
        if (overlayStore is null || !overlayStore.Exists)
        {
            return loaded.Config;
        }

        var overlay = overlayStore.LoadOrEmpty();
        var digest = ConfigOverlay.ComputeDigest(File.ReadAllBytes(configPath));
        overlay.BaseDigest ??= digest;
        var merged = ConfigOverlay.Merge(loaded.Config, overlay);
        var errors = TwpConfigValidator.Validate(merged.Config);
        if (errors.Count > 0)
        {
            editor?.MarkSuspended(errors);
            return loaded.Config;
        }

        return merged.Config;
    }

    internal static TwpConfig CloneWithRoutesClusters(
        TwpConfig baseline,
        IReadOnlyList<RouteConfig>? routes,
        IReadOnlyList<ClusterConfig>? clusters)
    {
        var clone = new TwpConfig
        {
            SchemaVersion = baseline.SchemaVersion,
            Listeners = baseline.Listeners.ToList(),
            Routes = routes?.ToList() ?? baseline.Routes.ToList(),
            Clusters = clusters?.ToList() ?? baseline.Clusters.ToList(),
            Policies = baseline.Policies.ToList(),
            Plugins = baseline.Plugins?.ToList(),
            ScriptPipeline = baseline.ScriptPipeline?.ToList(),
            StaticFiles = baseline.StaticFiles,
            Plus = baseline.Plus,
            Certificates = baseline.Certificates,
            Logging = baseline.Logging,
            Server = baseline.Server,
        };
        return clone;
    }
}
