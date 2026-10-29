using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Security;
using Titanium.Plus.Security.Waf;
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

/// <summary>The WAF on the real session path: headers, bodies, and the per-route <c>waf</c> policy.</summary>
[TestClass]
public class WafV2IntegrationTests
{
    [TestMethod]
    [Timeout(90_000)]
    public async Task SessionPath_DetectByDefault_BlockOnTheStrictRoute_AndBodiesAreScreened()
    {
        var hits = 0;
        await using var origin = await StartOriginAsync(() => Interlocked.Increment(ref hits));
        using var harness = await StartAsync(
            origin.Port,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["waf.enabled"] = "true",
                ["waf.inspectBodyBytes"] = "256",
            },
            [new RoutePolicyConfig { Name = "waf-strict", Kind = "waf", Mode = "block" }]);
        var stats = harness.Guard.Screening!.Stats;
        using var http = harness.Client();

        // Detect mode (the default): the attack is counted and still reaches the origin.
        var detected = await http.GetAsync("/open/?q=%3Cscript%3Ealert(1)%3C/script%3E");
        Assert.AreEqual(HttpStatusCode.OK, detected.StatusCode);
        Assert.AreEqual(1, Volatile.Read(ref hits));
        Assert.AreEqual(1, stats.WouldBlock);
        Assert.AreEqual(0, stats.Blocked);

