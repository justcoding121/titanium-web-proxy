using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy.cs</c> RFC 8441 DATA arms: native h2↔h2 tunnel relay and
///     STREAM_CLOSED when DATA arrives on a half-closed extended CONNECT stream.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyExtendedConnectDataProtocolFatGapTests
{
    private static readonly Encoding Ascii = Encoding.ASCII;
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: after the client half-closes an extended CONNECT stream, extra DATA must RST
    ///     STREAM_CLOSED (not ENHANCE_YOUR_CALM or silent drop).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ExtendedConnect_DataAfterRequestHalfClosed_RstStreamClosed()
    {
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithConnectProtocolAsync();
            var (streamId, _, _) = await originConn.ReadHeaderBlockAsync();
            var resp = originConn.EncodeHeaders([(":status", "200")], []);
            await originConn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
            try { while (true) await originConn.ReadFrameAsync(); } catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        using var rawClient = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port);

        var connect = rawClient.Connection.EncodeHeaders(
            [
                (":method", "CONNECT"), (":protocol", "websocket"), (":scheme", "https"),
                (":authority", $"localhost:{rawOrigin.Port}"), (":path", "/ws"),
            ],
            [("sec-websocket-version", "13")]);
        await rawClient.Connection.WriteHeaderBlockAsync(1, connect, endStream: false);

        var (_, responseHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);

        await rawClient.Connection.WriteFrameAsync(
            Http2FrameType.Data, 1, Http2FrameFlag.EndStream, Ascii.GetBytes("fin"));

        await rawClient.Connection.WriteFrameAsync(
            Http2FrameType.Data, 1, 0, Ascii.GetBytes("late"));

        var reset = await ReadRstOnStreamAsync(rawClient, 1, TimeSpan.FromSeconds(8));
        Assert.AreEqual(Http2ErrorCode.StreamClosed, reset,
            "DATA on a half-closed extended CONNECT stream must RST STREAM_CLOSED.");
    }

    /// <summary>
    ///     Bug class: bidirectional DATA on an established h2↔h2 websocket tunnel is not relayed.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ExtendedConnect_H2ToH2_EchoesTunnelPayload()
    {
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithConnectProtocolAsync();
            var (streamId, _, _) = await originConn.ReadHeaderBlockAsync();
            await originConn.WriteHeaderBlockAsync(streamId,
                originConn.EncodeHeaders([(":status", "200")], []), endStream: false);

            while (true)
            {
                var frame = await originConn.ReadFrameAsync();
                if (frame.Type == Http2FrameType.Data && frame.StreamId == streamId && frame.Payload.Length > 0)
                    await originConn.WriteFrameAsync(Http2FrameType.Data, streamId, 0, frame.Payload);
                if (frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway)
                    break;
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        using var rawClient = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port);
        var connect = rawClient.Connection.EncodeHeaders(
            [
                (":method", "CONNECT"), (":protocol", "websocket"), (":scheme", "https"),
                (":authority", $"localhost:{rawOrigin.Port}"), (":path", "/ws"),
            ],
            []);
        await rawClient.Connection.WriteHeaderBlockAsync(1, connect, endStream: false);
        await rawClient.Connection.ReadHeaderBlockAsync();

        var payload = Ascii.GetBytes("tunnel-bytes");
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Data, 1, 0, payload);

        var echoed = await ReadDataPayloadAsync(rawClient, 1, TimeSpan.FromSeconds(8));
        CollectionAssert.AreEqual(payload, echoed);
    }

    private static async Task<Http2ErrorCode> ReadRstOnStreamAsync(
        Http2RawClient raw, int streamId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.RstStream && frame.StreamId == streamId
                && frame.Payload.Length >= 4)
            {
                var code = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(0, 4));
                return (Http2ErrorCode)code;
            }
        }

        Assert.Fail("Timed out waiting for RST_STREAM.");
        return default;
    }

    private static async Task<byte[]> ReadDataPayloadAsync(
        Http2RawClient raw, int streamId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.Data && frame.StreamId == streamId && frame.Payload.Length > 0)
                return frame.Payload;
        }

        Assert.Fail("Timed out waiting for DATA echo.");
        return [];
    }
}
