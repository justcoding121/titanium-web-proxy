using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Http3.Dns;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     High-ROI reflection / MemoryStream coverage for handler helpers, HTTP/2 frame utilities,
///     SVCB wire helpers, and response body-write hooks that remain lightly covered.
/// </summary>
[TestClass]
public class HandlerAndProtocolHelperCoverageTests
{
    private static readonly BindingFlags PrivateStatic =
        BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string[] DnsLabelsExampleCom = { "example", "com" };

    private static SessionEventArgs MakeSession(ProxyServer proxy, ProxyEndPoint? endPoint = null)
    {
        endPoint ??= new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        return new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ResponseHandler body-write hooks
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ShouldCallBeforeResponseBodyWrite_TracksSubscription()
    {
        using var proxy = new ProxyServer(false, false, false);
        Assert.IsFalse(proxy.ShouldCallBeforeResponseBodyWrite());
        proxy.OnResponseBodyWrite += (_, _) => Task.CompletedTask;
        Assert.IsTrue(proxy.ShouldCallBeforeResponseBodyWrite());
    }

    [TestMethod]
    public async Task OnBeforeResponseBodyWrite_InvokesHandlerAndAllowsRewrite()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        var called = false;
        proxy.OnResponseBodyWrite += (_, e) =>
        {
            called = true;
            e.BodyBytes = Encoding.ASCII.GetBytes("rewritten");
            return Task.CompletedTask;
        };

        var args = new BeforeBodyWriteEventArgs(session, Encoding.ASCII.GetBytes("original"), false, true);
        await proxy.OnBeforeResponseBodyWrite(args);

        Assert.IsTrue(called);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("rewritten"), args.BodyBytes);
    }

    [TestMethod]
    public async Task OnBeforeResponseBodyWrite_WithoutHandler_IsNoOp()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        var original = Encoding.ASCII.GetBytes("unchanged");
        var args = new BeforeBodyWriteEventArgs(session, original, true, false);

        await proxy.OnBeforeResponseBodyWrite(args);

        Assert.AreSame(original, args.BodyBytes);
        Assert.IsFalse(proxy.ShouldCallBeforeResponseBodyWrite());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ProxyTimeoutHandler
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ResolveClientHeaderTimeout_MapsZeroAndPositiveSeconds()
    {
        using var proxy = new ProxyServer(false, false, false);
        Assert.IsNull(proxy.ResolveClientHeaderTimeout());

        proxy.ClientHeaderTimeoutSeconds = 7;
        Assert.AreEqual(TimeSpan.FromSeconds(7), proxy.ResolveClientHeaderTimeout());
    }

    [TestMethod]
    public void ShouldApplyResponseHeaderTimeout_ExemptsCommittedWebsocketAndSse()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var normal = MakeSession(proxy);
        Assert.IsTrue(ProxyServer.ShouldApplyResponseHeaderTimeout(normal));

        using var committed = MakeSession(proxy);
        typeof(SessionEventArgsBase).GetProperty("IsClientResponseCommitted",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .SetValue(committed, true);
        Assert.IsFalse(ProxyServer.ShouldApplyResponseHeaderTimeout(committed));

        using var websocket = MakeSession(proxy);
        websocket.HttpClient.Request.Headers.AddHeader(KnownHeaders.Upgrade, "websocket");
        Assert.IsFalse(ProxyServer.ShouldApplyResponseHeaderTimeout(websocket));

        using var sse = MakeSession(proxy);
        sse.HttpClient.Request.Headers.AddHeader(KnownHeaders.Accept, "text/event-stream");
        Assert.IsFalse(ProxyServer.ShouldApplyResponseHeaderTimeout(sse));
    }

    [TestMethod]
    public void ResolveTimeoutHelpers_PreferSessionOverrideThenServerDefault()
    {
        using var proxy = new ProxyServer(false, false, false)
        {
            ResponseHeaderTimeoutSeconds = 11,
            IdleReadTimeoutSeconds = 12,
            IdleWriteTimeoutSeconds = 13,
            RequestTimeoutSeconds = 14
        };
        using var session = MakeSession(proxy);

        Assert.AreEqual(TimeSpan.FromSeconds(11), proxy.ResolveResponseHeaderTimeout(session));
        Assert.AreEqual(TimeSpan.FromSeconds(12), proxy.ResolveIdleReadTimeout(session));
        Assert.AreEqual(TimeSpan.FromSeconds(13), proxy.ResolveIdleWriteTimeout(session));
        Assert.AreEqual(TimeSpan.FromSeconds(14), proxy.ResolveRequestTimeout(session));

        session.ResponseHeaderTimeout = TimeSpan.FromSeconds(3);
        session.IdleReadTimeout = TimeSpan.Zero; // disabled override
        session.IdleWriteTimeout = TimeSpan.FromMilliseconds(500);
        session.RequestTimeout = TimeSpan.FromSeconds(9);

        Assert.AreEqual(TimeSpan.FromSeconds(3), proxy.ResolveResponseHeaderTimeout(session));
        Assert.IsNull(proxy.ResolveIdleReadTimeout(session));
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), proxy.ResolveIdleWriteTimeout(session));
        Assert.AreEqual(TimeSpan.FromSeconds(9), proxy.ResolveRequestTimeout(session));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Http2NegotiationHandler helpers
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void IsApplicationProtocolCompatible_HandlesNullEmptyDefaultAndMismatch(bool useHttp2)
    {
        var method = typeof(ProxyServer).GetMethod("IsApplicationProtocolCompatible", PrivateStatic)!;
        var negotiated = useHttp2 ? SslApplicationProtocol.Http2 : SslApplicationProtocol.Http11;

        Assert.IsTrue((bool)method.Invoke(null, [negotiated, null])!);
        Assert.IsTrue((bool)method.Invoke(null, [negotiated, new List<SslApplicationProtocol>()])!);
        Assert.IsTrue((bool)method.Invoke(null, [default(SslApplicationProtocol),
            new List<SslApplicationProtocol> { SslApplicationProtocol.Http2 }])!);
        Assert.IsTrue((bool)method.Invoke(null,
            [negotiated, new List<SslApplicationProtocol> { negotiated }])!);
        Assert.IsFalse((bool)method.Invoke(null,
            [negotiated, new List<SslApplicationProtocol>
            {
                useHttp2 ? SslApplicationProtocol.Http11 : SslApplicationProtocol.Http2
            }])!);
    }

    [TestMethod]
    public void ParseHostAndPort_SplitsAuthorityOrDefaultsPort()
    {
        var method = typeof(ProxyServer).GetMethod("ParseHostAndPort", PrivateStatic)!;

        var withPort = ((string Host, int Port))method.Invoke(null, ["example.com:8443", 443])!;
        Assert.AreEqual("example.com", withPort.Host);
        Assert.AreEqual(8443, withPort.Port);

        var bare = ((string Host, int Port))method.Invoke(null, ["example.com", 443])!;
        Assert.AreEqual("example.com", bare.Host);
        Assert.AreEqual(443, bare.Port);

        var ipv6 = ((string Host, int Port))method.Invoke(null, ["[::1]:9443", 443])!;
        Assert.AreEqual("::1", ipv6.Host);
        Assert.AreEqual(9443, ipv6.Port);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SocksClientHandler.TryReadExactAsync
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task TryReadExactAsync_ReadsAcrossChunks_AndFailsOnEof()
    {
        var method = typeof(ProxyServer).GetMethod("TryReadExactAsync", PrivateStatic)!;
        var buffer = new byte[6];

        await using var ok = new MemoryStream([1, 2, 3, 4, 5, 6]);
        var okTask = (Task<bool>)method.Invoke(null, [ok, buffer, 0, 6, CancellationToken.None])!;
        Assert.IsTrue(await okTask);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6 }, buffer);

        await using var shortStream = new MemoryStream([9, 8]);
        var shortBuf = new byte[4];
        var shortTask = (Task<bool>)method.Invoke(null,
            [shortStream, shortBuf, 0, 4, CancellationToken.None])!;
        Assert.IsFalse(await shortTask);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Http2Helper private helpers
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("abc", false)]
    [DataRow("Abc", true)]
    [DataRow("content-type", false)]
    [DataRow("Content-Type", true)]
    [DataRow("", false)]
    public void HasUpperCaseAscii_DetectsLatinUppercase(string value, bool expected)
    {
        var method = typeof(Http2Helper).GetMethod(
            "HasUpperCaseAscii", PrivateStatic, binder: null, [typeof(ByteString)], modifiers: null)!;
        ByteString name = value.GetByteString();
        Assert.AreEqual(expected, (bool)method.Invoke(null, [name])!);
    }

    [TestMethod]
    [DataRow((byte)'0', true)]
    [DataRow((byte)'9', true)]
    [DataRow((byte)'/', false)]
    [DataRow((byte)':', false)]
    [DataRow((byte)'a', false)]
    public void IsAsciiDigit_ClassifiesBytes(byte value, bool expected)
    {
        var method = typeof(Http2Helper).GetMethod("IsAsciiDigit", PrivateStatic)!;
        Assert.AreEqual(expected, (bool)method.Invoke(null, [value])!);
    }

    [TestMethod]
    public async Task ForceRead_ReadsExactOrStopsAtEof()
    {
        var method = typeof(Http2Helper).GetMethod("ForceRead", PrivateStatic)!;
        var buffer = new byte[8];

        await using var full = new MemoryStream([1, 2, 3, 4, 5]);
        var fullTask = (Task<int>)method.Invoke(null, [full, buffer, 0, 5, CancellationToken.None])!;
        Assert.AreEqual(5, await fullTask);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 0, 0, 0 }, buffer);

        Array.Clear(buffer);
        await using var shortStream = new MemoryStream([7, 8]);
        var shortTask = (Task<int>)method.Invoke(null,
            [shortStream, buffer, 1, 4, CancellationToken.None])!;
        Assert.AreEqual(2, await shortTask);
        Assert.AreEqual(7, buffer[1]);
        Assert.AreEqual(8, buffer[2]);
    }

    [TestMethod]
    public async Task DiscardRejectedFramePayloadAsync_DrainsAvailableBytes()
    {
        var method = typeof(Http2Helper).GetMethod("DiscardRejectedFramePayloadAsync", PrivateStatic)!;
        var payload = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
        await using var input = new MemoryStream(payload);

        var task = (Task)method.Invoke(null, [input, payload.Length, CancellationToken.None])!;
        await task;

        Assert.AreEqual(payload.Length, input.Position);
    }

    [TestMethod]
    public async Task DiscardRejectedFramePayloadAsync_EofAndZeroLength_DoNotThrow()
    {
        var method = typeof(Http2Helper).GetMethod("DiscardRejectedFramePayloadAsync", PrivateStatic)!;

        await using var empty = new MemoryStream();
        await (Task)method.Invoke(null, [empty, 16, CancellationToken.None])!;

        await using var ignored = new MemoryStream([1, 2, 3]);
        await (Task)method.Invoke(null, [ignored, 0, CancellationToken.None])!;
        Assert.AreEqual(0, ignored.Position);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Http3OriginBridge remaining helper edges
    // ─────────────────────────────────────────────────────────────────────────

    private static MethodInfo BridgeMethod(string name) =>
        typeof(Http3OriginBridge).GetMethod(name, PrivateStatic)
        ?? throw new InvalidOperationException($"HTTP/3 bridge method {name} was not found.");

    [TestMethod]
    public void BuildRequestHeaders_StripsRemainingHopByHopHeaders()
    {
        var request = new Request
        {
            Method = "GET",
            IsHttps = true,
            RequestUriString = "https://origin.example/item"
        };
        request.Headers.AddHeader("Keep-Alive", "timeout=5");
        request.Headers.AddHeader("Proxy-Connection", "keep-alive");
        request.Headers.AddHeader("Transfer-Encoding", "chunked");
        request.Headers.AddHeader("Upgrade", "h2c");
        request.Headers.AddHeader("TE", "trailers");
        request.Headers.AddHeader("HTTP2-Settings", "AAMAAABkAAQAAP__");
        request.Headers.AddHeader("Proxy-Authenticate", "Basic");
        request.Headers.AddHeader("X-Keep", "yes");

        var headers = (List<(string, string)>)BridgeMethod("BuildRequestHeaders")
            .Invoke(null, [request, "fallback.example:443"])!;

        Assert.IsTrue(headers.Contains((":authority", "origin.example")));
        Assert.IsTrue(headers.Contains((":path", "/item")));
        Assert.IsTrue(headers.Contains(("x-keep", "yes")));
        Assert.IsFalse(headers.Any(h => h.Item1 is "keep-alive" or "proxy-connection"
            or "transfer-encoding" or "upgrade" or "te" or "http2-settings"
            or "proxy-authenticate"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UdpSvcbDnsResolver wire helpers / backoff reset
    // ─────────────────────────────────────────────────────────────────────────

    private delegate byte[] BuildDnsQueryDelegate(ReadOnlySpan<byte> queryId, string host, ushort rrType);
    private delegate bool ParseAlpnDelegate(ReadOnlySpan<byte> value);
    private delegate bool SkipDnsNameDelegate(ReadOnlySpan<byte> data, ref int offset);

    [TestMethod]
    public void WriteDnsName_EncodesLabelsAndTrimsTrailingDot()
    {
        var method = typeof(UdpSvcbDnsResolver).GetMethod("WriteDnsName", PrivateStatic)!;
        using var ms = new MemoryStream();
        method.Invoke(null, [ms, "Example.COM."]);

        CollectionAssert.AreEqual(
            new byte[] { 7, (byte)'E', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
                3, (byte)'C', (byte)'O', (byte)'M', 0 },
            ms.ToArray());
    }

    [TestMethod]
    public void BuildDnsQuery_EmitsHeaderQuestionAndType()
    {
        var method = typeof(UdpSvcbDnsResolver).GetMethod("BuildDnsQuery", PrivateStatic)!;
        var build = (BuildDnsQueryDelegate)Delegate.CreateDelegate(typeof(BuildDnsQueryDelegate), method);
        var query = build(new byte[] { 0xAB, 0xCD }, "svc.example", 65);

        Assert.AreEqual(0xAB, query[0]);
        Assert.AreEqual(0xCD, query[1]);
        Assert.AreEqual(0x01, query[2]); // RD
        Assert.AreEqual(0x00, query[3]);
        Assert.AreEqual(0x00, query[4]);
        Assert.AreEqual(0x01, query[5]); // QDCOUNT=1
        // trailing QTYPE=65, QCLASS=1
        Assert.AreEqual(0x00, query[^4]);
        Assert.AreEqual(65, query[^3]);
        Assert.AreEqual(0x00, query[^2]);
        Assert.AreEqual(0x01, query[^1]);
        CollectionAssert.AreEqual(
            Encoding.ASCII.GetBytes("svc"),
            query.Skip(12).Skip(1).Take(3).ToArray());
    }

    [TestMethod]
    public void ParseAlpnParam_DetectsH3AcrossEntries()
    {
        var method = typeof(UdpSvcbDnsResolver).GetMethod("ParseAlpnParam", PrivateStatic)!;
        var parse = (ParseAlpnDelegate)Delegate.CreateDelegate(typeof(ParseAlpnDelegate), method);

        Assert.IsTrue(parse(new byte[] { 2, (byte)'h', (byte)'3' }));
        Assert.IsTrue(parse(new byte[] { 2, (byte)'h', (byte)'2', 2, (byte)'h', (byte)'3' }));
        Assert.IsFalse(parse(new byte[] { 2, (byte)'h', (byte)'2' }));
        Assert.IsFalse(parse(new byte[] { 3, (byte)'h', (byte)'3' })); // length overrun
        Assert.IsFalse(parse(ReadOnlySpan<byte>.Empty));
    }

    [TestMethod]
    public void SkipDnsName_HandlesRootPointerReservedAndTruncation()
    {
        var method = typeof(UdpSvcbDnsResolver).GetMethod("SkipDnsName", PrivateStatic)!;
        var skip = (SkipDnsNameDelegate)Delegate.CreateDelegate(typeof(SkipDnsNameDelegate), method);

        var root = new byte[] { 0 };
        var offset = 0;
        Assert.IsTrue(skip(root, ref offset));
        Assert.AreEqual(1, offset);

        var pointer = new byte[] { 0xC0, 0x0C };
        offset = 0;
        Assert.IsTrue(skip(pointer, ref offset));
        Assert.AreEqual(2, offset);

        var reserved = new byte[] { 0x80, 0x01 };
        offset = 0;
        Assert.IsFalse(skip(reserved, ref offset));

        var truncated = new byte[] { 5, (byte)'a', (byte)'b' };
        offset = 0;
        Assert.IsFalse(skip(truncated, ref offset));
    }

    [TestMethod]
    public void NoteQuerySuccess_ClearsBackoffAndHalfOpenState()
    {
        var resolver = new UdpSvcbDnsResolver(new IPEndPoint(IPAddress.Loopback, 53));
        typeof(UdpSvcbDnsResolver).GetField("_consecutiveTransientFailures", PrivateInstance)!
            .SetValue(resolver, 4);
        typeof(UdpSvcbDnsResolver).GetField("_resolverBackoffUntilUtc", PrivateInstance)!
            .SetValue(resolver, DateTime.UtcNow.AddMinutes(2));
        typeof(UdpSvcbDnsResolver).GetField("_halfOpenProbeInFlight", PrivateInstance)!
            .SetValue(resolver, 1);

        typeof(UdpSvcbDnsResolver).GetMethod("NoteQuerySuccess", PrivateInstance)!
            .Invoke(resolver, null);

        Assert.AreEqual(0, (int)typeof(UdpSvcbDnsResolver)
            .GetField("_consecutiveTransientFailures", PrivateInstance)!.GetValue(resolver)!);
        Assert.AreEqual(DateTime.MinValue, (DateTime)typeof(UdpSvcbDnsResolver)
            .GetField("_resolverBackoffUntilUtc", PrivateInstance)!.GetValue(resolver)!);
        Assert.AreEqual(0, (int)typeof(UdpSvcbDnsResolver)
            .GetField("_halfOpenProbeInFlight", PrivateInstance)!.GetValue(resolver)!);
    }

    [TestMethod]
    public void TryEnterResolverProbe_AllowsHealthyAndSingleHalfOpen()
    {
        var resolver = new UdpSvcbDnsResolver(new IPEndPoint(IPAddress.Loopback, 53));
        var enter = typeof(UdpSvcbDnsResolver).GetMethod("TryEnterResolverProbe", PrivateInstance)!;

        Assert.IsTrue((bool)enter.Invoke(resolver, [DateTime.UtcNow])!);

        typeof(UdpSvcbDnsResolver).GetField("_consecutiveTransientFailures", PrivateInstance)!
            .SetValue(resolver, 2);
        typeof(UdpSvcbDnsResolver).GetField("_resolverBackoffUntilUtc", PrivateInstance)!
            .SetValue(resolver, DateTime.UtcNow.AddMinutes(-1));
        typeof(UdpSvcbDnsResolver).GetField("_halfOpenProbeInFlight", PrivateInstance)!
            .SetValue(resolver, 0);

        Assert.IsTrue((bool)enter.Invoke(resolver, [DateTime.UtcNow])!);
        Assert.AreEqual(1, (int)typeof(UdpSvcbDnsResolver)
            .GetField("_halfOpenProbeInFlight", PrivateInstance)!.GetValue(resolver)!);
        Assert.IsFalse((bool)enter.Invoke(resolver, [DateTime.UtcNow])!,
            "Second half-open probe must be rejected while one is in flight.");
    }

    [TestMethod]
    public void ParseDnsResponse_SkipsNonHttpsAnswers_ThenAcceptsHttps()
    {
        var id = new byte[] { 0x11, 0x22 };
        using var buf = new MemoryStream();
        buf.Write(id);
        buf.Write(new byte[] { 0x81, 0x80 });
        buf.Write(new byte[] { 0x00, 0x01 }); // QDCOUNT
        buf.Write(new byte[] { 0x00, 0x02 }); // ANCOUNT
        buf.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 });

        // question
        foreach (var label in DnsLabelsExampleCom)
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            buf.WriteByte((byte)bytes.Length);
            buf.Write(bytes);
        }
        buf.WriteByte(0);
        buf.Write(new byte[] { 0x00, 0x41, 0x00, 0x01 });

        // A record answer (skipped)
        buf.Write(new byte[] { 0xC0, 0x0C });
        buf.Write(new byte[] { 0x00, 0x01, 0x00, 0x01 }); // TYPE A
        buf.Write(new byte[] { 0x00, 0x00, 0x01, 0x2C }); // TTL
        buf.Write(new byte[] { 0x00, 0x04, 1, 2, 3, 4 });

        // HTTPS ServiceMode with h3
        buf.Write(new byte[] { 0xC0, 0x0C });
        buf.Write(new byte[] { 0x00, 0x41, 0x00, 0x01 });
        buf.Write(new byte[] { 0x00, 0x00, 0x01, 0x2C });
        var rdata = new byte[]
        {
            0x00, 0x01, // priority 1
            0x00, // target "."
            0x00, 0x01, 0x00, 0x03, 0x02, (byte)'h', (byte)'3' // alpn=h3
        };
        buf.Write(new byte[] { 0x00, (byte)rdata.Length });
        buf.Write(rdata);

        var result = UdpSvcbDnsResolver.ParseDnsResponseInternal(
            buf.ToArray(), id, "example.com", 443);
        Assert.IsNotNull(result);
        Assert.AreEqual(443, result.AltPort);
    }
}
