using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.IntegrationTests.Scripting;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     End-to-end checks of the script phase seams with a fake host: every phase is called when registered, deny and
///     header edits take effect, a script can pick the cluster, and L4 filters are chosen at accept.
///     No test sets <c>EnableHttpInterception</c>: a script host alone must move the listener onto the session path.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ScriptPhaseSeamTests
{
    private static readonly string[] PluginOrder = ["global", "route"];
    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task RequestAndResponseHeaders_AreCalledAndEditsTakeEffect_ThenLogRuns()
    {
        var host = new FakeScriptHost
        {
            RequestHeaders = _ => new ScriptPhaseResult
            {
                SetHeaders = new Dictionary<string, string> { ["X-Script"] = "req" },
                RemoveHeaders = ["X-Remove"],
            },
            ResponseHeaders = _ => new ScriptPhaseResult
            {
                SetHeaders = new Dictionary<string, string> { ["X-Resp-Script"] = "resp" },
            },
        };
        await using var rig = await Rig.StartAsync(host);

        using var request = new HttpRequestMessage(HttpMethod.Get, rig.Url("/echo"));
        request.Headers.TryAddWithoutValidation("X-Remove", "gone");
        using var response = await rig.Client.SendAsync(request);
        var echoed = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(echoed, "x-script=req;");
        StringAssert.Contains(echoed, "x-remove=;");
        Assert.AreEqual("resp", string.Join(",", response.Headers.GetValues("X-Resp-Script")));
        Assert.AreEqual(1, host.Count(ScriptPhases.RequestHeaders));
        Assert.AreEqual(1, host.Count(ScriptPhases.ResponseHeaders));

        var seen = host.Calls.First(c => c.Phase == ScriptPhases.RequestHeaders);
        Assert.AreEqual("GET", seen.Method);
        Assert.AreEqual("/echo", seen.Path);
        Assert.IsTrue(seen.Headers.Any(h => h.Key.Equals("X-Remove", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(200, host.Calls.First(c => c.Phase == ScriptPhases.ResponseHeaders).StatusCode);

        await WaitForAsync(() => host.Count(ScriptPhases.Log) == 1);
        StringAssert.Contains(host.Logs.Single(), "GET /echo 200");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task RequestDeny_AnswersLocally_AndNeverReachesTheOrigin()
    {
        var host = new FakeScriptHost { RequestHeaders = _ => ScriptPhaseResult.Deny(418, "nope") };
        await using var rig = await Rig.StartAsync(host);

        using var response = await rig.Client.GetAsync(rig.Url("/echo"));

        Assert.AreEqual(418, (int)response.StatusCode);
        Assert.AreEqual("nope", await response.Content.ReadAsStringAsync());
        Assert.AreEqual(0, rig.OriginA.Hits, "a denied request is never forwarded");
        Assert.AreEqual(0, host.Count(ScriptPhases.ResponseHeaders), "no origin answer means no response phase for it");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task ResponseDeny_ReplacesTheOriginAnswer()
    {
        var host = new FakeScriptHost { ResponseHeaders = _ => ScriptPhaseResult.Deny(502, "blocked") };
        await using var rig = await Rig.StartAsync(host);

        using var response = await rig.Client.GetAsync(rig.Url("/echo"));

        Assert.AreEqual(502, (int)response.StatusCode);
        Assert.AreEqual("blocked", await response.Content.ReadAsStringAsync());
        Assert.AreEqual(1, rig.OriginA.Hits);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task PausedPhase_IsResumedUntilTheScriptAnswers()
    {
        var host = new FakeScriptHost
        {
            RequestHeaders = _ => ScriptPhaseResult.Pause,
            Resume = ctx => ctx.ResumeCount < 2
                ? ScriptPhaseResult.Pause
                : new ScriptPhaseResult { SetHeaders = new Dictionary<string, string> { ["X-Script"] = "resumed" } },
        };
        await using var rig = await Rig.StartAsync(host);

        using var response = await rig.Client.GetAsync(rig.Url("/echo"));
        var echoed = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(2, host.Resumes.Count, "resumed twice, then the script answered");
        Assert.IsTrue(host.Resumes.All(p => p == ScriptPhases.RequestHeaders));
        StringAssert.Contains(echoed, "x-script=resumed;");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task ScriptThatNeverResumes_IsDeniedWith504()
    {
        var host = new FakeScriptHost { RequestHeaders = _ => ScriptPhaseResult.Pause, Resume = _ => ScriptPhaseResult.Pause };
        await using var rig = await Rig.StartAsync(host);

        using var response = await rig.Client.GetAsync(rig.Url("/echo"));

        Assert.AreEqual(504, (int)response.StatusCode);
        Assert.AreEqual(0, rig.OriginA.Hits);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task ThrowingScript_RefusesTheRequest()
    {
        var host = new FakeScriptHost { RequestHeaders = _ => throw new InvalidOperationException("boom") };
        await using var rig = await Rig.StartAsync(host);

        using var response = await rig.Client.GetAsync(rig.Url("/echo"));

        Assert.AreEqual(500, (int)response.StatusCode);
        Assert.AreEqual(0, rig.OriginA.Hits, "a failing script never opens a route");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task ClusterChoice_ReResolvesTheDestination_ForARoutedRequest()
    {
        var host = new FakeScriptHost
        {
            RequestHeaders = ctx => ctx.Path.Contains("pick=b")
                ? new ScriptPhaseResult { UpstreamClusterId = "b" }
                : ScriptPhaseResult.Continue,
        };
        await using var rig = await Rig.StartAsync(host, routed: true);

        using var plain = await rig.Client.GetAsync(rig.Url("/echo"));
        using var picked = await rig.Client.GetAsync(rig.Url("/echo?pick=b"));

        Assert.AreEqual("A", string.Join(",", plain.Headers.GetValues("X-Origin")), "no choice keeps the route's cluster");
        Assert.AreEqual("B", string.Join(",", picked.Headers.GetValues("X-Origin")), "the script's cluster wins");
        Assert.AreEqual(1, rig.OriginB.Hits);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task ClusterChoice_WorksForAnUnroutedRequest_AndUnknownClusterIs503()
    {
        var chosen = "b";
        var host = new FakeScriptHost { RequestHeaders = _ => new ScriptPhaseResult { UpstreamClusterId = chosen } };
        await using var rig = await Rig.StartAsync(host, routed: false);

        using var picked = await rig.Client.GetAsync(rig.Url("/echo"));
        Assert.AreEqual("B", string.Join(",", picked.Headers.GetValues("X-Origin")));

        chosen = "does-not-exist";
        using var missing = await rig.Client.GetAsync(rig.Url("/echo"));
        Assert.AreEqual(503, (int)missing.StatusCode);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task StreamingBodies_AreCalledPerPieceAndCanBeReplaced()
    {
        var host = new FakeScriptHost
        {
            Phases = ScriptPhases.RequestBody | ScriptPhases.ResponseBody,
            RequestBody = ctx => ctx.Chunk.Length > 0
                ? new ScriptPhaseResult { ReplaceBody = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(ctx.Chunk.Span).ToUpperInvariant()) }
                : ScriptPhaseResult.Continue,
            ResponseBody = ctx => ctx.Chunk.Length > 0
                ? new ScriptPhaseResult { ReplaceBody = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(ctx.Chunk.Span).ToUpperInvariant()) }
                : ScriptPhaseResult.Continue,
        };
        await using var rig = await Rig.StartAsync(host);

        using var response = await rig.Client.PostAsync(rig.Url("/echo"), new StringContent("hello", Encoding.ASCII));
        var echoed = await response.Content.ReadAsStringAsync();

        StringAssert.Contains(echoed, "BODY=HELLO", "the request body was upper-cased on the way up");
        StringAssert.Contains(echoed, "ECHO=POST", "the response body was upper-cased on the way down");
        Assert.IsTrue(host.Count(ScriptPhases.RequestBody) >= 1);
        Assert.IsTrue(host.Count(ScriptPhases.ResponseBody) >= 1);
        Assert.AreEqual(0, host.Count(ScriptPhases.RequestHeaders), "a host that does not list a phase is never called for it");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task BufferedBodies_ArriveOnceAndCanChangeLength()
    {
        var host = new FakeScriptHost
        {
            BodyMode = ScriptBodyMode.Buffered,
            Phases = ScriptPhases.RequestBody | ScriptPhases.ResponseBody,
            RequestBody = ctx => new ScriptPhaseResult
            {
                ReplaceBody = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(ctx.Chunk.Span) + "-edited-by-script"),
            },
            ResponseBody = ctx => new ScriptPhaseResult
            {
                ReplaceBody = Encoding.ASCII.GetBytes("[" + Encoding.ASCII.GetString(ctx.Chunk.Span) + "]"),
            },
        };
        await using var rig = await Rig.StartAsync(host);

        using var response = await rig.Client.PostAsync(rig.Url("/echo"), new StringContent("hello", Encoding.ASCII));
        var echoed = await response.Content.ReadAsStringAsync();

        Assert.IsTrue(echoed.StartsWith('[') && echoed.EndsWith(']'), echoed);
        StringAssert.Contains(echoed, "body=hello-edited-by-script");
        Assert.AreEqual(1, host.Count(ScriptPhases.RequestBody));
        Assert.AreEqual(1, host.Count(ScriptPhases.ResponseBody));
        Assert.IsTrue(host.Calls.Where(c => c.Phase is ScriptPhases.RequestBody or ScriptPhases.ResponseBody).All(c => c.IsLastChunk));
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task GlobalMiddleware_RunsBeforeRoutePlugins_AndCanDeny()
    {
        var order = new ConcurrentQueue<string>();
        var global = new DelegateMiddleware("global", order, args => args.HttpClient.Request.Headers.SetOrAddHeaderValue("X-Global", "1"));
        var route = new DelegateMiddleware("route", order, _ => { });
        await using var rig = await Rig.StartAsync(null, routed: true, global: [global], routePlugins: new Dictionary<string, IProxyMiddleware> { ["rp"] = route });

        using var response = await rig.Client.GetAsync(rig.Url("/echo"));
        var echoed = await response.Content.ReadAsStringAsync();

        CollectionAssert.AreEqual(PluginOrder, order.ToArray(), "the listener-wide chain runs first");
        StringAssert.Contains(echoed, "x-global=1;");

        var deny = new DelegateMiddleware("deny", order, args => args.GenericResponse("blocked", HttpStatusCode.UnavailableForLegalReasons));
        await using var denying = await Rig.StartAsync(null, routed: true, global: [deny], routePlugins: new Dictionary<string, IProxyMiddleware> { ["rp"] = route });
        using var refused = await denying.Client.GetAsync(denying.Url("/echo"));
        Assert.AreEqual(451, (int)refused.StatusCode);
        Assert.AreEqual(0, denying.OriginA.Hits);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task StreamingRequestTrailers_AreCalledAndEditsReachTheOrigin()
    {
        var host = new FakeScriptHost
        {
            Phases = ScriptPhases.RequestTrailers,
            RequestTrailers = _ => new ScriptPhaseResult { SetHeaders = new Dictionary<string, string> { ["X-Added"] = "yes" } },
        };
        await using var rig = await Rig.StartAsync(host, rawOrigin: true);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, rig.Port);
        var stream = client.GetStream();
        var wire = "POST /t HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\nTrailer: X-T\r\nConnection: close\r\n\r\n" +
                   "5\r\nhello\r\n0\r\nX-T: 1\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(wire));

        var seen = await rig.RawOrigin!.FirstRequestAsync();
        StringAssert.Contains(seen.ToLowerInvariant(), "x-t: 1");
        StringAssert.Contains(seen.ToLowerInvariant(), "x-added: yes");
        Assert.AreEqual(1, host.Count(ScriptPhases.RequestTrailers));
        var call = host.Calls.Single(c => c.Phase == ScriptPhases.RequestTrailers);
        Assert.IsTrue(call.Headers.Any(h => h.Key.Equals("X-T", StringComparison.OrdinalIgnoreCase) && h.Value == "1"));
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task L4Filters_AreChosenAtAccept_AndPlainListenersNeverTouchThem()
    {
        await using var origin = new EchoTcpOrigin("A");
        var filter = new FakeStreamFilter();

        var plainEnhanced = TcpPassthrough.EnhancedRelayCount;
        var plainFiltered = TcpPassthrough.FilteredRelayCount;
        await using (var plain = await PassthroughRig.StartAsync(origin.Port, null))
        {
            using var socket = await plain.ConnectAsync();
            Assert.AreEqual("A:ping", await RoundTripAsync(socket, "ping"));
        }

        Assert.AreEqual(plainEnhanced, TcpPassthrough.EnhancedRelayCount, "no filters: the plain relay ran");
        Assert.AreEqual(plainFiltered, TcpPassthrough.FilteredRelayCount);
        Assert.AreEqual(0, filter.Events.Count);

        var before = TcpPassthrough.FilteredRelayCount;
        await using (var filtered = await PassthroughRig.StartAsync(origin.Port, [filter]))
        {
            using var socket = await filtered.ConnectAsync();
            Assert.AreEqual("A:pong", await RoundTripAsync(socket, "pong"));
        }

        Assert.AreEqual(before + 1, TcpPassthrough.FilteredRelayCount, "a listener with filters picks the filtered relay at accept");
        await WaitForAsync(() => filter.Events.Contains("close"));
        var events = filter.Events.ToArray();
        Assert.AreEqual("new", events[0], "the new-connection hook runs first");
        CollectionAssert.Contains(events, "down:pong");
        CollectionAssert.Contains(events, "up:A:pong");
        CollectionAssert.Contains(events, "close");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task L4Filters_CanDenyAtAccept_AndChooseTheCluster()
    {
        await using var originA = new EchoTcpOrigin("A");
        await using var originB = new EchoTcpOrigin("B");

        var denier = new FakeStreamFilter { NewConnection = _ => StreamFilterResult.Deny };
        await using (var denied = await PassthroughRig.StartAsync(originA.Port, [denier]))
        {
            using var socket = await denied.ConnectAsync();
            var buffer = new byte[8];
            int read;
            try
            {
                await socket.SendAsync(Encoding.ASCII.GetBytes("x"));
                read = await socket.ReceiveAsync(buffer);
            }
            catch (SocketException)
            {
                read = 0;
            }

            Assert.AreEqual(0, read, "a denied connection is closed");
            Assert.AreEqual(0, originA.Accepted, "and never reaches the upstream");
        }

        var chooser = new FakeStreamFilter { NewConnection = _ => new StreamFilterResult { ClusterId = "b" } };
        await using var rig = await PassthroughRig.StartAsync(
            originA.Port, [chooser], defaultCluster: "a", clusters: [Cluster("a", originA.Port), Cluster("b", originB.Port)]);
        using var chosen = await rig.ConnectAsync();
        Assert.AreEqual("B:hi", await RoundTripAsync(chosen, "hi"), "the filter's cluster replaced the listener's default");
    }

    private static ClusterConfig Cluster(string id, int port) => new()
    {
        Id = id,
        Destinations = [new DestinationConfig { Id = id + "-d", Address = "127.0.0.1", Port = port }],
    };

    private static async Task<string> RoundTripAsync(Socket socket, string text)
    {
        await socket.SendAsync(Encoding.ASCII.GetBytes(text));
        var buffer = new byte[64];
        var total = 0;
        var expected = 2 + text.Length;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (total < expected)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(total), cts.Token);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return Encoding.ASCII.GetString(buffer, 0, total);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.IsTrue(condition(), "condition was not met in time");
    }

    private sealed class DelegateMiddleware(string name, ConcurrentQueue<string> order, Action<SessionEventArgs> act) : IProxyMiddleware
    {
        public ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken)
        {
            order.Enqueue(name);
            act((SessionEventArgs)context.Session);
            if (context.Session is SessionEventArgs { HttpClient.Request.CancelRequest: true })
            {
                context.IsHandled = true;
                return default;
            }

            return next(context, cancellationToken);
        }
    }

    /// <summary>One transparent HTTP listener in front of two echo origins, with an optional script host.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ProxyServer proxy;
        private readonly TransparentProxyEndPoint endPoint;
        private readonly ClusterManager manager;

        private Rig(ProxyServer proxy, TransparentProxyEndPoint endPoint, ClusterManager manager)
        {
            this.proxy = proxy;
            this.endPoint = endPoint;
            this.manager = manager;
        }

        public HttpClient Client { get; } = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
        public EchoHttpOrigin OriginA { get; private set; } = null!;
        public EchoHttpOrigin OriginB { get; private set; } = null!;
        public RawOrigin? RawOrigin { get; private set; }
        public int Port => endPoint.Port;

        public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

        public static async Task<Rig> StartAsync(
            IScriptPhaseHost? host,
            bool routed = false,
            IReadOnlyList<IProxyMiddleware>? global = null,
            IReadOnlyDictionary<string, IProxyMiddleware>? routePlugins = null,
            bool rawOrigin = false)
        {
            var originA = new EchoHttpOrigin("A");
            var originB = new EchoHttpOrigin("B");
            var raw = rawOrigin ? new RawOrigin() : null;
            var forwardPort = raw?.Port ?? originA.Port;

            var proxy = new ProxyServer(false, false, false) { EnableHttp2 = false };
            var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false)
            {
                GenericCertificateName = "localhost",
                ForwardHost = "127.0.0.1",
                ForwardPort = forwardPort,
                ForwardCleartext = true,
            };
            proxy.AddEndPoint(endPoint);

            var manager = new ClusterManager();
            var balancer = new LoadBalancer();
            manager.AttachHealthTracker(balancer.Health);
            await manager.ApplyAsync(
            [
                Cluster("a", originA.Port),
                Cluster("b", originB.Port),
            ]);

            RouteConfig[]? routes = null;
            if (routed)
            {
                routes =
                [
                    new RouteConfig
                    {
                        Id = "r",
                        ClusterId = "a",
                        Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                        Middleware = routePlugins is null ? null : ["rp"],
                    },
                ];
            }

            proxy.ReverseProxy = new ReverseProxyOptions
            {
                Routes = routes,
                ClusterManager = manager,
                RouteMatcher = new RouteMatcher(),
                LoadBalancer = balancer,
                ScriptPipeline = host,
                GlobalMiddleware = global,
                RoutePlugins = routePlugins,
            };
            proxy.Start();
            return new Rig(proxy, endPoint, manager) { OriginA = originA, OriginB = originB, RawOrigin = raw };
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            proxy.Stop();
            proxy.Dispose();
            await manager.DisposeAsync();
            await OriginA.DisposeAsync();
            await OriginB.DisposeAsync();
            RawOrigin?.Dispose();
        }
    }

    /// <summary>A passthrough listener, with optional L4 filters and clusters.</summary>
    private sealed class PassthroughRig : IAsyncDisposable
    {
        private readonly ProxyServer proxy;
        private readonly TransparentProxyEndPoint endPoint;
        private readonly ClusterManager manager;

        private PassthroughRig(ProxyServer proxy, TransparentProxyEndPoint endPoint, ClusterManager manager)
        {
            this.proxy = proxy;
            this.endPoint = endPoint;
            this.manager = manager;
        }

        public static async Task<PassthroughRig> StartAsync(
            int forwardPort,
            IReadOnlyList<IStreamScriptFilter>? filters,
            string? defaultCluster = null,
            ClusterConfig[]? clusters = null)
        {
            var proxy = new ProxyServer(false, false, false) { EnableHttp2 = false };
            var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false)
            {
                Passthrough = true,
                ForwardHost = "127.0.0.1",
                ForwardPort = forwardPort,
                StreamFilters = filters,
                PassthroughDefaultClusterId = defaultCluster,
            };
            proxy.AddEndPoint(endPoint);
            var manager = new ClusterManager();
            var balancer = new LoadBalancer();
            manager.AttachHealthTracker(balancer.Health);
            await manager.ApplyAsync(clusters ?? []);
            proxy.ReverseProxy = new ReverseProxyOptions
            {
                Clusters = clusters,
                ClusterManager = manager,
                RouteMatcher = new RouteMatcher(),
                LoadBalancer = balancer,
            };
            proxy.Start();
            return new PassthroughRig(proxy, endPoint, manager);
        }

        public async Task<Socket> ConnectAsync()
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, endPoint.Port);
            return socket;
        }

        public async ValueTask DisposeAsync()
        {
            proxy.Stop();
            proxy.Dispose();
            await manager.DisposeAsync();
        }
    }

    /// <summary>Echoes request headers and body, tags the answer with its name, and counts hits.</summary>
    private sealed class EchoHttpOrigin : IAsyncDisposable
    {
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        private readonly string name;
        private int hits;

        public EchoHttpOrigin(string name)
        {
            this.name = name;
            Exception? last = null;
            for (var i = 0; i < 8; i++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var candidate = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                listener.Prefixes.Clear();
                listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
                try
                {
                    listener.Start();
                    Port = candidate;
                    loop = Task.Run(ServeAsync);
                    return;
                }
                catch (Exception ex) when (ex is HttpListenerException or SocketException)
                {
                    last = ex;
                }
            }

            throw new InvalidOperationException("Failed to bind HttpListener.", last);
        }

        public int Port { get; }
        public int Hits => Volatile.Read(ref hits);

        private async Task ServeAsync()
        {
            while (!stop.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().WaitAsync(stop.Token).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                try
                {
                    Interlocked.Increment(ref hits);
                    string body;
                    using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.ASCII))
                    {
                        body = await reader.ReadToEndAsync().ConfigureAwait(false);
                    }

                    var seen = new StringBuilder();
                    foreach (var header in new[] { "X-Script", "X-Remove", "X-Global" })
                    {
                        seen.Append(header.ToLowerInvariant()).Append('=').Append(ctx.Request.Headers[header]).Append(';');
                    }

                    var bytes = Encoding.ASCII.GetBytes($"{seen}body={body};echo={ctx.Request.HttpMethod}");
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/plain";
                    ctx.Response.Headers["X-Origin"] = name;
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    ctx.Response.Close();
                }
                catch
                {
                    try { ctx.Response.Abort(); } catch { /* ignore */ }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            try { listener.Close(); } catch { /* ignore */ }
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        }
    }

    /// <summary>A bare TCP origin that records the first request's bytes (headers, body, and trailers) and answers 200.</summary>
    private sealed class RawOrigin : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource<string> first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RawOrigin()
        {
            listener.Start();
            _ = Task.Run(RunAsync);
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public Task<string> FirstRequestAsync() => first.Task.WaitAsync(TimeSpan.FromSeconds(15));

        private async Task RunAsync()
        {
            try
            {
                using var socket = await listener.AcceptSocketAsync();
                var text = new StringBuilder();
                var buffer = new byte[1024];
                while (!text.ToString().Contains("\r\n0\r\n", StringComparison.Ordinal) ||
                       !text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    var n = await socket.ReceiveAsync(buffer);
                    if (n == 0)
                    {
                        break;
                    }

                    text.Append(Encoding.ASCII.GetString(buffer, 0, n));
                }

                first.TrySetResult(text.ToString());
                await socket.SendAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
            }
            catch (Exception ex)
            {
                first.TrySetException(ex);
            }
        }

        public void Dispose() => listener.Stop();
    }

    /// <summary>Echoes each read back with its name as a prefix, and counts accepted connections.</summary>
    private sealed class EchoTcpOrigin : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly string name;
        private int accepted;

        public EchoTcpOrigin(string name)
        {
            this.name = name;
            listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public int Accepted => Volatile.Read(ref accepted);

        private async Task AcceptLoopAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await listener.AcceptSocketAsync(stop.Token);
                }
                catch
                {
                    return;
                }

                Interlocked.Increment(ref accepted);
                _ = Task.Run(() => EchoAsync(socket));
            }
        }

        private async Task EchoAsync(Socket socket)
        {
            using (socket)
            {
                var buffer = new byte[256];
                try
                {
                    while (true)
                    {
                        var n = await socket.ReceiveAsync(buffer, stop.Token);
                        if (n == 0)
                        {
                            return;
                        }

                        var reply = Encoding.ASCII.GetBytes(name + ":" + Encoding.ASCII.GetString(buffer, 0, n));
                        await socket.SendAsync(reply, stop.Token);
                    }
                }
                catch
                {
                    // The peer or the test ended the connection.
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            return default;
        }
    }
}
