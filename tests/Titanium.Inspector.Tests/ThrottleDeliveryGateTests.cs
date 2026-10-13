using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    // ---- HTTP/2 client through a decrypting Inspector (origin is a Kestrel HTTP/2 server) ----

    [TestMethod]
    [Timeout(90_000)]
    public async Task Http2Client_TrickleOnVersusOff_IsIdentical_AndStaysOnOneConnection()
    {
        using var origin = new Http2Origin();
        var fast = new NetworkThrottleProfile("fast", latencyMs: 0, bytesPerSecond: 8_000_000);

        var off = await FetchH2(origin.Port, fast, trickle: false, count: 3);
        var on = await FetchH2(origin.Port, fast, trickle: true, count: 3);

        foreach (var run in new[] { off, on })
        {
            Assert.IsTrue(run.All(r => r.Version == new Version(2, 0)), "The client must really be on HTTP/2.");
            Assert.IsTrue(run.All(r => r.Bytes.Length == BigBytes));
        }

        for (var i = 0; i < 3; i++)
        {
            CollectionAssert.AreEqual(off[i].Headers, on[i].Headers, "Trickle must not change HTTP/2 response headers.");
            CollectionAssert.AreEqual(off[i].Bytes, on[i].Bytes);
            Assert.AreEqual(off[i].Snapshot.ResponseBodyCapture, on[i].Snapshot.ResponseBodyCapture);
            CollectionAssert.AreEqual(off[i].Snapshot.ResponseBodyBytes, on[i].Snapshot.ResponseBodyBytes);
            Assert.IsTrue(on[i].Headers.Contains("set-cookie: a=1") && on[i].Headers.Contains("set-cookie: b=2"));
            Assert.IsFalse(on[i].ConnectionClose, "HTTP/2 responses must never carry Connection: close.");
        }

        Assert.AreEqual(1, on.Select(r => r.Snapshot.ConnectionId).Distinct().Count(),
            "All HTTP/2 streams stay on the one client connection.");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(90_000)]
    public async Task Http2Client_ParallelResponses_PayOneLatency_NotOnePerStream(bool trickle)
    {
        const int streams = 6;
        const int latencyMs = 500;
        using var origin = new Http2Origin();
        using var interception = NewInterception(
            new NetworkThrottleProfile("lat", latencyMs: latencyMs, bytesPerSecond: 50_000_000),
            trickle,
            decrypt: true);
        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            using var http = ProxyClient(interception.BoundPort, TimeSpan.FromSeconds(60), h2: true);
            // Warm the CONNECT tunnel and the HTTP/2 connection so handshakes are not measured.
            using (var warm = await http.GetAsync($"https://127.0.0.1:{origin.Port}/small?warm"))
            {
                Assert.AreEqual(new Version(2, 0), warm.Version, "The client must really be on HTTP/2.");
                Assert.AreEqual(30 * 1024, (await warm.Content.ReadAsByteArrayAsync()).Length);
            }

            var watch = Stopwatch.StartNew();
            var all = await Task.WhenAll(Enumerable.Range(0, streams)
                .Select(i => http.GetByteArrayAsync($"https://127.0.0.1:{origin.Port}/small?i={i}")));
            watch.Stop();

            Assert.IsTrue(all.All(b => b.Length == 30 * 1024));
            // Serialized latency would be about streams x 500 ms = 3000 ms.
            Assert.IsTrue(
                watch.ElapsedMilliseconds < latencyMs * 3,
                $"{streams} parallel HTTP/2 responses took {watch.ElapsedMilliseconds} ms; one latency is {latencyMs} ms.");
            Assert.IsTrue(watch.ElapsedMilliseconds >= latencyMs - 50, "The latency must actually be applied.");
        }
        finally
        {
            interception.EnsureShutdown();
        }
    }

    /// <summary>
    ///     Documents the HTTP/2 limit: bandwidth is not shaped (the response is locked once capture reads the
    ///     body, and sleeping on the frame loop would stall the connection). What must hold is that a slow
    ///     profile never stalls the connection, so a sibling request finishes while a large one is in flight.
    /// </summary>
    [TestMethod]
    [Timeout(90_000)]
    public async Task Http2Client_SlowProfile_NeverStallsTheConnection()
    {
        using var origin = new Http2Origin();
        using var interception = NewInterception(
            new NetworkThrottleProfile("slow", latencyMs: 100, bytesPerSecond: 60_000),
            trickle: true,
            decrypt: true);
        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            using var http = ProxyClient(interception.BoundPort, TimeSpan.FromSeconds(60), h2: true);
            var watch = Stopwatch.StartNew();
            var big = http.GetByteArrayAsync($"https://127.0.0.1:{origin.Port}/big");
            var small = await http.GetByteArrayAsync($"https://127.0.0.1:{origin.Port}/small");
            var smallDone = watch.Elapsed;

            Assert.AreEqual(30 * 1024, small.Length);
            Assert.AreEqual(BigBytes, (await big).Length);
            Assert.IsTrue(smallDone < TimeSpan.FromSeconds(3), $"A sibling request took {smallDone}.");
        }
        finally
        {
            interception.EnsureShutdown();
        }
    }

    private static async Task<List<(string[] Headers, byte[] Bytes, SessionSnapshot Snapshot, bool ConnectionClose, Version Version)>>
        FetchH2(int originPort, NetworkThrottleProfile profile, bool trickle, int count)
    {
        using var interception = NewInterception(profile, trickle, decrypt: true);
        var snapshots = new List<SessionSnapshot>();
        interception.SessionCaptured += (_, s) =>
        {
            lock (snapshots)
            {
                snapshots.Add(s);
            }
        };
        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            using var http = ProxyClient(interception.BoundPort, TimeSpan.FromSeconds(30), h2: true);
            var results = new List<(string[], byte[], SessionSnapshot, bool, Version)>();
            for (var i = 0; i < count; i++)
            {
                using var response = await http.GetAsync($"https://127.0.0.1:{originPort}/big?i={i}");
                var bytes = await response.Content.ReadAsByteArrayAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                SessionSnapshot snapshot;
                lock (snapshots)
                {
                    snapshot = snapshots.Last(s => s.Url.Contains($"i={i}", StringComparison.Ordinal));
                }

                results.Add((FlattenHeaders(response), bytes, snapshot, response.Headers.ConnectionClose == true, response.Version));
            }

            return results;
        }
        finally
        {
            interception.EnsureShutdown();
        }
    }

    private static string[] FlattenHeaders(HttpResponseMessage response) =>
        response.Headers
            .Concat(response.Content.Headers)
            .SelectMany(h => h.Value.Select(v => $"{h.Key.ToLowerInvariant()}: {v}"))
            .Where(h => !h.StartsWith("date:", StringComparison.Ordinal)
                        && !h.StartsWith("connection:", StringComparison.Ordinal)
                        && !h.StartsWith("proxy-connection:", StringComparison.Ordinal))
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToArray();

    private static InterceptionService NewInterception(NetworkThrottleProfile? profile, bool trickle, bool decrypt = false) =>
        new(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            DecryptHttps = decrypt,
            IgnoreServerCertificateErrors = decrypt,
            Capturing = true,
            ProgressiveThrottleDelivery = trickle,
            ThrottleProfile = profile,
        };

    private static HttpClient ProxyClient(int port, TimeSpan timeout, bool h2 = false)
    {
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.None,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        var client = new HttpClient(handler) { Timeout = timeout };
        if (h2)
        {
            client.DefaultRequestVersion = new Version(2, 0);
            // Offer h2 and http/1.1; each test asserts the version it actually got.
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }

        return client;
    }

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

    /// <summary>Kestrel HTTPS origin that negotiates HTTP/2, so the proxy offers HTTP/2 to the client.</summary>
    private sealed class Http2Origin : IDisposable
    {
        private readonly Microsoft.AspNetCore.Builder.WebApplication _app;
        private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _certificate;

        public Http2Origin()
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                "CN=127.0.0.1", rsa,
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var self = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            _certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
                self.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null);

            var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(IPAddress.Loopback, 0, listen =>
                {
                    listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
                    listen.UseHttps(_certificate);
                }));
            _app = builder.Build();
            _app.Run(async context =>
            {
                var path = context.Request.Path.Value ?? "/";
                var size = path.StartsWith("/small", StringComparison.Ordinal) ? 30 * 1024 : BigBytes;
                var body = new byte[size];
                Array.Fill(body, (byte)'Z');
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength = size;
                context.Response.Headers["Set-Cookie"] = new Microsoft.Extensions.Primitives.StringValues(["a=1", "b=2"]);
                context.Response.Headers["X-Origin"] = "gate";
                await context.Response.Body.WriteAsync(body);
            });
            _app.Start();
            var addresses = _app.Services
                .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
                .Addresses;
            Port = new Uri(addresses.First()).Port;
        }

        public int Port { get; }

        public void Dispose()
        {
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _certificate.Dispose();
        }
    }
}
