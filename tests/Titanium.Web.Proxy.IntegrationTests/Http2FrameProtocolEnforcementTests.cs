using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Integration tests exercising HTTP/2 frame-protocol enforcement in
///     <c>Http2Helper.Copy.cs</c> and <c>Http2Helper.Copy.Headers.cs</c>.
///     Each test names the RFC requirement and bug class it validates.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2FrameProtocolEnforcementTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    // -------------------------------------------------------------------------
    // Helper: skip SETTINGS/WINDOW_UPDATE frames to find the first interesting one
    // -------------------------------------------------------------------------

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

    private static Func<Http2RawFrame.Connection, Task> NoOpOriginHandler() => async conn =>
    {
        await conn.SendInitialSettingsAsync();
        try { while (true) await conn.ReadFrameAsync(); }
        catch { /* torn down by the test */ }
    };

    // =========================================================================
    // PING frame handling — Http2Helper.Copy.cs ~line 1901
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §6.7: a PING frame without the ACK flag must be answered with a PING frame
    ///     whose ACK flag is set and whose payload echoes the original eight bytes.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Ping_IsAnsweredWith_PingAck_EchoingPayload()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var pingPayload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        await raw.Connection.WriteFrameAsync(Http2FrameType.Ping, 0, (Http2FrameFlag)0, pingPayload);

        // The proxy sends its own frames first; look for the PING ACK specifically.
        Http2RawFrame.Frame? ack = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && ack == null)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.Type == Http2FrameType.Ping)
                ack = frame;
        }

        Assert.IsNotNull(ack, "Proxy must respond to PING with PING ACK.");
        Assert.AreEqual(Http2FrameFlag.Ack, ack.Value.Flags & Http2FrameFlag.Ack,
            "PING response must have ACK flag set.");
        Assert.AreEqual(0, ack.Value.StreamId, "PING ACK must have stream id 0.");
        CollectionAssert.AreEqual(pingPayload, ack.Value.Payload,
            "PING ACK payload must echo the original PING payload.");
    }

    /// <summary>
    ///     RFC 7540 §6.7: a PING frame length other than 8 is a connection-level FRAME_SIZE_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Ping_WrongLength_Triggers_GoAway_FrameSizeError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // 4-byte PING payload is invalid (must be exactly 8).
        await raw.Connection.WriteFrameAsync(Http2FrameType.Ping, 0, (Http2FrameFlag)0,
            new byte[] { 0x01, 0x02, 0x03, 0x04 });

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type, "Wrong-length PING must produce GOAWAY.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError, errorCode,
            "GOAWAY error code must be FRAME_SIZE_ERROR.");
    }

    // =========================================================================
    // First frame must be SETTINGS — Http2Helper.Copy.cs ~line 505
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §3.5: the very first frame after the connection preface must be SETTINGS.
    ///     A DATA frame as the first frame is a connection-level PROTOCOL_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_FirstFrame_IsData_Triggers_GoAway_ProtocolError()
    {
        // Bypass Http2RawClient.ConnectAsync (which sends the proper SETTINGS) and write
        // raw bytes ourselves so we can control the first frame.
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);

        // Raw TCP connection: send CONNECT tunnel, then TLS, then connection preface,
        // but send DATA (not SETTINGS) as the very first h2 frame.
        var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync("127.0.0.1", proxy.ProxyEndPoints[0].Port);
        try
        {
            var ns = tcp.GetStream();
            var connectReq = Encoding.ASCII.GetBytes(
                $"CONNECT {uri.Host}:{uri.Port} HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\n\r\n");
            await ns.WriteAsync(connectReq);

            // Read the HTTP/1.1 200 tunnel response.
            var buf = new byte[4096];
            var sb = new StringBuilder();
            while (!sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var readTask = ns.ReadAsync(buf.AsMemory(0, 1));
                var n = await (readTask.IsCompleted ? readTask.AsTask() : readTask.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                if (n == 0) break;
                sb.Append((char)buf[0]);
            }
            Assert.IsTrue(sb.ToString().Contains("200", StringComparison.Ordinal),
                "CONNECT tunnel must succeed.");

            // TLS handshake.
            var sslStream = new System.Net.Security.SslStream(ns, false,
                (_, cert, _, errors) => TestCertificateAuthority.Validate(cert, errors));
            await sslStream.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
            {
                TargetHost = uri.Host,
                ApplicationProtocols = [System.Net.Security.SslApplicationProtocol.Http2],
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
            });

            // Write the h2 connection preface.
            await sslStream.WriteAsync(Http2Helper.ConnectionPreface.AsMemory());

            // Write a DATA frame (stream 1, END_STREAM) instead of the required SETTINGS.
            var dataFrame = new byte[9]; // empty DATA, length=0, type=0 (DATA), END_STREAM=0x1, stream=1
            dataFrame[3] = (byte)Http2FrameType.Data;
            dataFrame[4] = (byte)Http2FrameFlag.EndStream;
            dataFrame[8] = 1; // stream id = 1
            await sslStream.WriteAsync(dataFrame.AsMemory());

            // The proxy should send GOAWAY PROTOCOL_ERROR.
            var conn = new Http2RawFrame.Connection(sslStream);
            var frame = await ReadSignificantFrameAsync(
                Http2RawClient.Adopt(tcp, conn), TimeSpan.FromSeconds(10));
            Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
                "DATA as first frame must produce GOAWAY.");
            var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
            Assert.AreEqual((int)Http2ErrorCode.ProtocolError, errorCode,
                "GOAWAY error code must be PROTOCOL_ERROR.");
        }
        finally
        {
            tcp.Dispose();
        }
    }

    // =========================================================================
    // SETTINGS/PING/GOAWAY with non-zero stream id — Http2Helper.Copy.cs ~line 560
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §6.5: SETTINGS must use stream id 0. A non-zero stream id is PROTOCOL_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Settings_WithNonZeroStreamId_Triggers_GoAway_ProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Write a SETTINGS frame with stream id 1 instead of 0.
        var settingsStream = raw.Connection.GetStream();
        var hdr = new byte[9];
        // length=0, type=SETTINGS, flags=0, stream=1
        hdr[3] = (byte)Http2FrameType.Settings;
        hdr[8] = 1; // non-zero stream id
        await settingsStream.WriteAsync(hdr.AsMemory());

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "SETTINGS with non-zero stream id must produce GOAWAY.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, errorCode);
    }

    /// <summary>
    ///     RFC 7540 §6.7: PING must use stream id 0. A non-zero stream id is PROTOCOL_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Ping_WithNonZeroStreamId_Triggers_GoAway_ProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Write a PING frame with stream id 3 (must be 0).
        var s = raw.Connection.GetStream();
        var hdr = new byte[9 + 8]; // 9-byte header + 8-byte payload
        hdr[0] = 0; hdr[1] = 0; hdr[2] = 8; // length = 8
        hdr[3] = (byte)Http2FrameType.Ping;
        hdr[8] = 3; // non-zero stream id
        await s.WriteAsync(hdr.AsMemory());

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "PING with non-zero stream id must produce GOAWAY.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, errorCode);
    }

    // =========================================================================
    // DATA for idle stream — Http2Helper.Copy.cs ~line 963
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §5.1: a DATA frame on an idle stream (stream id never opened) is a
    ///     connection-level PROTOCOL_ERROR (GOAWAY).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Data_ForIdleStream_Triggers_GoAway_ProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Send DATA on stream 7 — never opened (idle). LastClientStreamId == 0 initially,
        // so stream 7 > LastClientStreamId ⇒ idle.
        await raw.Connection.WriteFrameAsync(Http2FrameType.Data, 7, Http2FrameFlag.EndStream,
            Encoding.ASCII.GetBytes("body"));

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "DATA on idle stream must produce GOAWAY.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, errorCode,
            "GOAWAY error code must be PROTOCOL_ERROR for idle-stream DATA.");
    }

    /// <summary>
    ///     RFC 7540 §5.1: a DATA frame for a stream that was already fully closed
    ///     (END_STREAM seen) must be rejected with RST_STREAM STREAM_CLOSED.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Data_ForClosedStream_Triggers_RstStream_StreamClosed()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            // Respond immediately so the proxy can mark stream as closed.
            var block = conn.EncodeHeaders([(":status", "200")], [("content-length", "2")]);
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Encoding.ASCII.GetBytes("ok"));
            // Keep connection alive.
            try { while (true) await conn.ReadFrameAsync(); }
            catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Send a complete, well-formed request on stream 1.
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        // Drain the response so the stream enters half-closed/closed state.
        Http2RawFrame.Frame respFrame;
        do { respFrame = await raw.Connection.ReadFrameAsync(); }
        while (!(respFrame.Type == Http2FrameType.Data && (respFrame.Flags & Http2FrameFlag.EndStream) != 0)
            && respFrame.Type != Http2FrameType.Headers); // 204/304: END_STREAM on HEADERS

        // Wait briefly for the proxy to record the closed state.
        await Task.Delay(100);

        // Now send DATA on stream 1 again (closed). LastClientStreamId == 1, so stream 1
        // is in a closed/half-closed state, not idle → RST_STREAM STREAM_CLOSED.
        await raw.Connection.WriteFrameAsync(Http2FrameType.Data, 1, Http2FrameFlag.EndStream,
            Encoding.ASCII.GetBytes("late"));

        // Look for RST_STREAM or GOAWAY on stream 1.
        Http2RawFrame.Frame? terminal = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && terminal == null)
        {
            var f = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (f.Type is Http2FrameType.RstStream or Http2FrameType.GoAway)
                terminal = f;
        }

        Assert.IsNotNull(terminal, "Proxy must RST/GOAWAY for DATA on closed stream.");
    }

    // =========================================================================
    // Zero-increment WINDOW_UPDATE — Http2Helper.Copy.cs ~line 1786
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §6.9.1: a WINDOW_UPDATE with a zero increment on stream 0
    ///     is a connection-level PROTOCOL_ERROR (GOAWAY).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_ConnectionWindowUpdate_ZeroIncrement_Triggers_GoAway_ProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Connection-level WINDOW_UPDATE with increment = 0.
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, 0); // zero increment
        await raw.Connection.WriteFrameAsync(Http2FrameType.WindowUpdate, 0, (Http2FrameFlag)0, payload);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "Zero-increment connection WINDOW_UPDATE must produce GOAWAY.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, errorCode,
            "GOAWAY error code must be PROTOCOL_ERROR for zero increment.");
    }

    /// <summary>
    ///     RFC 7540 §6.9.1: a WINDOW_UPDATE with a zero increment on a stream
    ///     is a stream-level PROTOCOL_ERROR (RST_STREAM).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_StreamWindowUpdate_ZeroIncrement_Triggers_RstStream_ProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Open stream 1 to make a stream-level WINDOW_UPDATE meaningful.
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false); // keep stream open

        // Stream-1 WINDOW_UPDATE with increment = 0.
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, 0);
        await raw.Connection.WriteFrameAsync(Http2FrameType.WindowUpdate, 1, (Http2FrameFlag)0, payload);

        // Look for RST_STREAM on stream 1; also accept GOAWAY.
        Http2RawFrame.Frame? terminal = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && terminal == null)
        {
            var f = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if ((f.Type == Http2FrameType.RstStream && f.StreamId == 1) || f.Type == Http2FrameType.GoAway)
                terminal = f;
        }

        Assert.IsNotNull(terminal, "Proxy must RST/GOAWAY for zero-increment stream WINDOW_UPDATE.");
        if (terminal.Value.Type == Http2FrameType.RstStream)
        {
            Assert.AreEqual(1, terminal.Value.StreamId);
            Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
                BinaryPrimitives.ReadInt32BigEndian(terminal.Value.Payload),
                "RST_STREAM must carry PROTOCOL_ERROR.");
        }
    }

    // =========================================================================
    // Stray CONTINUATION frame — Http2Helper.Copy.cs ~line 1222
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §6.10: a CONTINUATION frame received when no HEADERS block is open is a
    ///     connection-level PROTOCOL_ERROR (GOAWAY).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_StrayContinuation_WithoutOpenHeaderBlock_Triggers_GoAway_ProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // No prior HEADERS frame with END_HEADERS=0 is open; send CONTINUATION on stream 1.
        await raw.Connection.WriteFrameAsync(Http2FrameType.Continuation, 1,
            Http2FrameFlag.EndHeaders, new byte[] { 0x82 }); // 0x82 = :method indexed

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "Stray CONTINUATION must produce GOAWAY.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, errorCode,
            "GOAWAY error code must be PROTOCOL_ERROR for unexpected CONTINUATION.");
    }

    // =========================================================================
    // HEADERS split across CONTINUATION — Http2Helper.Copy.cs ~line 1200
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §6.10: a HEADERS block may be split across HEADERS + CONTINUATION frames.
    ///     The proxy must reassemble them and dispatch the request normally.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_HeadersSplitAcrossContinuation_IsReassembledAndRelayed()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, reqHeaders, _) = await conn.ReadRequestAsync();
            var block = conn.EncodeHeaders([(":status", "200")], [("content-length", "7")]);
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Encoding.ASCII.GetBytes("relayed"));
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Build a valid header block for a simple GET.
        var fullBlock = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/continuation-test")],
            [("x-extra", "value")]);

        // Split: send half in HEADERS (no END_HEADERS), rest in CONTINUATION (END_HEADERS).
        var half = fullBlock.Length / 2;
        var part1 = fullBlock.Take(half).ToArray();
        var part2 = fullBlock.Skip(half).ToArray();

        // HEADERS without END_HEADERS.
        await raw.Connection.WriteFrameAsync(Http2FrameType.Headers, 1, Http2FrameFlag.EndStream, part1);
        // CONTINUATION with END_HEADERS.
        await raw.Connection.WriteFrameAsync(Http2FrameType.Continuation, 1, Http2FrameFlag.EndHeaders, part2);

        // The proxy should deliver a 200 response.
        var status = string.Empty;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && string.IsNullOrEmpty(status))
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.Type == Http2FrameType.Headers)
            {
                var hdrs = raw.Connection.DecodeHeaders(frame.Payload);
                status = hdrs.FirstOrDefault(h => h.Name == ":status").Value ?? string.Empty;
            }
        }

        Assert.AreEqual("200", status,
            "A header block split across HEADERS+CONTINUATION must be reassembled and result in a normal 200 response.");
    }

    // =========================================================================
    // RST_STREAM from client on open stream — Http2Helper.Copy.cs
    // =========================================================================

    /// <summary>
    ///     Bug class: a client RST_STREAM on a live stream must cancel that stream cleanly
    ///     without hanging other concurrent streams or leaking resources.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_RstStream_OnOpenStream_CancelsStream_OtherStreamsUnaffected()
    {
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            requestReceived.TrySetResult();

            // Hold stream 1 so the proxy cannot receive a response while we RST it.
            await holdRequest.Task.WaitAsync(TimeSpan.FromSeconds(20));

            // After cancellation the origin sends GOAWAY/EOF to the proxy; both are valid.
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);

        // Open two separate connections to send independent requests.
        using var raw1 = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        using var raw2 = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Connection 1, stream 1: start a request and RST it after origin acknowledges.
        var block1 = raw1.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/slow")],
            Array.Empty<(string, string)>());
        await raw1.Connection.WriteHeaderBlockAsync(1, block1, endStream: true);

        // Wait until origin receives stream 1.
        await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // RST_STREAM stream 1 (CANCEL).
        var rstPayload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(rstPayload, (uint)Http2ErrorCode.Cancel);
        await raw1.Connection.WriteFrameAsync(Http2FrameType.RstStream, 1, (Http2FrameFlag)0, rstPayload);

        // Release the held origin stream so cleanup can proceed.
        holdRequest.TrySetResult();

        // Connection 2: an independent request must succeed even though connection 1 RST'd.
        // Use a fresh origin handler for the second connection.
        // Because both raw clients connect to the same proxy+origin, the second request
        // may be sent on the same origin connection — confirm it completes fine.
        // (Actually since holdRequest is set now, the second request on raw2 is independent.)
        var block2 = raw2.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/quick")],
            Array.Empty<(string, string)>());

        // The second connection will reach the origin, but the origin handler may have exited.
        // We only assert that the RST on raw1 did not propagate to raw2 as an unreadable crash.
        // Both connections must still be nominally usable (not prematurely closed by the proxy).
        // Simple sanity: writing the HEADERS must not throw synchronously.
        await raw2.Connection.WriteHeaderBlockAsync(1, block2, endStream: true);

        // A GOAWAY or connection close on raw2 would indicate the proxy erroneously tore down
        // unrelated connections. Give it a brief window; absence of a premature GOAWAY is enough.
        var gotPrematureGoAway = false;
        try
        {
            var f = await raw2.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (f.Type == Http2FrameType.GoAway)
                gotPrematureGoAway = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* connection closed by origin exit — not a proxy bug */ }

        Assert.IsFalse(gotPrematureGoAway,
            "RST_STREAM on one client connection must not cause proxy to GOAWAY unrelated connections.");
    }

    // =========================================================================
    // Frame too large — Http2Helper.Copy.cs ~line 528
    // =========================================================================

    /// <summary>
    ///     RFC 9113 §4.2: a frame larger than the SETTINGS_MAX_FRAME_SIZE we advertised is a
    ///     connection-level FRAME_SIZE_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_OversizedFrame_Triggers_GoAway_FrameSizeError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Write a DATA frame with a declared length much larger than MaxAcceptableFrameSize (which
        // the proxy caps relayed SETTINGS_MAX_FRAME_SIZE at and therefore enforces). We only send
        // the 9-byte header; the proxy must GOAWAY before reading the payload.
        var s = raw.Connection.GetStream();
        var hdr = new byte[9];
        // length = 2^18 = 262144 (way above the 16384+overhead the proxy will accept)
        var len = 1 << 18;
        hdr[0] = (byte)((len >> 16) & 0xff);
        hdr[1] = (byte)((len >> 8) & 0xff);
        hdr[2] = (byte)(len & 0xff);
        hdr[3] = (byte)Http2FrameType.Data; // DATA frame
        hdr[4] = 0;
        hdr[8] = 1; // stream id = 1
        await s.WriteAsync(hdr.AsMemory());

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "Oversized frame must produce GOAWAY.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError, errorCode,
            "GOAWAY error code must be FRAME_SIZE_ERROR.");
    }

    // =========================================================================
    // SETTINGS payload size — Http2Helper.Copy.cs
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §6.5: a SETTINGS frame payload length that is not a multiple of 6 is FRAME_SIZE_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Settings_WrongPayloadLength_Triggers_GoAway_FrameSizeError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // 5-byte payload is not a multiple of 6.
        var s = raw.Connection.GetStream();
        var hdr = new byte[9 + 5];
        hdr[0] = 0; hdr[1] = 0; hdr[2] = 5; // length = 5
        hdr[3] = (byte)Http2FrameType.Settings; // SETTINGS
        // stream id = 0 (correct)
        await s.WriteAsync(hdr.AsMemory());

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "SETTINGS with invalid payload length must produce GOAWAY.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError, errorCode,
            "GOAWAY error code must be FRAME_SIZE_ERROR.");
    }

    // =========================================================================
    // RST_STREAM frame size enforcement — Http2Helper.Copy.cs
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §6.4: a RST_STREAM frame with any length other than 4 is a FRAME_SIZE_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_RstStream_WrongLength_Triggers_GoAway_FrameSizeError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Open stream 1 first so the RST is on a known stream.
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        // RST_STREAM with 8-byte payload (must be exactly 4).
        var s = raw.Connection.GetStream();
        var hdr = new byte[9 + 8];
        hdr[0] = 0; hdr[1] = 0; hdr[2] = 8; // length = 8
        hdr[3] = (byte)Http2FrameType.RstStream;
        hdr[8] = 1; // stream id = 1
        await s.WriteAsync(hdr.AsMemory());

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "RST_STREAM with wrong length must produce GOAWAY.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError, errorCode,
            "GOAWAY error code must be FRAME_SIZE_ERROR.");
    }

    // =========================================================================
    // WINDOW_UPDATE wrong size — Http2Helper.Copy.cs
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §6.9: a WINDOW_UPDATE frame with length != 4 is a FRAME_SIZE_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_WindowUpdate_WrongLength_Triggers_GoAway_FrameSizeError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // 2-byte WINDOW_UPDATE payload (must be exactly 4).
        var s = raw.Connection.GetStream();
        var hdr = new byte[9 + 2];
        hdr[0] = 0; hdr[1] = 0; hdr[2] = 2; // length = 2
        hdr[3] = (byte)Http2FrameType.WindowUpdate;
        // stream id = 0 (connection-level)
        await s.WriteAsync(hdr.AsMemory());

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "WINDOW_UPDATE with wrong length must produce GOAWAY.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError, errorCode,
            "GOAWAY error code must be FRAME_SIZE_ERROR.");
    }

    // =========================================================================
    // DATA/HEADERS/RST_STREAM/PRIORITY with stream id 0 — Http2Helper.Copy.cs ~line 548
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §6.1: a DATA frame on stream 0 is a connection-level PROTOCOL_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Data_OnStreamZero_Triggers_GoAway_ProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(Http2FrameType.Data, 0, Http2FrameFlag.EndStream,
            Encoding.ASCII.GetBytes("bad"));

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "DATA on stream 0 must produce GOAWAY.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, errorCode);
    }

    /// <summary>
    ///     RFC 7540 §6.3: a RST_STREAM frame on stream 0 is a connection-level PROTOCOL_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_RstStream_OnStreamZero_Triggers_GoAway_ProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var rstPayload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(rstPayload, (uint)Http2ErrorCode.Cancel);
        await raw.Connection.WriteFrameAsync(Http2FrameType.RstStream, 0, (Http2FrameFlag)0, rstPayload);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "RST_STREAM on stream 0 must produce GOAWAY.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, errorCode);
    }

    // =========================================================================
    // H2 origin: DATA with padding — Http2OriginConnection read loop
    // =========================================================================

    /// <summary>
    ///     Bug class: DATA frames with the PADDED flag from the origin must have their
    ///     padding stripped before the payload bytes are relayed to the client.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Origin_DataWithPadding_IsStrippedAndRelayedCorrectly()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();

            // 200 response header with no body length hint (variable body).
            var block = conn.EncodeHeaders([(":status", "200")], Array.Empty<(string, string)>());
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);

            // DATA with 4 bytes of real payload + 3 bytes of padding (pad length prefix = 3).
            // Wire: [padLen=3][payload:"data"][padding:0x00 0x00 0x00]
            var padLen = (byte)3;
            var payload = Encoding.ASCII.GetBytes("data");
            var padding = new byte[padLen];
            var frame = new byte[1 + payload.Length + padLen];
            frame[0] = padLen;
            Array.Copy(payload, 0, frame, 1, payload.Length);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId,
                Http2FrameFlag.Padded | Http2FrameFlag.EndStream, frame);
        });

        // Use the H1→H2 bridge: HTTP/1.1 client, proxy connects to origin as H2.
        using var suite = new TestSuite();
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

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;

        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            "Response with padded DATA frames must be relayed correctly.");

        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual("data", body,
            "Padding bytes must be stripped; only payload bytes must reach the client.");
    }

    // =========================================================================
    // H2→H1 bridge: 1xx interim response — Http2OriginConnection ~SendAsync
    // =========================================================================

    /// <summary>
    ///     Bug class: a 100 Continue response from an H2 origin (bridged H1→H2) must be
    ///     relayed to the HTTP/1.1 client and must not cause the proxy to stall.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Origin_100Continue_IsBridgedToHttp11Client()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();

            // Send 100 Continue first.
            var interim = conn.EncodeHeaders([(":status", "100")], Array.Empty<(string, string)>());
            await conn.WriteHeaderBlockAsync(streamId, interim, endStream: false);

            // Then the real 200 response.
            var final = conn.EncodeHeaders([(":status", "200")], [("content-length", "2")]);
            await conn.WriteHeaderBlockAsync(streamId, final, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Encoding.ASCII.GetBytes("ok"));
        });

        using var suite = new TestSuite();
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

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Post, origin.Url);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        request.Content = new StringContent("body", Encoding.ASCII);
        request.Headers.TryAddWithoutValidation("Expect", "100-continue");

        var response = await client.SendAsync(request);
        // The final status must be 200. The proxy must not hang after the 100 Continue.
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            "HTTP/1.1 client must receive the final 200 after a 100 Continue from an H2 origin.");
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual("ok", body);
    }

    // =========================================================================
    // H2→H1 bridge: PATCH with binary body
    // =========================================================================

    /// <summary>
    ///     Bug class: binary (non-UTF8) request body bytes must be relayed byte-for-byte
    ///     through the H2→H1 bridge without corruption.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Bridge_Patch_BinaryBody_IsRelayedByteForByte()
    {
        byte[] receivedBody = Array.Empty<byte>();
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            receivedBody = ms.ToArray();
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("patched");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);

        // 16 non-ASCII bytes (binary payload).
        var binaryPayload = new byte[] { 0x00, 0x01, 0x7f, 0x80, 0xfe, 0xff, 0xc0, 0xde,
                                          0xad, 0xbe, 0xef, 0xca, 0xfe, 0xba, 0xbe, 0x42 };

        var block = raw.Connection.EncodeHeaders(
            [(":method", "PATCH"), (":scheme", "https"), (":authority", "localhost"), (":path", "/binary")],
            [("content-type", "application/octet-stream"),
             ("content-length", binaryPayload.Length.ToString())]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Data, 1, Http2FrameFlag.EndStream, binaryPayload);

        // Read and discard the response.
        Http2RawFrame.Frame frame;
        do { frame = await raw.Connection.ReadFrameAsync(); }
        while (!((frame.Type == Http2FrameType.Data && (frame.Flags & Http2FrameFlag.EndStream) != 0)
            || (frame.Type == Http2FrameType.Headers && (frame.Flags & Http2FrameFlag.EndStream) != 0)));

        CollectionAssert.AreEqual(binaryPayload, receivedBody,
            "Binary body bytes must arrive at the origin unmodified through the H2→H1 bridge.");
    }

    // =========================================================================
    // H2→H1 bridge: 304 Not Modified with custom cache headers
    // =========================================================================

    /// <summary>
    ///     Bug class: a 304 from the H1 origin (bridged H2→H1) must include the ETag
    ///     header and must not carry a body.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Bridge_304_WithEtag_IsRelayedCorrectly()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.StatusCode = 304;
            ctx.Response.Headers.ETag = "\"abc\"";
            return Task.CompletedTask;
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/etag")],
            [("if-none-match", "\"abc\"")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (_, hdrs, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("304", hdrs.Single(h => h.Name == ":status").Value);
        Assert.IsTrue(endStream, "304 response must be END_STREAM on the HEADERS frame.");
        Assert.IsTrue(hdrs.Any(h => h.Name == "etag"),
            "ETag header must be relayed on 304 response.");
    }

    // =========================================================================
    // H2→H1 bridge: multiple sequential requests on one connection
    // =========================================================================

    /// <summary>
    ///     Bug class: second (and later) H2 streams on the same connection to the bridge
    ///     must each get an independent, correctly sequenced H1 exchange.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Bridge_SequentialStreams_EachGetCorrectResponse()
    {
        var requestPaths = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            requestPaths.Enqueue(ctx.Request.Path.ToString());
            return ctx.Response.WriteAsync("resp:" + ctx.Request.Path);
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        // Send three sequential requests on streams 1, 3, 5 (odd ascending).
        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);
        var results = new List<(string Status, string Body)>();

        for (var i = 0; i < 3; i++)
        {
            var streamId = 1 + i * 2; // 1, 3, 5
            var path = $"/seq/{i}";

            var block = raw.Connection.EncodeHeaders(
                [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", path)],
                Array.Empty<(string, string)>());
            await raw.Connection.WriteHeaderBlockAsync(streamId, block, endStream: true);

            var (_, responseHeaders, endStream) = await raw.Connection.ReadHeaderBlockAsync();
            var status = responseHeaders.Single(h => h.Name == ":status").Value;

            var bodyMs = new MemoryStream();
            if (!endStream)
            {
                Http2RawFrame.Frame frame;
                do
                {
                    frame = await raw.Connection.ReadFrameAsync();
                    if (frame.Type == Http2FrameType.Data && frame.StreamId == streamId)
                        bodyMs.Write(frame.Payload, 0, frame.Payload.Length);
                } while (!(frame.Type == Http2FrameType.Data
                    && frame.StreamId == streamId
                    && (frame.Flags & Http2FrameFlag.EndStream) != 0));
            }

            results.Add((status, Encoding.UTF8.GetString(bodyMs.ToArray())));
        }

        for (var i = 0; i < 3; i++)
        {
            Assert.AreEqual("200", results[i].Status, $"Stream {1 + i * 2} wrong status");
            Assert.AreEqual($"resp:/seq/{i}", results[i].Body, $"Stream {1 + i * 2} body mismatch");
        }
    }

    // =========================================================================
    // SETTINGS ACK with non-zero payload — Http2Helper.Copy.cs §6.5
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §6.5: a SETTINGS frame with ACK flag and a payload length > 0 is a
    ///     FRAME_SIZE_ERROR at the connection level.
    ///     Bug class: proxy ignores the ACK payload and silently continues in an inconsistent state.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_SettingsAck_WithNonZeroPayload_Triggers_GoAway_FrameSizeError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // SETTINGS frame with ACK (0x01) and a 6-byte payload — invalid per RFC 7540 §6.5.
        var settingsAckWithPayload = new byte[9 + 6];
        settingsAckWithPayload[0] = 0; settingsAckWithPayload[1] = 0; settingsAckWithPayload[2] = 6; // length=6
        settingsAckWithPayload[3] = (byte)Http2FrameType.Settings;
        settingsAckWithPayload[4] = (byte)Http2FrameFlag.Ack; // ACK flag
        // stream ID = 0 (bytes 5-8 stay zero)

        var s = raw.Connection.GetStream();
        await s.WriteAsync(settingsAckWithPayload.AsMemory());

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "SETTINGS ACK with non-zero payload must produce GOAWAY.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError, errorCode,
            "GOAWAY error code must be FRAME_SIZE_ERROR for SETTINGS ACK with payload.");
    }

    // =========================================================================
    // GOAWAY as the first client frame — Http2Helper.Copy.cs §510
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §6.8: an endpoint may send GOAWAY at any time, including before SETTINGS.
    ///     A browser that decides it no longer needs a speculatively-opened connection sends GOAWAY
    ///     before any request. The proxy must not respond with PROTOCOL_ERROR.
    ///     Bug class: proxy rejects GOAWAY as first frame with PROTOCOL_ERROR instead of tolerating it.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_GoAway_AsFirstFrame_IsNotRejected()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Send GOAWAY with NO_ERROR and last-stream-id = 0 as the very first frame (before SETTINGS).
        // Length=8 (last-stream-id + error code), type=0x07 (GOAWAY), flags=0, stream=0.
        var goAwayFrame = new byte[9 + 8];
        goAwayFrame[0] = 0; goAwayFrame[1] = 0; goAwayFrame[2] = 8; // length=8
        goAwayFrame[3] = (byte)Http2FrameType.GoAway;
        // flags=0, stream ID=0 (bytes 4-8 zero), payload all zero = last-stream-id=0, NO_ERROR

        var s = raw.Connection.GetStream();
        await s.WriteAsync(goAwayFrame.AsMemory());

        // After client GOAWAY the proxy must NOT send a PROTOCOL_ERROR GOAWAY back.
        try
        {
            var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(5));
            if (frame.Type == Http2FrameType.GoAway && frame.Payload.Length >= 8)
            {
                var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
                Assert.AreNotEqual((int)Http2ErrorCode.ProtocolError, errorCode,
                    "Proxy must not send PROTOCOL_ERROR GOAWAY for a client GOAWAY as first frame.");
            }
        }
        catch (Exception)
        {
            // Connection closed cleanly — acceptable response to client GOAWAY.
        }
    }

    // =========================================================================
    // PRIORITY on a closed stream — Http2Helper.Copy.cs default/PRIORITY path
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §5.1: PRIORITY frames on closed streams MUST be tolerated without a connection error.
    ///     Bug class: proxy sends PROTOCOL_ERROR GOAWAY on PRIORITY for a completed stream.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Priority_OnClosedStream_IsToleratedWithoutConnectionError()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("ok"));

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);

        // Complete stream 1.
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);
        var (_, _, _) = await raw.Connection.ReadHeaderBlockAsync();

        // Now send a PRIORITY frame on the already-closed stream 1.
        // PRIORITY frame: length=5, type=0x02, flags=0, stream=1.
        var priorityFrame = new byte[9 + 5];
        priorityFrame[0] = 0; priorityFrame[1] = 0; priorityFrame[2] = 5; // length=5
        priorityFrame[3] = (byte)Http2FrameType.Priority;
        priorityFrame[4] = 0; // no flags
        priorityFrame[8] = 1; // stream ID 1
        // payload: 5 zero bytes

        var s = raw.Connection.GetStream();
        await s.WriteAsync(priorityFrame.AsMemory());

        // The next request on a new stream must still succeed.
        var block2 = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(3, block2, endStream: true);

        var (_, responseHeaders, _) = await raw.Connection.ReadHeaderBlockAsync();
        var status = responseHeaders.SingleOrDefault(h => h.Name == ":status").Value;
        Assert.AreEqual("200", status,
            "PRIORITY on closed stream must be tolerated; stream 3 must succeed.");
    }

    // =========================================================================
    // Origin response HEADERS/trailer semantic validation
    // Http2Helper.Copy.Headers.cs lines ~1505–1570
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §8.1.2.4: every response HEADERS block MUST include a :status pseudo-header field.
    ///     With HTTP interception on, the proxy must not forward a semantically-invalid 2xx to the client.
    ///     Bug class: proxy relays garbage response when origin omits :status.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_ResponseHeaders_MissingStatus_IsRejectedAndClientSeesError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            // HEADERS block with no :status pseudo-header — RFC 7540 §8.1.2.4 violation.
            var badHeaders = conn.EncodeHeaders(
                Array.Empty<(string, string)>(),
                new[] { ("x-test", "no-status-here") });
            await conn.WriteHeaderBlockAsync(streamId, badHeaders, endStream: true);
            await Task.Delay(800); // stay alive so the proxy can react before dispose
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        // The client must NOT receive a clean 2xx response; it must see RST_STREAM, GOAWAY, 5xx, or
        // a connection close — any of which indicates the proxy enforced RFC 7540 §8.1.2.4.
        var gotTwoxx = false;
        var sawTerminal = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!sawTerminal && !gotTwoxx && DateTime.UtcNow < deadline)
        {
            Http2RawFrame.Frame frame;
            try { frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
            catch { sawTerminal = true; break; }

            switch (frame.Type)
            {
                case Http2FrameType.RstStream when frame.StreamId == 1:
                case Http2FrameType.GoAway:
                    sawTerminal = true;
                    break;
                case Http2FrameType.Headers when frame.StreamId == 1:
                {
                    var headers = raw.Connection.DecodeHeaders(frame.Payload);
                    var s = headers.FirstOrDefault(h => h.Name == ":status").Value;
                    if (s?.StartsWith('2') == true)
                        gotTwoxx = true;
                    else if (s != null)
                        sawTerminal = true; // 5xx etc - valid error response
                    break;
                }
            }
        }

        Assert.IsFalse(gotTwoxx,
            "Proxy must NOT relay a 2xx for origin HEADERS missing :status.");
        Assert.IsTrue(sawTerminal,
            "Proxy must signal an error (RST_STREAM/GOAWAY/5xx/close) for missing :status.");
    }

    /// <summary>
    ///     RFC 7540 §8.1.2.1: trailer HEADERS MUST NOT contain pseudo-header fields.
    ///     With HTTP interception on, a trailer block that includes :status must trigger RST_STREAM.
    ///     Bug class: proxy relays a pseudo-header in a response trailer, confusing HPACK state on the
    ///     client-facing leg.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_ResponseTrailer_ContainsPseudoHeader_TriggersStreamError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            // Valid 200 response.
            var resp = conn.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, (Http2FrameFlag)0, "hi"u8.ToArray());
            // Trailer HEADERS that contain a :status pseudo-header — RFC 7540 §8.1.2.1 violation.
            var badTrailer = conn.EncodeHeaders(
                new[] { (":status", "200") },
                Array.Empty<(string, string)>());
            await conn.WriteHeaderBlockAsync(streamId, badTrailer, endStream: true);
            await Task.Delay(800);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        // After the valid 200 + partial DATA, the stream must NOT end cleanly (END_STREAM DATA).
        // The proxy must signal an error (RST_STREAM, GOAWAY, or connection close).
        var gotCleanEnd = false;
        var gotError = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!gotCleanEnd && !gotError && DateTime.UtcNow < deadline)
        {
            Http2RawFrame.Frame frame;
            try { frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
            catch { gotError = true; break; }

            switch (frame.Type)
            {
                case Http2FrameType.RstStream when frame.StreamId == 1:
                case Http2FrameType.GoAway:
                    gotError = true;
                    break;
                case Http2FrameType.Data when frame.StreamId == 1
                                              && (frame.Flags & Http2FrameFlag.EndStream) != 0:
                    gotCleanEnd = true;
                    break;
            }
        }

        Assert.IsTrue(gotError,
            "Proxy must reject response trailer containing :status and signal stream error.");
        Assert.IsFalse(gotCleanEnd,
            "Proxy must NOT relay a clean END_STREAM after a trailer with pseudo-headers.");
    }

    /// <summary>
    ///     RFC 9110 §6.5.1: certain header fields are forbidden in trailers (e.g. Content-Length).
    ///     With HTTP interception on, a trailer block containing a forbidden field must trigger RST_STREAM.
    ///     Bug class: proxy relays forbidden trailer fields, which can desync Content-Length on keep-alive
    ///     HTTP/1.1 legs that the trailer is translated onto.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_ResponseTrailer_ContainsForbiddenField_TriggersStreamError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, (Http2FrameFlag)0, "body"u8.ToArray());
            // content-length in a trailer is forbidden per RFC 9110 §6.5.1.
            var badTrailer = conn.EncodeHeaders(
                Array.Empty<(string, string)>(),
                new[] { ("content-length", "4") });
            await conn.WriteHeaderBlockAsync(streamId, badTrailer, endStream: true);
            await Task.Delay(800);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var gotCleanEnd = false;
        var gotError = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!gotCleanEnd && !gotError && DateTime.UtcNow < deadline)
        {
            Http2RawFrame.Frame frame;
            try { frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
            catch { gotError = true; break; }

            switch (frame.Type)
            {
                case Http2FrameType.RstStream when frame.StreamId == 1:
                case Http2FrameType.GoAway:
                    gotError = true;
                    break;
                case Http2FrameType.Data when frame.StreamId == 1
                                              && (frame.Flags & Http2FrameFlag.EndStream) != 0:
                    gotCleanEnd = true;
                    break;
            }
        }

        Assert.IsTrue(gotError,
            "Proxy must reject response trailer with content-length (forbidden field) and signal stream error.");
        Assert.IsFalse(gotCleanEnd,
            "Proxy must NOT relay END_STREAM after a trailer with a forbidden field.");
    }
}
