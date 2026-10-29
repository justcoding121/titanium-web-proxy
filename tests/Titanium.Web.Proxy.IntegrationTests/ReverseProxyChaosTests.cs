using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Chaos rows for the reverse-proxy harness (the transparent listener with <c>BeforeRequest</c> pointing at a
///     scripted origin): the origin resets, stalls or truncates a response, and the client aborts mid-body. Every
///     row asserts the three properties an operator relies on - the failure reaches the client within a bound
///     (never a hang), retries stay bounded, and the proxy serves the next request normally.
///     <para>
///         Not repeated here because they already exist: origin never answers, TLS reset before response, EOF before
///         response and retry bounds on a decrypted tunnel (<see cref="OriginFailureMatrixTests" />,
///         <see cref="ProxyTimeoutTests" />), and an invalid config reload keeping the previous config
///         (<c>CliConfigMapReloadE2ETests</c>, <c>CliRunDialectsE2ETests</c> in <c>Titanium.E2E.Tests</c>).
///     </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class ReverseProxyChaosTests
{
    private const int Timeout30s = 30 * 1000;
    private static readonly TimeSpan BodyFailureBound = TimeSpan.FromSeconds(10);

    // A client that re-sends after a connection closed before any response byte multiplies the proxy's own
    // per-request teardown time, so the before-commit rows get a wider (still finite) bound.
    private static readonly TimeSpan PreCommitFailureBound = TimeSpan.FromSeconds(25);

    [TestMethod]
    [Timeout(Timeout30s)]
    public async Task OriginResetMidResponse_ClientBodyFailsFast_NoReplay_AndProxyStaysHealthy()
    {
        using var origin = new ChaosOrigin(async (index, socket, ct) =>
        {
            await ChaosOrigin.ReadRequestHeadAsync(socket, ct);
            if (index > 0)
            {
                await ChaosOrigin.SendAsync(socket, ChaosOrigin.OkResponse, ct);
                return;
            }

            await ChaosOrigin.SendAsync(socket,
                "HTTP/1.1 200 OK\r\nContent-Length: 1000000\r\nContent-Type: application/octet-stream\r\n\r\n" +
                new string('x', 2000), ct);
            await Task.Delay(100, ct);
            ChaosOrigin.Reset(socket);
        });

        using var harness = new ChaosHarness(origin);
        var (proxy, client, route) = (harness.Proxy, harness.Client, harness.Route);

        var sw = Stopwatch.StartNew();
        using var response = await client.GetAsync(ProxyUrl(proxy), HttpCompletionOption.ResponseHeadersRead);

        // Headers were already committed to the client, so the status stays 200 and the truncation shows up as a
        // failed body read - not as a second response spliced into the stream, and not as a replay to the origin.
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await AssertBodyReadFailsWithinAsync(response);
        Assert.IsTrue(sw.Elapsed < BodyFailureBound, $"reset surfaced after {sw.Elapsed}");
        Assert.AreEqual(1, origin.Accepted, "a response that was already committed must not be replayed");

        await AssertProxyServesNextRequestAsync(client, proxy, route);
    }

    [TestMethod]
    [Timeout(Timeout30s)]
    public async Task OriginStallMidBody_IdleReadTimeoutEndsTheResponse_AndProxyStaysHealthy()
    {
        using var origin = new ChaosOrigin(async (index, socket, ct) =>
        {
            await ChaosOrigin.ReadRequestHeadAsync(socket, ct);
            if (index > 0)
            {
                await ChaosOrigin.SendAsync(socket, ChaosOrigin.OkResponse, ct);
                return;
            }

            await ChaosOrigin.SendAsync(socket,
                "HTTP/1.1 200 OK\r\nContent-Length: 1000000\r\nContent-Type: application/octet-stream\r\n\r\n" +
                new string('x', 2000), ct);

            // Stall: keep the connection open and silent until the test tears the origin down.
            await Task.Delay(Timeout.Infinite, ct);
        });

        using var harness = new ChaosHarness(origin);
        var (proxy, client, route) = (harness.Proxy, harness.Client, harness.Route);
        proxy.IdleReadTimeoutSeconds = 1;

        var sw = Stopwatch.StartNew();
        using var response = await client.GetAsync(ProxyUrl(proxy), HttpCompletionOption.ResponseHeadersRead);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await AssertBodyReadFailsWithinAsync(response);
        Assert.IsTrue(sw.Elapsed < BodyFailureBound,
            $"a stalled origin body must end near the 1 s idle timeout, took {sw.Elapsed}");
        Assert.AreEqual(1, origin.Accepted, "a stalled committed response must not be replayed");

        await AssertProxyServesNextRequestAsync(client, proxy, route);
    }

    [DataTestMethod]
    [DataRow("HTTP/1.1 200 OK\r\nContent-Le", false, DisplayName = "half-status-and-headers-then-close")]
    [DataRow("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n", false, DisplayName = "headers-without-blank-line-then-close")]
    [DataRow("", true, DisplayName = "reset-right-after-request")]
    [DataRow("", false, DisplayName = "close-right-after-request")]
    [Timeout(60 * 1000)]
    public async Task OriginFailureBeforeResponseCommit_ClientSeesAFailure_WithBoundedRetries(
        string partialResponse, bool reset)
    {
        using var origin = new ChaosOrigin(async (index, socket, ct) =>
        {
            await ChaosOrigin.ReadRequestHeadAsync(socket, ct);
            if (partialResponse.Length > 0)
                await ChaosOrigin.SendAsync(socket, partialResponse, ct);

            if (reset) ChaosOrigin.Reset(socket);
            // else: returning disposes the socket, which is an orderly FIN with no (or half a) response.
        });

        using var harness = new ChaosHarness(origin);
        var (proxy, client, route) = (harness.Proxy, harness.Client, harness.Route);

        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(PreCommitFailureBound);
        HttpStatusCode? status = null;
        try
        {
            using var response = await client.GetAsync(ProxyUrl(proxy), cts.Token);
            status = response.StatusCode;
        }
        catch (HttpRequestException)
        {
            // An orderly close with no usable response is also a failure the client can see. A malformed header
            // line from the origin currently ends the client connection without a 502 (documented in
            // tests/fuzz/README.md); the contract asserted here is that it is never a success and never a hang.
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"the client was still waiting after {PreCommitFailureBound.TotalSeconds:0} s");
        }

        if (status is { } code)
            Assert.IsTrue(code is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout,
                $"expected 502/504 or a closed connection, got {(int)code}");
        Assert.IsTrue(sw.Elapsed < PreCommitFailureBound, $"failure took {sw.Elapsed}");

        // Per client request: the first attempt plus at most NetworkFailureRetryAttempts retries and one silent
        // re-dial; never a storm. The client may itself re-send on a connection closed before any response byte,
        // so the bound scales with the requests the proxy actually accepted.
        var requests = Math.Max(1, route.Requests);
        var bound = requests * (proxy.NetworkFailureRetryAttempts + 2);
        Assert.IsTrue(origin.Accepted <= bound,
            $"origin saw {origin.Accepted} connections for {requests} proxied request(s), bound is {bound}");

        // The origin is still broken, but the proxy must not be: a different origin works.
        using var healthy = new ChaosOrigin(async (_, socket, ct) =>
        {
            await ChaosOrigin.ReadRequestHeadAsync(socket, ct);
            await ChaosOrigin.SendAsync(socket, ChaosOrigin.OkResponse, ct);
        });
        route.Port = healthy.Port;
        using var next = await client.GetAsync(ProxyUrl(proxy));
        Assert.AreEqual(HttpStatusCode.OK, next.StatusCode);
    }

    [TestMethod]
    [Timeout(Timeout30s)]
    public async Task ClientAbortMidBody_ProxyReleasesTheOriginConnection_AndStaysHealthy()
    {
        var originSawBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originSawEnd = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var origin = new ChaosOrigin(async (_, socket, ct) =>
        {
            var head = await ChaosOrigin.ReadRequestHeadAsync(socket, ct);
            if (!head.StartsWith("POST", StringComparison.Ordinal))
            {
                await ChaosOrigin.SendAsync(socket, ChaosOrigin.OkResponse, ct);
                return;
            }

            // Drain the body until the proxy ends the connection. Reaching the end proves the abort propagated
            // instead of leaving the origin connection (and its worker) waiting for the rest of the body.
            var buffer = new byte[4096];
            var received = 0;
            try
            {
                while (true)
                {
                    var n = await socket.ReceiveAsync(buffer, SocketFlags.None, ct);
                    if (n == 0)
                    {
                        originSawEnd.TrySetResult($"eof after {received} body bytes");
                        return;
                    }

                    received += n;
                    if (received >= 100) originSawBody.TrySetResult();
                }
            }
            catch (SocketException ex)
            {
                originSawEnd.TrySetResult($"{ex.SocketErrorCode} after {received} body bytes");
            }
        });

        using var harness = new ChaosHarness(origin);
        var (proxy, client, route) = (harness.Proxy, harness.Client, harness.Route);

        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
            var stream = raw.GetStream();
            var head = Encoding.ASCII.GetBytes(
                "POST /upload HTTP/1.1\r\nHost: localhost\r\nContent-Length: 1000000\r\n\r\n");
            await stream.WriteAsync(head);
            await stream.WriteAsync(new byte[200]);
            await stream.FlushAsync();

            await originSawBody.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Abort with an RST in the middle of the declared body.
            raw.Client.LingerState = new LingerOption(true, 0);
        }

        var ended = await originSawEnd.Task.WaitAsync(BodyFailureBound);
        Assert.IsFalse(string.IsNullOrEmpty(ended));

        await AssertProxyServesNextRequestAsync(client, proxy, route);
    }

    /// <summary>The port of the scripted origin a proxy's <c>BeforeRequest</c> currently points at.</summary>
    private sealed class Route
    {
        private int port;
        private int requests;

        public int Port
        {
            get => Volatile.Read(ref port);
            set => Volatile.Write(ref port, value);
        }

        /// <summary>Requests the proxy accepted from clients (a client may itself retry a closed connection).</summary>
        public int Requests => Volatile.Read(ref requests);

        public void CountRequest() => Interlocked.Increment(ref requests);
    }

    /// <summary>
    ///     A reverse (transparent) listener whose <c>BeforeRequest</c> sends every request to a scripted origin, and
    ///     a direct client for it. Needs no Kestrel host, so it does not depend on the shared integration TLS server.
    /// </summary>
    private sealed class ChaosHarness : IDisposable
    {
        private readonly TestProxyServer testProxy;

        public ChaosHarness(ChaosOrigin origin)
        {
            testProxy = new TestProxyServer(isReverseProxy: true);
            Proxy = testProxy.ProxyServer;
            Proxy.NetworkFailureRetryAttempts = 1;

            Route = new Route { Port = origin.Port };
            Proxy.BeforeRequest += (_, e) =>
            {
                Route.CountRequest();
                e.HttpClient.Request.Url = $"http://127.0.0.1:{Route.Port}/";
                return Task.CompletedTask;
            };

            Client = TestHelper.GetHttpClient();
        }

        public ProxyServer Proxy { get; }

        public HttpClient Client { get; }

        public Route Route { get; }

        public void Dispose()
        {
            Client.Dispose();
            testProxy.Dispose();
        }
    }
    private static Uri ProxyUrl(ProxyServer proxy) => new($"http://localhost:{proxy.ProxyEndPoints[0].Port}/");

    private static async Task AssertBodyReadFailsWithinAsync(HttpResponseMessage response)
    {
        using var cts = new CancellationTokenSource(BodyFailureBound);
        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
            Assert.Fail($"the truncated body was delivered as complete ({bytes.Length} bytes)");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // expected: the connection was cut mid-body
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"the client body read was still hanging after {BodyFailureBound.TotalSeconds:0} s");
        }
    }

    private static async Task AssertProxyServesNextRequestAsync(HttpClient client, ProxyServer proxy, Route route)
    {
        using var healthy = new ChaosOrigin(async (_, socket, ct) =>
        {
            await ChaosOrigin.ReadRequestHeadAsync(socket, ct);
            await ChaosOrigin.SendAsync(socket, ChaosOrigin.OkResponse, ct);
        });
        route.Port = healthy.Port;

        using var response = await client.GetAsync(ProxyUrl(proxy));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("ok", await response.Content.ReadAsStringAsync());
    }

    /// <summary>A loopback TCP origin whose per-connection behaviour is a script.</summary>
    private sealed class ChaosOrigin : IDisposable
    {
        internal const string OkResponse =
            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\nContent-Type: text/plain\r\n\r\nok";

        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop = new();
        private int accepted;

        public ChaosOrigin(Func<int, Socket, CancellationToken, Task> script)
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(() => AcceptLoopAsync(script));
        }

        public int Port { get; }

        public int Accepted => Volatile.Read(ref accepted);

        private async Task AcceptLoopAsync(Func<int, Socket, CancellationToken, Task> script)
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var socket = await listener.AcceptSocketAsync(stop.Token);
                    var index = Interlocked.Increment(ref accepted) - 1;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await script(index, socket, stop.Token);
                        }
                        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException
                                                       or OperationCanceledException)
                        {
                            // The script's own connection died or the test ended; nothing to report.
                        }
                        finally
                        {
                            socket.Dispose();
                        }
                    });
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // listener stopped
            }
        }

        public static async Task<string> ReadRequestHeadAsync(Socket socket, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var length = 0;
            while (length < buffer.Length)
            {
                var n = await socket.ReceiveAsync(buffer.AsMemory(length), SocketFlags.None, ct);
                if (n == 0) break;
                length += n;
                if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
            }

            return Encoding.ASCII.GetString(buffer, 0, length);
        }

        public static async Task SendAsync(Socket socket, string text, CancellationToken ct)
        {
            await socket.SendAsync(Encoding.ASCII.GetBytes(text), SocketFlags.None, ct);
        }

        /// <summary>Closes with an RST (zero linger) instead of an orderly FIN.</summary>
        public static void Reset(Socket socket)
        {
            socket.LingerState = new LingerOption(true, 0);
            socket.Close(0);
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
            stop.Dispose();
        }
    }
}
