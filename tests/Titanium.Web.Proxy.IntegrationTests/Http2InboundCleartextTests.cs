using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class Http2InboundCleartextTests
{
    private static ProxyServer CreateCleartextReverseProxy()
    {
        var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        proxy.EnableHttp2 = true;
        proxy.AddEndPoint(new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false)
        {
            GenericCertificateName = "localhost"
        });
        proxy.Start();
        return proxy;
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Inbound_H2c_To_Cleartext_Http2_Origin_Succeeds()
    {
        using var rawServer = Http2RawOriginServer.CreateCleartext();
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();
            var headers = connection.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, headers, endStream: false);
            await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, "h2c-in"u8.ToArray());
        });

        using var proxy = CreateCleartextReverseProxy();
        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        endpoint.ForwardHost = "127.0.0.1";
        endpoint.ForwardPort = rawServer.Port;
        endpoint.ForwardCleartext = true;
        endpoint.BeforeHttpAuthenticate += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            return Task.CompletedTask;
        };

        using var rawClient = await Http2RawClient.ConnectCleartextDirectAsync(endpoint.Port);
        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "http"), (":authority", "localhost"), (":path", "/") },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        var (streamId, responseHeaders, endStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual(1, streamId);
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);
        if (!endStream)
        {
            var data = await ReadNextDataFrameAsync(rawClient.Connection);
            Assert.AreEqual("h2c-in", System.Text.Encoding.UTF8.GetString(data));
        }
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Inbound_H2c_To_Tls_Http2_Origin_Succeeds()
    {
        using var rawServer = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();
            var headers = connection.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, headers, endStream: false);
            await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, "h2c-tls"u8.ToArray());
        });

        using var proxy = CreateCleartextReverseProxy();
        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        endpoint.ForwardHost = "127.0.0.1";
        endpoint.ForwardPort = rawServer.Port;
        endpoint.ForwardCleartext = false;
        endpoint.BeforeHttpAuthenticate += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            return Task.CompletedTask;
        };

        using var rawClient = await Http2RawClient.ConnectCleartextDirectAsync(endpoint.Port);
        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "http"), (":authority", "localhost"), (":path", "/") },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        var (streamId, responseHeaders, endStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual(1, streamId);
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);
        if (!endStream)
        {
            var data = await ReadNextDataFrameAsync(rawClient.Connection);
            Assert.AreEqual("h2c-tls", System.Text.Encoding.UTF8.GetString(data));
        }
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Inbound_H2c_To_Http1_Origin_Via_Bridge_Succeeds()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            context.Response.StatusCode = 200;
            return context.Response.WriteAsync("h2c-to-h1");
        });

        using var proxy = CreateCleartextReverseProxy();
        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        endpoint.ForwardHost = "127.0.0.1";
        endpoint.ForwardPort = server.HttpListeningPort;
        endpoint.ForwardCleartext = true;
        endpoint.BeforeHttpAuthenticate += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var rawClient = await Http2RawClient.ConnectCleartextDirectAsync(endpoint.Port);
        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "http"), (":authority", "localhost"), (":path", "/") },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        var (_, responseHeaders, endStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);
        if (!endStream)
        {
            var data = await ReadNextDataFrameAsync(rawClient.Connection);
            Assert.AreEqual("h2c-to-h1", System.Text.Encoding.UTF8.GetString(data));
        }
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Inbound_H2c_Fails_When_EnableHttp2_False()
    {
        using var rawServer = Http2RawOriginServer.CreateCleartext();
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();
            var headers = connection.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, headers, true);
        });

        using var proxy = CreateCleartextReverseProxy();
        proxy.EnableHttp2 = false;
        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        endpoint.ForwardHost = "127.0.0.1";
        endpoint.ForwardPort = rawServer.Port;
        endpoint.ForwardCleartext = true;

        using var rawClient = await Http2RawClient.ConnectCleartextDirectAsync(endpoint.Port);
        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "http"), (":authority", "localhost"), (":path", "/") },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        // Proxy closes the socket after rejecting the preface; the client may see EOF or a reset.
        Exception? caught = null;
        try
        {
            await rawClient.Connection.ReadHeaderBlockAsync();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.IsNotNull(caught, "Expected the cleartext HTTP/2 client read to fail when EnableHttp2 is false.");
        Assert.IsTrue(caught is System.IO.EndOfStreamException or System.IO.IOException,
            $"Expected EndOfStreamException or IOException, got {caught!.GetType().FullName}: {caught.Message}");
    }

    private static async Task<byte[]> ReadNextDataFrameAsync(Http2RawFrame.Connection connection)
    {
        while (true)
        {
            var frame = await connection.ReadFrameAsync();
            if (frame.Type == Http2FrameType.Data)
                return frame.Payload;
        }
    }
}
