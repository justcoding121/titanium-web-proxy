using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Model;
using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Configuration;

namespace Titanium.Gateway.Control;

/// <summary>What one reconcile did.</summary>
public sealed record ReconcileOutcome(
    bool Leader,
    string ConfigHash,
    bool WroteOutput,
    int StatusWritten,
    int StatusFailed,
    bool Confirmed,
    bool HasListeners);

/// <summary>Everything the controller needs, built once by <see cref="GatewayOptions"/> or a test.</summary>
public sealed record GatewayControllerOptions
{
    public GatewayTranslatorOptions Translator { get; init; } = new();

    public required OutputOptions Output { get; init; }

    /// <summary>The reconciler never starts twice within this window, however many changes arrive.</summary>
    public TimeSpan MinReconcileInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>While a Gateway is not yet confirmed programmed, poll the data plane this often.</summary>
    public TimeSpan ConfirmPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Re-apply the ConfigMap and Secret this often even when nothing changed, to repair edits made by hand.</summary>
    public TimeSpan ResyncInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>After a failed pass, retry after this long.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
///     Ties the pieces together. Every change in the cluster (and every leadership change) signals a reconcile; the
///     loop collapses a burst of signals into one pass and never runs two within the minimum interval. A pass is:
///     snapshot the cache, render with the pure translator, and, on the leader only, write the ConfigMap and Secret,
///     poll the data plane for the config hash, and write status.
/// </summary>
public sealed class GatewayController
{
    private readonly ClusterCache _cache;
    private readonly GatewayTranslator _translator;
    private readonly OutputWriter _output;
    private readonly StatusWriter _status;
    private readonly EventWriter? _events;
    private readonly IDataPlaneObserver _observer;
    private readonly LeaderElector? _leader;
    private readonly GatewayControllerOptions _options;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private DateTimeOffset _lastOutputInvalidation;
    private bool _wasLeader;
    private int _pokeAttempt;

    /// <summary>A pod that still runs an old config is poked this many times (once per confirm poll) before the kubelet's own sync is left to do it.</summary>
    private const int MaxPokes = 24;

    public GatewayController( // NOSONAR S107 -- Controller wiring keeps dependencies explicit for tests and host composition.
        ClusterCache cache,
        OutputWriter output,
        StatusWriter status,
        IDataPlaneObserver observer,
        LeaderElector? leader,
        GatewayControllerOptions options,
        ILogger log,
        TimeProvider? time = null,
        EventWriter? events = null)
    {
        _events = events;
        _cache = cache;
        _output = output;
        _status = status;
        _observer = observer;
        _leader = leader;
        _options = options;
        _log = log;
        _time = time ?? TimeProvider.System;
        _translator = new GatewayTranslator(options.Translator);
        _lastOutputInvalidation = _time.GetUtcNow();
    }

    /// <summary>True when the caches are filled. Drives the controller's own readiness probe.</summary>
    public bool Ready => _cache.HasSynced;

    public bool IsLeader => _leader?.IsLeader ?? true;

    /// <summary>Asks for a reconcile. Cheap and safe from any thread.</summary>
    public void Signal() => _signals.Writer.TryWrite(true);

    /// <summary>Runs informers, leader election, and the reconcile loop until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _cache.Changed += Signal;
        if (_leader is not null)
        {
            _leader.LeadershipChanged += _ => Signal();
        }

        var tasks = new List<Task> { _cache.RunAsync(cancellationToken), ReconcileLoopAsync(cancellationToken) };
        if (_leader is not null)
        {
            tasks.Add(_leader.RunAsync(cancellationToken));
        }

        Signal();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task ReconcileLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var last = DateTimeOffset.MinValue;
            while (await _signals.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _signals.Reader.TryRead(out _);
                var wait = last + _options.MinReconcileInterval - _time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
                }

                // Changes that arrived while waiting are covered by this pass.
                _signals.Reader.TryRead(out _);
                last = _time.GetUtcNow();
                if (!_cache.HasSynced)
                {
                    continue;
                }