        // The strict route blocks the same request.
        var blocked = await http.GetAsync("/strict/?q=%3Cscript%3Ealert(1)%3C/script%3E");
        Assert.AreEqual(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.AreEqual("forbidden", await blocked.Content.ReadAsStringAsync());
        Assert.AreEqual(1, Volatile.Read(ref hits), "a blocked request never reaches the origin");
        Assert.AreEqual(1, stats.Blocked);

        // A clean request on the strict route passes.
        Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("/strict/?q=hello+world")).StatusCode);
        Assert.AreEqual(2, Volatile.Read(ref hits));

        // Headers: a scanner user agent.
        using (var scanner = new HttpRequestMessage(HttpMethod.Get, "/strict/"))
        {
            scanner.Headers.TryAddWithoutValidation("User-Agent", "sqlmap/1.7");
            Assert.AreEqual(HttpStatusCode.Forbidden, (await http.SendAsync(scanner)).StatusCode);
        }

        // Cookies.
        using (var cookie = new HttpRequestMessage(HttpMethod.Get, "/strict/"))
        {
            cookie.Headers.TryAddWithoutValidation("Cookie", "a=1; b=1' OR '1'='1");
            Assert.AreEqual(HttpStatusCode.Forbidden, (await http.SendAsync(cookie)).StatusCode);
        }

        var beforeBodies = Volatile.Read(ref hits);

        // Form body.
        using (var form = new HttpRequestMessage(HttpMethod.Post, "/strict/login")
        {
            Content = new StringContent("user=admin&pw=' OR '1'='1", Encoding.UTF8, "application/x-www-form-urlencoded"),
        })
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, (await http.SendAsync(form)).StatusCode);
        }

        // JSON body.
        using (var json = new HttpRequestMessage(HttpMethod.Post, "/strict/api")
        {
            Content = new StringContent("""{"q":"1 UNION SELECT user,pass FROM users"}""", Encoding.UTF8, "application/json"),
        })
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, (await http.SendAsync(json)).StatusCode);
        }

        // A clean JSON body passes and reaches the origin with its body intact.
        using (var clean = new HttpRequestMessage(HttpMethod.Post, "/strict/api")
        {
            Content = new StringContent("""{"name":"Alice"}""", Encoding.UTF8, "application/json"),
        })
        {
            var ok = await http.SendAsync(clean);
            Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
            StringAssert.Contains(await ok.Content.ReadAsStringAsync(), "len=16");
        }

        // Binary and over-limit bodies are not inspected.
        using (var binary = new HttpRequestMessage(HttpMethod.Post, "/strict/upload")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("<script>alert(1)</script>")),
        })
        {
            binary.Content.Headers.ContentType = new("application/octet-stream");
            Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(binary)).StatusCode);
        }

        var padding = new string('a', 300);
        using (var large = new HttpRequestMessage(HttpMethod.Post, "/strict/api")
        {
            Content = new StringContent($$"""{"pad":"{{padding}}","q":"<script>alert(1)</script>"}""", Encoding.UTF8, "application/json"),
        })
        {
            Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(large)).StatusCode, "body over waf.inspectBodyBytes");
        }

        Assert.AreEqual(beforeBodies + 3, Volatile.Read(ref hits), "clean JSON, binary, and large bodies reached the origin");
        Assert.AreEqual(1, stats.Bodies(WafBodyOutcome.NotEligible));
        Assert.AreEqual(1, stats.Bodies(WafBodyOutcome.TooLarge));
        Assert.IsTrue(stats.Bodies(WafBodyOutcome.Inspected) >= 3);
        Assert.IsTrue(stats.Detections("sqli") >= 3);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task SessionPath_ModeOff_WithAStrictRoute_OnlyScreensThatRoute()
    {
        var hits = 0;
        await using var origin = await StartOriginAsync(() => Interlocked.Increment(ref hits));
        using var harness = await StartAsync(
            origin.Port,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["waf.enabled"] = "true",
                ["waf.mode"] = "off",
            },
            [new RoutePolicyConfig { Name = "waf-strict", Kind = "waf", Mode = "block" }]);
        using var http = harness.Client();

        Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("/open/?q=%3Cscript%3E")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await http.GetAsync("/strict/?q=%3Cscript%3E")).StatusCode);
        Assert.AreEqual(0, harness.Guard.Screening!.Stats.Requests(WafMode.Detect));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task SessionPath_RouteExclusion_AllowsTheNamedParameterOnly()
    {
        var hits = 0;
        await using var origin = await StartOriginAsync(() => Interlocked.Increment(ref hits));
        using var harness = await StartAsync(
            origin.Port,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["waf.enabled"] = "true" },
            [new RoutePolicyConfig { Name = "waf-strict", Kind = "waf", Mode = "block", Exclusions = ["sqli@query:sql"] }]);
        using var http = harness.Client();

        Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("/strict/?sql=1%27+OR+%271%27%3D%271")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await http.GetAsync("/strict/?other=1%27+OR+%271%27%3D%271")).StatusCode);
    }

    private static async Task<Harness> StartAsync(
        int originPort, Dictionary<string, string> options, IReadOnlyList<RoutePolicyConfig> policies)
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
        var proxy = new ProxyServer(userTrustRootCertificate: false);
        var context = new PlusActivationContext
        {
            ProxyServer = proxy,
            ClusterManager = manager,
            Middleware = middleware,
            Options = options,
            Policies = policies,
        };
        var guard = WafGuard.TryStart(context, options) ?? throw new InvalidOperationException("WAF did not start");

        var loadBalancer = new LoadBalancer();
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "strict",
                    ClusterId = "c1",
                    Order = 1,
                    Policies = ["waf-strict"],
                    Match = new RouteMatch { Path = "/strict", PathKind = PathMatchKind.Prefix },
                },
                new RouteConfig
                {
                    Id = "open",
                    ClusterId = "c1",
                    Order = 2,
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
            Policies = policies,
            Clusters = clusters,
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = loadBalancer,
            Middleware = middleware,
            LatencyRecorder = loadBalancer,
        };

        var endpoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false);
        proxy.AddEndPoint(endpoint);
        proxy.Start();
        return new Harness(proxy, guard, endpoint.Port);
    }

    private static async Task<RunningOrigin> StartOriginAsync(Func<int> onRequest)
    {
        var host = new WebHostBuilder()
            .UseKestrel(o => o.Listen(IPAddress.Loopback, 0))
            .Configure(app => app.Run(async ctx =>
            {
                onRequest();
                using var buffer = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(buffer);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.WriteAsync($"ok len={buffer.Length}");
            }))
            .Build();
        await host.StartAsync();
        var feature = host.ServerFeatures.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        var address = feature?.Addresses.FirstOrDefault() ?? throw new InvalidOperationException("No server address");
        return new RunningOrigin(host, new Uri(address.Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)).Port);
    }

    private sealed class Harness : IDisposable
    {
        private readonly ProxyServer _proxy;
        private readonly int _port;

        public Harness(ProxyServer proxy, WafGuard guard, int port)
        {
            _proxy = proxy;
            Guard = guard;
            _port = port;
        }

        public WafGuard Guard { get; }

        public HttpClient Client() => new(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_port}/"),
            Timeout = TimeSpan.FromSeconds(15),
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

    private sealed class RunningOrigin : IAsyncDisposable
    {
        private readonly IWebHost _host;

        public RunningOrigin(IWebHost host, int port)
        {
            _host = host;
            Port = port;
        }

        public int Port { get; }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
