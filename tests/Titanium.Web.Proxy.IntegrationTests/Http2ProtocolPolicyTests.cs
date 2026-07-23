using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Coverage for the connection-scoped <see cref="UpstreamHttpProtocol" />/<c>AllowHttpProtocolTranslation</c>
///     policy on <see cref="TunnelConnectSessionEventArgs" /> (explicit CONNECT tunnels) and
///     <see cref="BeforeSslAuthenticateEventArgs" /> (transparent endpoints), decoupling which HTTP version
///     the proxy uses toward the origin from which version the client negotiates with the proxy. When a
///     policy would otherwise require translation but <c>AllowHttpProtocolTranslation</c> is left disabled,
///     the mismatch either downgrades the client offer to avoid needing it
///     (<see cref="UpstreamHttpProtocol.Http11" /> without translation) or fails the connection outright with
///     a clear, documented exception; when translation is explicitly enabled, the h2-client-to-HTTP/1.1-origin
///     bridge (see <see cref="Http2ToHttp11BridgeHandler" />) is exercised instead - the HTTP/1.1-client-to-h2
///     origin direction is a later milestone.
/// </summary>
[TestClass]
public class Http2ProtocolPolicyTests
{
    private static X509Certificate2 CreateOriginCertificate()
    {
        using var dummyProxy = new ProxyServer(false, false, false);
        dummyProxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        return dummyProxy.CertificateManager.CreateServerCertificate("localhost").Result;
    }

    [TestMethod]
    [Timeout(15 * 1000)]
    public async Task UpstreamHttpProtocol_Setter_Rejects_Undefined_Enum_Values()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        var proxy = testSuite.GetReverseProxy();
        proxy.EnableHttp2 = true;

        ArgumentOutOfRangeException? caught = null;
        var endpoint = proxy.ProxyEndPoints.OfType<Models.TransparentProxyEndPoint>().First();
        endpoint.ForwardPort = server.HttpsListeningPort;
        endpoint.BeforeSslAuthenticate += (_, e) =>
        {
            try
            {
                e.UpstreamHttpProtocol = (UpstreamHttpProtocol)999;
            }
            catch (ArgumentOutOfRangeException ex)
            {
                caught = ex;
            }

            return Task.CompletedTask;
        };

