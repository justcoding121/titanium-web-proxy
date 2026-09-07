using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Resilience;
using Titanium.Plus.Security;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Plus.Tests;

[TestClass]
public class PlusResilienceAndSecurityIntegrationTests
{
    [TestMethod]
    [Timeout(60_000)]
    public async Task Auth_401WithoutKey_200WithKey()
    {
        await using var origin = await StartOriginAsync(_ => Task.FromResult(200));
        using var harness = await StartReverseProxyAsync(origin.Port, options =>
        {
            options["security.apiKeys"] = "integration-key";
        });

        using var http = CreateDirectClient(harness.Port);

        var denied = await http.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var okReq = new HttpRequestMessage(HttpMethod.Get, "/");
        okReq.Headers.TryAddWithoutValidation("X-Api-Key", "integration-key");
        var ok = await http.SendAsync(okReq);
        Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
        StringAssert.Contains(await ok.Content.ReadAsStringAsync(), "ok");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Cors_OptionsPreflight_AndProxiedAcao()
    {
        await using var origin = await StartOriginAsync(_ => Task.FromResult(200));
        using var harness = await StartReverseProxyAsync(origin.Port, options =>
        {
            options["cors.enabled"] = "true";
            options["cors.allowOrigin"] = "https://app.example";
        });

        using var http = CreateDirectClient(harness.Port);

        using var optionsReq = new HttpRequestMessage(HttpMethod.Options, "/cors");
        var preflight = await http.SendAsync(optionsReq);
        Assert.AreEqual(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.IsTrue(
            preflight.Headers.TryGetValues("Access-Control-Allow-Origin", out var ao) &&
            ao.Contains("https://app.example"));

        var get = await http.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
        Assert.IsTrue(
            get.Headers.TryGetValues("Access-Control-Allow-Origin", out var getAo) &&
            getAo.Contains("https://app.example"));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Circuit_EjectsDestinationAfter5xx()
    {
        var hits = 0;
        await using var origin = await StartOriginAsync(_ =>
        {
            Interlocked.Increment(ref hits);
            return Task.FromResult(500);
        });

        using var harness = await StartReverseProxyAsync(origin.Port, options =>
        {
            options["resilience.circuit.enabled"] = "true";
            options["resilience.circuit.failureThreshold"] = "2";
            options["resilience.circuit.cooldownMs"] = "60000";
        });

        using var http = CreateDirectClient(harness.Port);
        _ = await http.GetAsync("/");
        _ = await http.GetAsync("/");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline &&
               harness.Manager.Snapshot.DestinationStates.GetValueOrDefault("d1") != DestinationState.Unhealthy)
        {
            await Task.Delay(50);
        }

        Assert.IsTrue(hits >= 2, $"expected >=2 origin hits, got {hits}");
        Assert.AreEqual(DestinationState.Unhealthy, harness.Manager.Snapshot.DestinationStates["d1"]);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Retry_SetsNetworkFailureRetryAttemptsOnGet()
    {
        await using var origin = await StartOriginAsync(_ => Task.FromResult(200));
        using var harness = await StartReverseProxyAsync(origin.Port, options =>
        {
            options["resilience.retry.idempotentAttempts"] = "3";
        });

        int? getAttempts = null;
        int? postAttempts = null;
        harness.Proxy.BeforeRequest += (_, e) =>
        {
            if (string.Equals(e.HttpClient.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                getAttempts = e.NetworkFailureRetryAttempts;
            }
            else if (string.Equals(e.HttpClient.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                postAttempts = e.NetworkFailureRetryAttempts;
            }

            return Task.CompletedTask;
        };

        using var http = CreateDirectClient(harness.Port);
        Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("/")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await http.PostAsync("/", new StringContent("x"))).StatusCode);

        Assert.AreEqual(3, getAttempts);
        Assert.AreEqual(0, postAttempts);
    }

    private static HttpClient CreateDirectClient(int port)
    {
        var handler = new HttpClientHandler { UseProxy = false };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    private static async Task<ProxyHarness> StartReverseProxyAsync(
        int originPort,
        Action<Dictionary<string, string>> configure)
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

        var middleware = new List<IProxyMiddleware>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        configure(options);

        var proxy = new ProxyServer(userTrustRootCertificate: false);
        var ctx = new PlusActivationContext
        {
            ProxyServer = proxy,
            ClusterManager = manager,
            Middleware = middleware,
            Options = options,
        };

        _ = AccessSecurity.TryStart(ctx, options);
        var circuit = CircuitBreakerController.TryStart(ctx, options);
        _ = IdempotentRetryGuard.TryStart(ctx, options);

        var loadBalancer = new LoadBalancer();
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r1",
                    ClusterId = "c1",
                    Order = 1,
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
            Clusters = clusters,
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = loadBalancer,
            Middleware = middleware.Count > 0 ? middleware : null,
            LatencyRecorder = loadBalancer,
        };

        var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false);
        proxy.AddEndPoint(ep);
        proxy.Start();

        return new ProxyHarness(proxy, manager, ep.Port, circuit);
    }

    private static async Task<RunningOrigin> StartOriginAsync(Func<HttpContext, Task<int>> statusFactory)
    {
        var host = new WebHostBuilder()
            .UseKestrel(o => o.Listen(IPAddress.Loopback, 0))
            .Configure(app => app.Run(async ctx =>
            {
                var status = await statusFactory(ctx);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.WriteAsync(status == 200 ? "ok" : "fail");
            }))
            .Build();
        await host.StartAsync();
        return new RunningOrigin(host, ReadPort(host));
    }

    private static int ReadPort(IWebHost host)
    {
        var feature = host.ServerFeatures.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        var addr = feature?.Addresses.FirstOrDefault()
                   ?? throw new InvalidOperationException("No server address");
        return new Uri(addr.Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)).Port;
    }

    private sealed class ProxyHarness : IDisposable
    {
        private readonly CircuitBreakerController? _circuit;

        public ProxyHarness(ProxyServer proxy, ClusterManager manager, int port, CircuitBreakerController? circuit)
        {
            Proxy = proxy;
            Manager = manager;
            Port = port;
            _circuit = circuit;
        }

        public ProxyServer Proxy { get; }
        public ClusterManager Manager { get; }
        public int Port { get; }

        public void Dispose()
        {
            _circuit?.Dispose();
            try { Proxy.Stop(); } catch { /* ignore */ }
            Proxy.Dispose();
        }
    }

    private sealed class RunningOrigin : IAsyncDisposable
    {
        private readonly IWebHost _host;
        public int Port { get; }

        public RunningOrigin(IWebHost host, int port)
        {
            _host = host;
            Port = port;
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
