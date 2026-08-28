using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Titanium.Plus.ControlPlane;
using Titanium.Plus;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Discovery;

/// <summary>
/// Service discovery → <see cref="IClusterManager.ApplyAsync"/>.
/// Primary modes: <c>file</c> and <c>dns</c>; <c>consul</c>/<c>k8s</c> are best-effort.
/// </summary>
public sealed class ServiceDiscovery : IDisposable
{
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
            case "consul":
                PlusLog.Info(context,
                    "Plus Discovery: mode=consul — polling discovery.consulUrl (file/dns are primary).");
                discovery.StartConsulBestEffort(context, options);
                break;
            case "k8s":
            case "kubernetes":
                PlusLog.Info(context,
                    "Plus Discovery: mode=k8s — use discovery.k8sUrl with Endpoints/EndpointSlice JSON subset, or file/dns.");
                discovery.StartK8sBestEffort(context, options);
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
        _ = Task.Run(() => ApplyFileInitialBestEffortAsync(context, full), _cts.Token);

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
            if (File.Exists(full))
            {
                await ApplyFileAsync(context, full, _cts.Token);
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
        var gate = new object();
        var pending = false;
        void OnChange(object _, FileSystemEventArgs __)
        {
            lock (gate)
            {
                if (pending)
                {
                    return;
                }

                pending = true;
            }

            QueueDebouncedFileApply(context, full, () =>
            {
                lock (gate)
                {
                    pending = false;
                }
            });
        }

        _watcher.Changed += OnChange;
        _watcher.Created += OnChange;
        _watcher.Renamed += OnChange;
        _watcher.EnableRaisingEvents = true;
    }

    private void QueueDebouncedFileApply(
        PlusActivationContext context, string full, Action clearPending)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, _cts.Token);
                await ApplyFileAsync(context, full, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                // shut down
            }
            catch (Exception ex)
            {
                PlusLog.Error(context, $"Plus Discovery: file apply failed: {ex.Message}");
            }
            finally
            {
                clearPending();
            }
        }, _cts.Token);
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
                ct => ApplyDnsAsync(context, dnsName, port, clusterId, ct),
                intervalMs,
                "Plus Discovery: DNS apply failed",
                context),
            _cts.Token);
    }

    private void StartConsulBestEffort(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("discovery.consulUrl", out var url) || string.IsNullOrWhiteSpace(url))
        {
            PlusLog.Warn(context, "Plus Discovery: no discovery.consulUrl — skipping HTTP poll.");
            return;
        }

        var intervalMs = int.TryParse(options.GetValueOrDefault("discovery.intervalMs"), out var ms) ? ms : 15000;
        var clusterId = options.GetValueOrDefault("discovery.clusterId") ?? "consul";
        PlusLog.Info(context, $"Plus Discovery: consul poll {url} every {intervalMs}ms");

        _ = Task.Run(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            await RunPeriodicAsync(
                async ct =>
                {
                    var json = await http.GetStringAsync(url, ct);
                    await ApplyDestinationsIfAnyAsync(context, clusterId, ParseConsulDestinations(json), ct);
                },
                intervalMs,
                "Plus Discovery: consul poll failed",
                context);
        }, _cts.Token);
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

    private static async Task ApplyDestinationsIfAnyAsync(
        PlusActivationContext context,
        string clusterId,
        List<DestinationConfig> destinations,
        CancellationToken cancellationToken)
    {
        if (destinations.Count == 0 || context.ClusterManager is null)
        {
            return;
        }

        await context.ClusterManager.ApplyAsync(
        [
            new ClusterConfig { Id = clusterId, Destinations = destinations },
        ], cancellationToken);
        context.RefreshReverseProxy?.Invoke();
    }

    internal static async Task ApplyFileAsync(PlusActivationContext context, string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var clusters = ParseClustersDocument(json);
        if (clusters is null || context.ClusterManager is null)
        {
            return;
        }

        await context.ClusterManager.ApplyAsync(clusters, cancellationToken);
        context.RefreshReverseProxy?.Invoke();
        PlusLog.Info(context, $"Plus Discovery: applied {clusters.Count} cluster(s) from {path}");
    }

    internal static async Task ApplyDnsAsync(
        PlusActivationContext context,
        string dnsName,
        int port,
        string clusterId,
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

        if (destinations.Count == 0)
        {
            return;
        }

        await context.ClusterManager.ApplyAsync(
        [
            new ClusterConfig { Id = clusterId, Destinations = destinations },
        ], cancellationToken);
        context.RefreshReverseProxy?.Invoke();
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

    public static List<DestinationConfig> ParseConsulDestinations(string json)
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
            if (!TryParseConsulItem(item, i, out var dest))
            {
                continue;
            }

            list.Add(dest);
            i++;
        }

        return list;
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

    private void StartK8sBestEffort(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("discovery.k8sUrl", out var url) || string.IsNullOrWhiteSpace(url))
        {
            PlusLog.Warn(context,
                "Plus Discovery: k8s mode requires discovery.k8sUrl (Endpoints/EndpointSlice JSON). Full API watch is not supported.");
            return;
        }

        var intervalMs = int.TryParse(options.GetValueOrDefault("discovery.intervalMs"), out var ms) ? ms : 15000;
        var clusterId = options.GetValueOrDefault("discovery.clusterId") ?? "k8s";
        PlusLog.Info(context, $"Plus Discovery: k8s poll {url} every {intervalMs}ms");

        _ = Task.Run(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            await RunPeriodicAsync(
                async ct =>
                {
                    var json = await http.GetStringAsync(url, ct);
                    await ApplyDestinationsIfAnyAsync(context, clusterId, ParseKubernetesDestinations(json), ct);
                },
                intervalMs,
                "Plus Discovery: k8s poll failed",
                context);
        }, _cts.Token);
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
        element.TryGetProperty(propertyName, out var value) ? value.GetString() : null;


    public void Dispose()
    {
        _cts.Cancel();
        _watcher?.Dispose();
        _cts.Dispose();
    }
}
