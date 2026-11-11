using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy.Headers</c> suite: origin response with forbidden
///     connection-specific header, and origin response trailers carrying a pseudo-header.
/// </summary>
[TestClass]
public class Http2CopyHeadersConnectAuthorityFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task<(Http2FrameType Type, int Error)> ReadRstOrGoAwayAsync(
        Http2RawClient raw, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.RstStream)
                return (frame.Type, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(0, 4)));
            if (frame.Type == Http2FrameType.GoAway)
                return (frame.Type, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
        }

        Assert.Fail("Timed out waiting for RST/GOAWAY.");
        return default;
    }

    /// <summary>
    ///     Bug class: origin response carrying Connection is relayed as-is.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_ResponseConnectionHeader_SoftResetsStream()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders(
                [(":status", "200"), ("connection", "keep-alive"), ("content-length", "0")],
                []);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/conn")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (type, _) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(12));
        Assert.IsTrue(type is Http2FrameType.RstStream or Http2FrameType.GoAway,
            $"Origin Connection response header must terminate exchange; got {type}");
    }

    /// <summary>
    ///     Bug class: origin response trailers with :status are accepted.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_ResponseTrailer_WithStatusPseudo_RstProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders([(":status", "200")], []);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
            var trailers = conn.EncodeHeaders([(":status", "200")], []);
            await conn.WriteHeaderBlockAsync(streamId, trailers, endStream: true);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/trail-status")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(12));
        Assert.IsTrue(type is Http2FrameType.RstStream or Http2FrameType.GoAway, $"got {type}");
        if (type == Http2FrameType.RstStream)
            Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }
}
