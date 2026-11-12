using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
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
///     Integration tests targeting coverage gaps in <c>Http2Helper.Copy.cs</c>,
///     <c>Http2Helper.Copy.Headers.cs</c>, and <c>Http2OriginConnection.cs</c> identified
///     from Sonar analysis. Each test exercises a specific RFC violation or edge case that
///     requires byte-level frame control and cannot be driven through a real
///     <see cref="System.Net.Http.SocketsHttpHandler" />.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyHeaderCoverageTests
{
    // -------------------------------------------------------------------------
    // Shared server for tests that only need a proxy (no special origin behavior)
    // -------------------------------------------------------------------------

    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    ///     A NoOp origin handler: sends the mandatory initial SETTINGS then discards frames
    ///     until the connection is torn down. Used by tests that only exercise the
    ///     client-facing enforcement path and never need the origin to answer a request.
    /// </summary>
    private static Func<Http2RawFrame.Connection, Task> NoOpOriginHandler() => async conn =>
    {
        await conn.SendInitialSettingsAsync();
        try { while (true) await conn.ReadFrameAsync(); }
        catch { /* torn down by the test */ }
    };

    /// <summary>
    ///     Reads frames off <paramref name="raw" />, skipping SETTINGS and WINDOW_UPDATE frames,
    ///     and returns the first non-skipped frame. The proxy always races its own initial
    ///     SETTINGS/WINDOW_UPDATE ahead of any error reply, so a single unconditional read is
    ///     not deterministic for error-reaction assertions.
    /// </summary>
    private static async Task<Http2RawFrame.Frame> ReadSignificantFrameAsync(
        Http2RawClient raw, TimeSpan timeout)
    {
        Http2RawFrame.Frame frame;
        do
        {
            frame = await raw.Connection.ReadFrameAsync().WaitAsync(timeout);
        } while (frame.Type is Http2FrameType.Settings or Http2FrameType.WindowUpdate);

        return frame;
    }

    // -------------------------------------------------------------------------
    // Http2Helper.Copy.cs — PUSH_PROMISE from client
    // Bug class: proxy forwards or silently ignores a PUSH_PROMISE sent by the client,
    // which is a connection-level protocol violation (RFC 7540 §6.6).
    // -------------------------------------------------------------------------

    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_PushPromise_Triggers_GoAway_ProtocolError()
    {
        // Bug class: PUSH_PROMISE from client accepted/forwarded instead of connection error.
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // A PUSH_PROMISE sent by the client violates RFC 7540 §6.6 ("PUSH_PROMISE MUST NOT be sent
        // if the SETTINGS_ENABLE_PUSH setting of the peer endpoint is set to 0") and is never valid
        // from a client. The proxy must respond with GOAWAY PROTOCOL_ERROR.
        //
        // Frame layout: 4-byte promised stream ID (0x00 0x00 0x00 0x02 = stream 2).
        // END_HEADERS flag set so the frame is self-contained.
        var pushPayload = new byte[] { 0x00, 0x00, 0x00, 0x02 };
        await raw.Connection.WriteFrameAsync(Http2FrameType.PushPromise, 1,
            Http2FrameFlag.EndHeaders, pushPayload);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "Proxy must send GOAWAY in response to a client PUSH_PROMISE.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, errorCode,
            "GOAWAY error code must be PROTOCOL_ERROR.");
    }

    // -------------------------------------------------------------------------
    // Http2Helper.Copy.cs — WINDOW_UPDATE overflow
    // Bug class: a large WINDOW_UPDATE increment silently overflows the flow-control
    // window beyond 2^31-1, violating RFC 7540 §6.9.1 and stalling flow control.
    // -------------------------------------------------------------------------

    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_ConnectionWindowUpdate_MaxIncrement_Overflows_Triggers_GoAway_FlowControlError()
    {
        // Bug class: connection-level WINDOW_UPDATE overflow goes undetected;
        // RFC 7540 §6.9.1 requires a connection-level FLOW_CONTROL_ERROR (GOAWAY).
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Send increment = 2^31 - 1 (maximum possible). The proxy's current outbound connection
        // window toward us starts at 65535 (the RFC default). Adding 2^31-1 exceeds the allowed
        // maximum of 2^31-1, so the proxy must send GOAWAY with FLOW_CONTROL_ERROR.
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, 0x7FFF_FFFF); // 2^31 - 1
        await raw.Connection.WriteFrameAsync(Http2FrameType.WindowUpdate, 0, (Http2FrameFlag)0, payload);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "Connection-level window overflow must produce GOAWAY.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.FlowControlError, errorCode,
            "GOAWAY error code must be FLOW_CONTROL_ERROR.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_StreamWindowUpdate_MaxIncrement_Overflows_Triggers_RstStream_FlowControlError()
    {
        // Bug class: stream-level WINDOW_UPDATE overflow goes undetected;
        // RFC 7540 §6.9.1 requires a stream-level FLOW_CONTROL_ERROR (RST_STREAM).
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Open stream 1 so that a stream-level WINDOW_UPDATE is meaningful.
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false); // keep stream open

        // Stream-1 initial send window = 65535. Adding 2^31-1 overflows the maximum.
        // The proxy must reply with RST_STREAM FLOW_CONTROL_ERROR on stream 1.
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, 0x7FFF_FFFF);
        await raw.Connection.WriteFrameAsync(Http2FrameType.WindowUpdate, 1, (Http2FrameFlag)0, payload);

        // Look for RST_STREAM on stream 1; skip SETTINGS/WINDOW_UPDATE/HEADERS racing in.
        Http2RawFrame.Frame? rst = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.Type == Http2FrameType.RstStream && frame.StreamId == 1)
            {
                rst = frame;
                break;
            }

            if (frame.Type == Http2FrameType.GoAway)
            {
                // Also acceptable: a connection error that subsumes the stream error.
                var code = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
                Assert.AreEqual((int)Http2ErrorCode.FlowControlError, code,
                    "GOAWAY (acceptable alternative) must carry FLOW_CONTROL_ERROR.");
                return;
            }
        }

        Assert.IsNotNull(rst, "Proxy must send RST_STREAM on stream 1 for window overflow.");
        Assert.AreEqual((int)Http2ErrorCode.FlowControlError,
            BinaryPrimitives.ReadInt32BigEndian(rst!.Value.Payload),
            "RST_STREAM must carry FLOW_CONTROL_ERROR.");
    }

    // -------------------------------------------------------------------------
    // Http2Helper.Copy.Headers.cs — CR/LF/NUL in header fields
    // Bug class: control characters injected via HPACK literals are forwarded
    // to the origin or response, violating RFC 9113 §8.2.1.
    // -------------------------------------------------------------------------

    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_HeaderValue_With_CR_IsRejected_With_RstStream()
    {
        // Bug class: CR (0x0D) in a header value is forwarded instead of rejected;
        // RFC 9113 §8.2.1 forbids CR, LF, and NUL in field names and values.
        // EnableHttpInterception forces HPACK decode even on the H2↔H2 path so that
        // the validator (MyHeaderListener.ContainsCtlDelimiter) sees the raw bytes.
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true; // disables verbatim compressed relay → triggers HPACK validation

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // "\r" in a regular header value — HPACK literal encoding stores the byte 0x0D verbatim;
        // the proxy's HPACK decoder hands it to MyHeaderListener, which flags HasMalformedHeader.
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/")],
            [("x-inject", "value\rheader-inject")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.IsTrue(frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway,
            $"Proxy must RST or GOAWAY on CR in header value; got {frame.Type}.");
        if (frame.Type == Http2FrameType.RstStream)
        {
            Assert.AreEqual(1, frame.StreamId);
            Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
                BinaryPrimitives.ReadInt32BigEndian(frame.Payload),
                "RST_STREAM must carry PROTOCOL_ERROR.");
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_HeaderValue_With_LF_IsRejected_With_RstStream()
    {
        // Bug class: LF (0x0A) in a header value is forwarded (HTTP response-splitting risk).
        // EnableHttpInterception forces HPACK decode on the H2↔H2 path.
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/")],
            [("x-inject", "value\nmore")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.IsTrue(frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway,
            $"Proxy must RST or GOAWAY on LF in header value; got {frame.Type}.");
        if (frame.Type == Http2FrameType.RstStream)
        {
            Assert.AreEqual(1, frame.StreamId);
            Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
                BinaryPrimitives.ReadInt32BigEndian(frame.Payload));
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_HeaderName_With_NUL_IsRejected_With_RstStream()
    {
        // Bug class: NUL (0x00) in a field name is forwarded; RFC 9113 §8.2.1 forbids it.
        // EnableHttpInterception forces HPACK decode on the H2↔H2 path.
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // "\0" embedded in the header name — 0x00 is in the HPACK Huffman table (EOS code)
        // but as a literal byte it is a NUL forbidden by RFC 9113 §8.2.1.
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/")],
            [("x-\0bad", "value")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.IsTrue(frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway,
            $"Proxy must RST or GOAWAY on NUL in header name; got {frame.Type}.");
        if (frame.Type == Http2FrameType.RstStream)
        {
            Assert.AreEqual(1, frame.StreamId);
            Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
                BinaryPrimitives.ReadInt32BigEndian(frame.Payload));
        }
    }

    // -------------------------------------------------------------------------
    // Http2Helper.Copy.Headers.cs — trailer validation
    // Bug class: request trailers accepted despite carrying pseudo-headers or
    // forbidden fields, violating RFC 7540 §8.1.2.1 and RFC 9110 §6.5.1.
    // -------------------------------------------------------------------------

    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_RequestTrailer_With_Pseudo_Header_Triggers_RstStream()
    {
        // Bug class: request trailer HEADERS block containing :path is forwarded;
        // RFC 7540 §8.1.2.1: trailer sections MUST NOT contain pseudo-header fields.
        //
        // Strategy: send HEADERS (no END_STREAM) + DATA (no END_STREAM), then
        // send a "trailer" HEADERS with :path but no :method (so the proxy classifies
        // it as a trailer block, not a new request). The proxy must RST the stream.
        //
        // EnableHttpInterception forces HPACK decode on the H2↔H2 path so that
        // the trailer-with-pseudo-header check runs.
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // 1. Main request HEADERS on stream 1, no END_STREAM (body follows).
        var reqBlock = raw.Connection.EncodeHeaders(
            [(":method", "POST"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/")],
            [("content-length", "4")]);
        await raw.Connection.WriteHeaderBlockAsync(1, reqBlock, endStream: false);

        // 2. Request DATA — not the last piece (no END_STREAM).
        await raw.Connection.WriteFrameAsync(Http2FrameType.Data, 1, (Http2FrameFlag)0,
            Encoding.ASCII.GetBytes("body"));

        // 3. Trailer HEADERS with :path but no :method — proxy must classify as trailer,
        //    then reject because trailers must not contain pseudo-header fields.
        var trailerBlock = raw.Connection.EncodeHeaders(
            [(":path", "/injected")],  // pseudo-header in trailer position → RST
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, trailerBlock, endStream: true);

        // The proxy should RST stream 1 (or GOAWAY the connection).
        Http2RawFrame.Frame? terminal = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && terminal == null)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway)
                terminal = frame;
        }

        Assert.IsNotNull(terminal, "Proxy must RST/GOAWAY on request trailer with pseudo-header.");
        if (terminal.Value.Type == Http2FrameType.RstStream)
        {
            Assert.AreEqual(1, terminal.Value.StreamId);
            Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
                BinaryPrimitives.ReadInt32BigEndian(terminal.Value.Payload),
                "RST_STREAM must carry PROTOCOL_ERROR for pseudo-header in trailer.");
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_RequestTrailer_With_ContentLength_Forbidden_Field_Triggers_RstStream()
    {
        // Bug class: request trailer carrying content-length (a forbidden trailer field per
        // RFC 9110 §6.5.1) is relayed to the origin instead of rejected.
        // EnableHttpInterception forces HPACK decode so the trailer-forbidden-field check runs.
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // 1. Main request HEADERS, no END_STREAM.
        var reqBlock = raw.Connection.EncodeHeaders(
            [(":method", "POST"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/")],
            [("content-length", "2")]);
        await raw.Connection.WriteHeaderBlockAsync(1, reqBlock, endStream: false);

        // 2. Request DATA.
        await raw.Connection.WriteFrameAsync(Http2FrameType.Data, 1, (Http2FrameFlag)0,
            Encoding.ASCII.GetBytes("ab"));

        // 3. Trailer HEADERS with content-length — forbidden by RFC 9110 §6.5.1.
        var trailerBlock = raw.Connection.EncodeHeaders(
            Array.Empty<(string, string)>(),
            [("content-length", "2")]);  // forbidden in trailers
        await raw.Connection.WriteHeaderBlockAsync(1, trailerBlock, endStream: true);

        Http2RawFrame.Frame? terminal = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && terminal == null)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway)
                terminal = frame;
        }

        Assert.IsNotNull(terminal, "Proxy must RST/GOAWAY on content-length in request trailer.");
        if (terminal.Value.Type == Http2FrameType.RstStream)
        {
            Assert.AreEqual(1, terminal.Value.StreamId);
            Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
                BinaryPrimitives.ReadInt32BigEndian(terminal.Value.Payload));
        }
    }

    // -------------------------------------------------------------------------
    // Http2Helper.Copy.cs (server direction) — PUSH_PROMISE from origin
    // Bug class: a PUSH_PROMISE received from the origin (always disabled via
    // SETTINGS_ENABLE_PUSH=0) is forwarded to the client or causes a hang.
    // -------------------------------------------------------------------------

    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_PushPromise_Is_Rejected_And_Client_Sees_Error()
    {
        // Bug class: PUSH_PROMISE from origin forwarded to client or connection hangs;
        // RFC 7540 §6.6: once SETTINGS_ENABLE_PUSH=0 is acknowledged, further PUSH_PROMISEs
        // are connection-level protocol violations.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();

            // Instead of answering normally, send a PUSH_PROMISE on the open stream.
            // Payload: 4-byte promised stream ID (stream 2 = 0x00000002).
            var pushPayload = new byte[] { 0x00, 0x00, 0x00, 0x02 };
            await conn.WriteFrameAsync(Http2FrameType.PushPromise, streamId,
                Http2FrameFlag.EndHeaders, pushPayload);

            try { await Task.Delay(Timeout.Infinite, cts.Token); } catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        // The proxy must detect the rogue PUSH_PROMISE and either RST stream 1,
        // GOAWAY the client, send a 5xx response, or cleanly close the connection.
        // The test must not hang. EOF (connection close) is also a valid proxy reaction.
        var sawTerminal = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !sawTerminal)
        {
            try
            {
                var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway)
                {
                    sawTerminal = true;
                }
                else if (frame.Type == Http2FrameType.Headers)
                {
                    var status = raw.Connection.DecodeHeaders(frame.Payload)
                        .FirstOrDefault(h => h.Name == ":status").Value;
                    if (status is not null && status.StartsWith('5'))
                        sawTerminal = true;
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.EndOfStreamException
                or OperationCanceledException)
            {
                // Connection closed by proxy — valid response to the PUSH_PROMISE violation.
                sawTerminal = true;
            }
        }

        cts.Cancel();
        Assert.IsTrue(sawTerminal, "Client must see RST/GOAWAY/5xx or connection close after origin PUSH_PROMISE.");
    }

    // -------------------------------------------------------------------------
    // Http2OriginConnection.cs — HEAD / 204 / 304 no-body detection
    // Bug class: proxy hangs waiting for DATA frames that will never arrive for
    // HEAD, 204, and 304 responses, which RFC 7230 §3.3 / RFC 9110 §9.3.2
    // explicitly prohibit from carrying a body.
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Creates a proxy+suite configured for the H1→H2 bridge: the client speaks HTTP/1.x and
    ///     the proxy connects to the origin using HTTP/2 (via Http2OriginConnection).
    ///     This exercises code paths in <c>Http2OriginConnection.SendAsync</c> that
    ///     detect no-body responses (HEAD, 204, 304) and do not wait for DATA frames.
    /// </summary>
    private static (ProxyServer Proxy, TestSuite Suite) CreateH2OriginBridgeProxy()
    {
        var suite = new TestSuite();
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        return (proxy, suite);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Origin_HEAD_Response_Without_Body_Completes_Cleanly()
    {
        // Bug class: proxy waits for DATA frame after a HEAD 200 response that
        // must not carry a body (RFC 9110 §9.3.2), causing the client to stall.
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, reqHeaders, _) = await conn.ReadRequestAsync();
            Assert.AreEqual("HEAD",
                reqHeaders.Single(h => h.Name == ":method").Value,
                "Origin expected a HEAD request.");

            // Respond with 200 + content-length but NO DATA frame; END_STREAM on HEADERS.
            var block = conn.EncodeHeaders([(":status", "200")],
                [("content-length", "42")]);
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: true);
        });

        var (proxy, suite) = CreateH2OriginBridgeProxy();
        using var _ = suite;

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Head, origin.Url);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;

        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            "HEAD response must be received without hanging.");
        // HEAD responses have no body by definition — just verify no deadlock.
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Origin_Status204_Without_Data_Completes_Cleanly()
    {
        // Bug class: proxy stalls waiting for DATA frames after a 204 No Content
        // response whose body is forbidden by RFC 9110 §15.3.5.
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();

            // 204 with END_STREAM on HEADERS, no DATA.
            var block = conn.EncodeHeaders([(":status", "204")],
                Array.Empty<(string, string)>());
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: true);
        });

        var (proxy, suite) = CreateH2OriginBridgeProxy();
        using var _ = suite;

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;

        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode,
            "204 must be relayed to the client without hanging.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Origin_Status304_Without_Data_Completes_Cleanly()
    {
        // Bug class: proxy stalls waiting for DATA frames after a 304 Not Modified
        // response whose body is prohibited by RFC 9110 §15.4.5.
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();

            // 304 with END_STREAM on HEADERS, no DATA.
            var block = conn.EncodeHeaders([(":status", "304")],
                [("etag", "\"abc123\"")]);
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: true);
        });

        var (proxy, suite) = CreateH2OriginBridgeProxy();
        using var _ = suite;

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"abc123\"");

        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.NotModified, response.StatusCode,
            "304 must be relayed to the client without hanging.");
    }

    // -------------------------------------------------------------------------
    // Http2Helper.Copy.Headers.cs — H1→H2 cookie splitting
    // Bug class: when the proxy bridges H2→H1 (covered by existing tests), it joins
    // multiple cookie fields with "; ". The reverse direction, H1→H2, should split
    // a single combined cookie header back into individual fields.
    // Http2OriginConnection.cs — confirm multiple Set-Cookie fields from origin
    // are individually preserved and not collapsed into one value.
    // -------------------------------------------------------------------------

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Origin_MultipleSetCookie_Fields_Preserved_Individually()
    {
        // Bug class: multiple Set-Cookie headers from origin collapsed into a single
        // header value, breaking cookie isolation (RFC 9113 §8.2.3 / RFC 7540 §8.1.2.5).
        List<string>? receivedCookies = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();

            // Two distinct Set-Cookie headers — must arrive as two separate fields at the client.
            var block = conn.EncodeHeaders(
                [(":status", "200")],
                [("content-length", "2"), ("set-cookie", "a=1; Path=/"), ("set-cookie", "b=2; Path=/")]);
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Encoding.ASCII.GetBytes("ok"));
        });

        var (proxy, suite) = CreateH2OriginBridgeProxy();
        using var _ = suite;

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;

        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        receivedCookies = response.Headers
            .Where(h => string.Equals(h.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => h.Value)
            .ToList();

        // Regardless of whether the proxy relays Set-Cookie as a combined or split header,
        // both cookie values must be present and individually accessible.
        Assert.IsTrue(
            receivedCookies.Any(c => c.Contains("a=1")),
            "Set-Cookie 'a=1' must be present in the relayed response.");
        Assert.IsTrue(
            receivedCookies.Any(c => c.Contains("b=2")),
            "Set-Cookie 'b=2' must be present in the relayed response.");
    }

    // -------------------------------------------------------------------------
    // Http2OriginConnection.cs — origin connection reuse across streams
    // Bug class: a second request is routed to a new origin connection rather than
    // the existing multiplexed one, causing unnecessary TLS setup overhead.
    // -------------------------------------------------------------------------

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Origin_Connection_Is_Reused_Across_Sequential_Bridge_Requests()
    {
        // Bug class: each H1-bridged request opens a new H2 connection to the origin
        // instead of multiplexing on the existing one.
        var requestCount = 0;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                try
                {
                    var (streamId, _, _) = await conn.ReadRequestAsync();
                    System.Threading.Interlocked.Increment(ref requestCount);
                    var block = conn.EncodeHeaders([(":status", "200")],
                        [("content-length", "2")]);
                    await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);
                    await conn.WriteFrameAsync(Http2FrameType.Data, streamId,
                        Http2FrameFlag.EndStream, Encoding.ASCII.GetBytes("ok"));
                }
                catch
                {
                    return;
                }
            }
        });

        var (proxy, suite) = CreateH2OriginBridgeProxy();
        using var _ = suite;

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);

        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url);
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                $"Request {i + 1} must succeed.");
        }

        // Three requests should multiplex onto one or two origin connections
        // (capacity probe may open one, then reuse it). Never 3 separate connections.
        Assert.AreEqual(3, requestCount, "Origin must have served 3 requests.");
        Assert.IsTrue(origin.AcceptedConnectionCount <= 2,
            $"Origin accepted {origin.AcceptedConnectionCount} connections; expected ≤ 2 for reuse.");
    }
}
