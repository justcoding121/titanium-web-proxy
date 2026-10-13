using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Inspector end-to-end release gates for trickle delivery (plan Step 5 gates 2, 3, 5): header and body
///     fidelity with and without the feature, real progressive arrival, keep-alive reuse, and client abort.
/// </summary>
[TestClass]
public class ThrottleDeliveryGateTests
{
    private const int BigBytes = 150 * 1024;

    [TestMethod]
    [Timeout(60_000)]
    public async Task Trickle_OnVersusOff_ClientSeesIdenticalHeadersAndBytes_AndCaptureIsUnchanged()
    {
        using var origin = new KeepAliveOrigin();
        var fast = new NetworkThrottleProfile("fast", latencyMs: 0, bytesPerSecond: int.MaxValue);

        var none = await Fetch(origin.Port, profile: null, trickle: false, "/big");
        var off = await Fetch(origin.Port, fast, trickle: false, "/big");
        var on = await Fetch(origin.Port, fast, trickle: true, "/big");

        // Gate 2: same client-visible headers (trickle on vs off) and same body bytes in all three.
        CollectionAssert.AreEqual(
            off.Headers,
            on.Headers,
            $"Trickle must not change response headers.\nOFF:\n{string.Join("\n", off.Headers)}\nON:\n{string.Join("\n", on.Headers)}\nNONE:\n{string.Join("\n", none.Headers)}");
        CollectionAssert.AreEqual(none.Bytes, on.Bytes);
        CollectionAssert.AreEqual(off.Bytes, on.Bytes);
        Assert.IsTrue(on.Headers.Contains("set-cookie: a=1"));
        Assert.IsTrue(on.Headers.Contains("set-cookie: b=2"), "Duplicate Set-Cookie headers must both survive.");
        Assert.IsTrue(on.Headers.Contains("x-origin: gate"));
        Assert.IsTrue(on.ConnectionClose, "A streamed HTTP/1 replacement announces that the connection ends.");
        Assert.IsFalse(off.ConnectionClose);
        Assert.IsFalse(none.ConnectionClose);

        // Gate 3: capture is unchanged by the delivery mode.
        Assert.AreEqual(none.Snapshot.ResponseBodyCapture, on.Snapshot.ResponseBodyCapture);
        Assert.AreEqual(off.Snapshot.ResponseBodyCapture, on.Snapshot.ResponseBodyCapture);
        CollectionAssert.AreEqual(none.Snapshot.ResponseBodyBytes, on.Snapshot.ResponseBodyBytes);
        CollectionAssert.AreEqual(off.Snapshot.ResponseBodyBytes, on.Snapshot.ResponseBodyBytes);
        Assert.AreEqual(off.Snapshot.StatusCode, on.Snapshot.StatusCode);
        CollectionAssert.AreEqual(
            NonThrottleEvents(off.Snapshot),
            NonThrottleEvents(on.Snapshot),
            "Statistics events other than the throttle note must be identical.");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Trickle_DeliversProgressively_AndDefaultTimeoutsDoNotAbortIt()
    {
        using var origin = new KeepAliveOrigin();
        using var interception = NewInterception(
            new NetworkThrottleProfile("slow", latencyMs: 0, bytesPerSecond: 60_000), trickle: true);
        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            using var http = ProxyClient(interception.BoundPort, TimeSpan.FromSeconds(30));
            var watch = Stopwatch.StartNew();
            using var response = await http.GetAsync(
                $"http://127.0.0.1:{origin.Port}/big", HttpCompletionOption.ResponseHeadersRead);
            await using var body = await response.Content.ReadAsStreamAsync();

            var buffer = new byte[8 * 1024];
            var total = 0;
            TimeSpan? at20Percent = null;
            int read;
            while ((read = await body.ReadAsync(buffer)) > 0)
            {
                total += read;
                if (at20Percent is null && total >= BigBytes / 5)
                {
                    at20Percent = watch.Elapsed;
                }
            }

            watch.Stop();
            Assert.AreEqual(BigBytes, total);
            Assert.IsNotNull(at20Percent);
            // 150 KiB at 60 kB/s is about 2.5 s. A burst would put all bytes within one read burst.
            Assert.IsTrue(watch.Elapsed >= TimeSpan.FromSeconds(1.8), $"Transfer finished in {watch.Elapsed}.");
            Assert.IsTrue(
                watch.Elapsed - at20Percent.Value >= TimeSpan.FromSeconds(1.2),
                $"Bytes arrived as a block: 20% at {at20Percent}, done at {watch.Elapsed}.");
        }
        finally
        {
            interception.EnsureShutdown();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public Task Trickle_H1Client_NeverStallsOnTheNextRequest_AndAnnouncesConnectionClose() =>
        KeepAliveReuse(new NetworkThrottleProfile("fast", latencyMs: 0, bytesPerSecond: 4_000_000), trickle: true);

    [TestMethod]
    [Timeout(60_000)]
    public Task Control_KeepAliveReuse_WithBurstDelivery() =>
        KeepAliveReuse(new NetworkThrottleProfile("fast", latencyMs: 0, bytesPerSecond: 4_000_000), trickle: false);

    [TestMethod]
    [Timeout(60_000)]
    public Task Control_KeepAliveReuse_WithNoThrottle() => KeepAliveReuse(profile: null, trickle: false);

    private static async Task KeepAliveReuse(NetworkThrottleProfile? profile, bool trickle)
    {
        using var origin = new KeepAliveOrigin();
        using var interception = NewInterception(profile, trickle);
        var captured = new List<(long Id, long Connection)>();
        interception.SessionCaptured += (_, s) =>
        {
            lock (captured)
            {
                captured.Add((s.Id, s.ConnectionId));
            }
        };
        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            using var http = ProxyClient(interception.BoundPort, TimeSpan.FromSeconds(20));
            for (var i = 0; i < 3; i++)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try
                {
                    var bytes = await http.GetByteArrayAsync($"http://127.0.0.1:{origin.Port}/big?i={i}", cts.Token);
                    Assert.AreEqual(BigBytes, bytes.Length);
                }
                catch (OperationCanceledException)
                {
                    int seen;
                    lock (captured)
                    {
                        seen = captured.Count;
                    }

                    Assert.Fail(
                        $"Request {i} stalled; origin connections={origin.Connections}, origin requests={origin.Requests}, captured={seen}.");
                }
            }

            lock (captured)
            {
                Assert.AreEqual(3, captured.Count);
                Assert.AreEqual(3, captured.Select(c => c.Id).Distinct().Count(), "Each exchange is captured exactly once.");
                // A streamed HTTP/1 replacement ends the client connection (announced with Connection: close),
                // because the consumed origin body cannot be drained. Every other mode keeps both legs alive.
                var expectedClientConnections = trickle ? 3 : 1;
                Assert.AreEqual(
                    expectedClientConnections,
                    captured.Select(c => c.Connection).Distinct().Count(),
                    "Unexpected client connection reuse.");
            }

            if (!trickle)
            {
                Assert.AreEqual(1, origin.Connections, "Burst delivery must leave the origin connection pooled.");
            }
        }
        finally
        {
            interception.EnsureShutdown();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Trickle_ClientAbort_StopsTheWriter_AndDoesNotStarveTheNextRequest()
    {
        using var origin = new KeepAliveOrigin();
        // 400 KiB at 20 kB/s would hold the shared download clock for about 20 s if the writer kept going.
        using var interception = NewInterception(
            new NetworkThrottleProfile("slow", latencyMs: 0, bytesPerSecond: 20_000), trickle: true);
        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            var aborter = ProxyClient(interception.BoundPort, TimeSpan.FromSeconds(30));
            using (var response = await aborter.GetAsync(
                       $"http://127.0.0.1:{origin.Port}/huge", HttpCompletionOption.ResponseHeadersRead))
            {
                await using var stream = await response.Content.ReadAsStreamAsync();
                var buffer = new byte[4096];
                var got = 0;
                while (got < 16 * 1024)
                {
                    var read = await stream.ReadAsync(buffer);
                    Assert.IsTrue(read > 0);
                    got += read;
                }
            }

            aborter.Dispose();

            using var next = ProxyClient(interception.BoundPort, TimeSpan.FromSeconds(30));
            var watch = Stopwatch.StartNew();
            var small = await next.GetByteArrayAsync($"http://127.0.0.1:{origin.Port}/small");
            watch.Stop();

            Assert.AreEqual(30 * 1024, small.Length);
            // 30 KiB at 20 kB/s is about 1.5 s alone. A writer that kept reserving would add many seconds.
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(8), $"Next request took {watch.Elapsed}.");
        }
        finally
        {
            interception.EnsureShutdown();
        }
    }

