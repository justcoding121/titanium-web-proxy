using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http11ToHttp2BridgeHandler</c> suite: ambiguous Content-Length framing,
///     Via loop → 508, and BeforeResponse status rewrite on the bridge.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http11ToHttp2AmbiguousFramingFatGapTests
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
    ///     Bug class: conflicting Content-Length on the H1→H2 bridge is forwarded to the
    ///     H2 origin instead of a client error.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_ConflictingContentLength_ReturnsClientError()
    {
        var originContacted = false;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try
            {
                _ = await conn.ReadRequestAsync();
                originContacted = true;
            }
            catch { /* torn down */ }
        });

        using var suite = new TestSuite(sharedServer);
        var (proxy, ep) = H1ToH2(suite);
        proxy.PolicyModes = ProxyPolicyModes.AllEnforce;

        var target = new Uri(origin.Url);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, ep.Port);

        // CONNECT then ambiguous POST on the MITM socket.
        var connect =
            $"CONNECT {target.Host}:{target.Port} HTTP/1.1\r\n" +
            $"Host: {target.Host}:{target.Port}\r\n\r\n";
        await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes(connect));
        using var reader = new StreamReader(tcp.GetStream(), Encoding.ASCII, leaveOpen: true);
        var connectStatus = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.IsNotNull(connectStatus);
        Assert.IsTrue(connectStatus!.Contains("200", StringComparison.Ordinal),
            $"CONNECT must succeed; got '{connectStatus}'");
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

        using var ssl = new System.Net.Security.SslStream(tcp.GetStream(), leaveInnerStreamOpen: true,
            (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(target.Host);

        var post =
            "POST /conflict HTTP/1.1\r\n" +
            $"Host: {target.Host}:{target.Port}\r\n" +
            "Content-Length: 5\r\n" +
            "Content-Length: 10\r\n" +
            "Connection: close\r\n\r\n" +
            "helloXXXXX";
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(post));

        using var sslReader = new StreamReader(ssl, Encoding.ASCII);
        var status = await sslReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(status);
        Assert.IsTrue(status!.Contains("400") || status.Contains("502") || status.Contains("431"),
            $"Ambiguous framing must fail on the H1→H2 bridge; got '{status}'");
        Assert.IsFalse(originContacted, "Ambiguous framing must not reach the H2 origin.");
    }

    /// <summary>
    ///     Bug class: Via loop on the H1→H2 bridge is ignored and the request is forwarded.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_ViaLoop_Returns508()
    {
        var originContacted = false;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try
            {
                _ = await conn.ReadRequestAsync();
                originContacted = true;
            }
            catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        proxy.ViaHeaderPseudonym = "h1h2-via-loop";

        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url + "via");
        request.Headers.TryAddWithoutValidation("Via", "1.1 h1h2-via-loop");
        using var response = await client.SendAsync(request);
        Assert.AreEqual((HttpStatusCode)508, response.StatusCode);
        Assert.IsFalse(originContacted);
    }

    /// <summary>
    ///     Bug class: BeforeResponse status rewrite on H1→H2 is ignored by the client reply.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_BeforeResponse_RewritesStatus()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var payload = Encoding.UTF8.GetBytes("orig");
                var resp = conn.EncodeHeaders(
                    [(":status", "200"), ("content-length", payload.Length.ToString())], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        proxy.BeforeResponse += (_, e) =>
        {
            e.HttpClient.Response.StatusCode = 203;
            e.HttpClient.Response.StatusDescription = "Non-Authoritative Information";
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url + "rewrite");
        Assert.AreEqual(HttpStatusCode.NonAuthoritativeInformation, response.StatusCode);
        Assert.AreEqual("orig", await response.Content.ReadAsStringAsync());
    }
}
