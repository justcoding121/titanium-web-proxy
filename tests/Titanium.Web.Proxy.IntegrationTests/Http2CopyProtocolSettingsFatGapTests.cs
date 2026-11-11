using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy.cs</c> protocol suite: SETTINGS_ENABLE_CONNECT_PROTOCOL negotiation
///     on the origin (server) leg — RFC 8441 §3 forbids downgrading 1→0 mid-connection.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyProtocolSettingsFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static byte[] SettingsEntry(ushort id, uint value)
    {
        var payload = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), id);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(2, 4), value);
        return payload;
    }

  /// <summary>
  ///     Bug class: origin SETTINGS_ENABLE_CONNECT_PROTOCOL 1→0 is relayed instead of GOAWAY.
  /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH2_OriginEnableConnectProtocolDowngrade_TearsDownExchange()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsWithConnectProtocolAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();

            await conn.WriteFrameAsync(
                Http2FrameType.Settings, 0, 0,
                SettingsEntry((ushort)Http2SettingsId.EnableConnectProtocol, 0));

            try
            {
                var block = conn.EncodeHeaders([(":status", "200")], [("content-length", "0")]);
                await conn.WriteHeaderBlockAsync(streamId, block, endStream: true);
            }
            catch
            {
                /* proxy already GOAWAY */
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", uri.Authority), (":path", "/")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var frame = await raw.Connection.ReadFrameAsync()
                    .WaitAsync(deadline - DateTime.UtcNow);
                if (frame.Type is Http2FrameType.GoAway or Http2FrameType.RstStream)
                {
                    return;
                }
            }

            Assert.Fail("Mid-connection ENABLE_CONNECT_PROTOCOL downgrade must fail the client leg.");
        }
        catch (EndOfStreamException)
        {
            /* proxy closed the MITM tunnel after GOAWAY on the origin leg */
        }
    }

    /// <summary>
    ///     Bug class: when the origin advertises ENABLE_CONNECT_PROTOCOL=1 the relay records downstream
    ///     capability but extended CONNECT still reaches the origin when both legs support RFC 8441.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH2_OriginAdvertisesConnectProtocol_WebSocketTunnelOpens()
    {
        var forwarded = false;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsWithConnectProtocolAsync();
            var (streamId, headers, _) = await conn.ReadHeaderBlockAsync();
            if (headers.Any(h => h.Name == ":protocol" && h.Value == "websocket"))
                forwarded = true;
            var resp = conn.EncodeHeaders([(":status", "200")], []);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
            try { while (true) await conn.ReadFrameAsync(); } catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        using var raw = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", origin.Port);
        var connect = raw.Connection.EncodeHeaders(
            [
                (":method", "CONNECT"), (":protocol", "websocket"), (":scheme", "https"),
                (":authority", $"localhost:{origin.Port}"), (":path", "/ws"),
            ],
            [("sec-websocket-version", "13"), ("sec-websocket-key", "dGhlIHNhbXBsZSBub25jZQ==")]);
        await raw.Connection.WriteHeaderBlockAsync(1, connect, endStream: false);

        var (_, respHeaders, _) = await raw.Connection.ReadHeaderBlockAsync();
        var status = respHeaders.Find(h => h.Name == ":status");
        Assert.AreNotEqual(default, status, "CONNECT response must include :status.");
        Assert.AreEqual("200", status.Value);
        Assert.IsTrue(forwarded, "Origin must see websocket extended CONNECT after SETTINGS_ENABLE_CONNECT_PROTOCOL=1.");
    }
}
