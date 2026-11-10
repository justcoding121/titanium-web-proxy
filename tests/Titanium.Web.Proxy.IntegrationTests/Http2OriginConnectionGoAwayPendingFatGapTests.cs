using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2OriginConnection</c> suite: GOAWAY after the first stream is admitted
///     must fail later streams (and surface 5xx on the H1↔H2 bridge), and origin PING
///     without ACK must be answered.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2OriginConnectionGoAwayPendingFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static (ProxyServer Proxy, ExplicitProxyEndPoint Ep) H1ToH2(TestSuite suite)
    {
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var ep = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        ep.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        return (proxy, ep);
    }

    /// <summary>
    ///     Bug class: after origin GOAWAY(last-stream-id=N), a later H1 request still hangs
    ///     on a stream the origin already abandoned.
    /// </summary>
    [TestMethod]
    [Timeout(45_000)]
    public async Task H1ToH2_GoAwayAfterFirst_LaterRequestIs5xx()
    {
        var connections = 0;
        var firstDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            var n = Interlocked.Increment(ref connections);
            await conn.SendInitialSettingsAsync();
            if (n > 1)
            {
                // Pool may dial a fresh member after GOAWAY; refuse every stream on it.
                var refuse = new byte[8];
                BinaryPrimitives.WriteInt32BigEndian(refuse.AsSpan(0, 4), 0);
                BinaryPrimitives.WriteInt32BigEndian(refuse.AsSpan(4, 4), (int)Http2ErrorCode.EnhanceYourCalm);
                await conn.WriteFrameAsync(Http2FrameType.GoAway, 0, 0, refuse);
                try { while (true) await conn.ReadFrameAsync(); } catch { }
                return;
            }

            var (streamId, _, _) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders([(":status", "204")], []);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            firstDone.TrySetResult();

            // GOAWAY so later streams on this connection are abandoned (and the pool member is unusable).
            var payload = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), streamId);
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4, 4), (int)Http2ErrorCode.NoError);
            await conn.WriteFrameAsync(Http2FrameType.GoAway, 0, 0, payload);
            try { while (true) await conn.ReadFrameAsync(); } catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);

        using (var first = await client.GetAsync(origin.Url + "a"))
            Assert.AreEqual(HttpStatusCode.NoContent, first.StatusCode);

        await firstDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150);

        using var second = await client.GetAsync(origin.Url + "b");
        Assert.IsTrue((int)second.StatusCode >= 500,
            $"Post-GOAWAY stream must surface as 5xx; got {(int)second.StatusCode}");
    }

    /// <summary>
    ///     Bug class: origin PING without ACK is ignored (no PING ACK), stalling peers that probe liveness.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_OriginPing_IsAcked()
    {
        var pingAcked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders([(":status", "204")], []);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);

            // Probe liveness only after the bridge has a live origin reader on this connection.
            var ping = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(ping, 0x1122334455667788UL);
            await conn.WriteFrameAsync(Http2FrameType.Ping, 0, 0, ping);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                var frame = await conn.ReadFrameAsync()
                    .WaitAsync(deadline - DateTime.UtcNow);
                if (frame.Type == Http2FrameType.Ping && (frame.Flags & Http2FrameFlag.Ack) != 0)
                {
                    pingAcked.TrySetResult();
                    break;
                }
            }
        });

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        // Drive a request so the origin connection is established and the reader is running.
        _ = await client.GetAsync(origin.Url + "ping");

        await pingAcked.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