        try
        {
            using var tcpClient = new System.Net.Sockets.TcpClient();
            await tcpClient.ConnectAsync("localhost", proxy.ProxyEndPoints[0].Port);
            using var sslStream = new SslStream(tcpClient.GetStream(), false,
                (_, certificate, chain, errors) => TestCertificateAuthority.Validate(certificate, errors));
            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.None
            });
        }
        catch
        {
            // irrelevant to this test - only whether the property setter itself validated is checked below.
        }

        Assert.IsNotNull(caught,
            "Setting an undefined UpstreamHttpProtocol value must throw ArgumentOutOfRangeException.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Explicit_Forced_Http11_Without_Translation_Never_Offers_Http2_To_Dual_Alpn_Client()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("h1-forced-ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            return Task.CompletedTask;
        };

        using var tunnel = await Http2RawClient.ConnectTunnelWithAlpnAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort,
            new List<SslApplicationProtocol> { SslApplicationProtocol.Http2, SslApplicationProtocol.Http11 });

        Assert.AreNotEqual(SslApplicationProtocol.Http2, tunnel.NegotiatedApplicationProtocol,
            "UpstreamHttpProtocol.Http11 without translation must never advertise h2 to the client.");

        var requestBytes =
            Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await tunnel.SslStream.WriteAsync(requestBytes, 0, requestBytes.Length);

        using var reader = new StreamReader(tunnel.SslStream, Encoding.ASCII, false, 4096, true);
        var statusLine = await reader.ReadLineAsync();
        Assert.IsTrue(statusLine != null && statusLine.StartsWith("HTTP/1.1 200"),
            $"Expected an HTTP/1.1 200 response, got: '{statusLine}'.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Explicit_Forced_Http11_With_Translation_And_Http2_Only_Client_Succeeds_Via_Bridge()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(async context =>
        {
            context.Response.Headers["X-Origin-Protocol"] = context.Request.Protocol;
            await context.Response.WriteAsync("h2-to-h11-bridge-ok");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        Exception? observedException = null;
        proxy.ExceptionFunc = ex => observedException = ex;

        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/") },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        var (streamId, responseHeaders, endStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual(1, streamId);
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value,
            "The h2 client should see a real translated response from the HTTP/1.1-only origin, not a failure.");
        Assert.AreEqual("HTTP/1.1", responseHeaders.Single(h => h.Name == "x-origin-protocol").Value,
            "The origin must have actually been spoken to over HTTP/1.1 even though the client used h2.");

        var body = new MemoryStream();
        if (!endStream)
        {
            Http2RawFrame.Frame frame;
            do
            {
                frame = await rawClient.Connection.ReadFrameAsync();
                if (frame.Type == Http2FrameType.Data && frame.StreamId == streamId)
                    body.Write(frame.Payload, 0, frame.Payload.Length);
            } while (frame.Type != Http2FrameType.Data || (frame.Flags & Http2FrameFlag.EndStream) == 0);
        }

        Assert.AreEqual("h2-to-h11-bridge-ok", Encoding.ASCII.GetString(body.ToArray()));
        Assert.IsNull(observedException, $"No exception should be raised on a successful bridge: {observedException}");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Explicit_Forced_Http11_With_Translation_And_Http2_Client_Post_With_Body_Succeeds_Via_Bridge()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var receivedBody = await reader.ReadToEndAsync();
            context.Response.Headers["X-Received-Content-Length"] = context.Request.ContentLength.ToString();
            await context.Response.WriteAsync($"echo:{receivedBody}");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        Exception? observedException = null;
        proxy.ExceptionFunc = ex => observedException = ex;

        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort);

        const string requestBody = "hello from an h2 client being bridged to an h1.1-only origin";
        var bodyBytes = Encoding.ASCII.GetBytes(requestBody);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "POST"), (":scheme", "https"), (":authority", "localhost"), (":path", "/") },
            new[] { ("content-length", bodyBytes.Length.ToString()) });
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, false);
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Data, 1, Http2FrameFlag.EndStream, bodyBytes);

        var (streamId, responseHeaders, endStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual(1, streamId);
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);
        Assert.AreEqual(bodyBytes.Length.ToString(),
            responseHeaders.Single(h => h.Name == "x-received-content-length").Value,
            "The HTTP/1.1 origin must have received the whole request body the h2 client sent.");

        var body = new MemoryStream();
        if (!endStream)
        {
            Http2RawFrame.Frame frame;
            do
            {
                frame = await rawClient.Connection.ReadFrameAsync();
                if (frame.Type == Http2FrameType.Data && frame.StreamId == streamId)
                    body.Write(frame.Payload, 0, frame.Payload.Length);
            } while (frame.Type != Http2FrameType.Data || (frame.Flags & Http2FrameFlag.EndStream) == 0);
        }

        Assert.AreEqual($"echo:{requestBody}", Encoding.ASCII.GetString(body.ToArray()));
        Assert.IsNull(observedException, $"No exception should be raised on a successful bridge: {observedException}");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task
        Explicit_Forced_Http11_With_Translation_Concurrent_Http2_Streams_Get_Independent_Http11_Origin_Round_Trips()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(async context =>
        {
            // Both requests deliberately delay by the same amount: if the bridge serialized origin round
            // trips (e.g. by sharing one TcpServerConnection across streams, which HTTP/1.1 cannot
            // multiplex) rather than giving each h2 stream its own independent connection, the two
            // requests would complete roughly 2x this delay apart instead of concurrently.
            await Task.Delay(400);
            await context.Response.WriteAsync($"response-for-{context.Request.Path}");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        Exception? observedException = null;
        proxy.ExceptionFunc = ex => observedException = ex;

        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var firstRequestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/first") },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, firstRequestHeaders, true);

        var secondRequestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/second") },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(3, secondRequestHeaders, true);

        var pendingStatus = new Dictionary<int, string>();
        var pendingBody = new Dictionary<int, MemoryStream>();
        var finishedStreams = new HashSet<int>();

        while (finishedStreams.Count < 2)
        {
            var frame = await rawClient.Connection.ReadFrameAsync();
            if (frame.Type == Http2FrameType.Headers)
            {
                var headers = rawClient.Connection.DecodeHeaders(frame.Payload);
                pendingStatus[frame.StreamId] = headers.Single(h => h.Name == ":status").Value;
                pendingBody[frame.StreamId] = new MemoryStream();
                if ((frame.Flags & Http2FrameFlag.EndStream) != 0) finishedStreams.Add(frame.StreamId);
            }
            else if (frame.Type == Http2FrameType.Data)
            {
                pendingBody[frame.StreamId].Write(frame.Payload, 0, frame.Payload.Length);
                if ((frame.Flags & Http2FrameFlag.EndStream) != 0) finishedStreams.Add(frame.StreamId);
            }
        }

        stopwatch.Stop();

        Assert.AreEqual("200", pendingStatus[1]);
        Assert.AreEqual("200", pendingStatus[3]);
        Assert.AreEqual("response-for-/first", Encoding.ASCII.GetString(pendingBody[1].ToArray()));
        Assert.AreEqual("response-for-/second", Encoding.ASCII.GetString(pendingBody[3].ToArray()));
        Assert.IsTrue(stopwatch.ElapsedMilliseconds < 750,
            "Two concurrent h2 streams bridged to an HTTP/1.1-only origin should get independent, concurrent " +
            $"origin round trips rather than being serialized onto one shared connection; took {stopwatch.ElapsedMilliseconds}ms.");
        Assert.IsNull(observedException, $"No exception should be raised on a successful bridge: {observedException}");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Explicit_Forced_Http2_To_Http2_Origin_With_Http2_Client_Succeeds()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();
            var headers = connection.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, headers, true);
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            return Task.CompletedTask;
        };

        using var rawClient =
            await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost", rawServer.Port);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/") },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        var (_, responseHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Explicit_Forced_Http2_To_Http11_Only_Origin_Fails_Clearly()
    {
        using var h11Server = new Http11OnlyOriginServer(CreateOriginCertificate());

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        Exception? observedException = null;
        proxy.ExceptionFunc = ex => observedException = ex;

        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            return Task.CompletedTask;
        };

        try
        {
            using var rawClient =
                await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost", h11Server.Port);

            var requestHeaders = rawClient.Connection.EncodeHeaders(
                new[] { (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/") },
                Array.Empty<(string, string)>());
            await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);
            await rawClient.Connection.ReadHeaderBlockAsync();
        }
        catch
        {
            // expected - the tunnel should fail rather than silently downgrade or hang.
        }

        for (var i = 0; i < 50 && observedException == null; i++)
            await Task.Delay(20);

        Assert.IsNotNull(observedException,
            "UpstreamHttpProtocol.Http2 against a non-h2 origin must surface a clear exception.");
        Assert.IsTrue(
            observedException!.Message.Contains("did not negotiate HTTP/2", StringComparison.OrdinalIgnoreCase),
            $"Expected an explicit 'did not negotiate HTTP/2' message, got: '{observedException.Message}'.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Explicit_Forced_Http2_With_Http11_Only_Client_And_No_Translation_Fails_Clearly()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("should-not-be-reached"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        Exception? observedException = null;
        proxy.ExceptionFunc = ex => observedException = ex;

        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            return Task.CompletedTask;
        };

        try
        {
            using var tunnel = await Http2RawClient.ConnectTunnelWithAlpnAsync(proxy.ProxyEndPoints[0].Port,
                "localhost", server.HttpsListeningPort,
                new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 });

            var requestBytes =
                Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
            await tunnel.SslStream.WriteAsync(requestBytes, 0, requestBytes.Length);

            using var reader = new StreamReader(tunnel.SslStream, Encoding.ASCII, false, 4096, true);
            var statusLine = await reader.ReadLineAsync();
            Assert.IsFalse(statusLine != null && statusLine.StartsWith("HTTP/1.1 200"),
                "The request must not succeed when the forced-h2 policy cannot be satisfied.");
        }
        catch
        {
            // also acceptable - the tunnel connection itself may be torn down instead.
        }

        for (var i = 0; i < 50 && observedException == null; i++)
            await Task.Delay(20);

        Assert.IsNotNull(observedException,
            "UpstreamHttpProtocol.Http2 with an h1.1-only client and no translation must surface a clear exception.");
        Assert.IsTrue(
            observedException!.Message.Contains("does not support HTTP/2", StringComparison.OrdinalIgnoreCase),
            $"Expected a 'client does not support HTTP/2' message, got: '{observedException.Message}'.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Transparent_Forced_Http11_Without_Translation_Never_Offers_Http2_To_Dual_Alpn_Client()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            await connection.ReadRequestAsync();
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetReverseProxy();
        proxy.EnableHttp2 = true;

        var endpoint = proxy.ProxyEndPoints.OfType<Models.TransparentProxyEndPoint>().First();
        endpoint.ForwardPort = rawServer.Port;
        endpoint.BeforeSslAuthenticate += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            return Task.CompletedTask;
        };

        var tcpClient = new System.Net.Sockets.TcpClient();
        await tcpClient.ConnectAsync("localhost", proxy.ProxyEndPoints[0].Port);
        using var _ = tcpClient;

        var sslStream = new SslStream(tcpClient.GetStream(), false,
            (_, certificate, chain, errors) => TestCertificateAuthority.Validate(certificate, errors));
        using var __ = sslStream;
        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            ApplicationProtocols =
                new List<SslApplicationProtocol> { SslApplicationProtocol.Http2, SslApplicationProtocol.Http11 },
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.None
        });

        Assert.AreNotEqual(SslApplicationProtocol.Http2, sslStream.NegotiatedApplicationProtocol,
            "UpstreamHttpProtocol.Http11 without translation must never advertise h2 to the client, even " +
            "though the forward target is a real h2 origin.");
    }
}
