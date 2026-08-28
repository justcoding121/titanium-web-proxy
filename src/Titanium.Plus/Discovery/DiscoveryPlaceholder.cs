using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Titanium.Plus.ControlPlane;
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
            case "k8s":
            case "kubernetes":
                Console.WriteLine(
                    $"Plus Discovery: mode={mode} — file/dns are primary; attempting best-effort HTTP discovery if configured.");
                discovery.StartConsulBestEffort(context, options);
                break;
            default:
                Console.WriteLine($"Plus Discovery: unknown mode={mode}");
                break;
        }

        return discovery;
    }

    private void StartFileWatch(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("discovery.file", out var path) || string.IsNullOrWhiteSpace(path))
        {
            Console.WriteLine("Plus Discovery: mode=file requires discovery.file");
            return;
        }

        var full = Path.GetFullPath(path);
        Console.WriteLine($"Plus Discovery: watching file {full}");
        _ = Task.Run(async () =>
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
                Console.WriteLine($"Plus Discovery: initial file apply failed: {ex.Message}");
            }
        });

        var dir = Path.GetDirectoryName(full);
        var name = Path.GetFileName(full);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name))
        {
            return;
        }

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
                    Console.WriteLine($"Plus Discovery: file apply failed: {ex.Message}");
                }
                finally
                {
                    lock (gate)
                    {
                        pending = false;
                    }
                }
            });
        }

        _watcher.Changed += OnChange;
        _watcher.Created += OnChange;
        _watcher.Renamed += OnChange;
        _watcher.EnableRaisingEvents = true;
    }

    private void StartDnsLoop(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("discovery.dnsName", out var dnsName) || string.IsNullOrWhiteSpace(dnsName))
        {
            Console.WriteLine("Plus Discovery: mode=dns requires discovery.dnsName");
            return;
        }

        var port = int.TryParse(options.GetValueOrDefault("discovery.dnsPort"), out var p) ? p : 80;
        var intervalMs = int.TryParse(options.GetValueOrDefault("discovery.intervalMs"), out var ms) ? ms : 15000;
        var clusterId = options.GetValueOrDefault("discovery.clusterId") ?? dnsName;
        Console.WriteLine($"Plus Discovery: DNS {dnsName}:{port} every {intervalMs}ms → cluster {clusterId}");

        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await ApplyDnsAsync(context, dnsName, port, clusterId, _cts.Token);
                    await Task.Delay(intervalMs, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Plus Discovery: DNS apply failed: {ex.Message}");
                    try
                    {
                        await Task.Delay(intervalMs, _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        });
    }

    private void StartConsulBestEffort(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("discovery.consulUrl", out var url) || string.IsNullOrWhiteSpace(url))
        {
            Console.WriteLine("Plus Discovery: no discovery.consulUrl — skipping HTTP poll.");
            return;
        }

        var intervalMs = int.TryParse(options.GetValueOrDefault("discovery.intervalMs"), out var ms) ? ms : 15000;
        var clusterId = options.GetValueOrDefault("discovery.clusterId") ?? "consul";
        Console.WriteLine($"Plus Discovery: consul poll {url} every {intervalMs}ms");

        _ = Task.Run(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var json = await http.GetStringAsync(url, _cts.Token);
                    var destinations = ParseConsulDestinations(json);
                    if (destinations.Count > 0 && context.ClusterManager is not null)
                    {
                        await context.ClusterManager.ApplyAsync(
                        [
                            new ClusterConfig { Id = clusterId, Destinations = destinations },
                        ], _cts.Token);
                        context.RefreshReverseProxy?.Invoke();
                    }

                    await Task.Delay(intervalMs, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Plus Discovery: consul poll failed: {ex.Message}");
                    try
                    {
                        await Task.Delay(intervalMs, _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        });
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
        Console.WriteLine($"Plus Discovery: applied {clusters.Count} cluster(s) from {path}");
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

    internal static List<DestinationConfig> ParseConsulDestinations(string json)
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
            string? address = null;
            var port = 80;
            string? id = null;

            if (item.TryGetProperty("Service", out var service))
            {
                address = service.TryGetProperty("Address", out var a) ? a.GetString() : null;
                if (service.TryGetProperty("Port", out var p) && p.TryGetInt32(out var pn))
                {
                    port = pn;
                }

                id = service.TryGetProperty("ID", out var sid) ? sid.GetString() : null;
            }
            else
            {
                address = item.TryGetProperty("ServiceAddress", out var a) ? a.GetString()
                    : item.TryGetProperty("Address", out a) ? a.GetString() : null;
                if (item.TryGetProperty("ServicePort", out var p) && p.TryGetInt32(out var pn))
                {
                    port = pn;
                }
                else if (item.TryGetProperty("Port", out p) && p.TryGetInt32(out pn))
                {
                    port = pn;
                }

                id = item.TryGetProperty("ServiceID", out var sid) ? sid.GetString()
                    : item.TryGetProperty("ID", out sid) ? sid.GetString() : null;
            }

            if (string.IsNullOrWhiteSpace(address))
            {
                continue;
            }

            list.Add(new DestinationConfig
            {
                Id = id ?? $"consul-{i}",
                Address = address,
                Port = port,
            });
            i++;
        }

        return list;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _watcher?.Dispose();
        _cts.Dispose();
    }
}

/// <summary>Legacy stub type name.</summary>
public sealed class DiscoveryPlaceholder;
