#pragma warning disable TWP001
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="Http2Helper" /> copy/parse helpers and <see cref="Http2OriginConnection" /> DATA framing strips.
/// </summary>
[TestClass]
public class Http2HelperCopyParseWave42FatGapTests
{
    private delegate int ReadHttp2FrameLengthDelegate(byte[] buffer);
    private delegate int ReadHttp2StreamIdDelegate(byte[] buffer);
    private delegate int ReadHttp2UInt31Delegate(byte[] buffer);
    private delegate int ReadHttp2ErrorCodeDelegate(byte[] buffer);
    private delegate ReadOnlySpan<byte> StripDataFramingSpanDelegate(ReadOnlySpan<byte> payload, Http2FrameFlag flags);
    private delegate byte[] StripDataFramingFromSpanDelegate(ReadOnlySpan<byte> payload, Http2FrameFlag flags);

    [TestMethod]
    public void Http2CopyParseHelpers_FrameLengthStreamIdErrorAndPadding()
    {
        var length = typeof(Http2Helper).GetMethod("ReadHttp2FrameLength", Wave42FatGapTestSupport.PrivateStatic)!
            .CreateDelegate<ReadHttp2FrameLengthDelegate>();
        Assert.AreEqual(0x010203, length([0x01, 0x02, 0x03, 0, 0, 0, 0, 0, 0]));

        var streamId = typeof(Http2Helper).GetMethod("ReadHttp2StreamId", Wave42FatGapTestSupport.PrivateStatic)!
            .CreateDelegate<ReadHttp2StreamIdDelegate>();
        Assert.AreEqual(0x01020304, streamId([0, 0, 0, 0, 0, 0x81, 0x02, 0x03, 0x04]));

        var u31 = typeof(Http2Helper).GetMethod("ReadHttp2UInt31", Wave42FatGapTestSupport.PrivateStatic)!
            .CreateDelegate<ReadHttp2UInt31Delegate>();
        Assert.AreEqual(0x01020304, u31([0x81, 0x02, 0x03, 0x04]));

        var err = typeof(Http2Helper).GetMethod("ReadHttp2ErrorCode", Wave42FatGapTestSupport.PrivateStatic)!
            .CreateDelegate<ReadHttp2ErrorCodeDelegate>();
        Assert.AreEqual(unchecked((int)0x01020304), err([0x01, 0x02, 0x03, 0x04]));

        var padded = typeof(Http2Helper).GetMethod("GetHttp2PaddedDataRange", Wave42FatGapTestSupport.PrivateStatic)!;
        object?[] args = [new byte[] { 2, 9, 8, 7, 0, 0 }, 6, true, 0, 0];
        padded.Invoke(null, args);
        Assert.AreEqual(1, args[3]);
        Assert.AreEqual(3, args[4]);

        args = [new byte[] { 9, 1, 2 }, 3, true, 0, 0];
        padded.Invoke(null, args);
        Assert.AreEqual(1, args[3]);
        Assert.AreEqual(0, args[4]);

        args = [new byte[] { 1, 2, 3 }, 3, false, 0, 0];
        padded.Invoke(null, args);
        Assert.AreEqual(0, args[3]);
        Assert.AreEqual(3, args[4]);
    }

    [TestMethod]
    public void Http2Origin_StripDataFramingOverloads_CoverPaddedAndEmpty()
    {
        var byteOverload = typeof(Http2OriginConnection).GetMethod("StripDataFraming", Wave42FatGapTestSupport.PrivateStatic,
            binder: null, [typeof(byte[]), typeof(Http2FrameFlag)], modifiers: null)!;
        var unpadded = new byte[] { 1, 2, 3 };
        Assert.AreSame(unpadded, byteOverload.Invoke(null, [unpadded, (Http2FrameFlag)0]));
        var padded = new byte[] { 1, 9, 0 };
        CollectionAssert.AreEqual(new byte[] { 9 },
            (byte[])byteOverload.Invoke(null, [padded, Http2FrameFlag.Padded])!);
        Assert.AreSame(Array.Empty<byte>(),
            byteOverload.Invoke(null, [Array.Empty<byte>(), Http2FrameFlag.Padded]));

        var fromSpan = typeof(Http2OriginConnection).GetMethod("StripDataFraming", Wave42FatGapTestSupport.PrivateStatic,
            binder: null, [typeof(ReadOnlySpan<byte>), typeof(Http2FrameFlag)], modifiers: null)!
            .CreateDelegate<StripDataFramingFromSpanDelegate>();
        CollectionAssert.AreEqual(unpadded, fromSpan(unpadded, 0));
        CollectionAssert.AreEqual(new byte[] { 9 }, fromSpan(padded, Http2FrameFlag.Padded));

        var spanOnly = typeof(Http2OriginConnection).GetMethod("StripDataFramingSpan", Wave42FatGapTestSupport.PrivateStatic)!
            .CreateDelegate<StripDataFramingSpanDelegate>();
        CollectionAssert.AreEqual(unpadded, spanOnly(unpadded, 0).ToArray());
        CollectionAssert.AreEqual(new byte[] { 9 }, spanOnly(padded, Http2FrameFlag.Padded).ToArray());
        Assert.AreEqual(0, spanOnly(ReadOnlySpan<byte>.Empty, Http2FrameFlag.Padded).Length);
    }

    [TestMethod]
    public void Http2Helper_AsciiLowerInternMethodReportAndBind()
    {
        var lower = typeof(Http2Helper).GetMethod("AsciiToLowerByteString", Wave42FatGapTestSupport.PrivateStatic)!;
        Assert.AreEqual("host", ((ByteString)lower.Invoke(null, ["Host".GetByteString()])!).GetString());

        var intern = typeof(Http2Helper).GetMethod("InternCommonHttpMethod", Wave42FatGapTestSupport.PrivateStatic)!
            .CreateDelegate<InternCommonHttpMethodDelegate>();
        foreach (var name in new[] { "GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS", "CONNECT" })
            Assert.AreEqual<string>(name, intern(Encoding.ASCII.GetBytes(name), name.GetByteString()));

        typeof(Http2Helper).GetMethod("ReportException", Wave42FatGapTestSupport.PrivateStatic)!
            .Invoke(null, [NullLogger.Instance, new ProxyHttpException("cov", new IOException("peer"), null)]);
    }

    private delegate string InternCommonHttpMethodDelegate(ReadOnlySpan<byte> methodSpan, ByteString method);
}
#pragma warning restore TWP001
