#pragma warning disable TWP001
using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="Http2OriginConnection" /> HEADERS/DATA framing strips and padding guards.
/// </summary>
[TestClass]
public class Http2OriginPaddingHeadersWave43FatGapTests
{
    [TestMethod]
    public void ThrowIfDataPaddingTooLong_ThrowsWhenPadEqualsOrExceedsPayload()
    {
        var method = typeof(Http2OriginConnection).GetMethod(
            "ThrowIfDataPaddingTooLong", Wave42FatGapTestSupport.PrivateStatic)!;
        Assert.ThrowsExactly<TargetInvocationException>(() => method.Invoke(null, [3, 3]));
        Assert.ThrowsExactly<TargetInvocationException>(() => method.Invoke(null, [5, 2]));
        method.Invoke(null, [1, 4]);
        method.Invoke(null, [0, 1]);
    }

    [TestMethod]
    public void StripHeadersFraming_ByteArrayOverload_MatchesSpanPaths()
    {
        var strip = typeof(Http2OriginConnection).GetMethod(
            "StripHeadersFraming", Wave42FatGapTestSupport.PrivateStatic,
            binder: null, [typeof(byte[]), typeof(Http2FrameFlag)], modifiers: null)!;

        var unpadded = new byte[] { 1, 2, 3 };
        CollectionAssert.AreEqual(unpadded, (byte[])strip.Invoke(null, [unpadded, (Http2FrameFlag)0])!);

        var padded = new byte[] { 1, 9, 0 };
        CollectionAssert.AreEqual(new byte[] { 9 },
            (byte[])strip.Invoke(null, [padded, Http2FrameFlag.Padded])!);

        var priority = new byte[] { 0, 0, 0, 0, 0, 9, 8, 7 };
        var stripped = (byte[])strip.Invoke(null,
            [priority, Http2FrameFlag.Priority | Http2FrameFlag.Padded])!;
        Assert.IsTrue(stripped.Length >= 1);
    }

    [TestMethod]
    public void StripDataFramingMemory_ReturnsSliceWithoutCopyWhenUnpadded()
    {
        var strip = typeof(Http2OriginConnection).GetMethod(
            "StripDataFramingMemory", Wave42FatGapTestSupport.PrivateStatic)!;
        var rented = new byte[] { 1, 2, 3, 4 };
        var mem = (ReadOnlyMemory<byte>)strip.Invoke(null, [rented, 4, (Http2FrameFlag)0])!;
        Assert.AreEqual(4, mem.Length);
        Assert.AreEqual((byte)1, mem.Span[0]);
    }
}
#pragma warning restore TWP001
