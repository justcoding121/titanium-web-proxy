using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Extra reflection seams for Sonar new_coverage (≥80%). Every method asserts.
/// </summary>
[TestClass]
public class SonarGateCoverageBumpTests
{
    private static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    private static MethodInfo BridgeMethod(string name) =>
        typeof(Http3OriginBridge).GetMethod(name, PrivateStatic)
        ?? throw new InvalidOperationException($"HTTP/3 bridge method {name} was not found.");

    private static SessionEventArgs MakeSession(ProxyServer proxy)
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        return new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
    }

    private delegate ReadOnlySpan<byte> OriginAuthorityDelegate(Request request, string sniHost);
    private delegate ReadOnlySpan<byte> OriginPathDelegate(Request request);

    [TestMethod]
    public void RentFramedHeaderBlock_EmptyMultiFramePriorityAndAppend()
    {
        var overloads = typeof(Http2Helper).GetMethods(PrivateStatic)
            .Where(m => m.Name == "RentFramedHeaderBlock")
            .ToArray();
        Assert.IsTrue(overloads.Length >= 2);

        var withAppend = overloads.First(m => m.GetParameters().Length == 9);
        var header = new Http2FrameHeader();
        var buf = new byte[9];

        // Empty block → single HEADERS frame (9 bytes header only).
        var empty = (ArraySegment<byte>)withAppend.Invoke(null,
        [
            header, buf, 1, Http2FrameType.Headers, true, false,
            ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, 16384
        ])!;
        Assert.AreEqual(9, empty.Count);
        ArrayPool<byte>.Shared.Return(empty.Array!);

        // Small max frame size forces CONTINUATION + EndHeaders on last frame.
        var payload = Encoding.ASCII.GetBytes(new string('a', 20));
        var multi = (ArraySegment<byte>)withAppend.Invoke(null,
        [
            header, buf, 3, Http2FrameType.Headers, false, true,
            new ReadOnlyMemory<byte>(payload), ReadOnlyMemory<byte>.Empty, 8
        ])!;
        Assert.IsTrue(multi.Count > 9 + 8);
        Assert.AreEqual((byte)Http2FrameType.Headers, multi.Array![3]);
        Assert.AreEqual((byte)Http2FrameType.Continuation, multi.Array![9 + 8 + 3]);
        ArrayPool<byte>.Shared.Return(multi.Array!);

        // Append spans past data.Length → CopyHeaderBlockSegment append-only / split branches.
        var data = Encoding.ASCII.GetBytes("ABCD");
        var append = Encoding.ASCII.GetBytes("EFGH");
        var combined = (ArraySegment<byte>)withAppend.Invoke(null,
        [
            header, buf, 5, Http2FrameType.Headers, true, false,
            new ReadOnlyMemory<byte>(data), new ReadOnlyMemory<byte>(append), 16
        ])!;
        Assert.AreEqual(9 + 8, combined.Count);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("ABCDEFGH"),
            combined.Array!.AsSpan(9, 8).ToArray());
        ArrayPool<byte>.Shared.Return(combined.Array!);

        // maxFrameSize <= 0 clamps to 16384.
        var clamped = (ArraySegment<byte>)withAppend.Invoke(null,
        [
            header, buf, 7, Http2FrameType.Headers, false, false,
            new ReadOnlyMemory<byte>([1, 2, 3]), ReadOnlyMemory<byte>.Empty, 0
        ])!;
        Assert.AreEqual(12, clamped.Count);
        ArrayPool<byte>.Shared.Return(clamped.Array!);
    }

    [TestMethod]
    public void RentFramedTrailers_EncodesLowercasedNames()
    {
        var settings = new Http2Settings { MaxFrameSize = 16384 };
        var header = new Http2FrameHeader();
        var buf = new byte[9];
        var trailers = new HeaderCollection();
        trailers.AddHeader("X-Trail", "v1");
        trailers.AddHeader("x-already", "v2");

        var framed = Http2Helper.RentFramedTrailers(settings, header, buf, 9, trailers, endStream: true);
        Assert.IsTrue(framed.Count > 9);
        Assert.AreEqual((byte)Http2FrameType.Headers, framed.Array![3]);
        Assert.AreEqual((byte)(Http2FrameFlag.EndHeaders | Http2FrameFlag.EndStream), framed.Array![4]);
        ArrayPool<byte>.Shared.Return(framed.Array!);
    }

    [TestMethod]
    public async Task WriteHeaderBlockAsync_SplitsAcrossContinuationFrames()
    {
        var write = typeof(Http2Helper).GetMethod("WriteHeaderBlockAsync", PrivateStatic)!;
        var header = new Http2FrameHeader();
        var buf = new byte[9];
        var data = Encoding.ASCII.GetBytes(new string('z', 17));
        await using var ms = new MemoryStream();
        await (Task)write.Invoke(null,
        [
            header, buf, 11, Http2FrameType.Headers, true, false,
            new ReadOnlyMemory<byte>(data), 8, ms
        ])!;

        var wire = ms.ToArray();
        Assert.IsTrue(wire.Length >= 9 + 8 + 9 + 8 + 9 + 1);
        Assert.AreEqual((byte)Http2FrameType.Headers, wire[3]);
        Assert.AreEqual((byte)Http2FrameType.Continuation, wire[9 + 8 + 3]);
        Assert.AreEqual((byte)Http2FrameFlag.EndStream,
            (byte)((Http2FrameFlag)wire[4] & Http2FrameFlag.EndStream));
    }

    [TestMethod]
    public void ScheduleFinalize_CoversCompressedRelayBranches()
    {
        var schedule = typeof(Http2Helper).GetMethod("ScheduleFinalize", PrivateStatic)!;
        using var proxy = new ProxyServer(false, false, false);
        using var cts = new CancellationTokenSource();
        var connectionState = new Http2ConnectionState(42, cts);

        // Compressed relay, null SessionArgs → immediate pool return (FinalizedFlag cleared by pool).
        var nullArgs = new Http2StreamState(1);
        Assert.IsTrue(nullArgs.IsCompressedRelay);
        schedule.Invoke(null, [nullArgs, (Func<SessionEventArgs, Task>)(_ => Task.CompletedTask),
            NullLogger.Instance, connectionState]);
        Assert.IsNull(nullArgs.SessionArgs);

        // Sync completed AfterResponse on compressed-relay stream with a session.
        using var sessionOk = MakeSession(proxy);
        var syncState = new Http2StreamState(7, sessionOk);
        syncState.EnableResponseDataCompressedRelay();
        Assert.IsTrue(syncState.IsCompressedRelay);

        var syncCalls = 0;
        schedule.Invoke(null, [syncState, (Func<SessionEventArgs, Task>)(_ =>
        {
            syncCalls++;
            return Task.CompletedTask;
        }), NullLogger.Instance, connectionState]);
        Assert.AreEqual(1, syncCalls);
        Assert.IsNull(syncState.SessionArgs); // returned to pool

        // Second schedule after pool reset: null SessionArgs path (no extra callback).
        schedule.Invoke(null, [syncState, (Func<SessionEventArgs, Task>)(_ =>
        {
            syncCalls++;
            return Task.CompletedTask;
        }), NullLogger.Instance, connectionState]);
        Assert.AreEqual(1, syncCalls);

        // Faulted completed AfterResponse.
        using var sessionFault = MakeSession(proxy);
        var faultState = new Http2StreamState(9, sessionFault);
        faultState.EnableResponseDataCompressedRelay();
        schedule.Invoke(null, [faultState, (Func<SessionEventArgs, Task>)(_ =>
            Task.FromException(new InvalidOperationException("after-fault"))),
            NullLogger.Instance, connectionState]);
        Assert.IsNull(faultState.SessionArgs);

        // Throwing AfterResponse (sync throw).
        using var sessionThrow = MakeSession(proxy);
        var throwState = new Http2StreamState(11, sessionThrow);
        throwState.EnableResponseDataCompressedRelay();
        schedule.Invoke(null, [throwState, (Func<SessionEventArgs, Task>)(_ =>
            throw new InvalidOperationException("after-throw")),
            NullLogger.Instance, connectionState]);
        Assert.IsNull(throwState.SessionArgs);

        // Async AfterResponse → CompleteMitmCompressedFinalizeAsync path.
        using var sessionAsync = MakeSession(proxy);
        var asyncState = new Http2StreamState(15, sessionAsync);
        asyncState.EnableResponseDataCompressedRelay();
        var afterGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        schedule.Invoke(null, [asyncState, (Func<SessionEventArgs, Task>)(async _ =>
        {
            await afterGate.Task;
        }), NullLogger.Instance, connectionState]);
        afterGate.TrySetResult();
        Assert.IsTrue(SpinWait.SpinUntil(() => asyncState.SessionArgs is null, TimeSpan.FromSeconds(2)));

        // Non-compressed path tracks FinalizeStreamAsync.
        using var sessionNormal = MakeSession(proxy);
        var normal = new Http2StreamState(13, sessionNormal);
        Assert.IsFalse(normal.IsCompressedRelay);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        schedule.Invoke(null, [normal, (Func<SessionEventArgs, Task>)(async _ =>
        {
            await tcs.Task;
        }), NullLogger.Instance, connectionState]);
        tcs.TrySetResult();
        Assert.IsTrue(SpinWait.SpinUntil(() => normal.SessionArgs is null, TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public void ViaHelpers_MatchProtocolPseudonymPortAndIpv6Edges()
    {
        var viaEntry = typeof(ProxyServer).GetMethod("ViaEntryMatches", PrivateStatic)!;
        var viaToken = typeof(ProxyServer).GetMethod("ViaTokenMatches", PrivateStatic)!;
        var portSuffix = typeof(ProxyServer).GetMethod("IsNumericPortSuffix", PrivateStatic)!;

        Assert.IsTrue((bool)viaEntry.Invoke(null, ["1.1 titanium", "1.1", "titanium"])!);
        Assert.IsFalse((bool)viaEntry.Invoke(null, ["2.0 titanium", "1.1", "titanium"])!);
        Assert.IsFalse((bool)viaEntry.Invoke(null, ["titanium", "1.1", "titanium"])!); // no protocol RWS
        Assert.IsFalse((bool)viaEntry.Invoke(null, ["\ttitanium", "1.1", "titanium"])!);

        Assert.IsFalse((bool)viaToken.Invoke(null, ["1.1", "titanium"])!); // no received-by
        Assert.IsFalse((bool)viaToken.Invoke(null, ["1.1\t", "titanium"])!); // empty received-by
        Assert.IsTrue((bool)viaToken.Invoke(null, ["1.1 titanium:8080", "titanium"])!);
        Assert.IsFalse((bool)viaToken.Invoke(null, ["1.1 titanium.example", "titanium"])!);
        Assert.IsTrue((bool)viaToken.Invoke(null, ["1.1 [2001:db8::1]", "2001:db8::1"])!);
        Assert.IsTrue((bool)viaToken.Invoke(null, ["1.1 [2001:db8::1]:8443", "2001:db8::1"])!);
        Assert.IsFalse((bool)viaToken.Invoke(null, ["1.1 [2001:db8::1]:abc", "2001:db8::1"])!);
        Assert.IsFalse((bool)viaToken.Invoke(null, ["1.1 [::1]x", "::1"])!);

        Assert.IsFalse((bool)portSuffix.Invoke(null, [""])!);
        Assert.IsFalse((bool)portSuffix.Invoke(null, [":"])!);
        Assert.IsFalse((bool)portSuffix.Invoke(null, ["8080"])!);
        Assert.IsFalse((bool)portSuffix.Invoke(null, [":80a"])!);
        Assert.IsTrue((bool)portSuffix.Invoke(null, [":443"])!);
    }

    [TestMethod]
    public void OriginRequestAuthorityAndPathBytes_CoverFallbacks()
    {
        var auth = BridgeMethod("OriginRequestAuthorityBytes")
            .CreateDelegate<OriginAuthorityDelegate>();
        var path = BridgeMethod("OriginRequestPathBytes")
            .CreateDelegate<OriginPathDelegate>();

        var withAuth = new Request { Method = "GET", Authority = "auth.example".GetByteString() };
        Assert.AreEqual("auth.example", Encoding.ASCII.GetString(auth(withAuth, "sni.example")));

        var withHost = new Request { Method = "GET", Host = "host.example" };
        Assert.AreEqual("host.example", Encoding.ASCII.GetString(auth(withHost, "sni.example")));

        var fallback = new Request { Method = "HEAD" };
        Assert.AreEqual("sni.example", Encoding.ASCII.GetString(auth(fallback, "sni.example")));

        Assert.AreEqual("/", Encoding.ASCII.GetString(path(fallback)));
        var withPath = new Request { Method = "GET", RequestUriString8 = "/q?x=1".GetByteString() };
        Assert.AreEqual("/q?x=1", Encoding.ASCII.GetString(path(withPath)));
    }

    [TestMethod]
    public void ResolveTransparentForwardTarget_PrefersUpstreamThenForwardHost()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        session.UpstreamConnectHost = "routed.example";
        session.UpstreamConnectPort = 8443;
        var routed = ((string? Host, int? Port))BridgeMethod("ResolveTransparentForwardTarget")
            .Invoke(null, [session])!;
        Assert.AreEqual("routed.example", routed.Host);
        Assert.AreEqual(8443, routed.Port);

        var transparent = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            ForwardHost = "fwd.example",
            ForwardPort = 9443,
        };
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        using var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        using var tSession = new SessionEventArgs(proxy, transparent, clientStream, null, cts);
        var fwd = ((string? Host, int? Port))BridgeMethod("ResolveTransparentForwardTarget")
            .Invoke(null, [tSession])!;
        Assert.AreEqual("fwd.example", fwd.Host);
        Assert.AreEqual(9443, fwd.Port);
    }

    [TestMethod]
    public void StaticSchemeOverride_NeedFallbackBranches()
    {
        Assert.AreEqual(Http2Helper.StaticSchemeOverrideResult.NeedFallback,
            Http2Helper.TryApplyStaticIndexedSchemeOverride(
                [0x86], "ftp".GetByteString(), out _));

        // Indexed index with 7-bit continuation marker (0x80) → NeedFallback.
        Assert.AreEqual(Http2Helper.StaticSchemeOverrideResult.NeedFallback,
            Http2Helper.TryApplyStaticIndexedSchemeOverride(
                [0x80], ProxyServer.UriSchemeHttp8, out _));

        // Two "from" scheme indices → ambiguous NeedFallback.
        Assert.AreEqual(Http2Helper.StaticSchemeOverrideResult.NeedFallback,
            Http2Helper.TryApplyStaticIndexedSchemeOverride(
                [0x87, 0x87], ProxyServer.UriSchemeHttp8, out _));

        // Dynamic table size update with continuation → NeedFallback.
        Assert.AreEqual(Http2Helper.StaticSchemeOverrideResult.NeedFallback,
            Http2Helper.TryApplyStaticIndexedSchemeOverride(
                [0x3f], ProxyServer.UriSchemeHttp8, out _));

        // Empty block → NeedFallback (no scheme).
        Assert.AreEqual(Http2Helper.StaticSchemeOverrideResult.NeedFallback,
            Http2Helper.TryApplyStaticIndexedSchemeOverride(
                Array.Empty<byte>(), ProxyServer.UriSchemeHttps8, out _));
    }

    [TestMethod]
    public void LinuxFirefoxProxy_ShellQuote_EscapesSingleQuotes()
    {
        var quote = typeof(LinuxFirefoxProxy).GetMethod("ShellQuote", PrivateStatic)!;
        Assert.AreEqual("''", (string)quote.Invoke(null, [null])!);
        Assert.AreEqual("'plain'", (string)quote.Invoke(null, ["plain"])!);
        Assert.AreEqual("'it'\\''s'", (string)quote.Invoke(null, ["it's"])!);
    }

    [TestMethod]
    public void StripHeadersFraming_SpanPriorityAndPaddedCombos()
    {
        var strip = typeof(Http2OriginConnection).GetMethod("StripHeadersFraming", PrivateStatic,
            binder: null, [typeof(ReadOnlySpan<byte>), typeof(Http2FrameFlag)], modifiers: null)!;
        var del = strip.CreateDelegate<StripHeadersSpanDelegate>();

        // Priority only: skip 5-byte dependency/weight prefix.
        var priority = new byte[] { 0, 0, 0, 1, 16, (byte)'a', (byte)'b' };
        CollectionAssert.AreEqual(new byte[] { (byte)'a', (byte)'b' },
            del(priority, Http2FrameFlag.Priority).ToArray());

        // Padded + Priority.
        var both = new byte[] { 1, 0, 0, 0, 1, 16, (byte)'x', 0 };
        CollectionAssert.AreEqual(new byte[] { (byte)'x' },
            del(both, Http2FrameFlag.Padded | Http2FrameFlag.Priority).ToArray());

        // Priority but fewer than 5 bytes after pad strip → keep remaining.
        var shortPri = new byte[] { 0, 1, 2 };
        CollectionAssert.AreEqual(new byte[] { 0, 1, 2 },
            del(shortPri, Http2FrameFlag.Priority).ToArray());
    }

    private delegate ReadOnlySpan<byte> StripHeadersSpanDelegate(
        ReadOnlySpan<byte> payload, Http2FrameFlag flags);

    [TestMethod]
    public void StatusCodeBytesAndShouldOmitHttp2Header_CoverCommonBranches()
    {
        var status = typeof(Http2Helper).GetMethod("StatusCodeBytes",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var code in new[] { 200, 204, 206, 301, 302, 304, 400, 404, 500, 502, 418, 503 })
        {
            var bytes = (ByteString)status.Invoke(null, [code])!;
            Assert.IsTrue(bytes.Length >= 3, "status "+code);
        }

        var omit = typeof(Http2Helper).GetMethod("ShouldOmitHttp2Header",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.IsTrue((bool)omit.Invoke(null, ["Upgrade".GetByteString()])!);
        Assert.IsTrue((bool)omit.Invoke(null, ["Keep-Alive".GetByteString()])!);
        Assert.IsTrue((bool)omit.Invoke(null, ["Proxy-Connection".GetByteString()])!);
        Assert.IsTrue((bool)omit.Invoke(null, ["Transfer-Encoding".GetByteString()])!);
        Assert.IsTrue((bool)omit.Invoke(null, ["te".GetByteString()])!);
        Assert.IsTrue((bool)omit.Invoke(null, ["host".GetByteString()])!);
        Assert.IsFalse((bool)omit.Invoke(null, ["content-length".GetByteString()])!);
        Assert.IsFalse((bool)omit.Invoke(null, ["accept".GetByteString()])!);
    }

    [TestMethod]
    public void Http3ResponseMayHaveBody_ExtraMatrix_CoversRemainingCombos()
    {
        var mayHave = typeof(Http3OriginBridge).GetMethod("ResponseMayHaveBody",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.IsFalse((bool)mayHave.Invoke(null, [101, "GET", 10L, false, false])!);
        Assert.IsFalse((bool)mayHave.Invoke(null, [204, "POST", 5L, true, true])!);
        Assert.IsTrue((bool)mayHave.Invoke(null, [201, "POST", -1L, true, false])!);
        Assert.IsTrue((bool)mayHave.Invoke(null, [200, "PUT", -1L, false, true])!);
        Assert.IsFalse((bool)mayHave.Invoke(null, [304, "GET", -1L, true, true])!);
        Assert.IsTrue((bool)mayHave.Invoke(null, [200, "GET", 1L, false, false])!);
    }
}
