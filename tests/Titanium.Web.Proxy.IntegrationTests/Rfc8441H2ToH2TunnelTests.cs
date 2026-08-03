using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Integration tests for the RFC 8441 h2↔h2 native extended CONNECT tunnel, where both the
///     client and the origin speak HTTP/2 and the proxy relays DATA frames without translation.
///     These tests complement <see cref="Rfc8441TunnelTests" /> (which covers the h2→h1 bridge) by
///     exercising the proxy's capability gating, SETTINGS negotiation, tunnel lifecycle, and relay
///     correctness when the origin advertises SETTINGS_ENABLE_CONNECT_PROTOCOL=1.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Rfc8441H2ToH2TunnelTests
{
    private static TestServer sharedServer = null!;

    private static readonly Encoding Ascii = Encoding.ASCII;

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

    // -------------------------------------------------------------------------
    // Happy path
    // -------------------------------------------------------------------------

    /// <summary>
    ///     When the origin advertises SETTINGS_ENABLE_CONNECT_PROTOCOL=1 and the client sends a valid
    ///     extended CONNECT for :protocol=websocket, the proxy must forward the CONNECT to the origin,
    ///     relay the 200 OK back, and then relay DATA frames bidirectionally without modification.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ExtendedConnect_Rfc8441Enabled_H2ToH2_TunnelOpensAndEchoesData()
    {
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithConnectProtocolAsync();

            var (reqStreamId, reqHeaders, endStream) = await originConn.ReadHeaderBlockAsync();

            Assert.AreEqual("CONNECT", reqHeaders.Single(h => h.Name == ":method").Value,
                "Origin must receive :method=CONNECT.");
            Assert.AreEqual("websocket", reqHeaders.Single(h => h.Name == ":protocol").Value,
                "Origin must receive :protocol=websocket.");
            Assert.AreEqual("https", reqHeaders.Single(h => h.Name == ":scheme").Value,
                "Origin must receive :scheme=https (RFC 8441 §5).");
            Assert.IsFalse(endStream,
                "Extended CONNECT HEADERS from proxy must NOT carry END_STREAM.");

            var resp200 = originConn.EncodeHeaders(new[] { (":status", "200") },
                Array.Empty<(string, string)>());
            await originConn.WriteHeaderBlockAsync(reqStreamId, resp200, false);

            while (true)
            {
                var frame = await originConn.ReadFrameAsync();
                if (frame.Type == Http2FrameType.Data && frame.StreamId == reqStreamId)
                {
                    if (frame.Payload.Length > 0)
                        await originConn.WriteFrameAsync(Http2FrameType.Data, reqStreamId, 0, frame.Payload);
                    if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    {
                        await originConn.WriteFrameAsync(Http2FrameType.Data, reqStreamId,
                            Http2FrameFlag.EndStream, Array.Empty<byte>());
                        break;
                    }
                }
                else if (frame.Type == Http2FrameType.RstStream || frame.Type == Http2FrameType.GoAway)
                {
                    break;
                }
            }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        using var rawClient = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "CONNECT"), (":protocol", "websocket"), (":scheme", "https"),
                (":authority", $"localhost:{rawOrigin.Port}"), (":path", "/ws")
            },
            new[] { ("sec-websocket-version", "13"), ("sec-websocket-key", "dGhlIHNhbXBsZSBub25jZQ==") });
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, false);

        var (_, responseHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value,
            "Proxy must relay the origin's 200 OK establishing the tunnel.");

        var payload = Ascii.GetBytes("hello-h2-to-h2");
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Data, 1, 0, payload);

        var received = new List<byte>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (received.Count < payload.Length && !cts.IsCancellationRequested)
        {
            var frame = await rawClient.Connection.ReadFrameAsync();
            if (frame.Type == Http2FrameType.Data && frame.StreamId == 1)
                received.AddRange(frame.Payload);
        }

        Assert.IsFalse(cts.IsCancellationRequested, "Timed out waiting for echoed data over h2↔h2 tunnel.");
        CollectionAssert.AreEqual(payload, received.ToArray(),
            "DATA bytes relayed through native h2↔h2 tunnel must be identical to what was sent.");

        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Data, 1, Http2FrameFlag.EndStream,
            Array.Empty<byte>());
    }

    // -------------------------------------------------------------------------
    // SETTINGS / capability gating
    // -------------------------------------------------------------------------

    /// <summary>
    ///     When the origin does NOT advertise SETTINGS_ENABLE_CONNECT_PROTOCOL=1, the proxy MUST NOT
    ///     forward the extended CONNECT to the origin; it must reset only that stream with REFUSED_STREAM.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ExtendedConnect_H2ToH2_OriginWithoutSetting8_ReturnsRefusedStream()
    {
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsAsync(); // no ENABLE_CONNECT_PROTOCOL
            // Drain remaining frames; the proxy should never forward the CONNECT here.
            try
            {
                var buf = new byte[4096];
                while (true)
                    _ = await originConn.ReadFrameAsync();
            }
            catch { /* expected on connection close */ }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        using var rawClient = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "CONNECT"), (":protocol", "websocket"), (":scheme", "https"),
                (":authority", $"localhost:{rawOrigin.Port}"), (":path", "/ws")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, false);

        var errorCode = await rawClient.Connection.ReadRstOrGoAwayErrorCodeAsync(1);
        Assert.AreEqual(Http2ErrorCode.RefusedStream, errorCode,
            "Proxy must reset with REFUSED_STREAM when origin does not advertise ENABLE_CONNECT_PROTOCOL=1.");
    }

    /// <summary>
    ///     If the origin sends SETTINGS_ENABLE_CONNECT_PROTOCOL with an invalid value (neither 0 nor 1),
    ///     the proxy must treat it as a connection error and send GOAWAY(PROTOCOL_ERROR).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ExtendedConnect_H2ToH2_InvalidSettingValue_CausesGoAway()
    {
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            // Send SETTINGS_ENABLE_CONNECT_PROTOCOL=3 (invalid; must be 0 or 1).
            var payload = new byte[6];
            payload[0] = (byte)(((int)Http2SettingsId.EnableConnectProtocol >> 8) & 0xff);
            payload[1] = (byte)((int)Http2SettingsId.EnableConnectProtocol & 0xff);
            payload[5] = 3;
            await originConn.WriteFrameAsync(Http2FrameType.Settings, 0, 0, payload);

            try { while (true) await originConn.ReadFrameAsync(); }
            catch { /* expected */ }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        using var rawClient = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        bool sawGoAway = false;
        try
        {
            while (!cts.IsCancellationRequested && !sawGoAway)
            {
                var frame = await rawClient.Connection.ReadFrameAsync();
                if (frame.Type == Http2FrameType.GoAway)
                    sawGoAway = true;
            }
        }
        catch (Exception) { sawGoAway = true; }

        Assert.IsTrue(sawGoAway,
            "Proxy must send GOAWAY when origin sends SETTINGS_ENABLE_CONNECT_PROTOCOL with an invalid value.");
    }

    // -------------------------------------------------------------------------
    // Header validation
    // -------------------------------------------------------------------------

    /// <summary>
    ///     An extended CONNECT without :scheme must be rejected with RST_STREAM(PROTOCOL_ERROR).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ExtendedConnect_H2ToH2_MissingScheme_CausesProtocolError()
    {
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithConnectProtocolAsync();
            try { while (true) await originConn.ReadFrameAsync(); } catch { /* expected */ }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        using var rawClient = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port);

        var headers = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "CONNECT"), (":protocol", "websocket"),
                (":authority", $"localhost:{rawOrigin.Port}"), (":path", "/ws")
                // no :scheme
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, headers, false);

        var (responseHeaders, _, errorCode) = await rawClient.Connection.ReadHeadersOrRstAsync(1);
        Assert.IsTrue(responseHeaders == null || errorCode == Http2ErrorCode.ProtocolError,
            "Missing :scheme in extended CONNECT must result in a protocol error, not 200.");
    }

    /// <summary>
    ///     An extended CONNECT for an unsupported protocol (not 'websocket') must return 501.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ExtendedConnect_H2ToH2_UnknownProtocol_Returns501()
    {
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithConnectProtocolAsync();
            try { while (true) await originConn.ReadFrameAsync(); } catch { /* expected */ }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        using var rawClient = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port);

        var headers = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "CONNECT"), (":protocol", "connect-tcp"), (":scheme", "https"),
                (":authority", $"localhost:{rawOrigin.Port}"), (":path", "/proxy")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, headers, false);

        var (responseHeaders, _, _) = await rawClient.Connection.ReadHeadersOrRstAsync(1);
        Assert.IsNotNull(responseHeaders, "Proxy must reply with a HEADERS frame for unsupported protocol.");
        Assert.AreEqual("501", responseHeaders!.Single(h => h.Name == ":status").Value,
            "Unsupported :protocol in extended CONNECT must be rejected with 501 Not Implemented.");
    }

    // -------------------------------------------------------------------------
    // BeforeRequest API visibility
    // -------------------------------------------------------------------------

    /// <summary>
    ///     The BeforeRequest event handler must see a non-null ExtendedConnectProtocol on the request,
    ///     and Request.UpgradeToWebSocket must return true for :protocol=websocket.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ExtendedConnect_H2ToH2_BeforeRequest_CanIdentifyWebSocketUpgrade()
    {
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithConnectProtocolAsync();
            var (sid, _, _) = await originConn.ReadHeaderBlockAsync();
            var resp = originConn.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await originConn.WriteHeaderBlockAsync(sid, resp, false);
            await rawClient_EndStream_TunnelCompletes(originConn, sid);
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        string? observedProtocol = null;
        bool observedUpgradeToWebSocket = false;
        proxy.BeforeRequest += (_, e) =>
        {
            observedProtocol = e.HttpClient.Request.ExtendedConnectProtocol;
            observedUpgradeToWebSocket = e.HttpClient.Request.UpgradeToWebSocket;
            return Task.CompletedTask;
        };

        using var rawClient = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "CONNECT"), (":protocol", "websocket"), (":scheme", "https"),
                (":authority", $"localhost:{rawOrigin.Port}"), (":path", "/ws")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, false);

        var (_, responseHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);

        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Data, 1, Http2FrameFlag.EndStream,
            Array.Empty<byte>());

        await Task.Delay(200); // allow BeforeRequest to complete

        Assert.AreEqual("websocket", observedProtocol,
            "BeforeRequest must see ExtendedConnectProtocol = 'websocket' for RFC 8441 requests.");
        Assert.IsTrue(observedUpgradeToWebSocket,
            "Request.UpgradeToWebSocket must return true for h2 extended CONNECT :protocol=websocket.");
    }

    // -------------------------------------------------------------------------
    // Body API guards
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Calling GetRequestBody() inside BeforeRequest for an extended CONNECT request must throw
    ///     InvalidOperationException, not hang indefinitely waiting for END_STREAM.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ExtendedConnect_H2ToH2_GetRequestBodyThrowsInvalidOperation()
    {
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithConnectProtocolAsync();
            try { while (true) await originConn.ReadFrameAsync(); } catch { /* expected */ }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        Exception? caughtException = null;
        proxy.BeforeRequest += async (_, e) =>
        {
            if (e.HttpClient.Request.ExtendedConnectProtocol == null) return;
            try { _ = await e.GetRequestBody(); }
            catch (InvalidOperationException ex) { caughtException = ex; }
            catch (Exception ex) { caughtException = ex; }
        };

        using var rawClient = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "CONNECT"), (":protocol", "websocket"), (":scheme", "https"),
                (":authority", $"localhost:{rawOrigin.Port}"), (":path", "/ws")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (caughtException == null && !cts.IsCancellationRequested)
            await Task.Delay(50);

        Assert.IsInstanceOfType<InvalidOperationException>(caughtException,
            "GetRequestBody() must throw InvalidOperationException for an extended CONNECT request.");
    }

    // -------------------------------------------------------------------------
    // Post-establishment HEADERS rejection
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Once the 200 OK tunnel is established, any subsequent HEADERS frame on that stream
    ///     (e.g. trailers) must be rejected with RST_STREAM(PROTOCOL_ERROR) per RFC 9113 §8.5.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ExtendedConnect_H2ToH2_PostEstablishment_HeadersRejected()
    {
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithConnectProtocolAsync();
            var (sid, _, _) = await originConn.ReadHeaderBlockAsync();
            var resp = originConn.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await originConn.WriteHeaderBlockAsync(sid, resp, false);
            try { while (true) await originConn.ReadFrameAsync(); } catch { /* expected */ }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        using var rawClient = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "CONNECT"), (":protocol", "websocket"), (":scheme", "https"),
                (":authority", $"localhost:{rawOrigin.Port}"), (":path", "/ws")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, false);

        var (_, responseHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value,
            "Tunnel must be established before testing post-establishment HEADERS.");

        // Send a HEADERS frame (simulating trailers) on the established tunnel stream.
        var trailers = rawClient.Connection.EncodeHeaders(
            Array.Empty<(string, string)>(), new[] { ("x-trailer", "value") });
        await rawClient.Connection.WriteHeaderBlockAsync(1, trailers, endStream: true);

        // The proxy must reset the stream with PROTOCOL_ERROR.
        var errorCode = await rawClient.Connection.ReadRstOrGoAwayErrorCodeAsync(1);
        Assert.AreEqual(Http2ErrorCode.ProtocolError, errorCode,
            "HEADERS on an established extended CONNECT tunnel must cause RST_STREAM(PROTOCOL_ERROR).");
    }

    // -------------------------------------------------------------------------
    // Multiplexing
    // -------------------------------------------------------------------------

    /// <summary>
    ///     While an extended CONNECT h2↔h2 tunnel is open, a normal GET on another stream must complete
    ///     successfully - the tunnel must not block the connection's other streams.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ExtendedConnect_H2ToH2_MultiplexedWithNormalRequest_BothSucceed()
    {
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithConnectProtocolAsync();

            var tunnelStreamId = -1;
            var normalStreamId = -1;
            var responded200 = false;
            var respondedNormal = false;

            while (!responded200 || !respondedNormal)
            {
                var (sid, headers, endStream) = await originConn.ReadHeaderBlockAsync();
                var method = headers.FirstOrDefault(h => h.Name == ":method").Value;

                if (method == "CONNECT" && headers.Any(h => h.Name == ":protocol"))
                {
                    tunnelStreamId = sid;
                    var resp200 = originConn.EncodeHeaders(new[] { (":status", "200") },
                        Array.Empty<(string, string)>());
                    await originConn.WriteHeaderBlockAsync(sid, resp200, false);
                    responded200 = true;
                }
                else
                {
                    normalStreamId = sid;
                    var respOk = originConn.EncodeHeaders(new[] { (":status", "200") },
                        new[] { ("content-length", "2") });
                    await originConn.WriteHeaderBlockAsync(sid, respOk, false);
                    await originConn.WriteFrameAsync(Http2FrameType.Data, sid, Http2FrameFlag.EndStream,
                        Ascii.GetBytes("ok"));
                    respondedNormal = true;
                }
            }

            if (tunnelStreamId > 0)
            {
                // Echo whatever DATA arrives for the tunnel.
                try
                {
                    while (true)
                    {
                        var frame = await originConn.ReadFrameAsync();
                        if (frame.Type == Http2FrameType.Data && frame.StreamId == tunnelStreamId)
                        {
                            if (frame.Payload.Length > 0)
                                await originConn.WriteFrameAsync(Http2FrameType.Data, tunnelStreamId, 0, frame.Payload);
                            if ((frame.Flags & Http2FrameFlag.EndStream) != 0) break;
                        }
                    }
                }
                catch { /* expected on close */ }
            }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        using var rawClient = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port);

        // Stream 1: extended CONNECT tunnel
        var tunnelReqHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "CONNECT"), (":protocol", "websocket"), (":scheme", "https"),
                (":authority", $"localhost:{rawOrigin.Port}"), (":path", "/ws")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, tunnelReqHeaders, false);

        // Stream 3: normal GET (interleaved)
        var getHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "GET"), (":scheme", "https"),
                (":authority", $"localhost:{rawOrigin.Port}"), (":path", "/normal")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(3, getHeaders, true);

        // Read the two responses (order may vary).
        var tunnelStatus = string.Empty;
        var normalStatus = string.Empty;
        var normalBodyBytes = new List<byte>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while ((tunnelStatus == string.Empty || normalStatus == string.Empty || normalBodyBytes.Count < 2)
               && !cts.IsCancellationRequested)
        {
            var frame = await rawClient.Connection.ReadFrameAsync();
            if (frame.Type == Http2FrameType.Headers)
            {
                var headers = rawClient.Connection.DecodeHeaders(frame.Payload);
                var status = headers.FirstOrDefault(h => h.Name == ":status").Value ?? string.Empty;
                if (frame.StreamId == 1) tunnelStatus = status;
                else if (frame.StreamId == 3) normalStatus = status;
            }
            else if (frame.Type == Http2FrameType.Data && frame.StreamId == 3)
            {
                normalBodyBytes.AddRange(frame.Payload);
            }
        }

        Assert.IsFalse(cts.IsCancellationRequested, "Timed out waiting for multiplexed responses.");
        Assert.AreEqual("200", tunnelStatus, "Tunnel (stream 1) must return 200.");
        Assert.AreEqual("200", normalStatus, "Normal GET (stream 3) must return 200 while tunnel is open.");

        // Close the tunnel cleanly.
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Data, 1, Http2FrameFlag.EndStream,
            Array.Empty<byte>());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static async Task rawClient_EndStream_TunnelCompletes(Http2RawFrame.Connection originConn, int sid)
    {
        try
        {
            while (true)
            {
                var frame = await originConn.ReadFrameAsync();
                if (frame.Type == Http2FrameType.Data && frame.StreamId == sid &&
                    (frame.Flags & Http2FrameFlag.EndStream) != 0)
                    break;
                if (frame.Type == Http2FrameType.RstStream || frame.Type == Http2FrameType.GoAway)
                    break;
            }
        }
        catch { /* expected on close */ }
    }
}
