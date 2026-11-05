using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>Opaque HTTP/2 WebTransport. Off by default. On, capsule DATA is copied.</summary>
[DoNotParallelize]
[TestClass]
public class WebTransportBridgeTests
{
    private static TestServer sharedServer = null!;
    private static readonly Encoding Ascii = Encoding.ASCII;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebTransport_OptInOff_Returns501()
    {
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithWebTransportAsync();
            try { while (true) await originConn.ReadFrameAsync(); } catch { /* expected */ }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        using var rawClient = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port);
        var headers = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "CONNECT"), (":protocol", "webtransport"), (":scheme", "https"),
                (":authority", $"localhost:{rawOrigin.Port}"), (":path", "/app")
            },
            new[] { ("capsule-protocol", "?1") });
        await rawClient.Connection.WriteHeaderBlockAsync(1, headers, false);
        var (responseHeaders, _, _) = await rawClient.Connection.ReadHeadersOrRstAsync(1);
        Assert.IsNotNull(responseHeaders);
        Assert.AreEqual("501", responseHeaders!.Single(h => h.Name == ":status").Value);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebTransport_OptIn_H2ToH2_EchoesCapsuleBytes()
    {
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithWebTransportAsync();
            var (reqStreamId, reqHeaders, _) = await originConn.ReadHeaderBlockAsync();
            Assert.AreEqual("webtransport", reqHeaders.Single(h => h.Name == ":protocol").Value);
            var resp200 = originConn.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await originConn.WriteHeaderBlockAsync(reqStreamId, resp200, false);
            while (true)
            {
                var frame = await originConn.ReadFrameAsync();
                if (frame.Type == Http2FrameType.Data && frame.StreamId == reqStreamId)
                {
                    if (frame.Payload.Length > 0)
                        await originConn.WriteFrameAsync(Http2FrameType.Data, reqStreamId, 0, frame.Payload);
                    if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                        break;
                }
                else if (frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway)
                {
                    break;
                }
            }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.ProxyEndPoints[0].AcceptWebTransport = true;

        using var rawClient = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port);
        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "CONNECT"), (":protocol", "webtransport"), (":scheme", "https"),
                (":authority", $"localhost:{rawOrigin.Port}"), (":path", "/app")
            },
            new[] { ("capsule-protocol", "?1") });
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, false);
        var (_, responseHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);

        var payload = Ascii.GetBytes("wt-capsule");
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Data, 1, 0, payload);
        var received = new List<byte>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (received.Count < payload.Length && !cts.IsCancellationRequested)
        {
            var frame = await rawClient.Connection.ReadFrameAsync();
            if (frame.Type == Http2FrameType.Data && frame.StreamId == 1)
                received.AddRange(frame.Payload);
        }

        Assert.IsFalse(cts.IsCancellationRequested);
        CollectionAssert.AreEqual(payload, received.ToArray());
    }
}
