using System;
using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     <c>Http2Helper.Copy</c> early-reject paths must discard unread payloads before
///     GOAWAY so the client always observes the connection error frame.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyRejectPayloadDrainGapCoverageTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task IdleOriginAsync(Http2RawOriginServer origin)
    {
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try { while (true) await conn.ReadFrameAsync(); } catch { }
        });
        await Task.CompletedTask;
    }

    private static async Task<(Http2FrameType Type, int Error)> ReadRstOrGoAwayAsync(
        Http2RawClient raw, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.RstStream)
                return (frame.Type, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(0, 4)));
            if (frame.Type == Http2FrameType.GoAway)
                return (frame.Type, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
        }

        Assert.Fail("Timed out waiting for RST/GOAWAY.");
        return default;
    }

    /// <summary>
    ///     Bug class: first frame after preface is DATA with payload; GOAWAY is lost on desync.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Preface_FirstDataFrame_WithPayload_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var uri = new Uri(origin.Url);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", proxy.ProxyEndPoints[0].Port);
        var ns = tcp.GetStream();
        var connectReq = Encoding.ASCII.GetBytes(
            $"CONNECT {uri.Host}:{uri.Port} HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\n\r\n");
        await ns.WriteAsync(connectReq);
        var buf = new byte[1];
        var sb = new StringBuilder();
        while (!sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var n = await ns.ReadAsync(buf.AsMemory(0, 1));
            if (n == 0) break;
            sb.Append((char)buf[0]);
        }

        await using var sslStream = new SslStream(ns, false,
            (_, cert, _, errors) => TestCertificateAuthority.Validate(cert, errors));
        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = uri.Host,
            ApplicationProtocols = [SslApplicationProtocol.Http2],
        });

        await sslStream.WriteAsync(Http2Helper.ConnectionPreface.AsMemory());
        var payload = Encoding.ASCII.GetBytes("preface");
        var hdr = new byte[9];
        var len = payload.Length;
        hdr[0] = (byte)((len >> 16) & 0xff);
        hdr[1] = (byte)((len >> 8) & 0xff);
        hdr[2] = (byte)(len & 0xff);
        hdr[3] = (byte)Http2FrameType.Data;
        hdr[4] = (byte)Http2FrameFlag.EndStream;
        hdr[8] = 1;
        await sslStream.WriteAsync(hdr.AsMemory());
        await sslStream.WriteAsync(payload.AsMemory());

        var conn = new Http2RawFrame.Connection(sslStream);
        using var raw = Http2RawClient.Adopt(tcp, conn);
        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: first frame after preface is PING (not SETTINGS); GOAWAY is lost
    ///     if the 8-byte payload is not drained.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Preface_FirstPingFrame_WithPayload_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var uri = new Uri(origin.Url);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", proxy.ProxyEndPoints[0].Port);
        var ns = tcp.GetStream();
        var connectReq = Encoding.ASCII.GetBytes(
            $"CONNECT {uri.Host}:{uri.Port} HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\n\r\n");
        await ns.WriteAsync(connectReq);
        var buf = new byte[1];
        var sb = new StringBuilder();
        while (!sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var n = await ns.ReadAsync(buf.AsMemory(0, 1));
            if (n == 0) break;
            sb.Append((char)buf[0]);
        }

        await using var sslStream = new SslStream(ns, false,
            (_, cert, _, errors) => TestCertificateAuthority.Validate(cert, errors));
        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = uri.Host,
            ApplicationProtocols = [SslApplicationProtocol.Http2],
        });

        await sslStream.WriteAsync(Http2Helper.ConnectionPreface.AsMemory());
        var conn = new Http2RawFrame.Connection(sslStream);
        await conn.WriteFrameAsync(
            Http2FrameType.Ping, 0, (Http2FrameFlag)0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        using var raw = Http2RawClient.Adopt(tcp, conn);
        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: SETTINGS on a non-zero stream leaves payload unread and the client never sees GOAWAY.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Settings_OnNonZeroStream_WithPayload_DrainsGoAway()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var entry = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(0, 2), (ushort)Http2SettingsId.MaxConcurrentStreams);
        BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(2, 4), 50);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Settings, 5, 0, entry);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: PING on a non-zero stream leaves payload unread and the client never sees GOAWAY.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Ping_OnNonZeroStream_WithPayload_DrainsGoAway()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Ping, 9, 0, new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 });

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: first frame after preface is WINDOW_UPDATE; GOAWAY is lost if the
    ///     4-byte increment is not drained.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Preface_FirstWindowUpdateFrame_WithPayload_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var uri = new Uri(origin.Url);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", proxy.ProxyEndPoints[0].Port);
        var ns = tcp.GetStream();
        var connectReq = Encoding.ASCII.GetBytes(
            $"CONNECT {uri.Host}:{uri.Port} HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\n\r\n");
        await ns.WriteAsync(connectReq);
        var buf = new byte[1];
        var sb = new StringBuilder();
        while (!sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var n = await ns.ReadAsync(buf.AsMemory(0, 1));
            if (n == 0) break;
            sb.Append((char)buf[0]);
        }

        await using var sslStream = new SslStream(ns, false,
            (_, cert, _, errors) => TestCertificateAuthority.Validate(cert, errors));
        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = uri.Host,
            ApplicationProtocols = [SslApplicationProtocol.Http2],
        });

        await sslStream.WriteAsync(Http2Helper.ConnectionPreface.AsMemory());
        var inc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(inc, 1024);
        var conn = new Http2RawFrame.Connection(sslStream);
        await conn.WriteFrameAsync(Http2FrameType.WindowUpdate, 0, 0, inc);
        using var raw = Http2RawClient.Adopt(tcp, conn);
        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: GOAWAY on a non-zero stream leaves optional debug data unread.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task GoAway_OnNonZeroStream_WithPayload_DrainsGoAway()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var payload = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4, 4), (int)Http2ErrorCode.NoError);
        payload[8] = (byte)'d';
        payload[9] = (byte)'b';
        payload[10] = (byte)'g';
        payload[11] = (byte)'!';
        await raw.Connection.WriteFrameAsync(Http2FrameType.GoAway, 9, 0, payload);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: first frame after preface is HEADERS; GOAWAY is lost if the HPACK block is not drained.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Preface_FirstHeadersFrame_WithPayload_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var uri = new Uri(origin.Url);

        using var raw = await ConnectExplicitPostPrefaceAsync(proxy, uri);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":path", "/"), (":authority", "x")],
            []);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Headers, 1, Http2FrameFlag.EndHeaders, block);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: first frame after preface is RST_STREAM; GOAWAY is lost if the error code
    ///     payload is not drained.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Preface_FirstRstStreamFrame_WithPayload_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var uri = new Uri(origin.Url);

        using var raw = await ConnectExplicitPostPrefaceAsync(proxy, uri);
        var rst = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(rst, (int)Http2ErrorCode.Cancel);
        await raw.Connection.WriteFrameAsync(Http2FrameType.RstStream, 3, 0, rst);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: HEADERS on stream 0 sends GOAWAY but the compressed block desyncs the reader.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Headers_OnStreamZero_WithPayload_DrainsGoAway()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var block = raw.Connection.EncodeHeaders([(":status", "200")], []);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Headers, 0, Http2FrameFlag.EndHeaders, block);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: first frame after preface is CONTINUATION; GOAWAY is lost if the block is not drained.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Preface_FirstContinuationFrame_WithPayload_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var uri = new Uri(origin.Url);

        using var raw = await ConnectExplicitPostPrefaceAsync(proxy, uri);
        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Continuation, 1, Http2FrameFlag.EndHeaders, new byte[] { 0x82 });

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: first frame after preface is PUSH_PROMISE; GOAWAY is lost if the block is not drained.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Preface_FirstPushPromiseFrame_WithPayload_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var uri = new Uri(origin.Url);

        using var raw = await ConnectExplicitPostPrefaceAsync(proxy, uri);
        var payload = new byte[6];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), 2);
        payload[4] = 0x82;
        payload[5] = 0x86;
        await raw.Connection.WriteFrameAsync(Http2FrameType.PushPromise, 1, Http2FrameFlag.EndHeaders, payload);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: first frame after preface is PRIORITY; GOAWAY is lost if the 5-byte
    ///     priority payload is not drained.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Preface_FirstPriorityFrame_WithPayload_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var uri = new Uri(origin.Url);

        using var raw = await ConnectExplicitPostPrefaceAsync(proxy, uri);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Priority, 3, 0, new byte[] { 0, 0, 0, 0, 16 });

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: RST_STREAM on stream 0 sends GOAWAY but the error-code payload desyncs the reader.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task RstStream_OnStreamZero_WithPayload_DrainsGoAway()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var rst = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(rst, (int)Http2ErrorCode.Cancel);
        await raw.Connection.WriteFrameAsync(Http2FrameType.RstStream, 0, 0, rst);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: PRIORITY on stream 0 sends GOAWAY but the priority payload desyncs the reader.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Priority_OnStreamZero_WithPayload_DrainsGoAway()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var priority = new byte[5];
        await raw.Connection.WriteFrameAsync(Http2FrameType.Priority, 0, 0, priority);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: CONTINUATION on stream 0 sends GOAWAY but the HPACK block desyncs the reader.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Continuation_OnStreamZero_WithPayload_DrainsGoAway()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Continuation, 0, Http2FrameFlag.EndHeaders, new byte[] { 0x82 });

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: PUSH_PROMISE on stream 0 sends GOAWAY but the promise block desyncs the reader.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task PushPromise_OnStreamZero_WithPayload_DrainsGoAway()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var payload = new byte[6];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), 2);
        payload[4] = 0x82;
        payload[5] = 0x86;
        await raw.Connection.WriteFrameAsync(Http2FrameType.PushPromise, 0, Http2FrameFlag.EndHeaders, payload);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: first frame after preface is oversize DATA; GOAWAY is lost if the
    ///     declared payload is not drained (preface violation path).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Preface_FirstOversizeData_WithFullPayload_DrainsGoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var uri = new Uri(origin.Url);

        using var raw = await ConnectExplicitPostPrefaceAsync(proxy, uri);
        const int oversize = 16385;
        var junk = new byte[oversize];
        Array.Fill(junk, (byte)'z');

        var hdr = new byte[9];
        hdr[0] = (byte)((oversize >> 16) & 0xff);
        hdr[1] = (byte)((oversize >> 8) & 0xff);
        hdr[2] = (byte)(oversize & 0xff);
        hdr[3] = (byte)Http2FrameType.Data;
        hdr[4] = 0;
        hdr[8] = 1;
        await raw.Connection.GetStream().WriteAsync(hdr);
        await raw.Connection.GetStream().WriteAsync(junk);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: after SETTINGS, oversize HEADERS must drain the HPACK block before GOAWAY.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task PostSettings_OversizeHeaders_WithFullPayload_DrainsGoAwayFrameSizeError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        const int oversize = 16385;
        var junk = new byte[oversize];
        Array.Fill(junk, (byte)0x82);

        var hdr = new byte[9];
        hdr[0] = (byte)((oversize >> 16) & 0xff);
        hdr[1] = (byte)((oversize >> 8) & 0xff);
        hdr[2] = (byte)(oversize & 0xff);
        hdr[3] = (byte)Http2FrameType.Headers;
        hdr[4] = (byte)Http2FrameFlag.EndHeaders;
        hdr[8] = 1;
        await raw.Connection.GetStream().WriteAsync(hdr);
        await raw.Connection.GetStream().WriteAsync(junk);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError, code);
    }

    /// <summary>
    ///     Bug class: after SETTINGS, declared frame length above the cap must drain the
    ///     unread payload before GOAWAY so the client observes FRAME_SIZE_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task PostSettings_OversizeData_WithFullPayload_DrainsGoAwayFrameSizeError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        const int oversize = 16385;
        var junk = new byte[oversize];
        Array.Fill(junk, (byte)'z');

        var hdr = new byte[9];
        hdr[0] = (byte)((oversize >> 16) & 0xff);
        hdr[1] = (byte)((oversize >> 8) & 0xff);
        hdr[2] = (byte)(oversize & 0xff);
        hdr[3] = (byte)Http2FrameType.Data;
        hdr[4] = 0;
        hdr[8] = 1;
        await raw.Connection.GetStream().WriteAsync(hdr);
        await raw.Connection.GetStream().WriteAsync(junk);

        var (type, code) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError, code);
    }

    private static async Task<Http2RawClient> ConnectExplicitPostPrefaceAsync(ProxyServer proxy, Uri uri)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", proxy.ProxyEndPoints[0].Port);
        var ns = tcp.GetStream();
        var connectReq = Encoding.ASCII.GetBytes(
            $"CONNECT {uri.Host}:{uri.Port} HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\n\r\n");
        await ns.WriteAsync(connectReq);
        var buf = new byte[1];
        var sb = new StringBuilder();
        while (!sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var n = await ns.ReadAsync(buf.AsMemory(0, 1));
            if (n == 0) break;
            sb.Append((char)buf[0]);
        }

        var sslStream = new SslStream(ns, false,
            (_, cert, _, errors) => TestCertificateAuthority.Validate(cert, errors));
        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = uri.Host,
            ApplicationProtocols = [SslApplicationProtocol.Http2],
        });

        await sslStream.WriteAsync(Http2Helper.ConnectionPreface.AsMemory());
        var conn = new Http2RawFrame.Connection(sslStream);
        return Http2RawClient.Adopt(tcp, conn);
    }
}
