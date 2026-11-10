using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2ToHttp11BridgeHandler</c> suite: HTTP/1.1 chunked body (no Content-Length) reaches the H2 client.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2ToHttp11ChunkedResponseFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

  private static (ProxyServer Proxy, ExplicitProxyEndPoint Endpoint) CreateH2ToH1(TestSuite suite)
    {
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        return (proxy, endpoint);
    }

    /// <summary>
    ///     Bug class: chunked upstream body is truncated to zero bytes on the H2 bridge.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_OriginChunkedBody_RoundTrips()
    {
        const string marker = "chunked-wave40-body";
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            ctx.Response.Headers.Remove("Content-Length");
            await ctx.Response.WriteAsync(marker);
        });
        var (_, endpoint) = CreateH2ToH1(suite);

        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/chunk")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (_, headers, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", headers.Single(h => h.Name == ":status").Value);
        var body = endStream ? [] : await ReadDataUntilEndAsync(raw);
        Assert.AreEqual(marker, Encoding.UTF8.GetString(body));
    }

    private static async Task<byte[]> ReadDataUntilEndAsync(Http2RawClient raw)
    {
        using var ms = new System.IO.MemoryStream();
        while (true)
        {
            var frame = await raw.Connection.ReadFrameAsync();
            if (frame.Type == Http2FrameType.Data)
            {
                ms.Write(frame.Payload, 0, frame.Payload.Length);
                if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    return ms.ToArray();
            }
            else if (frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway)
                return ms.ToArray();
        }
    }
}
