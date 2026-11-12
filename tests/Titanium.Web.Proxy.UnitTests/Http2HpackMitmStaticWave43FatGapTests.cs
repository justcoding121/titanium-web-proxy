#pragma warning disable TWP001
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat MITM static HPACK relay helpers in <see cref="Http2Helper" /> (Copy.Headers seam).
/// </summary>
[TestClass]
public class Http2HpackMitmStaticWave43FatGapTests
{
    private delegate bool EqualsAsciiIgnoreCaseDelegate(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b);

    [TestMethod]
    public void HpackSkipReencodeAndMitmStaticRelay_CoverCopyHeaderBranches()
    {
        var flags = Wave42FatGapTestSupport.PrivateStatic;
        var equals = typeof(Http2Helper).GetMethod("EqualsAsciiIgnoreCase", flags)!
            .CreateDelegate<EqualsAsciiIgnoreCaseDelegate>();
        Assert.IsTrue(equals("Host"u8, "host"u8));
        Assert.IsFalse(equals("ab"u8, "abc"u8));

        var omit = typeof(Http2Helper).GetMethod("ShouldOmitHttp2Header", flags,
            binder: null, [typeof(ByteString), typeof(ByteString)], modifiers: null)!;
        ByteString Empty = default;
        Assert.IsTrue((bool)omit.Invoke(null, ["Connection".GetByteString(), Empty])!);
        Assert.IsTrue((bool)omit.Invoke(null, ["host".GetByteString(), Empty])!);
        Assert.IsFalse((bool)omit.Invoke(null, ["te".GetByteString(), "trailers".GetByteString()])!);

        var skipLit = typeof(Http2Helper).GetMethod("TrySkipHpackLiteral", flags,
            binder: null, [typeof(byte[]), typeof(int).MakeByRefType()], modifiers: null)!;
        var skipStr = typeof(Http2Helper).GetMethod("TrySkipHpackString", flags,
            binder: null, [typeof(byte[]), typeof(int).MakeByRefType()], modifiers: null)!;

        var literal = new byte[] { 0x00, 0x01, (byte)'a', 0x01, (byte)'b' };
        object?[] litArgs = [literal, 0];
        Assert.IsTrue((bool)skipLit.Invoke(null, litArgs)!);
        Assert.AreEqual(literal.Length, litArgs[1]);

        object?[] badStr = [new byte[] { 0x05, 1 }, 0];
        Assert.IsFalse((bool)skipStr.Invoke(null, badStr)!);

        var buildSuffix = typeof(Http2Helper).GetMethod("BuildStaticLiteralAppendSuffix", flags)!;
        Assert.IsNull(buildSuffix.Invoke(null,
            [default(MitmCompressedRelayHelper.AddedHeaderBuffer), null, null]));
        var withExtra = (byte[]?)buildSuffix.Invoke(null,
            [default(MitmCompressedRelayHelper.AddedHeaderBuffer), "via", "1.1 twp"]);
        Assert.IsNotNull(withExtra);

        var before = new HeaderCollection();
        before.AddHeader("accept", "*/*");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(before);
        var after = new HeaderCollection();
        after.AddHeader("accept", "*/*");
        after.AddHeader("x-added", "1");
        var prepare = typeof(Http2Helper).GetMethod("TryPrepareMitmStaticHpackRelay", flags)!;
        var prepArgs = new object?[]
        {
            new byte[] { 0x82, 0x87, 0x84 }, baseline, after, true, "1.1 twp", null, null
        };
        _ = (bool)prepare.Invoke(null, prepArgs)!;

        var settings = new Http2Settings();
        var listener = new Http2Helper.MyHeaderListener(new HeaderCollection(), isRequest: true);
        listener.AddHeader(StaticTable.KnownHeaderMethod, "GET".GetByteString(), false);
        listener.AddHeader(StaticTable.KnownHeaderAuhtority, "origin.test".GetByteString(), false);
        listener.AddHeader(StaticTable.KnownHeaderScheme, "https".GetByteString(), false);
        listener.AddHeader(StaticTable.KnownHeaderPath, "/".GetByteString(), false);
        var headers = new HeaderCollection();
        headers.AddHeader("X-Mixed", "1");
        var reencode = typeof(Http2Helper).GetMethod("ReencodeCompressedRequestBlock", flags)!;
        var reencoded = (byte[])reencode.Invoke(null,
            [settings, listener, headers, ProxyServer.UriSchemeHttp8])!;
        Assert.IsTrue(reencoded.Length > 0);
    }

    [TestMethod]
    public void ScheduleFinalize_CompressedRelayPoolReturnBranch()
    {
        var schedule = typeof(Http2Helper).GetMethod("ScheduleFinalize", Wave42FatGapTestSupport.PrivateStatic)!;
        using var proxy = new ProxyServer(false, false, false);
        using var cts = new CancellationTokenSource();
        var connectionState = new Http2ConnectionState(42, cts);
        var state = new Http2StreamState(1);
        schedule.Invoke(null, [state, (Func<SessionEventArgs, Task>)(_ => Task.CompletedTask),
            NullLogger.Instance, connectionState]);
        Assert.IsNull(state.SessionArgs);
    }
}
#pragma warning restore TWP001
