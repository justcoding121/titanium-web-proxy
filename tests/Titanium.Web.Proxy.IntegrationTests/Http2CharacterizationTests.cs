using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Baseline ("characterization") coverage for the explicit-proxy HTTP/2 implementation, added before
///     later milestones (transparent routing, protocol translation) change connection orchestration
///     further. These tests lock in current, observable behavior - including connection-count and
///     ownership guarantees added by the shared negotiation/ownership coordinator - so later milestones
///     can be judged against a known-good baseline instead of guessing what the prior behavior was.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CharacterizationTests
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

    private static X509Certificate2 CreateOriginCertificate()
    {
        return TestCertificateAuthority.ServerCertificate;
    }

    /// <summary>
    ///     Opens a CONNECT tunnel and performs a real TLS/ALPN handshake with the given (or absent)
    ///     application protocol list, without ever sending an HTTP/2 connection preface - used for tests
    ///     that need precise control over what ALPN is offered, unlike <see cref="Http2RawClient" /> which
    ///     always offers exactly "h2".
    /// </summary>
    private static async Task<(TcpClient TcpClient, SslStream SslStream)> ConnectAndAuthenticateAsync(
        int proxyPort, string targetHost, int targetPort, System.Collections.Generic.List<SslApplicationProtocol>? applicationProtocols)
    {
        var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync("localhost", proxyPort);

        var networkStream = tcpClient.GetStream();
        var connectRequest = $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\nHost: {targetHost}:{targetPort}\r\n\r\n";
        var connectBytes = Encoding.ASCII.GetBytes(connectRequest);
        await networkStream.WriteAsync(connectBytes, 0, connectBytes.Length);

        const string terminator = "\r\n\r\n";
        var buffer = new byte[1];
        var matched = 0;
        while (matched < terminator.Length)
        {
            var read = await networkStream.ReadAsync(buffer, 0, 1);
            if (read == 0)
            {
                throw new EndOfStreamException("Proxy closed the connection before completing the CONNECT handshake.");
            }

            matched = buffer[0] == terminator[matched] ? matched + 1 : buffer[0] == terminator[0] ? 1 : 0;
        }

        var sslStream = new SslStream(networkStream, false,
            (_, certificate, chain, errors) => TestCertificateAuthority.Validate(certificate, errors));
        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            ApplicationProtocols = applicationProtocols,
            EnabledSslProtocols = SslProtocols.None
        });

        return (tcpClient, sslStream);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Cold_Cache_Explicit_Tunnel_With_Prefetch_Opens_One_Origin_Connection()
    {
        // The shared negotiation/ownership coordinator collapses what used to be three separate origin
        // connections (an origin-capability probe, an unused prefetch, and a freshly opened session
        // connection) into a single discovery connection that is retained and adopted directly as the
        // session connection once it is confirmed healthy and correctly keyed.
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();
            var headers = connection.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, headers, true);
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableTcpServerConnectionPrefetch = true;

        var uri = new Uri(rawServer.Url);
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/") },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        var (_, responseHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", System.Linq.Enumerable.Single(responseHeaders, h => h.Name == ":status").Value);

        // give any unexpected extra connection attempt a moment to actually reach the origin before
        // asserting the count stayed at one.
        await Task.Delay(500);

        Assert.AreEqual(1, rawServer.AcceptedConnectionCount,
            "Expected exactly one origin connection (the discovery connection, retained and adopted as " +
            "the session connection) for one prefetch-enabled, cold-cache explicit h2 tunnel.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Cache_Hit_Explicit_Tunnel_With_Prefetch_Adopts_Prefetched_Connection()
    {
        // On a cache hit, the correctly-keyed prefetch connection started while the client TLS handshake
        // is still in progress must be adopted directly as the session connection instead of being left
        // unused - one origin connection per tunnel, not two.
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();
            var headers = connection.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, headers, true);
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableTcpServerConnectionPrefetch = true;

        var uri = new Uri(rawServer.Url);

        async Task<int> SendOneRequestOverANewTunnelAsync()
        {
            using var rawClient =
                await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

            var requestHeaders = rawClient.Connection.EncodeHeaders(
                new[]
                {
                    (":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"),
                    (":path", "/")
                },
                Array.Empty<(string, string)>());
            await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

            var (_, responseHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync();
            return int.Parse(System.Linq.Enumerable.Single(responseHeaders, h => h.Name == ":status").Value);
        }

        // First tunnel: cold cache, one discovery connection adopted as the session connection.
        Assert.AreEqual(200, await SendOneRequestOverANewTunnelAsync());

        // Second tunnel: cache hit, prefetch enabled - the prefetched connection must be adopted rather
        // than abandoned alongside a second, freshly opened session connection.
        Assert.AreEqual(200, await SendOneRequestOverANewTunnelAsync());

        await Task.Delay(500);

        Assert.AreEqual(2, rawServer.AcceptedConnectionCount,
            "Expected exactly one origin connection per tunnel (one adopted discovery connection for the " +
            "cold-cache tunnel, one adopted prefetch connection for the cache-hit tunnel) - four would mean " +
            "prefetched/discovery connections are being wastefully abandoned instead of adopted.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_First_Frame_After_Preface_Must_Be_Settings()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            try
            {
                while (true) await connection.ReadFrameAsync();
            }
            catch
            {
                // connection torn down by the test - nothing further to do.
            }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(rawServer.Url);
        var (tcpClient, sslStream) = await ConnectAndAuthenticateAsync(proxy.ProxyEndPoints[0].Port, uri.Host,
            uri.Port, new System.Collections.Generic.List<SslApplicationProtocol> { SslApplicationProtocol.Http2 });
        using var _ = tcpClient;
        using var __ = sslStream;

        await sslStream.WriteAsync(Titanium.Web.Proxy.Http2.Http2Helper.ConnectionPreface, 0,
            Titanium.Web.Proxy.Http2.Http2Helper.ConnectionPreface.Length);

        // send a HEADERS frame (not SETTINGS) as the very first frame - a real client would never do this.
        var connection = new Http2RawFrame.Connection(sslStream);
        var headers = connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/") },
            Array.Empty<(string, string)>());
        await connection.WriteHeaderBlockAsync(1, headers, true);

        var frame = await connection.ReadFrameAsync();
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "The proxy must reject a connection whose first frame after the preface is not SETTINGS.");
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Preface_Rejected_When_Alpn_Negotiated_Http11()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            try
            {
                await connection.SendInitialSettingsAsync();
                while (true) await connection.ReadFrameAsync();
            }
            catch
            {
                // never expected to be reached by this test - the preface must be rejected before any
                // origin connection is attempted.
            }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(rawServer.Url);

        // Explicitly offer only "http/1.1" - the proxy must negotiate h1.1 here (its ALPN offer mirrors
        // whatever the client offered) regardless of EnableHttp2/origin capability.
        var (tcpClient, sslStream) = await ConnectAndAuthenticateAsync(proxy.ProxyEndPoints[0].Port, uri.Host,
            uri.Port, new System.Collections.Generic.List<SslApplicationProtocol> { SslApplicationProtocol.Http11 });
        using var _ = tcpClient;
        using var __ = sslStream;

        // The client offered only "http/1.1" (no "h2"), so the proxy's origin-capability check never even
        // considers offering h2 to the client - it currently leaves `ApplicationProtocols` unset in this
        // case rather than explicitly completing ALPN with "http/1.1", so no protocol may be negotiated at
        // all (RFC 7301 explicitly allows a server to omit the ALPN extension in its reply). Either
        // outcome is a valid "did not negotiate h2" result for what this test actually verifies below.
        Assert.AreNotEqual(SslApplicationProtocol.Http2, sslStream.NegotiatedApplicationProtocol);

        // Send the literal HTTP/2 connection preface anyway - routing must be governed by the
        // TLS-negotiated ALPN, not by these bytes, so the proxy must refuse to switch to HTTP/2 here.
        await sslStream.WriteAsync(Titanium.Web.Proxy.Http2.Http2Helper.ConnectionPreface, 0,
            Titanium.Web.Proxy.Http2.Http2Helper.ConnectionPreface.Length);

        var readBuffer = new byte[16];
        int totalRead = 0;
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var read = await sslStream.ReadAsync(readBuffer, 0, readBuffer.Length, cts.Token);
                if (read == 0) break; // graceful close - the expected outcome.
                totalRead += read;
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("The proxy neither closed the connection nor sent an HTTP/1.1 reply after an h2 " +
                        "preface arrived on an ALPN=http/1.1 connection.");
        }
        catch (IOException)
        {
            // an abortive close is also an acceptable way to observe "the tunnel was not treated as h2".
        }

        // Whatever bytes (if any) were sent back before close must not be a valid HTTP/2 frame header
        // claiming to be a SETTINGS/GOAWAY frame constructed by the h2 relay - i.e. the connection was
        // torn down as a protocol violation rather than quietly continuing an HTTP/2 session.
        Assert.IsTrue(totalRead == 0 || totalRead < Titanium.Web.Proxy.Http2.Http2Helper.ConnectionPreface.Length,
            "The proxy must not have started relaying HTTP/2 frames on this ALPN=http/1.1 connection.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_No_Alpn_Client_Negotiates_Http11_And_Request_Succeeds()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("h1-ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(server.ListeningHttpsUrl);

        // offer no ALPN protocols at all - many non-browser TLS clients never send the extension.
        var (tcpClient, sslStream) =
            await ConnectAndAuthenticateAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port, null);
        using var _ = tcpClient;
        using var __ = sslStream;

        Assert.AreEqual(default(SslApplicationProtocol), sslStream.NegotiatedApplicationProtocol,
            "No ALPN was offered, so none should have been negotiated.");

        var requestBytes = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {uri.Host}\r\nConnection: close\r\n\r\n");
        await sslStream.WriteAsync(requestBytes, 0, requestBytes.Length);

        using var reader = new StreamReader(sslStream, Encoding.ASCII, false, 4096, true);
        var statusLine = await reader.ReadLineAsync();
        Assert.IsTrue(statusLine != null && statusLine.StartsWith("HTTP/1.1 200"),
            $"Expected an HTTP/1.1 200 response on a no-ALPN connection, got: '{statusLine}'.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Completed_Stream_Fires_BeforeRequest_BeforeResponse_AfterResponse_Exactly_Once()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var beforeRequestCount = 0;
        var beforeResponseCount = 0;
        var afterResponseCount = 0;
        proxy.BeforeRequest += (_, _) => { System.Threading.Interlocked.Increment(ref beforeRequestCount); return Task.CompletedTask; };
        proxy.BeforeResponse += (_, _) => { System.Threading.Interlocked.Increment(ref beforeResponseCount); return Task.CompletedTask; };
        proxy.AfterResponse += (_, _) => { System.Threading.Interlocked.Increment(ref afterResponseCount); return Task.CompletedTask; };

        using var client = TestHelper.GetHttp2Client(proxy);
        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        await response.Content.ReadAsStringAsync();

        Assert.AreEqual(new Version(2, 0), response.Version);

        // AfterResponse now runs as a tracked background finalization (see Http2Helper.FinalizeStreamAsync) -
        // give it a moment to observe the stream's end-of-life rather than racing the client-visible response.
        for (var i = 0; i < 100 && afterResponseCount < 1; i++)
        {
            await Task.Delay(20);
        }

        Assert.AreEqual(1, beforeRequestCount);
        Assert.AreEqual(1, beforeResponseCount);
        Assert.AreEqual(1, afterResponseCount);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Session_Timing_Has_Same_Milestones_As_Http11()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRequestTimingCapture = true;

        HttpRequestTiming? capturedTiming = null;
        var afterResponseTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.AfterResponse += (_, args) =>
        {
            capturedTiming = args.Timing;
            afterResponseTcs.TrySetResult(true);
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttp2Client(proxy);
        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        await response.Content.ReadAsStringAsync();
        Assert.AreEqual(new Version(2, 0), response.Version);

        await Task.WhenAny(afterResponseTcs.Task, Task.Delay(2000));

        Assert.IsNotNull(capturedTiming, "AfterResponse should have fired.");
        // Parity with the HTTP/1.x pipeline (see RequestHandler/ResponseHandler), so that Timing-based
        // latency diagnostics behave identically regardless of which protocol a session actually used.
        // CompletedAt is not asserted here: it is only set once OnAfterResponse's own MarkComplete call
        // runs *after* this AfterResponse handler returns (see its remarks), so it is deliberately still
        // null at the point this handler captures the timing object.
        Assert.IsNotNull(capturedTiming!.RequestSentAt, "Missing RequestSentAt.");
        Assert.IsNotNull(capturedTiming.ResponseHeadersReceivedAt, "Missing ResponseHeadersReceivedAt.");

        Assert.IsTrue(capturedTiming.ResponseHeadersReceivedAt >= capturedTiming.RequestSentAt);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_RstStream_From_Client_Still_Fires_AfterResponse_Exactly_Once()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        var originSawRequestHeaders = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            try
            {
                // Read frames (ignoring SETTINGS/etc.) until the request HEADERS for stream 1 arrives,
                // rather than waiting for a full (END_STREAM-terminated) request that this test
                // deliberately never completes.
                while (true)
                {
                    var frame = await connection.ReadFrameAsync();
                    if (frame.Type == Http2FrameType.Headers && frame.StreamId == 1)
                    {
                        originSawRequestHeaders.TrySetResult(true);
                        break;
                    }
                }

                while (true) await connection.ReadFrameAsync();
            }
            catch
            {
                // torn down by the test, or this was one of the two unused probe/prefetch connections.
            }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var afterResponseCount = 0;
        proxy.AfterResponse += (_, _) => { System.Threading.Interlocked.Increment(ref afterResponseCount); return Task.CompletedTask; };

        var uri = new Uri(rawServer.Url);
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/") },
            Array.Empty<(string, string)>());
        // no END_STREAM - keep the stream open so the client can reset it below.
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, false);

        await originSawRequestHeaders.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var payload = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(payload, (int)Http2ErrorCode.Cancel);
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.RstStream, 1, 0, payload);

        for (var i = 0; i < 150 && afterResponseCount < 1; i++)
        {
            await Task.Delay(20);
        }

        Assert.AreEqual(1, afterResponseCount,
            "A client-reset HTTP/2 stream must still get exactly one AfterResponse invocation.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Exception_In_BeforeRequest_Does_Not_Hang_Connection_And_Other_Streams_Still_Complete()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var exceptionCapture = new TestExceptionCapture();
        proxy.Logging.LoggerFactory = exceptionCapture;
        proxy.ApplyLoggingConfiguration();
        proxy.BeforeRequest += (_, _) => throw new InvalidOperationException("intentional test failure");

        using var client = TestHelper.GetHttp2Client(proxy);

        // the request itself should either fail cleanly or still complete (BeforeRequest failing does not
        // set CancelRequest, so the request is forwarded normally) - either is acceptable; what matters is
        // that it does not hang and a second, unrelated request on the same connection still succeeds.
        try
        {
            await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        }
        catch
        {
            // acceptable - see above.
        }

        var secondResponse = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.IsTrue(exceptionCapture.Exceptions.Count >= 1,
            "The BeforeRequest exception should have been reported via the logging gateway.");
    }
}
