using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy.Headers</c> suite: unknown extended-CONNECT <c>:protocol</c>
///     tokens must 501 (not forward), and origin EnableConnectProtocol=0 refuses websocket.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyHeadersUnknownProtocolFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: an unknown <c>:protocol</c> is forwarded to the origin instead of 501.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ExtendedConnect_UnknownProtocol_Returns501()
    {
        var originContacted = false;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync(enableConnectProtocol: true);
            try
            {
                _ = await conn.ReadRequestAsync();
                originContacted = true;
            }
            catch { /* torn down */ }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "CONNECT"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/x"),
             (":protocol", "not-a-real-protocol")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false);

        var (headers, _, _) = await raw.Connection.ReadHeadersOrRstAsync(1);
        Assert.IsNotNull(headers, "Unknown :protocol must get a HEADERS reply.");
        Assert.AreEqual("501", headers!.Single(h => h.Name == ":status").Value);
        Assert.IsFalse(originContacted, "Unknown :protocol must not reach the origin.");
    }

    /// <summary>
    ///     Bug class: websocket CONNECT is forwarded when the origin omitted SETTINGS_ENABLE_CONNECT_PROTOCOL.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ExtendedConnect_OriginDidNotAdvertise_Returns5xxOrRst()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            // Advertise SETTINGS without EnableConnectProtocol.
            await conn.SendInitialSettingsAsync(enableConnectProtocol: false);
            try { while (true) await conn.ReadFrameAsync(); } catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "CONNECT"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/ws"),
             (":protocol", "websocket")],
            [("sec-websocket-version", "13"), ("sec-websocket-key", "dGhlIHNhbXBsZSBub25jZQ==")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false);

        var (headers, _, error) = await raw.Connection.ReadHeadersOrRstAsync(1);
        if (headers is not null)
        {
            var status = headers.Single(h => h.Name == ":status").Value;
            Assert.IsTrue(status is "501" or "502" or "503",
                $"Origin without ENABLE_CONNECT_PROTOCOL must refuse; got {status}");
        }
        else
        {
            Assert.AreNotEqual(Http2ErrorCode.NoError, error,
                "Must RST/GOAWAY when origin omitted ENABLE_CONNECT_PROTOCOL.");
        }
    }
}
