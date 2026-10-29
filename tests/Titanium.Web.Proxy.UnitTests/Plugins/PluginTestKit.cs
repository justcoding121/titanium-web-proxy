using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Plugins;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests.Plugins;

/// <summary>Shared pieces for the plugin tests: plugin configs, a raw origin that echoes the request head, a proxy harness, and a meter probe.</summary>
internal static class PluginTestKit
{
    public static readonly string TestPlugins = Path.Combine(AppContext.BaseDirectory, "Titanium.Web.Proxy.TestPlugins.dll");

    public static readonly string SamplePlugin = Path.Combine(AppContext.BaseDirectory, "Titanium.Web.Proxy.Examples.RoutePlugin.dll");

    public static PluginConfig Plugin(
        string name,
        string typeName,
        string? failMode = null,
        int? timeoutMs = null,
        IReadOnlyList<(string Key, string Value)>? settings = null) => new()
    {
        Name = name,
        Assembly = TestPlugins,
        TypeName = "Titanium.Web.Proxy.TestPlugins." + typeName,
        FailMode = failMode,
        TimeoutMs = timeoutMs,
        Settings = (settings ?? []).ToDictionary(s => s.Key, s => s.Value),
    };

    public static string NewMarker() => Path.Combine(Path.GetTempPath(), "twp-plugin-" + Guid.NewGuid().ToString("N") + ".log");

    public static string[] ReadMarker(string path) => File.Exists(path) ? File.ReadAllLines(path) : [];

    /// <summary>Polls until the runtime reclaims every handle, forcing a collection between looks.</summary>
    public static bool CollectedWithin(IEnumerable<WeakReference> handles, int attempts = 40)
    {
        var list = handles.ToList();
        for (var i = 0; i < attempts; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (list.All(h => !h.IsAlive))
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return false;
    }

    /// <summary>Starts a proxy whose routes are given, with the plugin host's plugins wired in.</summary>
    public static async Task<ProxyHarness> StartProxyAsync(int originPort, PluginHost? host, params RouteConfig[] routes)
    {
        var manager = new ClusterManager();
        var clusters = new List<ClusterConfig>
        {
            new()
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = originPort }],
            },
        };
        await manager.ApplyAsync(clusters);

        var proxy = new ProxyServer(userTrustRootCertificate: false);
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes = routes,
            Clusters = clusters,
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
            RoutePlugins = host?.Plugins,
        };
        var endpoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false);
        proxy.AddEndPoint(endpoint);
        proxy.Start();
        return new ProxyHarness(proxy, endpoint.Port);
    }

    public static RouteConfig Route(string id, string path, int order, params string[] plugins) => new()
    {
        Id = id,
        ClusterId = "c1",
        Order = order,
        Match = new RouteMatch { Path = path, PathKind = PathMatchKind.Prefix },
        Middleware = plugins.Length == 0 ? null : plugins,
    };

    internal sealed class ProxyHarness : IDisposable
    {
        private readonly ProxyServer _proxy;

        public ProxyHarness(ProxyServer proxy, int port)
        {
            _proxy = proxy;
            Port = port;
        }

        public int Port { get; }

        public ProxyServer Proxy => _proxy;

        public HttpClient Client() => new(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{Port}/"),
            Timeout = TimeSpan.FromSeconds(20),
        };

        public void Dispose()
        {
            try
            {
                _proxy.Stop();
            }
            catch (Exception)
            {
                // Already stopped.
            }

            _proxy.Dispose();
        }
    }

    /// <summary>An HTTP/1.1 origin that answers every request with the request head it received, over keep-alive connections.</summary>
    internal sealed class EchoOrigin : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _connections;
        private int _requests;

        public EchoOrigin()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public int Connections => Volatile.Read(ref _connections);

        public int Requests => Volatile.Read(ref _requests);

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _connections);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (Exception)
            {
                // Stopped.
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var pending = new StringBuilder();
                while (!_stop.IsCancellationRequested)
                {
                    int end;
                    while ((end = pending.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal)) < 0)
                    {
                        var read = await stream.ReadAsync(buffer, _stop.Token);
                        if (read == 0)
                        {
                            return;
                        }

                        pending.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    var head = pending.ToString(0, end);
                    pending.Remove(0, end + 4);
                    Interlocked.Increment(ref _requests);
                    var body = Encoding.ASCII.GetBytes(head);
                    var response = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\n\r\n");
                    await stream.WriteAsync(response, _stop.Token);
                    await stream.WriteAsync(body, _stop.Token);
                }
            }
            catch (Exception)
            {
                // The client went away.
            }
        }
    }

    /// <summary>Totals the <c>twp.plugin.*</c> instruments by name and tags, for tests that assert on metrics.</summary>
    internal sealed class PluginMeter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, double> _totals = [];

        public PluginMeter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ProxyMetrics.MeterName &&
                    instrument.Name.StartsWith("twp.plugin.", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Add(i, v, t));
            _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Add(i, v, t));
            _listener.Start();
        }

        /// <summary>Key form: <c>twp.plugin.errors{plugin=a,kind=timeout}</c> (tags in the order the instrument records them).</summary>
        public double Total(string key)
        {
            lock (_totals)
            {
                return _totals.GetValueOrDefault(key);
            }
        }

        public double Sum(string instrument)
        {
            lock (_totals)
            {
                return _totals.Where(p => p.Key.StartsWith(instrument, StringComparison.Ordinal)).Sum(p => p.Value);
            }
        }

        public int Count(string instrumentPrefix)
        {
            lock (_totals)
            {
                return _totals.Keys.Count(k => k.StartsWith(instrumentPrefix, StringComparison.Ordinal));
            }
        }

        public void Dispose() => _listener.Dispose();

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var key = instrument.Name + "{" + string.Join(",", tags.ToArray().Select(t => $"{t.Key}={t.Value}")) + "}";
            lock (_totals)
            {
                _totals[key] = _totals.GetValueOrDefault(key) + (instrument is Histogram<double> ? 1 : value);
            }
        }
    }
}
