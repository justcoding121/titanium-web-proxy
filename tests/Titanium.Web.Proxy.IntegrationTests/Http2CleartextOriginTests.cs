using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class Http2CleartextOriginTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Tls_Client_To_Cleartext_Http2_Origin_Succeeds()
    {
        using var rawServer = Http2RawOriginServer.CreateCleartext();
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();
            var headers = connection.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, headers, endStream: false);
            await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, "h2c-ok"u8.ToArray());
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetReverseProxy();
        proxy.EnableHttp2 = true;
        proxy.SupportedSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        endpoint.ForwardHost = "127.0.0.1";
        endpoint.ForwardPort = rawServer.Port;
        endpoint.ForwardCleartext = true;
        endpoint.BeforeSslAuthenticate += (_, e) =>
        {
            e.DecryptSsl = true;
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            return Task.CompletedTask;
        };

        using var rawClient = await Http2RawClient.ConnectDirectAsync(proxy.ProxyEndPoints[0].Port, "localhost");

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/") },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        var (streamId, responseHeaders, endStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual(1, streamId);
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);

        if (!endStream)
        {
            var data = await ReadNextDataFrameAsync(rawClient.Connection);
            Assert.AreEqual("h2c-ok", System.Text.Encoding.UTF8.GetString(data));
        }
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http1_Tls_Client_To_Cleartext_Http2_Origin_Via_Bridge_Succeeds()
    {
        using var rawServer = Http2RawOriginServer.CreateCleartext();
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();
            var headers = connection.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, headers, endStream: false);
            await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, "h1-to-h2c"u8.ToArray());
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetReverseProxy();
        proxy.EnableHttp2 = true;
        proxy.SupportedSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        endpoint.ForwardHost = "127.0.0.1";
        endpoint.ForwardPort = rawServer.Port;
        endpoint.ForwardCleartext = true;
        endpoint.BeforeSslAuthenticate += (_, e) =>
        {
            e.DecryptSsl = true;
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        };
        // Force HTTP/1.1 on the client so the H1→H2 translation bridge is exercised.
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15), DefaultRequestVersion = HttpVersion.Version11, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
        using var response = await client.GetAsync($"https://127.0.0.1:{proxy.ProxyEndPoints[0].Port}/");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("h1-to-h2c", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Plus_ForwardCleartext_Against_Http1_Origin_Fails_Closed()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            context.Response.StatusCode = 200;
            return context.Response.WriteAsync("http1-only");
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.EnableHttp2 = true;
        proxy.SupportedSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        endpoint.ForwardHost = "127.0.0.1";
        endpoint.ForwardPort = server.HttpListeningPort;
        endpoint.ForwardCleartext = true;
        endpoint.BeforeSslAuthenticate += (_, e) =>
        {
            e.DecryptSsl = true;
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            return Task.CompletedTask;
        };

        using var rawClient = await Http2RawClient.ConnectDirectAsync(proxy.ProxyEndPoints[0].Port, "localhost");
        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/") },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        await Assert.ThrowsExceptionAsync<System.IO.EndOfStreamException>(async () =>
        {
            await rawClient.Connection.ReadHeaderBlockAsync();
        });
    }

    private static async Task<byte[]> ReadNextDataFrameAsync(Http2RawFrame.Connection connection)
    {
        while (true)
        {
            var frame = await connection.ReadFrameAsync();
            if (frame.Type == Http2FrameType.Data)
            {
                return frame.Payload;
            }
        }
    }
}
