using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Titanium.Cli.Telemetry;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Cli.Tests.Telemetry;

/// <summary>What an origin saw for one request.</summary>
internal sealed class SeenRequest
{
    public string PathAndQuery { get; init; } = "";
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : "";
}

/// <summary>A loopback HTTP origin that records the headers it receives.</summary>
internal sealed class TestOrigin : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly ConcurrentQueue<SeenRequest> seen = new();
    private readonly Func<int, (int Status, string Body)> answer;
    private int hits;

    public TestOrigin(Func<int, (int Status, string Body)>? answer = null)
    {
        this.answer = answer ?? (_ => (200, "ok"));
        Port = Ports.Free();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    public IReadOnlyList<SeenRequest> Requests => seen.ToArray();

    public int Hits => Volatile.Read(ref hits);

    public void Dispose()
    {
        try
        {
            listener.Stop();
            listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already closed.
        }
    }

    private async Task AcceptAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => Serve(context));
        }
    }

    private void Serve(HttpListenerContext context)
    {
        try
        {
            var request = new SeenRequest { PathAndQuery = context.Request.RawUrl ?? "" };
            foreach (string name in context.Request.Headers)
            {
                request.Headers[name] = context.Request.Headers[name] ?? "";
            }

            seen.Enqueue(request);
            var (status, body) = answer(Interlocked.Increment(ref hits));
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes);
            context.Response.Close();
        }
        catch (Exception)
        {
            try { context.Response.Abort(); } catch (Exception) { /* shutting down */ }
        }
    }
}

internal static class Ports
{
    public static int Free()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

/// <summary>A reverse proxy listening on a loopback port that sends everything under <c>/</c> to a cluster.</summary>
internal sealed class TestProxy : IDisposable
{
    private TestProxy(ProxyServer proxy, ClusterManager manager, int port)
    {
        Proxy = proxy;
        Manager = manager;
        Port = port;
    }

    public ProxyServer Proxy { get; }

    public ClusterManager Manager { get; }

    public int Port { get; }

    public Uri Url(string pathAndQuery) => new($"http://127.0.0.1:{Port}{pathAndQuery}");

    public static async Task<TestProxy> StartAsync(
        string routeId,
        string clusterId,
        IReadOnlyList<int> destinationPorts,
        RetryConfig? retry = null,
        IReadOnlyList<RoutePolicyConfig>? policies = null,
        Action<ProxyServer>? configure = null)
    {
        var manager = new ClusterManager();
        var destinations = destinationPorts
            .Select((port, index) => new DestinationConfig { Id = $"d{index + 1}", Address = "127.0.0.1", Port = port })
            .ToList();
        var clusters = new List<ClusterConfig> { new() { Id = clusterId, Destinations = destinations } };
        await manager.ApplyAsync(clusters);

        var proxy = new ProxyServer(userTrustRootCertificate: false);
        configure?.Invoke(proxy);
        var loadBalancer = new LoadBalancer();
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = routeId,
                    ClusterId = clusterId,
                    Order = 1,
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                    Retry = retry,
                    Policies = policies?.Select(p => p.Name).ToList(),
                },
            ],
            Policies = policies,
            Clusters = clusters,
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = loadBalancer,
            LatencyRecorder = loadBalancer,
        };

        var endpoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false);
        proxy.AddEndPoint(endpoint);
        proxy.Start();
        return new TestProxy(proxy, manager, endpoint.Port);
    }

    public void Dispose()
    {
        try { Proxy.Stop(); } catch (Exception) { /* best effort */ }
        Proxy.Dispose();
    }
}

internal static class TelemetryTestOptions
{
    /// <summary>Options for an OTLP/HTTP collector, resolved through the same path the CLI uses.</summary>
    public static TelemetryOptions For(
        OtlpReceiver receiver,
        Action<TelemetryConfig>? tweak = null,
        Dictionary<string, string>? env = null)
    {
        var config = new TelemetryConfig
        {
            ServiceName = "twp-test",
            Otlp = new OtlpExporterConfig { Endpoint = receiver.Endpoint, Protocol = "http", TimeoutMs = 2000 },
            Traces = new TelemetryTracesConfig { Enabled = true, Sampler = "always" },
        };
        tweak?.Invoke(config);
        var variables = new Dictionary<string, string> { ["OTEL_BSP_SCHEDULE_DELAY"] = "50" };
        if (env is not null)
        {
            foreach (var pair in env)
            {
                variables[pair.Key] = pair.Value;
            }
        }

        return TelemetryOptions.Resolve(config, name => variables.GetValueOrDefault(name));
    }

    public static TelemetryHost Start(ProxyServer proxy, TelemetryOptions options) =>
        TelemetryHost.TryStart(proxy, options, message => throw new InvalidOperationException(message))
        ?? throw new InvalidOperationException("Telemetry did not start.");

    public static HttpClient Client() =>
        new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
}
