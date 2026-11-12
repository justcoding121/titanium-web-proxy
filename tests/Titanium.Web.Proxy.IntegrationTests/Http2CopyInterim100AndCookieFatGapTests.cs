using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy.Headers</c> suite: origin 100 Continue then 200,
///     Set-Cookie relay, and Cookie request forwarding.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyInterim100AndCookieFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: informational 100 from origin is dropped and the final 200 never arrives.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_Interim100_Then200_ReachesClient()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var interim = conn.EncodeHeaders([(":status", "100")], []);
                await conn.WriteHeaderBlockAsync(streamId, interim, endStream: false);
                var final = conn.EncodeHeaders(
                    [(":status", "200"), ("content-length", "2")], []);
                await conn.WriteHeaderBlockAsync(streamId, final, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                    Encoding.ASCII.GetBytes("ok"));
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "POST"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/cont"),
             ("content-length", "1")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Data, 1, Http2FrameFlag.EndStream, [(byte)'x']);

        string? firstStatus = null;
        string? finalStatus = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && finalStatus is null)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.Type is not (Http2FrameType.Headers or Http2FrameType.Continuation))
                continue;
            var headers = raw.Connection.DecodeHeaders(frame.Payload);
            var status = headers.FirstOrDefault(h => h.Name == ":status").Value;
            if (string.IsNullOrEmpty(status))
                continue;
            if (firstStatus is null)
                firstStatus = status;
            else
                finalStatus = status;
        }

        Assert.AreEqual("100", firstStatus);
        Assert.AreEqual("200", finalStatus);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_SetCookie_ReachesClient()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var resp = conn.EncodeHeaders(
                    [(":status", "204"), ("set-cookie", "gap=1; Path=/")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/ck")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type is not (Http2FrameType.Headers or Http2FrameType.Continuation))
                continue;
            var cookies = raw.Connection.DecodeHeaders(frame.Payload)
                .Where(h => h.Name == "set-cookie")
                .Select(h => h.Value)
                .ToList();
            if (cookies.Count == 0)
                continue;
            Assert.IsTrue(cookies.Any(c => c.Contains("gap=1", StringComparison.Ordinal)), string.Join(" | ", cookies));
            return;
        }

        Assert.Fail("No Set-Cookie in response.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CookieHeader_ReachesOrigin()
    {
        string? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                seen = headers.FirstOrDefault(h => h.Name == "cookie").Value;
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/send-ck")],
            [("cookie", "sent=1")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type is Http2FrameType.Headers or Http2FrameType.Continuation)
            {
                foreach (var h in raw.Connection.DecodeHeaders(frame.Payload))
                {
                    if (h.Name == ":status" && h.Value == "204")
                    {
                        Assert.AreEqual("sent=1", seen);
                        return;
                    }
                }
            }
        }

        Assert.Fail("Timed out waiting for 204.");
    }
}
