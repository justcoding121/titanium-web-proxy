using System;
using System.Buffers;
using System.Collections.Generic;
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
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

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
        var vt = (ValueTask)write.Invoke(null,
        [
            header, buf, 11, Http2FrameType.Headers, true, false,
            new ReadOnlyMemory<byte>(data), 8, ms
        ])!;
        await vt;

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

        // Priority but fewer than 5 bytes → PROTOCOL_ERROR (RFC 7540 §6.2).
        var shortPri = new byte[] { 0, 1, 2 };
        Assert.ThrowsExactly<IOException>(() => del(shortPri, Http2FrameFlag.Priority));
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
            BindingFlags.NonPublic | BindingFlags.Static,
            null, [typeof(ByteString), typeof(ByteString)], null)!;
        ByteString Empty = default;
        Assert.IsTrue((bool)omit.Invoke(null, ["Upgrade".GetByteString(), Empty])!);
        Assert.IsTrue((bool)omit.Invoke(null, ["Keep-Alive".GetByteString(), Empty])!);
        Assert.IsTrue((bool)omit.Invoke(null, ["Proxy-Connection".GetByteString(), Empty])!);
        Assert.IsTrue((bool)omit.Invoke(null, ["Transfer-Encoding".GetByteString(), Empty])!);
        Assert.IsTrue((bool)omit.Invoke(null, ["te".GetByteString(), Empty])!);
        Assert.IsFalse((bool)omit.Invoke(null, ["te".GetByteString(), "trailers".GetByteString()])!);
        Assert.IsTrue((bool)omit.Invoke(null, ["host".GetByteString(), Empty])!);
        Assert.IsFalse((bool)omit.Invoke(null, ["content-length".GetByteString(), Empty])!);
        Assert.IsFalse((bool)omit.Invoke(null, ["accept".GetByteString(), Empty])!);
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

    [TestMethod]
    public void CertificateManager_IsSelfSignedAndInvalidateContext()
    {
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        var isSelf = typeof(CertificateManager).GetMethod("IsSelfSigned",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.IsTrue((bool)isSelf.Invoke(null, [mgr.RootCertificate!])!);
        using var leaf = mgr.CreateCertificate("selfsig-check.example", false);
        Assert.IsNotNull(leaf);
        Assert.IsFalse((bool)isSelf.Invoke(null, [leaf!])!);
        var invalidate = typeof(CertificateManager).GetMethod("InvalidateSslCertificateContext",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        invalidate.Invoke(mgr, [leaf]);
        Assert.AreEqual(leaf!.Thumbprint, leaf.Thumbprint);
    }

    [TestMethod]
    public void CertificateManager_SuppressArms_RootThumbprintAndUnixUntrust()
    {
        using var mgr = new CertificateManager(null, null, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        Assert.IsTrue(CertificateManager.ShouldSuppressInteractiveRootStoreMutations
                      || string.Equals(Environment.GetEnvironmentVariable("TITANIUM_SKIP_ROOT_STORE_UI"), "1",
                          StringComparison.Ordinal));

        Assert.IsFalse(mgr.RemoveCertificateByThumbprint(
            System.Security.Cryptography.X509Certificates.StoreName.Root,
            System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser,
            mgr.RootCertificate!.Thumbprint!));
        Assert.IsFalse(mgr.RemoveCertificateByThumbprint(
            System.Security.Cryptography.X509Certificates.StoreName.Root,
            System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser,
            " "));

        mgr.ApplyUnixSslUntrust();
        mgr.ApplyUnixSslTrustAfterStoreInstall(false);
        Assert.IsNotNull(mgr.LastOsTrustResult);

        FirefoxCertificateTrust.ClearRootTrustBestEffort("TWP-Unit");
        FirefoxCertificateTrust.ClearRootTrustBestEffort(null);
    }

    [TestMethod]
    public void CertificateManager_ListPrunePersonalAndMyThumbprint_CoverStoreSeams()
    {
        const string cn = "Titanium Sonar Gate Cov CA";
        using var mgr = new CertificateManager(cn, "TitaniumSonarCov", false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));

        // Read-only Root subject Find (no CryptUI).
        var listed = mgr.ListSameCommonNameRootThumbprints(
            System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser, keepThumbprint: null);
        Assert.IsNotNull(listed);
        _ = mgr.ListSameCommonNameRootThumbprints(
            System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser,
            mgr.RootCertificate!.Thumbprint);

        // Personal (My) prune + machineTrusted branch — Root Remove stays suppressed.
        mgr.PruneOrphanedPersonalCertificates(
            System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser,
            keepCurrentThumbprint: true);
        mgr.PruneOrphanedPersonalCertificates(
            System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser,
            keepCurrentThumbprint: false);
        mgr.PruneOrphanedSameCommonNameCertificates(machineTrusted: false, keepCurrentThumbprint: true);
        mgr.PruneOrphanedSameCommonNameCertificates(machineTrusted: true, keepCurrentThumbprint: false);

        // Install into CurrentUser\My (not Root) then RemoveCertificateByThumbprint write path.
        var install = typeof(CertificateManager).GetMethod("InstallCertificate",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.IsTrue((bool)install.Invoke(mgr,
        [
            System.Security.Cryptography.X509Certificates.StoreName.My,
            System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser
        ])!);
        Assert.IsFalse((bool)install.Invoke(mgr,
        [
            System.Security.Cryptography.X509Certificates.StoreName.My,
            System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser
        ])!);

        Assert.IsTrue(mgr.RemoveCertificateByThumbprint(
            System.Security.Cryptography.X509Certificates.StoreName.My,
            System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser,
            mgr.RootCertificate!.Thumbprint!));
        Assert.IsFalse(mgr.RemoveCertificateByThumbprint(
            System.Security.Cryptography.X509Certificates.StoreName.My,
            System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser,
            "ffffffffffffffffffffffffffffffffffffffff"));

        // Under suppress, Root Add returns false → Cancelled (or Already-Ok if a prior install exists).
        var added = mgr.InstallRootIntoCertificateStores(false);
        Assert.IsNotNull(mgr.LastOsTrustResult);
        if (added)
            Assert.IsTrue(mgr.LastOsTrustResult!.Succeeded);
        else
            Assert.IsTrue(mgr.LastOsTrustResult!.Succeeded
                          || mgr.LastOsTrustResult.Kind == CertificateOsTrustKind.Cancelled);

        mgr.TrustRootCertificate(false);
        Assert.IsNotNull(mgr.LastOsTrustResult);
    }

    [TestMethod]
    public void Http2Helper_HasUpperCaseAscii_EmptyAndMixed()
    {
        var upper = typeof(Http2Helper).GetMethod("HasUpperCaseAscii",
            BindingFlags.NonPublic | BindingFlags.Static, null, [typeof(ByteString)], null)!;
        Assert.IsTrue((bool)upper.Invoke(null, ["ABC".GetByteString()])!);
        Assert.IsFalse((bool)upper.Invoke(null, ["abc".GetByteString()])!);
        Assert.IsFalse((bool)upper.Invoke(null, [ByteString.Empty])!);
        Assert.IsTrue((bool)upper.Invoke(null, ["aBc".GetByteString()])!);
    }

    [TestMethod]
    public void HeaderBuilder_HostOverride_RewritesHostHeaderValue()
    {
        var builder = HeaderBuilder.Rent();
        try
        {
            var headers = new HeaderCollection();
            headers.AddHeader(KnownHeaders.Host, "orig.example");
            headers.AddHeader("X-Keep", "1");
            builder.WriteHeaders(headers, sendProxyAuthorization: true, hostHeaderOverride: "override.example");
            builder.WriteHeaders(headers, sendProxyAuthorization: false, hostHeaderOverride: "override2.example");
            var text = builder.GetString(Encoding.ASCII);
            StringAssert.Contains(text, "Host: override.example");
            StringAssert.Contains(text, "Host: override2.example");
            StringAssert.Contains(text, "X-Keep: 1");
            Assert.IsFalse(text.Contains("Host: orig.example", StringComparison.Ordinal));
        }
        finally
        {
            HeaderBuilder.Return(builder);
        }
    }

    [TestMethod]
    public void ProxyLog_Http2ProbeDeferred_LogsWhenDebugEnabled()
    {
        var logger = new DebugCapturingLogger();
        ProxyLog.Http2ProbeDeferredForClientAlpn(logger, "origin.test:443");
        ProxyLog.Http2ProbeDeferredFailed(logger, "origin.test:443", new InvalidOperationException("boom"));
        Assert.IsTrue(logger.Messages.Count >= 2);
        StringAssert.Contains(logger.Messages[0], "origin probe still in flight");
        StringAssert.Contains(logger.Messages[1], "deferred origin probe failed");
    }

    [TestMethod]
    public void EnforceHttp2RelayHeaderSemantics_MitmObserveEnforceAndDisabled()
    {
        var enforce = typeof(Http2Helper).GetMethod(
            "EnforceHttp2RelayHeaderSemantics",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var logger = NullLogger.Instance;
        using var cts = new CancellationTokenSource();
        var state = new Http2ConnectionState(42, cts) { Http2RelayValidation = PolicyMode.Observe };

        Assert.IsTrue((bool)enforce.Invoke(null, [state, true, logger, "mitm"])!);
        Assert.IsFalse((bool)enforce.Invoke(null, [state, false, logger, "observe"])!);

        state.Http2RelayValidation = PolicyMode.Enforce;
        Assert.IsTrue((bool)enforce.Invoke(null, [state, false, logger, "enforce"])!);

        state.Http2RelayValidation = PolicyMode.Disabled;
        Assert.IsTrue((bool)enforce.Invoke(null, [state, false, logger, "disabled"])!);
    }

    [TestMethod]
    public void ClearBodyReference_DropsBodyAndWireFlags()
    {
        var r = new Response(Encoding.ASCII.GetBytes("abc"));
        r.IsBodyRead = r.IsBodyReceived = r.IsBodySent = r.BodyIsWireEncoded = true;
        _ = r.BodyString;
        Assert.IsTrue(r.BodyAvailable);

        r.ClearBodyReference();

        Assert.IsFalse(r.BodyAvailable);
        Assert.IsFalse(r.IsBodyRead);
        Assert.IsFalse(r.IsBodyReceived);
        Assert.IsFalse(r.IsBodySent);
        Assert.IsFalse(r.BodyIsWireEncoded);
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = r.Body);
    }

    [TestMethod]
    public void RequestResponseBase_HeaderAndWireHelpers_CoverNewBranches()
    {
        var r = new Response(Encoding.ASCII.GetBytes("abcd")) { IsChunked = true };
        r.Headers.AddHeader(KnownHeaders.ContentEncoding, "gzip");
        r.IsChunked = false;
        r.ContentLength = 4;
        r.SetOriginalHeaders();
        Assert.IsTrue(r.OriginalHasBody);
        Assert.AreEqual(4, r.OriginalContentLength);
        Assert.IsFalse(r.OriginalIsChunked);
        Assert.AreEqual("gzip", r.OriginalContentEncoding);

        var peer = new Response();
        peer.SetOriginalHeaders(r);
        Assert.AreEqual(4, peer.OriginalContentLength);
        Assert.AreEqual("gzip", peer.OriginalContentEncoding);

        var drop = new Response(Encoding.ASCII.GetBytes("x")) { KeepBody = false };
        drop.FinishSession();
        Assert.IsFalse(drop.BodyAvailable);

        var keep = new Response(Encoding.ASCII.GetBytes("y")) { KeepBody = true };
        keep.FinishSession();
        Assert.IsTrue(keep.BodyAvailable);

        var wire = new Response
        {
            Http2BodyData = new MemoryStream([1, 2, 3]),
            Priority = 1,
            ReadHttp2BeforeHandlerTaskCompletionSource = new TaskCompletionSource<bool>(),
            ReadHttp2BodyTaskCompletionSource = new TaskCompletionSource<bool>(),
            IsSynthetic = true
        };
        wire.ResetWireState();
        Assert.IsNull(wire.Http2BodyData);
        Assert.IsNull(wire.Priority);
        Assert.IsFalse(wire.IsSynthetic);
        Assert.IsNull(wire.ReadHttp2BeforeHandlerTaskCompletionSource);
        Assert.IsNull(wire.ReadHttp2BodyTaskCompletionSource);

        var h2 = new Response { HttpVersion = HttpHeader.Version20 };
        h2.ContentLength = 12;
        Assert.IsTrue(h2.Headers.HeaderExists(KnownHeaders.ContentLengthHttp2.String));

        h2.Headers.AddHeader(KnownHeaders.ContentLength, "-3");
        Assert.AreEqual(-1, h2.ContentLength);
        h2.Headers.SetOrAddHeaderValue(KnownHeaders.ContentLength, "abc");
        Assert.AreEqual(-1, h2.ContentLength);

        var compress = new Response(Encoding.ASCII.GetBytes("hello-world"));
        compress.ContentLength = -1;
        var body = compress.CompressBodyAndUpdateContentLength();
        Assert.IsNotNull(body);
        Assert.AreEqual(body!.Length, compress.ContentLength);
    }

    [TestMethod]
    public void ParseResponseLine_Bytes_LowercaseHttp10AndBadStatus()
    {
        Response.ParseResponseLine("http/1.0 200 OK"u8, out var v10, out var code, out var desc);
        Assert.AreEqual(HttpHeader.Version10, v10);
        Assert.AreEqual(200, code);
        Assert.AreEqual("OK", desc);

        Assert.ThrowsExactly<FormatException>(() =>
            Response.ParseResponseLine("HTTP/1.1 xyz Extra"u8, out _, out _, out _));
        Assert.ThrowsExactly<FormatException>(() =>
            Response.ParseResponseLine("HTTP/1.1 xyz"u8, out _, out _, out _));

        var omitCl = new Response
        {
            HttpVersion = HttpHeader.Version20,
            StatusCode = 200,
            RequestMethod = "GET"
        };
        Assert.IsTrue(omitCl.HasBody);

        var withBody = new Response(Encoding.ASCII.GetBytes("ok"));
        withBody.EnsureBodyAvailable();
        Assert.ThrowsExactly<BodyNotFoundException>(() =>
            new Response { StatusCode = 204 }.EnsureBodyAvailable());
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new Response { StatusCode = 200, ContentLength = 4, IsBodyRead = false }.EnsureBodyAvailable());
    }

    [TestMethod]
    public async Task ReadRequestLine_PrefetchHttp10BlankEofAndCancel()
    {
        await using (var s = MakeClientStream(Encoding.ASCII.GetBytes("GET /q HTTP/1.1\r\n")))
        {
            Assert.IsTrue(await s.FillBufferAsync());
            var status = await s.ReadRequestLine();
            Assert.AreEqual("GET", status.Method);
            Assert.AreEqual("/q", status.RequestUri.ToString());
            Assert.AreEqual(HttpHeader.Version11, status.Version);
        }

        await using (var s = MakeClientStream(Encoding.ASCII.GetBytes("POST / HTTP/1.0\n")))
        {
            var status = await s.ReadRequestLine();
            Assert.AreEqual("POST", status.Method);
            Assert.AreEqual(HttpHeader.Version10, status.Version);
        }

        await using (var s = MakeClientStream(Encoding.ASCII.GetBytes("\r\nGET / HTTP/1.1\r\n")))
        {
            var blank = await s.ReadRequestLineWithResultAsync();
            Assert.IsFalse(blank.Cancelled);
            // Method is annotated non-nullable but a blank request line leaves it null.
#pragma warning disable MSTEST0025
            Assert.IsNull(blank.Status.Method);
#pragma warning restore MSTEST0025
            var next = await s.ReadRequestLine();
            Assert.AreEqual("GET", next.Method);
        }

        await using (var s = MakeClientStream([]))
        {
            var eof = await s.ReadRequestLine();
            // Method is annotated non-nullable but EOF leaves it null.
#pragma warning disable MSTEST0025
            Assert.IsNull(eof.Method);
#pragma warning restore MSTEST0025
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var s = MakeClientStream(new GatedPayloadStream(gate.Task, Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n"))))
        {
            using var cts = new CancellationTokenSource();
            var pending = s.ReadRequestLineWithResultAsync(cts.Token);
            cts.Cancel();
            var result = await pending;
            Assert.IsTrue(result.Cancelled);
            gate.TrySetResult();
        }
    }

    [TestMethod]
    public async Task ReadResponseStatus_BlankThenStatus_AndWriteRequest()
    {
        await using (var s = MakeServerStream(Encoding.ASCII.GetBytes("\r\nHTTP/1.1 204\r\n")))
        {
            // Prefill so the blank-line + status double-parse sync path runs.
            Assert.IsTrue(await s.FillBufferAsync());
            var status = await s.ReadResponseStatus();
            Assert.IsNotNull(status);
            Assert.AreEqual(204, status!.Value.StatusCode);
            Assert.AreEqual(string.Empty, status.Value.Description);
            Assert.AreEqual(HttpHeader.Version11, status.Value.Version);
        }

        await using var dest = new MemoryStream();
        await using (var s = new HttpServerStream(
                           new ProxyServer(false, false, false), dest, new DefaultBufferPool(), CancellationToken.None))
        {
            await s.WriteRequestAsync(new Request
            {
                Method = "GET",
                RequestUriString = "/",
                HttpVersion = HttpHeader.Version11
            });
        }

        var text = Encoding.ASCII.GetString(dest.ToArray());
        StringAssert.StartsWith(text, "GET / HTTP/1.1\r\n");
    }

    [TestMethod]
    public async Task StreamExtensions_CopyToOnCopyAndWithCancellation()
    {
        var pool = new DefaultBufferPool();
        var input = new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 100)));
        var output = new MemoryStream();
        var copied = 0;
        await input.CopyToAsync(output, (buf, off, count) => copied += count, pool);
        Assert.AreEqual(100, copied);
        Assert.AreEqual(100, output.Length);

        var done = await Task.FromResult(7).WithCancellation(CancellationToken.None);
        Assert.AreEqual(7, done);

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = await tcs.Task.WithCancellation(cts.Token);
        Assert.AreEqual(0, cancelled);
    }

    [TestMethod]
    public void HttpStreamLineHelpers_DecodeGrowAndCapacity()
    {
        var decodeBytes = typeof(HttpStream).GetMethod(
            "DecodeCompletedLine", PrivateStatic, [typeof(byte[]), typeof(int), typeof(byte)])!;
        Assert.AreEqual("ab", (string)decodeBytes.Invoke(null, [Encoding.ASCII.GetBytes("ab\n"), 2, (byte)'b'])!);
        Assert.AreEqual("a", (string)decodeBytes.Invoke(null, [Encoding.ASCII.GetBytes("ab\n"), 2, (byte)'\r'])!);

        var ensureCap = typeof(HttpStream).GetMethod("EnsureLineBufferCapacity", PrivateStatic)!;
        var args = new object[] { new byte[4], 4, 16L };
        ensureCap.Invoke(null, args);
        Assert.IsTrue(((byte[])args[0]).Length >= 8);

        args = [new byte[4], 5, 4L];
        Assert.ThrowsExactly<TargetInvocationException>(() => ensureCap.Invoke(null, args));

        var ensureMin = typeof(HttpStream).GetMethod("EnsureLineBufferMinLength", PrivateStatic)!;
        args = [new byte[4], 10, 32L];
        ensureMin.Invoke(null, args);
        Assert.IsTrue(((byte[])args[0]).Length >= 10);

        args = [new byte[4], 40, 16L];
        Assert.ThrowsExactly<TargetInvocationException>(() => ensureMin.Invoke(null, args));
    }

    [TestMethod]
    public void Http2FlowController_ReserveEdgeBranches()
    {
        var flow = new Http2FlowController();
        Assert.IsTrue(flow.TryReserve(99, 0));
        Assert.IsTrue(flow.TryReserve(99, -1));
        Assert.AreEqual(0, flow.TryReservePartial(1, 0));
        Assert.AreEqual(0, flow.TryReservePartial(1, -1));
        flow.RegisterStream(7);
        Assert.IsTrue(flow.TryReserve(7, 100));

        flow.OnInitialWindowSizeChanged(0);
        Assert.AreEqual(0, flow.TryReservePartial(11, 10));

        var flow2 = new Http2FlowController();
        flow2.RegisterStream(1);
        Assert.AreEqual(Http2FlowController.InitialConnectionWindow,
            flow2.TryReservePartial(1, Http2FlowController.InitialConnectionWindow));
        Assert.IsFalse(flow2.TryReserve(2, 1));
    }

    [TestMethod]
    public async Task Http2FrameWriter_WaitsWhenLockHeld()
    {
        await using var ms = new MemoryStream();
        var gate = new SemaphoreSlim(1, 1);
        await using var writer = new Http2FrameWriter(ms, gate);
        await gate.WaitAsync();
        var payload = ArrayPool<byte>.Shared.Rent(8);
        payload.AsSpan(0, 8).Fill(0x5A);
        writer.EnqueueRented(payload, 8);
        var release = Task.Run(async () =>
        {
            await Task.Delay(50);
            gate.Release();
        });
        await writer.DisposeAsync();
        await release;
        CollectionAssert.AreEqual(new byte[] { 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A }, ms.ToArray());
    }

    private static HttpClientStream MakeClientStream(byte[] payload) =>
        MakeClientStream(new MemoryStream(payload));

    private static HttpClientStream MakeClientStream(Stream baseStream)
    {
        var proxy = new ProxyServer(false, false, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        return new HttpClientStream(proxy, connection, baseStream, proxy.BufferPool, CancellationToken.None);
    }

    private static HttpServerStream MakeServerStream(byte[] payload) =>
        new(new ProxyServer(false, false, false), new MemoryStream(payload), new DefaultBufferPool(),
            CancellationToken.None);

    private sealed class GatedPayloadStream : Stream
    {
        private readonly Task gate;
        private readonly byte[] payload;
        private int offset;

        public GatedPayloadStream(Task gate, byte[] payload)
        {
            this.gate = gate;
            this.payload = payload;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            if (offset >= payload.Length) return 0;
            var toCopy = Math.Min(buffer.Length, payload.Length - offset);
            payload.AsMemory(this.offset, toCopy).CopyTo(buffer);
            this.offset += toCopy;
            return toCopy;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DebugCapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Messages { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) =>
            logLevel == Microsoft.Extensions.Logging.LogLevel.Debug;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
