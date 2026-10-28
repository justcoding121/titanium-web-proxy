#pragma warning disable CA1416
#pragma warning disable TWP001
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     MaxConnectionsPerServer is a cap on open upstream sockets, including ones sitting idle in the pool.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ConnectionCapTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task Cap2_SlowOrigin_TenRequests_OpenAtMostTwoSockets_AndAllSucceed()
    {
        await using var origin = new HoldingOrigin(TimeSpan.FromMilliseconds(400));
        using var proxy = StartProxy(origin.Port, maxConnections: 2, connectTimeout: TimeSpan.FromSeconds(8));
        var peak = await RunConcurrent(proxy, origin, 10);
        Assert.IsTrue(peak <= 2, $"peak sockets {peak}");
        Assert.AreEqual(1, proxy.TcpConnectionFactory.BudgetCountForTests);
        await proxy.StopAsync();
        await WaitUntilAsync(() => proxy.TcpConnectionFactory.BudgetOpenForTests() == 0);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Waiter_ProceedsWhenTheHeldConnectionIsReleased()
    {
        await using var origin = new HoldingOrigin(gate: true);
        using var proxy = StartProxy(origin.Port, maxConnections: 1, connectTimeout: TimeSpan.FromSeconds(8));
        var first = GetAsync(proxy);
        await origin.WaitForAcceptedAsync(1);
        var second = GetAsync(proxy);
        await WaitUntilAsync(() => proxy.TcpConnectionFactory.BudgetWaitersForTests() > 0,
            () => $"waiters {proxy.TcpConnectionFactory.BudgetWaitersForTests()}");
        Assert.AreEqual(1, origin.Accepted);
        Assert.AreEqual(1, proxy.TcpConnectionFactory.BudgetOpenForTests());
        origin.Release();
        var responses = await Task.WhenAll(first, second);
        Assert.AreEqual(HttpStatusCode.OK, responses[0].StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, responses[1].StatusCode);
        Assert.IsTrue(origin.Peak <= 1, $"peak sockets {origin.Peak}");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Timeout_Returns503_AndLeavesTheDestinationHealthy()
    {
        await using var origin = new HoldingOrigin(gate: true);
        var manager = new ClusterManager();
        using var proxy = StartProxy(origin.Port, maxConnections: 1, connectTimeout: TimeSpan.FromMilliseconds(600),
            manager: manager, passive: true);
        var held = GetAsync(proxy);
        await origin.WaitForAcceptedAsync(1);
        using var timed = await GetAsync(proxy);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, timed.StatusCode);
        Assert.IsTrue(timed.Headers.Contains("Retry-After"));
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("d1"));
        origin.Release();
        using var finished = await held;
        Assert.AreEqual(HttpStatusCode.OK, finished.StatusCode);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ThreeCapacityTimeouts_DoNotEjectDestination_Http1()
    {
        await using var origin = new HoldingOrigin(gate: true);
        var manager = new ClusterManager();
        using var proxy = StartProxy(origin.Port, maxConnections: 1, connectTimeout: TimeSpan.FromMilliseconds(600),
            manager: manager, passive: true);
        var held = GetAsync(proxy);
        await origin.WaitForAcceptedAsync(1);
        for (var i = 0; i < 3; i++)
        {
            using var timed = await GetAsync(proxy);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, timed.StatusCode, $"request {i}");
            Assert.IsTrue(timed.Headers.Contains("Retry-After"), $"request {i} missing Retry-After");
        }

        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("d1"));
        origin.Release();
        using var finished = await held;
        Assert.AreEqual(HttpStatusCode.OK, finished.StatusCode);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ThreeCapacityTimeouts_DoNotEjectDestination_Http2Client()
    {
        await using var origin = new HoldingOrigin(gate: true);
        var manager = new ClusterManager();
        using var proxy = StartTranslatedProxy(origin.Port, manager, http3: false);
        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        using var raw = await Http2RawClient.ConnectCleartextDirectAsync(endpoint.Port);
        await WriteH2GetAsync(raw, 1);
        await origin.WaitForAcceptedAsync(1);
        for (var i = 0; i < 3; i++)
        {
            var streamId = 3 + (i * 2);
            await WriteH2GetAsync(raw, streamId);
            var (id, headers, _) = await raw.Connection.ReadHeaderBlockAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(streamId, id);
            Assert.AreEqual("503", headers.Single(h => h.Name == ":status").Value);
            Assert.IsTrue(headers.Any(h => h.Name.Equals("retry-after", StringComparison.OrdinalIgnoreCase)));
        }

        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("d1"));
        origin.Release();
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ThreeCapacityTimeouts_DoNotEjectDestination_Http3Client()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
        {
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
        }

        await using var origin = new HoldingOrigin(gate: true);
        var manager = new ClusterManager();
        using var proxy = StartTranslatedProxy(origin.Port, manager, http3: true);
        var endpoint = proxy.ProxyEndPoints.OfType<TransparentQuicProxyEndPoint>().First();
        await using var client = await QuicHttp3Client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, endpoint.Port), "localhost");
        var held = client.SendAsync("GET", $"127.0.0.1:{origin.Port}", "/");
        await origin.WaitForAcceptedAsync(1);
        for (var i = 0; i < 3; i++)
        {
            var timed = await client.SendAsync("GET", $"127.0.0.1:{origin.Port}", "/");
            Assert.AreEqual(503, timed.StatusCode, $"request {i}");
        }

        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("d1"));
        origin.Release();
        var finished = await held.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(200, finished.StatusCode);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CancelWhileWaiting_DoesNotFreeTheHeldSlot()
    {
        await using var origin = new HoldingOrigin(gate: true);
        using var proxy = StartProxy(origin.Port, maxConnections: 1, connectTimeout: TimeSpan.FromSeconds(8));
        var held = GetAsync(proxy);
        await origin.WaitForAcceptedAsync(1);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => GetAsync(proxy, cts.Token));
        Assert.AreEqual(1, proxy.TcpConnectionFactory.BudgetOpenForTests());
        origin.Release();
        using var finished = await held;
        Assert.AreEqual(HttpStatusCode.OK, finished.StatusCode);
        // The abandoned waiter acquires the freed slot, opens its own origin socket, and gives the slot
        // back once the origin answers. Let that settle before stopping so the stop does not race it.
        await origin.WaitForAcceptedAsync(2);
        await WaitUntilAsync(() => proxy.TcpConnectionFactory.BudgetOpenForTests() == 0,
            () => $"open {proxy.TcpConnectionFactory.BudgetOpenForTests()} before stop");
        await proxy.StopAsync();
        await WaitUntilAsync(() => proxy.TcpConnectionFactory.BudgetOpenForTests() == 0,
            () => $"open {proxy.TcpConnectionFactory.BudgetOpenForTests()}, origin accepted {origin.Accepted}");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task UnsetCap_AllocatesNoBudget_AndOpensMoreThanTwoSockets()
    {
        await using var origin = new HoldingOrigin(gate: true);
        using var proxy = StartProxy(origin.Port, maxConnections: null, connectTimeout: null);
        var tasks = new Task<HttpResponseMessage>[8];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = GetAsync(proxy);
        }

        await origin.WaitForAcceptedAsync(3);
        Assert.AreEqual(0, proxy.TcpConnectionFactory.BudgetCountForTests);
        origin.Release();
        var responses = await Task.WhenAll(tasks);
        foreach (var response in responses)
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            response.Dispose();
        }

        Assert.IsTrue(origin.Peak > 2, $"peak sockets {origin.Peak}");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ReleasedConnection_WakesWaiterThatIsWaitingForPool()
    {
        await using var origin = new KeepAliveOrigin(gateFirst: true);
        using var proxy = StartProxy(origin.Port, maxConnections: 1, connectTimeout: TimeSpan.FromSeconds(3));
        var first = GetAsync(proxy);
        await origin.WaitForAcceptedAsync(1);
        var started = Stopwatch.StartNew();
        var second = GetAsync(proxy);
        await Task.Delay(150);
        origin.Release();
        var responses = await Task.WhenAll(first, second);
        Assert.IsTrue(started.Elapsed < TimeSpan.FromSeconds(2), $"second request took {started.Elapsed}");
        Assert.AreEqual(HttpStatusCode.OK, responses[0].StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, responses[1].StatusCode);
        Assert.AreEqual(1, origin.Accepted, "the waiter must rent the pooled socket");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task NoCacheRequest_EvictsIdleConnection_InsteadOfWaiting()
    {
        await using var origin = new KeepAliveOrigin(gateFirst: false);
        using var proxy = StartProxy(origin.Port, maxConnections: 1, connectTimeout: TimeSpan.FromMilliseconds(800));
        using var warm = await GetAsync(proxy);
        Assert.AreEqual(HttpStatusCode.OK, warm.StatusCode);
        await WaitUntilAsync(() => proxy.TcpConnectionFactory.BudgetOpenForTests() == 1);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://cap.test/");
        request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
        request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        var started = Stopwatch.StartNew();
        using var upgraded = await SendAsync(proxy, request);
        Assert.IsTrue(started.Elapsed < TimeSpan.FromMilliseconds(500), $"no-cache request took {started.Elapsed}");
        Assert.AreEqual(HttpStatusCode.OK, upgraded.StatusCode);
        Assert.IsTrue(origin.Accepted >= 2, $"accepted {origin.Accepted}");
    }

    private static async Task<int> RunConcurrent(ProxyServer proxy, HoldingOrigin origin, int count)
    {
        var tasks = new Task<HttpResponseMessage>[count];
        for (var i = 0; i < count; i++)
        {
            tasks[i] = GetAsync(proxy);
        }

        var responses = await Task.WhenAll(tasks);
        var peak = origin.Peak;
        foreach (var response in responses)
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            response.Dispose();
        }

        return peak;
    }

    private static Task<HttpResponseMessage> GetAsync(ProxyServer proxy, CancellationToken cancellationToken = default)
    {
        return SendAsync(proxy, new HttpRequestMessage(HttpMethod.Get, "http://cap.test/"), cancellationToken);
    }

    private static Task<HttpResponseMessage> SendAsync(ProxyServer proxy, HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        var ep = proxy.ProxyEndPoints[0];
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{ep.Port}"),
            UseProxy = true,
            MaxConnectionsPerServer = 32,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        return http.SendAsync(request, cancellationToken);
    }

    private static ProxyServer StartProxy(int originPort, int? maxConnections, TimeSpan? connectTimeout,
        ClusterManager? manager = null, bool passive = false)
    {
        manager ??= new ClusterManager();
        var cluster = new ClusterConfig
        {
            Id = "c1",
            Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = originPort }],
            HttpClient = maxConnections is null && connectTimeout is null
                ? null
                : new ClusterHttpClientConfig
                {
                    MaxConnectionsPerServer = maxConnections,
                    ConnectTimeout = connectTimeout,
                },
            HealthCheck = passive
                ? new HealthCheckConfig
                {
                    Passive = new PassiveHealthConfig { Enabled = true, ConsecutiveFailures = 1 },
                }
                : null,
        };
        manager.ApplyAsync([cluster]).AsTask().GetAwaiter().GetResult();
        var proxy = new ProxyServer(userTrustRootCertificate: false);
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c1",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
        };
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false));
        proxy.Start();
        return proxy;
    }

    private static ProxyServer StartTranslatedProxy(int originPort, ClusterManager manager, bool http3)
    {
        ApplyCapCluster(originPort, manager);
        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp2 = !http3,
            EnableHttp3 = http3,
            EnableHttpsSvcbDnsDiscovery = false,
        };
        if (http3)
        {
            proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
            proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
            proxy.CertificateManager.SaveFakeCertificates = false;
            proxy.ServerCertificateValidationCallback += (_, args) =>
            {
                args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
                return Task.CompletedTask;
            };
        }

        proxy.ReverseProxy = ReverseOptions(manager);
        if (http3)
        {
            var quic = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
            {
                ForwardHost = "127.0.0.1",
                ForwardPort = originPort,
                ForwardCleartext = true,
            };
            quic.BeforeQuicAuthenticate += (_, e) =>
            {
                e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
                e.AllowHttpProtocolTranslation = true;
                return Task.CompletedTask;
            };
            proxy.AddEndPoint(quic);
        }
        else
        {
            var clear = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false)
            {
                ForwardHost = "127.0.0.1",
                ForwardPort = originPort,
                ForwardCleartext = true,
            };
            clear.BeforeHttpAuthenticate += (_, e) =>
            {
                e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
                e.AllowHttpProtocolTranslation = true;
                return Task.CompletedTask;
            };
            proxy.AddEndPoint(clear);
        }

        proxy.Start();
        return proxy;
    }

    private static void ApplyCapCluster(int originPort, ClusterManager manager)
    {
        var cluster = new ClusterConfig
        {
            Id = "c1",
            Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = originPort }],
            HttpClient = new ClusterHttpClientConfig
            {
                MaxConnectionsPerServer = 1,
                ConnectTimeout = TimeSpan.FromMilliseconds(600),
            },
            HealthCheck = new HealthCheckConfig
            {
                Passive = new PassiveHealthConfig { Enabled = true, ConsecutiveFailures = 1 },
            },
        };
        manager.ApplyAsync([cluster]).AsTask().GetAwaiter().GetResult();
    }

    private static ReverseProxyOptions ReverseOptions(ClusterManager manager) => new()
    {
        Routes =
        [
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            },
        ],
        ClusterManager = manager,
        RouteMatcher = new RouteMatcher(),
        LoadBalancer = new LoadBalancer(),
    };

    private static async Task WriteH2GetAsync(Http2RawClient raw, int streamId)
    {
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "http"), (":authority", "cap.test"), (":path", "/")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(streamId, block, endStream: true);
    }

    private static async Task WaitUntilAsync(Func<bool> ready, Func<string>? describe = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!ready() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        if (!ready())
        {
            Assert.Fail("Condition not reached. " + describe?.Invoke());
        }
    }

    private sealed class HoldingOrigin : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cts = new();
        private readonly TimeSpan delay;
        private readonly bool gate;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task acceptLoop;
        private int accepted;
        private int peak;
        private int inFlight;

        public HoldingOrigin(TimeSpan delay)
        {
            this.delay = delay;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptLoop = AcceptLoopAsync();
        }

        public HoldingOrigin(bool gate)
        {
            this.gate = gate;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptLoop = AcceptLoopAsync();
        }

        public int Port { get; }

        public int Accepted => Volatile.Read(ref accepted);

        public int Peak => Volatile.Read(ref peak);

        public void Release() => release.TrySetResult();

        public async Task WaitForAcceptedAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Accepted < count && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.IsTrue(Accepted >= count, $"accepted {Accepted}, wanted at least {count}");
        }

        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            listener.Stop();
            release.TrySetResult();
            try
            {
                await acceptLoop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // The accept loop ends when the listener stops.
            }

            cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cts.Token);
                }
                catch
                {
                    return;
                }

                Interlocked.Increment(ref accepted);
                var now = Interlocked.Increment(ref inFlight);
                int seen;
                do
                {
                    seen = Volatile.Read(ref peak);
                    if (now <= seen)
                    {
                        break;
                    }
                }
                while (Interlocked.CompareExchange(ref peak, now, seen) != seen);

                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                await using var stream = client.GetStream();
                var buffer = new byte[4096];
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(read), cts.Token);
                    if (n == 0)
                    {
                        return;
                    }

                    read += n;
                    if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        break;
                    }
                }

                if (gate)
                {
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
                }
                else
                {
                    await Task.Delay(delay);
                }

                var body = "ok"u8.ToArray();
                var header = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header);
                await stream.WriteAsync(body);
            }
            catch
            {
                // The client or the test stopped the origin.
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
                client.Dispose();
            }
        }
    }

    /// <summary>Answers every request with keep-alive and leaves the socket open for the next one.</summary>
    private sealed class KeepAliveOrigin : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cts = new();
        private readonly bool gateFirst;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task acceptLoop;
        private int accepted;
        private int requests;

        public KeepAliveOrigin(bool gateFirst)
        {
            this.gateFirst = gateFirst;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptLoop = AcceptLoopAsync();
        }

        public int Port { get; }

        public int Accepted => Volatile.Read(ref accepted);

        public void Release() => release.TrySetResult();

        public async Task WaitForAcceptedAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Accepted < count && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.IsTrue(Accepted >= count, $"accepted {Accepted}, wanted at least {count}");
        }

        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            listener.Stop();
            release.TrySetResult();
            try
            {
                await acceptLoop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // The accept loop ends when the listener stops.
            }

            cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cts.Token);
                }
                catch
                {
                    return;
                }

                Interlocked.Increment(ref accepted);
                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                await using var stream = client.GetStream();
                var buffer = new byte[4096];
                while (!cts.IsCancellationRequested)
                {
                    var read = 0;
                    while (read < buffer.Length)
                    {
                        var n = await stream.ReadAsync(buffer.AsMemory(read), cts.Token);
                        if (n == 0)
                        {
                            return;
                        }

                        read += n;
                        if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal))
                        {
                            break;
                        }
                    }

                    var requestNumber = Interlocked.Increment(ref requests);
                    if (gateFirst && requestNumber == 1)
                    {
                        await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    }

                    var body = "ok"u8.ToArray();
                    var header = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Length: " + body.Length + "\r\nConnection: keep-alive\r\n\r\n");
                    await stream.WriteAsync(header);
                    await stream.WriteAsync(body);
                }
            }
            catch
            {
                // The client or the test stopped the origin.
            }
            finally
            {
                client.Dispose();
            }
        }
    }
}