    private static InterceptionService NewInterception(NetworkThrottleProfile? profile, bool trickle) =>
        new(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            DecryptHttps = false,
            Capturing = true,
            ProgressiveThrottleDelivery = trickle,
            ThrottleProfile = profile,
        };

    private static HttpClient ProxyClient(int port, TimeSpan timeout) =>
        new(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.None,
        })
        { Timeout = timeout };

    private static string[] NonThrottleEvents(SessionSnapshot snapshot) =>
        (snapshot.Events ?? [])
        .Where(e => !e.Key.StartsWith("stats.event.throttle", StringComparison.Ordinal))
        .Select(e => $"{e.Key}:{e.Failure}")
        .ToArray();

    private static async Task<(string[] Headers, byte[] Bytes, SessionSnapshot Snapshot, bool ConnectionClose)> Fetch(
        int originPort, NetworkThrottleProfile? profile, bool trickle, string path)
    {
        using var interception = NewInterception(profile, trickle);
        SessionSnapshot? snapshot = null;
        interception.SessionCaptured += (_, s) => snapshot = s;
        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            using var http = ProxyClient(interception.BoundPort, TimeSpan.FromSeconds(20));
            using var response = await http.GetAsync($"http://127.0.0.1:{originPort}{path}");
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var headers = response.Headers
                .Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key.ToLowerInvariant()}: {v}"))
                .Where(h => !h.StartsWith("date:", StringComparison.Ordinal)
                            && !h.StartsWith("connection:", StringComparison.Ordinal)
                            && !h.StartsWith("proxy-connection:", StringComparison.Ordinal))
                .OrderBy(h => h, StringComparer.Ordinal)
                .ToArray();
            Assert.IsNotNull(snapshot);
            return (headers, bytes, snapshot!, response.Headers.ConnectionClose == true);
        }
        finally
        {
            interception.EnsureShutdown();
        }
    }

    /// <summary>Minimal HTTP/1.1 origin with keep-alive that counts accepted connections.</summary>
    private sealed class KeepAliveOrigin : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private int _connections;

        public KeepAliveOrigin()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoop);
        }

        public int Port { get; }

        public int Connections => Volatile.Read(ref _connections);

        public int Requests => Volatile.Read(ref _requests);

        private int _requests;

        private async Task AcceptLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    Interlocked.Increment(ref _connections);
                    _ = Task.Run(() => Serve(client));
                }
            }
            catch
            {
                // listener stopped
            }
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var head = new StringBuilder();
                    var one = new byte[1];
                    while (!_cts.IsCancellationRequested)
                    {
                        head.Clear();
                        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                        {
                            if (await stream.ReadAsync(one, _cts.Token) == 0)
                            {
                                return;
                            }

                            head.Append((char)one[0]);
                        }

                        Interlocked.Increment(ref _requests);
                        var path = head.ToString().Split(' ')[1];
                        var size = path.StartsWith("/huge", StringComparison.Ordinal) ? 400 * 1024
                            : path.StartsWith("/small", StringComparison.Ordinal) ? 30 * 1024
                            : BigBytes;
                        var body = new byte[size];
                        Array.Fill(body, (byte)'Z');
                        var header = "HTTP/1.1 200 OK\r\n"
                                     + "Content-Type: application/octet-stream\r\n"
                                     + "Set-Cookie: a=1\r\nSet-Cookie: b=2\r\nX-Origin: gate\r\n"
                                     + $"Content-Length: {size}\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _cts.Token);
                        await stream.WriteAsync(body, _cts.Token);
                    }
                }
                catch
                {
                    // client went away
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
