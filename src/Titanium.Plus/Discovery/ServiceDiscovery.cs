using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Titanium.Plus.ControlPlane;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Discovery;

/// <summary>
/// Service discovery → <see cref="IClusterManager.ApplyAsync"/>.
/// Modes: <c>file</c>, <c>dns</c>, and <c>dnsSrv</c> (timer based), <c>consul</c> (blocking queries), and <c>k8s</c>
/// (EndpointSlice list-then-watch). Remote modes back off exponentially with jitter and keep the last
/// known good destinations on every error; an empty or failed result is never applied.
/// </summary>
public sealed class ServiceDiscovery : IDisposable
{
    private const string LabelSelectorQueryKey = "labelSelector";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new LoadBalanceAlgorithmConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true),
        },
    };

    private readonly CancellationTokenSource _cts = new();
    private FileSystemWatcher? _watcher;
    private Timer? _filePollTimer;
    private DateTime _lastAppliedWriteUtc = DateTime.MinValue;
    private long _lastAppliedLength = -1;

    /// <summary>0 idle, 1 apply scheduled, 2 another change arrived while one was scheduled.</summary>
    private int _fileWatchState;
    private readonly DiscoveryApplyState _dnsApplyState = new();
    private readonly DiscoveryApplyState _dnsSrvApplyState = new();
    private int _dnsSrvTruncateLogged;

    public static ServiceDiscovery? TryStart(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("discovery.mode", out var mode) || string.IsNullOrWhiteSpace(mode))
        {
            return null;
        }

        var discovery = new ServiceDiscovery();
        var normalized = mode.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "file":
                discovery.StartFileWatch(context, options);
                break;
            case "dns":
                discovery.StartDnsLoop(context, options);
                break;
            case "dnssrv":
            case "dns-srv":
            case "dns_srv":
                discovery.StartDnsSrvLoop(context, options);
                break;
            case "consul":
                discovery.StartConsul(context, options);
                break;
            case "k8s":
            case "kubernetes":
                discovery.StartKubernetes(context, options);
                break;
            default:
                PlusLog.Warn(context, $"Plus Discovery: unknown mode={mode}");
                break;
        }

        return discovery;
    }

    private void StartFileWatch(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("discovery.file", out var path) || string.IsNullOrWhiteSpace(path))
        {
            PlusLog.Warn(context, "Plus Discovery: mode=file requires discovery.file");
            return;
        }

        var full = Path.GetFullPath(path);
        PlusLog.Info(context, $"Plus Discovery: watching file {full}");
        // Apply once synchronously so unit tests / cold start do not race the background task.
        ApplyFileInitialBestEffortAsync(context, full).GetAwaiter().GetResult();

        var dir = Path.GetDirectoryName(full);
        var name = Path.GetFileName(full);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name))
        {
            return;
        }

        AttachFileWatcher(context, full, dir, name);
    }

    private async Task ApplyFileInitialBestEffortAsync(PlusActivationContext context, string full)
    {
        try
        {
            if (File.Exists(full) && await ApplyFileAsync(context, full, _cts.Token))
            {
                StampAppliedFile(full);
            }
        }
        catch (Exception ex)
        {
            PlusLog.Error(context, $"Plus Discovery: initial file apply failed: {ex.Message}");
        }
    }

    private void AttachFileWatcher(PlusActivationContext context, string full, string dir, string name)
    {
        _watcher = new FileSystemWatcher(dir, name)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        // Dropping a change that arrives while one apply is scheduled loses the real write:
        // macOS FSEvents fires once when WriteAllText truncates the file and again when the new bytes land.
        void OnChange(object _, FileSystemEventArgs __) => ScheduleFileApply(context, full);

        _watcher.Changed += OnChange;
        _watcher.Created += OnChange;
        _watcher.Renamed += OnChange;
        _watcher.EnableRaisingEvents = true;

        // FileSystemWatcher misses some overwrite patterns (especially on macOS). Poll mtime/size as a backstop.
        _filePollTimer = new Timer(
            _ =>
            {
                try
                {
                    if (!File.Exists(full))
                    {
                        return;
                    }

                    var info = new FileInfo(full);
                    if (info.LastWriteTimeUtc == _lastAppliedWriteUtc && info.Length == _lastAppliedLength)
                    {
                        return;
                    }

                    ScheduleFileApply(context, full);
                }
                catch (IOException)
                {
                    // Mid-write; the next tick retries.
                }
                catch (UnauthorizedAccessException)
                {
                    // Mid-write; the next tick retries.
                }
            },
            null,
            dueTime: 500,
            period: 500);
    }

    private void ScheduleFileApply(PlusActivationContext context, string full)
    {
        var previous = Interlocked.CompareExchange(ref _fileWatchState, 1, 0);
        if (previous == 0)
        {
            QueueDebouncedFileApply(context, full);
            return;
        }

        if (previous == 1)
        {
            Interlocked.CompareExchange(ref _fileWatchState, 2, 1);
        }
    }

    private void QueueDebouncedFileApply(PlusActivationContext context, string full)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Re-apply when a change landed during the wait or the read, so a truncate-then-write
                // pair cannot leave the cluster on the previous file contents.
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(200, _cts.Token);
                    if (!await ApplyFileAsync(context, full, _cts.Token))
                    {
                        // Truncate-then-write often yields one empty read before the bytes land.
                        for (var retry = 0; retry < 5 && !_cts.IsCancellationRequested; retry++)
                        {
                            await Task.Delay(100, _cts.Token);
                            if (await ApplyFileAsync(context, full, _cts.Token))
                            {
                                StampAppliedFile(full);
                                break;
                            }
                        }
                    }
                    else
                    {
                        StampAppliedFile(full);
                    }

                    if (Interlocked.Exchange(ref _fileWatchState, 0) != 2)
                    {
                        return;
                    }

                    Interlocked.Exchange(ref _fileWatchState, 1);
                }
            }
            catch (OperationCanceledException)
            {
                // shut down
            }
            catch (Exception ex)
            {
                PlusLog.Error(context, $"Plus Discovery: file apply failed: {ex.Message}");
                Interlocked.Exchange(ref _fileWatchState, 0);
            }
        }, _cts.Token);
    }

    private void StampAppliedFile(string full)
    {
        try
        {
            var info = new FileInfo(full);
            _lastAppliedWriteUtc = info.LastWriteTimeUtc;
            _lastAppliedLength = info.Length;
        }
        catch (IOException)
        {
            // Leave prior stamp so the poll retries.
        }
        catch (UnauthorizedAccessException)
        {
            // Leave prior stamp so the poll retries.
        }
    }

    private void StartDnsLoop(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("discovery.dnsName", out var dnsName) || string.IsNullOrWhiteSpace(dnsName))
        {
            PlusLog.Warn(context, "Plus Discovery: mode=dns requires discovery.dnsName");
            return;
        }

        var port = int.TryParse(options.GetValueOrDefault("discovery.dnsPort"), out var p) ? p : 80;
        var intervalMs = int.TryParse(options.GetValueOrDefault("discovery.intervalMs"), out var ms) ? ms : 15000;
        var clusterId = options.GetValueOrDefault("discovery.clusterId") ?? dnsName;
        PlusLog.Info(context, $"Plus Discovery: DNS {dnsName}:{port} every {intervalMs}ms → cluster {clusterId}");

        _ = Task.Run(
            () => RunPeriodicAsync(
                ct => ApplyDnsAsync(context, dnsName, port, clusterId, _dnsApplyState, ct),
                intervalMs,
                "Plus Discovery: DNS apply failed",
                context),
            _cts.Token);
    }

    private void StartDnsSrvLoop(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("discovery.srvName", out var srvName) || string.IsNullOrWhiteSpace(srvName))
        {
            PlusLog.Warn(context, "Plus Discovery: mode=dnsSrv requires discovery.srvName");
            return;
        }

        var clusterId = options.GetValueOrDefault("discovery.clusterId") ?? srvName.Trim();
        var intervalMs = DnsSrvDiscovery.ReadIntervalMs(options);
        var maxTargets = DnsSrvDiscovery.ReadMaxTargets(options);
        var dnsServer = options.GetValueOrDefault("discovery.dnsServer");
        PlusLog.Info(context,
            $"Plus Discovery: DNS SRV {srvName.Trim()} every {intervalMs}ms (cap {maxTargets}) → cluster {clusterId}");
        _ = Task.Run(
            () => RunDnsSrvAsync(context, options, srvName.Trim(), clusterId, maxTargets, dnsServer, intervalMs, _cts.Token),
            _cts.Token);
    }

    private async Task RunDnsSrvAsync(
        PlusActivationContext context,
        IReadOnlyDictionary<string, string> options,
        string srvName,
        string clusterId,
        int maxTargets,
        string? dnsServer,
        int intervalMs,
        CancellationToken cancellationToken)
    {
        var backoffSettings = ReadBackoff(options);
        var backoff = new DiscoveryBackoff(backoffSettings.MinMs, backoffSettings.MaxMs);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var (destinations, ttlSeconds, truncated) = await DnsSrvDiscovery.QueryAsync(
                    srvName,
                    clusterId,
                    maxTargets,
                    dnsServer,
                    cancellationToken).ConfigureAwait(false);
                if (truncated && Interlocked.Exchange(ref _dnsSrvTruncateLogged, 1) == 0)
                {
                    PlusLog.Warn(context, $"Plus Discovery: DNS SRV {srvName} returned more targets than srvMaxTargets ({maxTargets}); list was truncated.");
                }

                await ApplyDestinationsIfChangedAsync(context, clusterId, destinations, _dnsSrvApplyState, cancellationToken)
                    .ConfigureAwait(false);
                backoff.Reset();
                var delayMs = Math.Max(intervalMs, ttlSeconds > 0 ? ttlSeconds * 1000 : 0);
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var delay = backoff.Next();
                PlusLog.Warn(context,
                    $"Plus Discovery: DNS SRV query failed ({ex.Message}); keeping last known destinations, retry in {(int)delay.TotalMilliseconds}ms");
                await DelayAsync(delay, cancellationToken);
            }
        }
    }

    private void StartConsul(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("discovery.consulUrl", out var url) || string.IsNullOrWhiteSpace(url))
        {
            PlusLog.Warn(context, "Plus Discovery: mode=consul requires discovery.consulUrl.");
            return;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            PlusLog.Warn(context, "Plus Discovery: discovery.consulUrl must be an absolute http(s) URL.");
            return;
        }

        var settings = new ConsulSettings(
            uri,
            options.GetValueOrDefault("discovery.clusterId") ?? "consul",
            ReadInt(options, "discovery.intervalMs", 15000, 100, 3_600_000),
            ReadInt(options, "discovery.consulWaitSeconds", 30, 1, 600),
            !string.Equals(options.GetValueOrDefault("discovery.consulPassing"), "false", StringComparison.OrdinalIgnoreCase),
            options.GetValueOrDefault("discovery.consulToken"),
            ReadBackoff(options));
        PlusLog.Info(context,
            $"Plus Discovery: consul blocking query {uri.GetLeftPart(UriPartial.Path)} (wait {settings.WaitSeconds}s, passing={settings.PassingOnly})");

        _ = Task.Run(() => RunConsulAsync(context, settings, _cts.Token), _cts.Token);
    }

    private static async Task RunConsulAsync(PlusActivationContext context, ConsulSettings settings, CancellationToken ct) // NOSONAR S3776 -- Consul blocking loop keeps index, backoff, and apply state in one place.
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var backoff = new DiscoveryBackoff(settings.Backoff.MinMs, settings.Backoff.MaxMs);
        var applied = new DiscoveryApplyState();
        ulong index = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var wait = TimeSpan.FromSeconds(settings.WaitSeconds);
                using var request = new HttpRequestMessage(HttpMethod.Get, BuildConsulUrl(settings, index));
                if (!string.IsNullOrEmpty(settings.Token))
                {
                    request.Headers.TryAddWithoutValidation("X-Consul-Token", settings.Token);
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(wait + TimeSpan.FromTicks(wait.Ticks / 16) + TimeSpan.FromSeconds(10));
                var started = Stopwatch.GetTimestamp();
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"consul returned HTTP {(int)response.StatusCode}");
                }

                var json = await response.Content.ReadAsStringAsync(linked.Token);
                var destinations = ParseConsulDestinations(json, settings.PassingOnly);
                await ApplyDestinationsIfChangedAsync(context, settings.ClusterId, destinations, applied, ct);
                backoff.Reset();

                var next = ReadConsulIndex(response);
                var unchanged = next is not null && next == index;
                if (next is null or 0)
                {
                    // Not a blocking-capable answer: fall back to a plain interval.
                    index = 0;
                    await DelayAsync(TimeSpan.FromMilliseconds(settings.IntervalMs), ct);
                }
                else
                {
                    // An index that goes backwards means the server state was reset: start over.
                    index = next.Value < index ? 0 : next.Value;
                    if (unchanged && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1))
                    {
                        await DelayAsync(TimeSpan.FromMilliseconds(settings.IntervalMs), ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var delay = backoff.Next();
                PlusLog.Warn(context,
                    $"Plus Discovery: consul query failed ({ex.Message}); keeping last known destinations, retry in {(int)delay.TotalMilliseconds}ms");
                await DelayAsync(delay, ct);
            }
        }
    }

    private static ulong? ReadConsulIndex(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Consul-Index", out var values) &&
        ulong.TryParse(values.FirstOrDefault(), out var parsed)
            ? parsed
            : null;

    internal static Uri BuildConsulUrl(ConsulSettings settings, ulong index)
    {
        var add = new List<KeyValuePair<string, string>>();
        if (settings.PassingOnly)
        {
            add.Add(new("passing", "true"));
        }

        if (index > 0)
        {
            add.Add(new("index", index.ToString(CultureInfo.InvariantCulture)));
            add.Add(new("wait", settings.WaitSeconds.ToString(CultureInfo.InvariantCulture) + "s"));
        }

        return WithQuery(settings.Url, add, "passing", "index", "wait");
    }

    /// <summary>Rebuilds <paramref name="url"/> with <paramref name="add"/> query pairs, dropping <paramref name="remove"/> first.</summary>
    internal static Uri WithQuery(Uri url, IEnumerable<KeyValuePair<string, string>> add, params string[] remove)
    {
        var parts = new List<string>();
        foreach (var raw in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = raw.Split('=', 2)[0];
            if (!remove.Any(r => string.Equals(Uri.UnescapeDataString(key), r, StringComparison.OrdinalIgnoreCase)))
            {
                parts.Add(raw);
            }
        }

        foreach (var pair in add)
        {
            parts.Add(Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value));
        }

        var builder = new UriBuilder(url) { Query = string.Join('&', parts) };
        return builder.Uri;
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> options, string key, int fallback, int min, int max) =>
        int.TryParse(options.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;

    private static BackoffSettings ReadBackoff(IReadOnlyDictionary<string, string> options)
    {
        var min = ReadInt(options, "discovery.backoffMinMs", 1000, 1, 600_000);
        var max = ReadInt(options, "discovery.backoffMaxMs", 30_000, min, 3_600_000);
        return new BackoffSettings(min, max);
    }

    /// <summary>Waits for <paramref name="delay"/>; returns false when discovery is shutting down.</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RunPeriodicAsync(
        Func<CancellationToken, Task> tick,
        int intervalMs,
        string errorPrefix,
        PlusActivationContext context)
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await tick(_cts.Token);
                await Task.Delay(intervalMs, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                PlusLog.Error(context, $"{errorPrefix}: {ex.Message}");
                if (!await TryDelayAsync(intervalMs))
                {
                    return;
                }
            }
        }
    }

    private async Task<bool> TryDelayAsync(int intervalMs)
    {
        try
        {
            await Task.Delay(intervalMs, _cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Applies <paramref name="destinations"/> when the set is non-empty and differs from what was last applied.
    /// An empty set is never applied, so the last known good destinations survive upstream outages.
    /// </summary>
    private static async Task<bool> ApplyDestinationsIfChangedAsync(
        PlusActivationContext context,
        string clusterId,
        List<DestinationConfig> destinations,
        DiscoveryApplyState state,
        CancellationToken cancellationToken)
    {
        if (destinations.Count == 0 || context.ClusterManager is null)
        {
            return false;
        }

        var signature = DiscoveryApplyState.Sign(destinations);
        if (string.Equals(signature, state.Signature, StringComparison.Ordinal))
        {
            return false;
        }

        var cluster = DiscoveryClusterMerge.WithDestinations(context.ClusterManager, clusterId, destinations);
        await context.ClusterManager.ApplyAsync([cluster], cancellationToken);
        state.Signature = signature;
        context.RefreshReverseProxy?.Invoke();
        PlusLog.Info(context, $"Plus Discovery: applied {destinations.Count} destination(s) to cluster {clusterId}");
        return true;
    }

    /// <returns><see langword="true"/> when clusters were parsed and applied; <see langword="false"/> on empty/invalid JSON.</returns>
    internal static async Task<bool> ApplyFileAsync(PlusActivationContext context, string path, CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (IOException)
        {
            // Truncate/replace in flight.
            return false;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        List<ClusterConfig>? clusters;
        try
        {
            clusters = ParseClustersDocument(json);
        }
        catch (JsonException)
        {
            // Partial write between truncate and flush.
            return false;
        }

        if (clusters is null || context.ClusterManager is null)
        {
            return false;
        }

        await context.ClusterManager.ApplyAsync(clusters, cancellationToken);
        context.RefreshReverseProxy?.Invoke();
        PlusLog.Info(context, $"Plus Discovery: applied {clusters.Count} cluster(s) from {path}");
        return true;
    }

    internal static async Task ApplyDnsAsync(
        PlusActivationContext context,
        string dnsName,
        int port,
        string clusterId,
        DiscoveryApplyState state,
        CancellationToken cancellationToken)
    {
        if (context.ClusterManager is null)
        {
            return;
        }

        var addresses = await Dns.GetHostAddressesAsync(dnsName, cancellationToken);
        var destinations = new List<DestinationConfig>();
        for (var i = 0; i < addresses.Length; i++)
        {
            var ip = addresses[i];
            destinations.Add(new DestinationConfig
            {
                Id = $"{clusterId}-{ip}",
                Address = ip.ToString(),
                Port = port,
            });
        }

        await ApplyDestinationsIfChangedAsync(context, clusterId, destinations, state, cancellationToken);
    }

    internal static List<ClusterConfig>? ParseClustersDocument(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<ClusterConfig>>(json, JsonOptions);
        }

        if (root.ValueKind == JsonValueKind.Object &&
            (root.TryGetProperty("clusters", out var el) || root.TryGetProperty("Clusters", out el)))
        {
            return JsonSerializer.Deserialize<List<ClusterConfig>>(el.GetRawText(), JsonOptions);
        }

        return null;
    }

    public static List<DestinationConfig> ParseConsulDestinations(string json) =>
        ParseConsulDestinations(json, passingOnly: false);

    /// <summary>
    /// Parses a Consul health or catalog service response. With <paramref name="passingOnly"/>, an entry whose
    /// <c>Checks</c> array holds any check that is not <c>passing</c> is dropped.
    /// </summary>
    public static List<DestinationConfig> ParseConsulDestinations(string json, bool passingOnly)
    {
        var list = new List<DestinationConfig>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        var i = 0;
        foreach (var item in root.EnumerateArray())
        {
            if (passingOnly && !AllChecksPassing(item))
            {
                continue;
            }

            if (!TryParseConsulItem(item, i, out var dest))
            {
                continue;
            }

            list.Add(dest);
            i++;
        }

        return list;
    }

    private static bool AllChecksPassing(JsonElement item)
    {
        if (!item.TryGetProperty("Checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        foreach (var check in checks.EnumerateArray())
        {
            var status = check.ValueKind == JsonValueKind.Object ? TryGetString(check, "Status") : null;
            if (!string.Equals(status, "passing", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseConsulItem(JsonElement item, int index, out DestinationConfig dest)
    {
        dest = null!;
        string? address;
        var port = 80;
        string? id;

        if (item.TryGetProperty("Service", out var service))
        {
            address = TryGetString(service, "Address");
            if (service.TryGetProperty("Port", out var p) && p.TryGetInt32(out var pn))
            {
                port = pn;
            }

            id = TryGetString(service, "ID");
            if (string.IsNullOrWhiteSpace(address) && item.TryGetProperty("Node", out var node) &&
                node.ValueKind == JsonValueKind.Object)
            {
                // Consul leaves Service.Address empty when the service listens on the node address.
                address = TryGetString(node, "Address");
            }
        }
        else
        {
            address = TryGetString(item, "ServiceAddress") ?? TryGetString(item, "Address");
            if ((item.TryGetProperty("ServicePort", out var p) || item.TryGetProperty("Port", out p)) &&
                p.TryGetInt32(out var pn))
            {
                port = pn;
            }

            id = TryGetString(item, "ServiceID") ?? TryGetString(item, "ID");
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        dest = new DestinationConfig
        {
            Id = id ?? $"consul-{index}",
            Address = address,
            Port = port,
        };
        return true;
    }

    private const string DefaultServiceAccountDir = "/var/run/secrets/kubernetes.io/serviceaccount";

    private void StartKubernetes(PlusActivationContext context, IReadOnlyDictionary<string, string> options) // NOSONAR S3776 -- Kubernetes discovery wiring validates URL, selectors, and service-account paths together.
    {
        var saOption = options.GetValueOrDefault("discovery.k8sServiceAccountDir");
        var explicitSa = !string.IsNullOrWhiteSpace(saOption);
        var saDir = explicitSa ? saOption!.Trim() : DefaultServiceAccountDir;

        Uri? api;
        var configured = options.GetValueOrDefault("discovery.k8sUrl");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out api) ||
                api.Scheme is not ("http" or "https"))
            {
                PlusLog.Warn(context, "Plus Discovery: discovery.k8sUrl must be an absolute http(s) URL.");
                return;
            }
        }
        else
        {
            api = InClusterApiServer();
            if (api is null)
            {
                PlusLog.Warn(context,
                    "Plus Discovery: mode=k8s needs discovery.k8sUrl or an in-cluster environment (KUBERNETES_SERVICE_HOST).");
                return;
            }
        }

        var service = options.GetValueOrDefault("discovery.k8sService");
        var extraSelector = options.GetValueOrDefault("discovery.k8sLabelSelector");
        var selectors = new List<string>();
        if (!string.IsNullOrWhiteSpace(service))
        {
            selectors.Add("kubernetes.io/service-name=" + service.Trim());
        }

        if (!string.IsNullOrWhiteSpace(extraSelector))
        {
            selectors.Add(extraSelector.Trim());
        }

        // A path on discovery.k8sUrl, or a bare URL with no selector, is taken as the exact collection
        // (or single Endpoints/EndpointSlice object, which is polled) URL.
        var explicitUrl = api.AbsolutePath.Length > 1 ||
                          (!string.IsNullOrWhiteSpace(configured) && selectors.Count == 0);
        Uri listUrl;
        if (explicitUrl)
        {
            listUrl = api;
        }
        else
        {
            if (selectors.Count == 0)
            {
                PlusLog.Warn(context,
                    "Plus Discovery: mode=k8s requires discovery.k8sService or discovery.k8sLabelSelector (or a full EndpointSlice URL in discovery.k8sUrl).");
                return;
            }

            var ns = options.GetValueOrDefault("discovery.k8sNamespace");
            if (string.IsNullOrWhiteSpace(ns))
            {
                ns = ReadNamespaceFile(Path.Combine(saDir, "namespace")) ?? "default";
            }

            listUrl = new Uri(api, $"/apis/discovery.k8s.io/v1/namespaces/{Uri.EscapeDataString(ns.Trim())}/endpointslices");
        }

        var settings = new KubernetesSettings(
            listUrl,
            selectors.Count == 0 ? null : string.Join(',', selectors),
            options.GetValueOrDefault("discovery.clusterId") ?? "k8s",
            options.GetValueOrDefault("discovery.k8sPortName"),
            ReadInt(options, "discovery.intervalMs", 15000, 100, 3_600_000),
            ReadInt(options, "discovery.k8sWatchTimeoutSeconds", 300, 1, 3600),
            ReadInt(options, "discovery.k8sTokenRefreshSeconds", 60, 0, 86_400),
            saDir,
            explicitSa,
            ReadBackoff(options));
        PlusLog.Info(context,
            $"Plus Discovery: k8s EndpointSlice list-then-watch {listUrl.GetLeftPart(UriPartial.Path)} selector={settings.LabelSelector ?? "(none)"}");

        _ = Task.Run(() => RunKubernetesAsync(context, settings, _cts.Token), _cts.Token);
    }

    private static Uri? InClusterApiServer()
    {
        var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT_HTTPS") ??
                   Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT") ?? "443";
        host = host.Trim();
        if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('['))
        {
            host = "[" + host + "]";
        }

        return Uri.TryCreate($"https://{host}:{port}", UriKind.Absolute, out var uri) ? uri : null;
    }

    private static string? ReadNamespaceFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();
            return text.Length > 0 ? text : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task RunKubernetesAsync(PlusActivationContext context, KubernetesSettings s, CancellationToken ct) // NOSONAR S3776 -- Kubernetes list-watch loop coordinates credentials, slices, and apply state.
    {
        var credentials = new ServiceAccountCredentials(
            Path.Combine(s.ServiceAccountDir, "token"),
            Path.Combine(s.ServiceAccountDir, "ca.crt"),
            TimeSpan.FromSeconds(s.TokenRefreshSeconds));
        var sendToken = s.ListUrl.Scheme == Uri.UriSchemeHttps || s.TokenExplicit;
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (s.ListUrl.Scheme == Uri.UriSchemeHttps)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = credentials.ValidateServerCertificate;
        }

        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var backoff = new DiscoveryBackoff(s.Backoff.MinMs, s.Backoff.MaxMs);
        var applied = new DiscoveryApplyState();
        var slices = new EndpointSliceSet();
        string? resourceVersion = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (resourceVersion is null)
                {
                    var list = await ListAsync(s, http, credentials, sendToken, ct);
                    if (list.Snapshot is not null)
                    {
                        // A single Endpoints/EndpointSlice object has no list resourceVersion to watch: poll it.
                        await ApplyDestinationsIfChangedAsync(context, s.ClusterId, list.Snapshot, applied, ct);
                        backoff.Reset();
                        await DelayAsync(TimeSpan.FromMilliseconds(s.IntervalMs), ct);
                        continue;
                    }

                    slices.ReplaceAll(list.Slices!);
                    await ApplyDestinationsIfChangedAsync(context, s.ClusterId, slices.Flatten(), applied, ct);
                    resourceVersion = list.ResourceVersion ?? string.Empty;
                }

                resourceVersion = await WatchAsync(
                    context, s, http, credentials, sendToken, resourceVersion, slices, applied, backoff, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (KubernetesGoneException)
            {
                resourceVersion = null;
                var delay = backoff.Next();
                PlusLog.Info(context,
                    $"Plus Discovery: k8s resourceVersion expired (410 Gone); relisting in {(int)delay.TotalMilliseconds}ms");
                await DelayAsync(delay, ct);
            }
            catch (Exception ex)
            {
                if (ex is JsonException)
                {
                    // Cannot trust the stream position after a malformed event.
                    resourceVersion = null;
                }

                var delay = backoff.Next();
                PlusLog.Warn(context,
                    $"Plus Discovery: k8s discovery failed ({ex.Message}); keeping last known destinations, retry in {(int)delay.TotalMilliseconds}ms");
                await DelayAsync(delay, ct);
            }
        }
    }

    private static HttpRequestMessage CreateKubernetesRequest(
        Uri url, ServiceAccountCredentials credentials, bool sendToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (sendToken && credentials.GetToken() is { } token)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        return request;
    }

    private static void ThrowForKubernetesStatus(HttpResponseMessage response, ServiceAccountCredentials credentials)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Gone)
        {
            throw new KubernetesGoneException();
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // The projected token may have rotated since the last read.
            _ = credentials.GetToken(forceReload: true);
        }

        throw new HttpRequestException($"kubernetes API returned HTTP {(int)response.StatusCode}");
    }

    private static async Task<KubernetesListResult> ListAsync(
        KubernetesSettings s, HttpClient http, ServiceAccountCredentials credentials, bool sendToken, CancellationToken ct)
    {
        var url = s.LabelSelector is null
            ? s.ListUrl
            : WithQuery(s.ListUrl, [new(LabelSelectorQueryKey, s.LabelSelector)], LabelSelectorQueryKey);
        using var request = CreateKubernetesRequest(url, credentials, sendToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token);
        ThrowForKubernetesStatus(response, credentials);
        var json = await response.Content.ReadAsStringAsync(linked.Token);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items) &&
            items.ValueKind == JsonValueKind.Array)
        {
            var slices = new Dictionary<string, List<DestinationConfig>>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in items.EnumerateArray())
            {
                slices[ReadSliceName(item) ?? "slice-" + index++] = ParseEndpointSlice(item, s.PortName);
            }

            return new KubernetesListResult(ReadResourceVersion(root), slices, null);
        }

        if (root.ValueKind == JsonValueKind.Object &&
            (root.TryGetProperty("subsets", out _) || root.TryGetProperty("endpoints", out _)))
        {
            return new KubernetesListResult(null, null, ParseKubernetesDestinations(json));
        }

        throw new InvalidDataException("unrecognized kubernetes response (expected an EndpointSliceList)");
    }

    /// <summary>Streams one watch window. Returns the resourceVersion to resume from.</summary>
    private static async Task<string> WatchAsync( // NOSONAR S107 -- Watch parameters mirror the Kubernetes client context without an extra options bag.
        PlusActivationContext context,
        KubernetesSettings s,
        HttpClient http,
        ServiceAccountCredentials credentials,
        bool sendToken,
        string resourceVersion,
        EndpointSliceSet slices,
        DiscoveryApplyState applied,
        DiscoveryBackoff backoff,
        CancellationToken ct)
    {
        // A previous apply may have failed after the set changed; converge before waiting for events.
        await ApplyDestinationsIfChangedAsync(context, s.ClusterId, slices.Flatten(), applied, ct);

        var query = new List<KeyValuePair<string, string>>
        {
            new("watch", "true"),
            new("allowWatchBookmarks", "true"),
            new("timeoutSeconds", s.WatchTimeoutSeconds.ToString(CultureInfo.InvariantCulture)),
        };
        if (resourceVersion.Length > 0)
        {
            query.Add(new("resourceVersion", resourceVersion));
        }

        if (s.LabelSelector is not null)
        {
            query.Add(new(LabelSelectorQueryKey, s.LabelSelector));
        }

        // A selector already present on an explicit discovery.k8sUrl stays unless a configured one replaces it.
        var replaced = s.LabelSelector is null
            ? new[] { "watch", "allowWatchBookmarks", "timeoutSeconds", "resourceVersion" }
            : ["watch", "allowWatchBookmarks", "timeoutSeconds", "resourceVersion", LabelSelectorQueryKey];
        var url = WithQuery(s.ListUrl, query, replaced);
        using var request = CreateKubernetesRequest(url, credentials, sendToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(s.WatchTimeoutSeconds + 15));
        var started = Stopwatch.GetTimestamp();
        var events = 0;
        var current = resourceVersion;

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            ThrowForKubernetesStatus(response, credentials);
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(linked.Token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                current = await ProcessWatchEventAsync(context, s, line, current, slices, applied, ct);
                events++;
                backoff.Reset();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The watch window elapsed; resume from the last resourceVersion.
        }

        if (events == 0 && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
        {
            throw new IOException("watch closed without delivering events");
        }

        return current;
    }

    private static async Task<string> ProcessWatchEventAsync(
        PlusActivationContext context,
        KubernetesSettings s,
        string line,
        string resourceVersion,
        EndpointSliceSet slices,
        DiscoveryApplyState applied,
        CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = TryGetString(root, "type");
        if (!root.TryGetProperty("object", out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("watch event has no object");
        }

        switch (type)
        {
            case "BOOKMARK":
                return ReadResourceVersion(obj) ?? resourceVersion;
            case "ERROR":
                if (obj.TryGetProperty("code", out var code) && code.TryGetInt32(out var status) && status == 410)
                {
                    throw new KubernetesGoneException();
                }

                throw new InvalidOperationException("watch error: " + (TryGetString(obj, "message") ?? "unknown"));
            case "ADDED":
            case "MODIFIED":
            case "DELETED":
                var name = ReadSliceName(obj) ?? throw new JsonException("watch event has no metadata.name");
                if (type == "DELETED")
                {
                    slices.Remove(name);
                }
                else
                {
                    slices.Upsert(name, ParseEndpointSlice(obj, s.PortName));
                }

                await ApplyDestinationsIfChangedAsync(context, s.ClusterId, slices.Flatten(), applied, ct);
                return ReadResourceVersion(obj) ?? resourceVersion;
            default:
                return resourceVersion;
        }
    }

    private static string? ReadResourceVersion(JsonElement element) =>
        element.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            ? TryGetString(metadata, "resourceVersion")
            : null;

    private static string? ReadSliceName(JsonElement element) =>
        element.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            ? TryGetString(metadata, "name")
            : null;

    /// <summary>
    /// Ready destinations of one EndpointSlice object. An endpoint whose <c>conditions.ready</c> is false is
    /// skipped; a missing condition counts as ready. With <paramref name="portName"/> the matching named port
    /// is used, otherwise the first TCP port.
    /// </summary>
    internal static List<DestinationConfig> ParseEndpointSlice(JsonElement slice, string? portName) // NOSONAR S3776 -- EndpointSlice parsing keeps port selection and ready endpoints in one routine.
    {
        var list = new List<DestinationConfig>();
        var port = 80;
        if (slice.TryGetProperty("ports", out var ports) && ports.ValueKind == JsonValueKind.Array &&
            ports.GetArrayLength() > 0)
        {
            int? chosen = null;
            foreach (var candidate in ports.EnumerateArray())
            {
                var protocol = TryGetString(candidate, "protocol");
                if (protocol is not null && !string.Equals(protocol, "TCP", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(portName) &&
                    !string.Equals(TryGetString(candidate, "name"), portName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (candidate.TryGetProperty("port", out var number) && number.TryGetInt32(out var parsed))
                {
                    chosen = parsed;
                    break;
                }
            }

            if (chosen is null)
            {
                return list;
            }

            port = chosen.Value;
        }

        if (!slice.TryGetProperty("endpoints", out var endpoints) || endpoints.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var endpoint in endpoints.EnumerateArray())
        {
            if (endpoint.TryGetProperty("conditions", out var conditions) &&
                conditions.ValueKind == JsonValueKind.Object &&
                conditions.TryGetProperty("ready", out var ready) &&
                ready.ValueKind == JsonValueKind.False)
            {
                continue;
            }

            if (!endpoint.TryGetProperty("addresses", out var addresses) || addresses.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var zone = endpoint.TryGetProperty("zone", out var zoneElement) && zoneElement.ValueKind == JsonValueKind.String
                ? zoneElement.GetString()
                : null;

            foreach (var element in addresses.EnumerateArray())
            {
                var address = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
                if (string.IsNullOrWhiteSpace(address))
                {
                    continue;
                }

                list.Add(new DestinationConfig
                {
                    Id = $"k8s-{address}-{port}",
                    Address = address,
                    Port = port,
                    Zone = zone,
                });
            }
        }

        return list;
    }

    /// <summary>
    /// Parses a documented subset: Endpoints-style <c>subsets[].addresses[].ip</c> + <c>ports[].port</c>,
    /// or EndpointSlice-style <c>endpoints[].addresses[]</c> + <c>ports[].port</c>.
    /// </summary>
    public static List<DestinationConfig> ParseKubernetesDestinations(string json)
    {
        var list = new List<DestinationConfig>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var i = 0;

        if (root.TryGetProperty("subsets", out var subsets))
        {
            AppendEndpointsSubsets(subsets, list, ref i);
            return list;
        }

        if (root.TryGetProperty("endpoints", out var endpoints))
        {
            AppendEndpointSlice(root, endpoints, list, ref i);
        }

        return list;
    }

    private static void AppendEndpointsSubsets(JsonElement subsets, List<DestinationConfig> list, ref int i)
    {
        foreach (var subset in subsets.EnumerateArray())
        {
            var port = ReadFirstPort(subset, "ports") ?? 80;
            if (!subset.TryGetProperty("addresses", out var addresses))
            {
                continue;
            }

            foreach (var addr in addresses.EnumerateArray())
            {
                var ip = TryGetString(addr, "ip");
                if (string.IsNullOrWhiteSpace(ip))
                {
                    continue;
                }

                list.Add(new DestinationConfig
                {
                    Id = TryGetString(addr, "hostname") ?? $"k8s-{i}",
                    Address = ip,
                    Port = port,
                });
                i++;
            }
        }
    }

    private static void AppendEndpointSlice(
        JsonElement root, JsonElement endpoints, List<DestinationConfig> list, ref int i)
    {
        var port = ReadFirstPort(root, "ports") ?? 80;
        foreach (var ep in endpoints.EnumerateArray())
        {
            if (!ep.TryGetProperty("addresses", out var addresses))
            {
                continue;
            }

            foreach (var addr in addresses.EnumerateArray())
            {
                var ip = addr.GetString();
                if (string.IsNullOrWhiteSpace(ip))
                {
                    continue;
                }

                list.Add(new DestinationConfig
                {
                    Id = $"k8s-{i}",
                    Address = ip,
                    Port = port,
                });
                i++;
            }
        }
    }

    private static int? ReadFirstPort(JsonElement parent, string portsProperty)
    {
        if (parent.TryGetProperty(portsProperty, out var ports) && ports.GetArrayLength() > 0 &&
            ports[0].TryGetProperty("port", out var p) && p.TryGetInt32(out var pn))
        {
            return pn;
        }

        return null;
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose()
    {
        _cts.Cancel();
        _filePollTimer?.Dispose();
        _watcher?.Dispose();
        _cts.Dispose();
    }
}

internal sealed record BackoffSettings(int MinMs, int MaxMs);

internal sealed record ConsulSettings(
    Uri Url,
    string ClusterId,
    int IntervalMs,
    int WaitSeconds,
    bool PassingOnly,
    string? Token,
    BackoffSettings Backoff);

internal sealed record KubernetesSettings(
    Uri ListUrl,
    string? LabelSelector,
    string ClusterId,
    string? PortName,
    int IntervalMs,
    int WatchTimeoutSeconds,
    int TokenRefreshSeconds,
    string ServiceAccountDir,
    bool TokenExplicit,
    BackoffSettings Backoff);

internal sealed record KubernetesListResult(
    string? ResourceVersion,
    Dictionary<string, List<DestinationConfig>>? Slices,
    List<DestinationConfig>? Snapshot);

/// <summary>The API server rejected a resourceVersion (HTTP 410 or a watch ERROR event): relist.</summary>
public sealed class KubernetesGoneException : Exception
{
    public KubernetesGoneException()
        : base("kubernetes resourceVersion is too old (410 Gone)")
    {
    }
}

/// <summary>Remembers what was last applied so unchanged results do not churn the cluster manager.</summary>
internal sealed class DiscoveryApplyState
{
    public string? Signature { get; set; }

    public static string Sign(List<DestinationConfig> destinations)
    {
        var parts = new string[destinations.Count];
        for (var i = 0; i < parts.Length; i++)
        {
            var d = destinations[i];
            parts[i] = $"{d.Id}|{d.Address}|{d.Port}|{d.UseHttps}|{d.Weight}";
        }

        Array.Sort(parts, StringComparer.Ordinal);
        return string.Join('\n', parts);
    }
}

/// <summary>Ready destinations per EndpointSlice name; the union is what gets applied.</summary>
internal sealed class EndpointSliceSet
{
    private readonly Dictionary<string, List<DestinationConfig>> _slices = new(StringComparer.Ordinal);

    public void ReplaceAll(Dictionary<string, List<DestinationConfig>> slices)
    {
        _slices.Clear();
        foreach (var pair in slices)
        {
            _slices[pair.Key] = pair.Value;
        }
    }

    public void Upsert(string name, List<DestinationConfig> destinations) => _slices[name] = destinations;

    public void Remove(string name) => _slices.Remove(name);

    public List<DestinationConfig> Flatten()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DestinationConfig>();
        foreach (var name in _slices.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            foreach (var destination in _slices[name].Where(d => seen.Add(d.Id)))
            {
                result.Add(destination);
            }
        }

        return result;
    }
}

/// <summary>Exponential backoff with equal jitter: each delay is uniform in [cap/2, cap].</summary>
internal sealed class DiscoveryBackoff
{
    private readonly int _minMs;
    private readonly int _maxMs;
    private readonly Func<double> _random;
    private int _attempt;

    public DiscoveryBackoff(int minMs, int maxMs, Func<double>? random = null)
    {
        _minMs = Math.Max(1, minMs);
        _maxMs = Math.Max(_minMs, maxMs);
        _random = random ?? Random.Shared.NextDouble;
    }

    public TimeSpan Next()
    {
        var delay = Compute(_attempt, _minMs, _maxMs, _random());
        if (_attempt < 30)
        {
            _attempt++;
        }

        return delay;
    }

    public void Reset() => _attempt = 0;

    public static TimeSpan Compute(int attempt, int minMs, int maxMs, double jitter)
    {
        var cap = Math.Min((double)maxMs, minMs * Math.Pow(2, Math.Clamp(attempt, 0, 30)));
        var half = cap / 2;
        return TimeSpan.FromMilliseconds(half + half * Math.Clamp(jitter, 0, 1));
    }
}

/// <summary>
/// Service-account token and CA bundle. The projected token rotates on disk, so it is re-read after
/// <c>ttl</c> (and immediately after a 401/403); a changed CA file is picked up the same way.
/// </summary>
internal sealed class ServiceAccountCredentials
{
    private readonly string _tokenPath;
    private readonly string _caPath;
    private readonly TimeSpan _ttl;
    private readonly object _gate = new();
    private string? _token;
    private DateTime _tokenLoadedUtc = DateTime.MinValue;
    private X509Certificate2Collection? _roots;
    private DateTime _caCheckedUtc = DateTime.MinValue;
    private DateTime _caWriteUtc = DateTime.MinValue;

    public ServiceAccountCredentials(string tokenPath, string caPath, TimeSpan ttl)
    {
        _tokenPath = tokenPath;
        _caPath = caPath;
        _ttl = ttl;
    }

    public string? GetToken(bool forceReload = false)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (!forceReload && _token is not null && now - _tokenLoadedUtc < _ttl)
            {
                return _token;
            }

            _tokenLoadedUtc = now;
            try
            {
                if (File.Exists(_tokenPath))
                {
                    var text = File.ReadAllText(_tokenPath).Trim();
                    if (text.Length > 0)
                    {
                        _token = text;
                    }
                }
            }
            catch (IOException)
            {
                // Keep the previous token; the file may be mid-rotation.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep the previous token; the file may be mid-rotation.
            }

            return _token;
        }
    }

    public bool ValidateServerCertificate(
        object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (certificate is null ||
            (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
        {
            return false;
        }

        var roots = GetRoots();
        if (roots is null || roots.Count == 0)
        {
            return false;
        }

        using var leaf = new X509Certificate2(certificate);
        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        custom.ChainPolicy.CustomTrustStore.AddRange(roots);
        if (chain is not null)
        {
            foreach (var element in chain.ChainElements.Skip(1))
            {
                custom.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        return custom.Build(leaf);
    }

    private X509Certificate2Collection? GetRoots()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_roots is not null && now - _caCheckedUtc < _ttl)
            {
                return _roots;
            }

            _caCheckedUtc = now;
            try
            {
                if (!File.Exists(_caPath))
                {
                    return _roots;
                }

                var write = File.GetLastWriteTimeUtc(_caPath);
                if (_roots is null || write != _caWriteUtc)
                {
                    var loaded = new X509Certificate2Collection();
                    loaded.ImportFromPemFile(_caPath);
                    _roots = loaded;
                    _caWriteUtc = write;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                // Keep the previous bundle.
            }

            return _roots;
        }
    }
}
