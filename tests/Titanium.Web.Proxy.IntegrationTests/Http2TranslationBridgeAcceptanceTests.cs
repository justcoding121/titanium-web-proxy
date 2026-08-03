using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.Models;

using Titanium.Web.Proxy.IntegrationTests.Setup;
namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Closes out the protocol-translation acceptance matrix for both bridges added in this delivery
///     (<see cref="Http2ToHttp11BridgeHandler" /> and the HTTP/1.1-to-h2 origin bridge): synthetic
///     short-circuit responses from <c>BeforeRequest</c>, response header mutation from
///     <c>BeforeResponse</c>, and large/streamed bodies in both directions. <see cref="Http2ProtocolPolicyTests" />
///     already covers the basic success/failure matrix and sequential connection reuse; this file focuses on
///     the interception-API and body-size edge cases called out as acceptance gates.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2TranslationBridgeAcceptanceTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
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
    public async Task H2ToH11Bridge_Ok_From_BeforeRequest_Short_Circuits_Without_Contacting_Origin()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var originWasContacted = false;
        server.HandleRequest(context =>
        {
            originWasContacted = true;
            return context.Response.WriteAsync("origin-should-not-be-contacted");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var exceptionCapture = new TestExceptionCapture();
        proxy.Logging.LoggerFactory = exceptionCapture;
        proxy.ApplyLoggingConfiguration();

        proxy.BeforeRequest += (_, e) =>
        {
            e.Ok("synthetic-ok-from-before-request");
            return Task.CompletedTask;
        };

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
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);

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

        Assert.AreEqual("synthetic-ok-from-before-request", Encoding.ASCII.GetString(body.ToArray()));
        Assert.IsFalse(originWasContacted,
            "A synthetic BeforeRequest response must short-circuit the h2-to-HTTP/1.1 bridge before it ever " +
            "opens/uses the HTTP/1.1 origin connection.");
        Assert.IsNull(exceptionCapture.LastException, $"No exception should be raised: {exceptionCapture.LastException}");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H11ToH2Bridge_Ok_From_BeforeRequest_Short_Circuits_Without_Contacting_Origin()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var originWasContacted = false;
        server.HandleRequest(context =>
        {
            originWasContacted = true;
            return context.Response.WriteAsync("origin-should-not-be-contacted");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var exceptionCapture = new TestExceptionCapture();
        proxy.Logging.LoggerFactory = exceptionCapture;
        proxy.ApplyLoggingConfiguration();

        proxy.BeforeRequest += (_, e) =>
        {
            e.Ok("synthetic-ok-from-before-request");
            return Task.CompletedTask;
        };

        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var tunnel = await Http2RawClient.ConnectTunnelWithAlpnAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort,
            new System.Collections.Generic.List<System.Net.Security.SslApplicationProtocol>
                { System.Net.Security.SslApplicationProtocol.Http11 });

        var requestBytes = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await tunnel.SslStream.WriteAsync(requestBytes, 0, requestBytes.Length);

        using var reader = new StreamReader(tunnel.SslStream, Encoding.ASCII, false, 4096, true);
        var statusLine = await reader.ReadLineAsync();
        Assert.IsTrue(statusLine != null && statusLine.StartsWith("HTTP/1.1 200"),
            $"Expected an HTTP/1.1 200 response, got: '{statusLine}'.");

        while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
        {
            // skip headers
        }

        var body = await reader.ReadToEndAsync();
        Assert.AreEqual("synthetic-ok-from-before-request", body);
        Assert.IsFalse(originWasContacted,
            "A synthetic BeforeRequest response must short-circuit the HTTP/1.1-to-h2 bridge before it ever " +
            "opens/uses the h2 origin connection.");
        Assert.IsNull(exceptionCapture.LastException, $"No exception should be raised: {exceptionCapture.LastException}");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H2ToH11Bridge_Large_Response_Body_Is_Relayed_Correctly()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        // Kept comfortably under the default 65535-byte h2 flow-control window (both stream- and connection-
        // level) so this test can validate multi-DATA-frame relay through the bridge without needing the
        // minimal Http2RawClient test double to also implement sending WINDOW_UPDATE frames back to the proxy.
        var expectedBody = new string('x', 50_000);
        server.HandleRequest(context => context.Response.WriteAsync(expectedBody));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var exceptionCapture = new TestExceptionCapture();
        proxy.Logging.LoggerFactory = exceptionCapture;
        proxy.ApplyLoggingConfiguration();

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
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);

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

        Assert.AreEqual(expectedBody, Encoding.ASCII.GetString(body.ToArray()));
        Assert.IsNull(exceptionCapture.LastException, $"No exception should be raised: {exceptionCapture.LastException}");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H11ToH2Bridge_Large_Request_Body_Is_Relayed_Correctly()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var expectedBody = new string('y', 500_000);
        server.HandleRequest(async context =>
        {
            using var bodyReader = new StreamReader(context.Request.Body);
            var receivedBody = await bodyReader.ReadToEndAsync();
            context.Response.Headers["X-Received-Length"] = receivedBody.Length.ToString();
            await context.Response.WriteAsync("large-body-received");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var exceptionCapture = new TestExceptionCapture();
        proxy.Logging.LoggerFactory = exceptionCapture;
        proxy.ApplyLoggingConfiguration();

        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var tunnel = await Http2RawClient.ConnectTunnelWithAlpnAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort,
            new System.Collections.Generic.List<System.Net.Security.SslApplicationProtocol>
                { System.Net.Security.SslApplicationProtocol.Http11 });

        var bodyBytes = Encoding.ASCII.GetBytes(expectedBody);
        var requestText = "POST / HTTP/1.1\r\n" +
                           "Host: localhost\r\n" +
                           $"Content-Length: {bodyBytes.Length}\r\n" +
                           "Connection: close\r\n\r\n";
        var requestBytes = Encoding.ASCII.GetBytes(requestText);
        await tunnel.SslStream.WriteAsync(requestBytes, 0, requestBytes.Length);
        await tunnel.SslStream.WriteAsync(bodyBytes, 0, bodyBytes.Length);

        using var reader = new StreamReader(tunnel.SslStream, Encoding.ASCII, false, 4096, true);
        var statusLine = await reader.ReadLineAsync();
        Assert.IsTrue(statusLine != null && statusLine.StartsWith("HTTP/1.1 200"),
            $"Expected an HTTP/1.1 200 response, got: '{statusLine}'.");

        var sawReceivedLengthHeader = false;
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            if (line.StartsWith("X-Received-Length:", StringComparison.OrdinalIgnoreCase))
            {
                sawReceivedLengthHeader = true;
                Assert.IsTrue(line.Contains(bodyBytes.Length.ToString()),
                    $"The origin must have received the full {bodyBytes.Length}-byte body: '{line}'.");
            }

        Assert.IsTrue(sawReceivedLengthHeader, "Expected to see the X-Received-Length response header.");

        var responseBody = await reader.ReadToEndAsync();
        Assert.AreEqual("large-body-received", responseBody);
        Assert.IsNull(exceptionCapture.LastException, $"No exception should be raised: {exceptionCapture.LastException}");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H11ToH2Bridge_BeforeResponse_Header_Mutation_Is_Applied()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            context.Response.Headers["X-Original"] = "from-origin";
            return context.Response.WriteAsync("h11-to-h2-header-mutation-ok");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var exceptionCapture = new TestExceptionCapture();
        proxy.Logging.LoggerFactory = exceptionCapture;
        proxy.ApplyLoggingConfiguration();

        proxy.BeforeResponse += (_, e) =>
        {
            e.HttpClient.Response.Headers.RemoveHeader("X-Original");
            e.HttpClient.Response.Headers.AddHeader("X-Mutated-By-BeforeResponse", "yes");
            return Task.CompletedTask;
        };

        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var tunnel = await Http2RawClient.ConnectTunnelWithAlpnAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort,
            new System.Collections.Generic.List<System.Net.Security.SslApplicationProtocol>
                { System.Net.Security.SslApplicationProtocol.Http11 });

        var requestBytes = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await tunnel.SslStream.WriteAsync(requestBytes, 0, requestBytes.Length);

        using var reader = new StreamReader(tunnel.SslStream, Encoding.ASCII, false, 4096, true);
        var statusLine = await reader.ReadLineAsync();
        Assert.IsTrue(statusLine != null && statusLine.StartsWith("HTTP/1.1 200"));

        var sawMutatedHeader = false;
        var sawOriginalHeader = false;
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
            if (line.StartsWith("X-Mutated-By-BeforeResponse:", StringComparison.OrdinalIgnoreCase))
                sawMutatedHeader = true;
            if (line.StartsWith("X-Original:", StringComparison.OrdinalIgnoreCase))
                sawOriginalHeader = true;
        }

        Assert.IsTrue(sawMutatedHeader, "BeforeResponse header additions must be relayed to the HTTP/1.1 client.");
        Assert.IsFalse(sawOriginalHeader, "BeforeResponse header removals must be honored before relaying.");

        var body = await reader.ReadToEndAsync();
        Assert.AreEqual("h11-to-h2-header-mutation-ok", body);
        Assert.IsNull(exceptionCapture.LastException, $"No exception should be raised: {exceptionCapture.LastException}");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H2ToH11Bridge_RstStream_From_Client_Mid_Response_Still_Fires_AfterResponse_Exactly_Once()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var responseHeadersSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.HandleRequest(async context =>
        {
            await context.Response.WriteAsync("partial-");
            await context.Response.Body.FlushAsync();
            responseHeadersSent.TrySetResult(true);

            // Keep the HTTP/1.1 response open (never completing it) so the proxy's read of the origin's
            // body is still in flight when the h2 client below resets its stream, exactly matching the
            // "message boundary is no longer recoverable" scenario this bridge must tear down cleanly
            // rather than hang or attempt to pool.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // expected once the proxy closes its side of the origin connection.
            }
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var afterResponseCount = 0;
        proxy.AfterResponse += (_, _) =>
        {
            System.Threading.Interlocked.Increment(ref afterResponseCount);
            return Task.CompletedTask;
        };

        var exceptionCapture = new TestExceptionCapture();
        proxy.Logging.LoggerFactory = exceptionCapture;
        proxy.ApplyLoggingConfiguration();

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

        // Read the response HEADERS and at least the first DATA frame so the origin's response is
        // provably in flight before the stream is reset.
        await rawClient.Connection.ReadHeaderBlockAsync();
        await rawClient.Connection.ReadFrameAsync();
        await responseHeadersSent.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var payload = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(payload, (int)Http2ErrorCode.Cancel);
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.RstStream, 1, 0, payload);

        for (var i = 0; i < 500 && afterResponseCount < 1; i++) await Task.Delay(20);

        Assert.AreEqual(1, afterResponseCount,
            "A client-reset h2 stream bridged to an HTTP/1.1 origin must still get exactly one AfterResponse " +
            "invocation.");
        Assert.IsNull(exceptionCapture.LastException, $"No exception should be raised: {exceptionCapture.LastException}");
    }
}