                try
                {
                    var outcome = await ReconcileOnceAsync(cancellationToken).ConfigureAwait(false);
                    if (outcome.Leader && (outcome.StatusFailed > 0))
                    {
                        ScheduleSignal(_options.RetryInterval, cancellationToken);
                    }
                    else if (outcome.Leader && outcome.HasListeners && !outcome.Confirmed)
                    {
                        ScheduleSignal(_options.ConfirmPollInterval, cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError(ex, "Reconcile failed: {Message}", ex.Message);
                    ScheduleSignal(_options.RetryInterval, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // RunAsync was cancelled.
        }
    }

    private void ScheduleSignal(TimeSpan delay, CancellationToken cancellationToken) =>
        _ = Task.Delay(delay, _time, cancellationToken).ContinueWith(
            _ => Signal(),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);

    /// <summary>One pass. Public so tests can drive it without the loop.</summary>
    public async Task<ReconcileOutcome> ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        var leader = IsLeader;
        if (leader && !_wasLeader)
        {
            _output.Invalidate();
        }

        _wasLeader = leader;
        var now = _time.GetUtcNow();
        if (now - _lastOutputInvalidation >= _options.ResyncInterval)
        {
            _output.Invalidate();
            _lastOutputInvalidation = now;
        }

        var state = _cache.Snapshot();
        var render = _translator.Render(state);
        var bytes = new UTF8Encoding(false).GetBytes(TwpConfigLoader.ToJson(render.Config));
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var hasListeners = render.Config.Listeners.Count > 0;
        if (!leader)
        {
            return new ReconcileOutcome(false, hash, false, 0, 0, false, hasListeners);
        }

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in render.TlsFiles)
        {
            if (_cache.GetFile(file) is { } content)
            {
                files[file.FileName] = content;
            }
        }

        foreach (var pair in render.InlineTlsFiles)
        {
            files[pair.Key] = pair.Value;
        }

        var wrote = await _output.ApplyAsync(bytes, files, OutputWriter.SignatureOf(render.Config.Listeners), cancellationToken).ConfigureAwait(false);
        if (wrote && _log.IsEnabled(LogLevel.Information))
        {
            _log.LogInformation("Published data-plane config {Hash} ({Routes} routes, {Clusters} clusters, {Listeners} listeners).",
                hash[..12], render.Config.Routes.Count, render.Config.Clusters.Count, render.Config.Listeners.Count);
        }

        var (observation, pods) = await ObserveAsync(state, cancellationToken).ConfigureAwait(false);
        var confirmed = observation.Confirms(hash);
        await PokeAsync(wrote, hash, observation, pods, cancellationToken).ConfigureAwait(false);
        var (written, failed) = await _status.ApplyAsync(render.Finalize(observation, hash), state, cancellationToken).ConfigureAwait(false);
        if (_events is not null)
        {
            await _events.ApplyAsync(render.Events, cancellationToken).ConfigureAwait(false);
        }

        return new ReconcileOutcome(true, hash, wrote, written, failed, confirmed, hasListeners);
    }

    /// <summary>
    ///     A mounted ConfigMap reaches a pod when the kubelet next syncs it, up to about a minute later. Touching the
    ///     pod makes the kubelet sync at once, so a route change takes effect in seconds. Pods that still run an old
    ///     config are touched again on each confirm poll.
    /// </summary>
    private async Task PokeAsync(bool wrote, string hash, DataPlaneObservation observation, IReadOnlyList<string?> pods, CancellationToken cancellationToken)
    {
        IEnumerable<string> targets;
        string token;
        if (wrote)
        {
            _pokeAttempt = 0;
            targets = pods.OfType<string>();
            token = hash[..16];
        }
        else if (!observation.Confirms(hash) && _pokeAttempt < MaxPokes)
        {
            targets = pods
                .Select((name, i) => (name, stale: i >= observation.PodHashes.Count || observation.PodHashes[i] != hash))
                .Where(p => p.name is not null && p.stale)
                .Select(p => p.name!)
                .ToList();
            token = $"{hash[..16]}.{++_pokeAttempt}";
        }
        else
        {
            return;
        }

        var list = targets.ToList();
        if (list.Count > 0 && await _output.PokePodsAsync(list, token, cancellationToken).ConfigureAwait(false) is var failed and > 0
            && _log.IsEnabled(LogLevel.Debug))
        {
            _log.LogDebug("Could not ask {Count} data-plane pods to refresh their config volume.", failed);
        }
    }

    private async Task<(DataPlaneObservation Observation, IReadOnlyList<string?> Pods)> ObserveAsync(ClusterState state, CancellationToken cancellationToken)
    {
        var service = _options.Translator.DataPlaneServiceName;
        var ns = _options.Translator.DataPlaneServiceNamespace;
        if (service is null || ns is null)
        {
            return (DataPlaneObservation.None, []);
        }

        var endpoints = state.EndpointSlices
            .Where(s => s.Namespace == ns && s.ServiceName == service)
            .SelectMany(s => s.Endpoints)
            .Where(e => e.Ready != false)
            .SelectMany(e => e.Addresses.Select(a => (Address: a, e.PodName)))
            .DistinctBy(e => e.Address, StringComparer.Ordinal)
            .OrderBy(e => e.Address, StringComparer.Ordinal)
            .ToList();
        var observation = await _observer.ObserveAsync(endpoints.Select(e => e.Address).ToList(), cancellationToken).ConfigureAwait(false);
        return (observation, endpoints.Select(e => e.PodName).ToList());
    }
}
