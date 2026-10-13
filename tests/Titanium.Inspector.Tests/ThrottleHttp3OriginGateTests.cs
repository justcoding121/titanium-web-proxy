#pragma warning disable CA1416
#pragma warning disable TWP001

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Network throttle against an HTTP/3 origin. The Inspector is an explicit TCP proxy, so HTTP/3 only ever
///     appears on the origin side: a browser (HTTP/1.1 or HTTP/2) talks to the Inspector and the Inspector
///     reaches an HTTP/3 origin (for example after Alt-Svc). Every case runs on the cold (CONNECT-time) and the
///     warm (capability-cache) route into HTTP/3.
/// </summary>
[TestClass]
public class ThrottleHttp3OriginGateTests
{
    private const int BigBytes = 150 * 1024;

    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
        {
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
        }
    }

    [TestMethod]
    [Timeout(90_000)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Http2Client_ToHttp3Origin_Throttled_IsIdenticalToUnthrottled(bool warm)
    {
        RequireQuic();
        await using var origin = StartOrigin(withContentLength: true);
        var none = await Fetch(origin, warm, profile: null, h2: true, "/big");
        var on = await Fetch(origin, warm, new NetworkThrottleProfile("fast", latencyMs: 0, bytesPerSecond: 8_000_000), h2: true, "/big");

        Assert.AreEqual(BigBytes, on.Bytes.Length);
        CollectionAssert.AreEqual(none.Bytes, on.Bytes);
        CollectionAssert.AreEqual(
            none.Headers,
            on.Headers,
            $"THROTTLED:\n{string.Join("\n", on.Headers)}\nNONE:\n{string.Join("\n", none.Headers)}");
        Assert.AreEqual(none.Snapshot.ResponseBodyCapture, on.Snapshot.ResponseBodyCapture);
        CollectionAssert.AreEqual(none.Snapshot.ResponseBodyBytes, on.Snapshot.ResponseBodyBytes);
        Assert.AreEqual(none.Snapshot.StatusCode, on.Snapshot.StatusCode);
    }

    [TestMethod]
    [Timeout(90_000)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Http2Client_ToHttp3Origin_BandwidthIsShaped_AndBytesArriveProgressively(bool warm)
    {
        RequireQuic();
        await using var origin = StartOrigin(withContentLength: true);
        var run = await Fetch(
            origin, warm, new NetworkThrottleProfile("slow", latencyMs: 0, bytesPerSecond: 60_000), h2: true, "/big");

        Assert.AreEqual(BigBytes, run.Bytes.Length);
        // 150 KiB at 60 kB/s is about 2.5 s; a burst would take a few milliseconds.
        Assert.IsTrue(run.Elapsed >= TimeSpan.FromSeconds(1.8), $"Transfer finished in {run.Elapsed}.");
        Assert.IsTrue(
            run.Elapsed - run.At20Percent >= TimeSpan.FromSeconds(1.2),
            $"Bytes arrived as a block: 20% at {run.At20Percent}, done at {run.Elapsed}.");
    }

    [TestMethod]
    [Timeout(90_000)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Http1Client_ToHttp3Origin_BandwidthIsShaped_AndKeepsFidelity(bool warm)
    {
        RequireQuic();
        await using var origin = StartOrigin(withContentLength: true);
        var none = await Fetch(origin, warm, profile: null, h2: false, "/big");
        var on = await Fetch(
            origin, warm, new NetworkThrottleProfile("slow", latencyMs: 0, bytesPerSecond: 80_000), h2: false, "/big");

        CollectionAssert.AreEqual(none.Bytes, on.Bytes);
        CollectionAssert.AreEqual(
            none.Headers,
            on.Headers,
            $"THROTTLED:\n{string.Join("\n", on.Headers)}\nNONE:\n{string.Join("\n", none.Headers)}");
        Assert.IsTrue(on.Elapsed >= TimeSpan.FromSeconds(1.4), $"150 KiB at 80 kB/s took {on.Elapsed}.");
    }

    [TestMethod]
    [Timeout(90_000)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(true, false)]
    public async Task ManySequentialRequests_UnderAThrottle_AllComplete(bool warm, bool h2)
    {
        RequireQuic();
        await using var origin = StartOrigin(withContentLength: true);
        using var interception = NewInterception(new NetworkThrottleProfile("fast", latencyMs: 10, bytesPerSecond: 20_000_000));
        await interception.StartAsync(IPAddress.Loopback, 0);
        try
        {
            RouteToHttp3(interception, origin, warm);
            using var http = ProxyClient(interception.BoundPort, h2);
            for (var i = 0; i < 12; i++)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var bytes = await http.GetByteArrayAsync($"https://localhost:{origin.Port}/small?i={i}", cts.Token);
                Assert.AreEqual(30 * 1024, bytes.Length, $"request {i}");
            }
        }
        finally
        {
            interception.EnsureShutdown();
        }
    }

    [TestMethod]
    [Timeout(90_000)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Http2Client_ToHttp3Origin_UnknownLength_StillDeliversTheExactBytes(bool warm)
    {
        RequireQuic();
        await using var origin = StartOrigin(withContentLength: false);
        var none = await Fetch(origin, warm, profile: null, h2: true, "/big");
        var on = await Fetch(
            origin, warm, new NetworkThrottleProfile("fast", latencyMs: 20, bytesPerSecond: 8_000_000), h2: true, "/big");

        Assert.AreEqual(BigBytes, on.Bytes.Length);
        CollectionAssert.AreEqual(none.Bytes, on.Bytes);
    }

    private static QuicHttp3OriginServer StartOrigin(bool withContentLength)
    {
        var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            var size = req.Path.StartsWith("/small", StringComparison.Ordinal) ? 30 * 1024 : BigBytes;
            var body = new byte[size];
            Array.Fill(body, (byte)'Z');
            var headers = new List<(string, string)> { ("set-cookie", "a=1"), ("set-cookie", "b=2"), ("x-origin", "gate") };
            if (withContentLength)
            {
                headers.Add(("content-length", size.ToString()));
            }

            return Task.FromResult(new QuicHttp3Response(
                200, string.Empty, body, extraHeaders: headers, contentType: "application/octet-stream", dataFrameSize: 16 * 1024));
        });
        return origin;
    }

    private static InterceptionService NewInterception(NetworkThrottleProfile? profile) =>
        new(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            DecryptHttps = true,
            IgnoreServerCertificateErrors = true,
            Capturing = true,
            ThrottleProfile = profile,
        };

    /// <summary>Routes new CONNECTs to the HTTP/3 origin, the way Alt-Svc discovery would.</summary>
    private static void RouteToHttp3(InterceptionService interception, QuicHttp3OriginServer origin, bool warm)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var proxy = (ProxyServer)typeof(InterceptionService).GetField("_proxy", flags)!.GetValue(interception)!;
        var endpoint = (ExplicitProxyEndPoint)typeof(InterceptionService).GetField("_endPoint", flags)!.GetValue(interception)!;

        proxy.EnableHttp2 = true;
        proxy.EnableHttp3 = true;
        proxy.EnableHttpsSvcbDnsDiscovery = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = true;
            return Task.CompletedTask;
        };

        if (warm)
        {
            proxy.Http3OriginCapabilityCache.Set($"localhost:{origin.Port}", int.MinValue, TimeSpan.FromMinutes(5), targetName: null);
            proxy.Http3WarmOrigins.Mark("localhost", origin.Port);
            proxy.BeforeRequest += (_, args) =>
            {
                args.UpstreamHttpProtocol = UpstreamHttpProtocol.Auto;
                return Task.CompletedTask;
            };
        }
        else
        {
            endpoint.BeforeTunnelConnectRequest += (_, e) =>
            {
                e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
                e.AllowHttpProtocolTranslation = true;
                return Task.CompletedTask;
            };
        }
    }

    private static HttpClient ProxyClient(int port, bool h2)
    {
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.None,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        if (h2)
        {
            client.DefaultRequestVersion = new Version(2, 0);
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }

        return client;
    }

    private static async Task<(string[] Headers, byte[] Bytes, SessionSnapshot Snapshot, TimeSpan Elapsed, TimeSpan At20Percent)> Fetch(
        QuicHttp3OriginServer origin, bool warm, NetworkThrottleProfile? profile, bool h2, string path)
    {
        using var interception = NewInterception(profile);
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
            RouteToHttp3(interception, origin, warm);
            using var http = ProxyClient(interception.BoundPort, h2);

            // Warm the CONNECT tunnel and the origin connection so handshakes are not measured.
            using (var warmup = await http.GetAsync($"https://localhost:{origin.Port}/small?warm"))
            {
                Assert.AreEqual(HttpStatusCode.OK, warmup.StatusCode);
                await warmup.Content.ReadAsByteArrayAsync();
            }

            var watch = Stopwatch.StartNew();
            using var response = await http.GetAsync(
                $"https://localhost:{origin.Port}{path}?run", HttpCompletionOption.ResponseHeadersRead);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            if (h2)
            {
                Assert.AreEqual(new Version(2, 0), response.Version, "The client must really be on HTTP/2.");
            }

            await using var body = await response.Content.ReadAsStreamAsync();
            using var all = new MemoryStream();
            var buffer = new byte[8 * 1024];
            var at20 = TimeSpan.Zero;
            int read;
            while ((read = await body.ReadAsync(buffer)) > 0)
            {
                all.Write(buffer, 0, read);
                if (at20 == TimeSpan.Zero && all.Length >= BigBytes / 5)
                {
                    at20 = watch.Elapsed;
                }
            }

            watch.Stop();
            SessionSnapshot snapshot;
            lock (snapshots)
            {
                snapshot = snapshots.Last(s => s.Url.Contains("?run", StringComparison.Ordinal));
            }

            var headers = response.Headers
                .Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key.ToLowerInvariant()}: {v}"))
                .Where(h => !h.StartsWith("date:", StringComparison.Ordinal)
                            && !h.StartsWith("connection:", StringComparison.Ordinal)
                            && !h.StartsWith("proxy-connection:", StringComparison.Ordinal))
                .OrderBy(h => h, StringComparer.Ordinal)
                .ToArray();
            return (headers, all.ToArray(), snapshot, watch.Elapsed, at20);
        }
        finally
        {
            interception.EnsureShutdown();
        }
    }
}
