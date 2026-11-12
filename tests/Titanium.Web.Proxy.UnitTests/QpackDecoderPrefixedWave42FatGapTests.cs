#pragma warning disable TWP001
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
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
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class QpackDecoderPrefixedWave42FatGapTests
{


    [TestMethod]
    public void QpackDecoder_PrefixedIntAndLiteralEdges()
    {
        var prefixed = (Wave42FatGapTestSupport.TryReadPrefixedIntDelegate)Delegate.CreateDelegate(typeof(Wave42FatGapTestSupport.TryReadPrefixedIntDelegate),
            typeof(QpackDecoder).GetMethod("TryReadPrefixedInt", Wave42FatGapTestSupport.PrivateStatic)!);
        Assert.IsFalse(prefixed(ReadOnlySpan<byte>.Empty, 8, out _, out _));
        Assert.IsTrue(prefixed(new byte[] { 0x05 }, 8, out var small, out var consumed));
        Assert.AreEqual(5UL, small);
        Assert.AreEqual(1, consumed);

        var overflow = new byte[12];
        overflow[0] = 0xFF;
        for (var i = 1; i < overflow.Length; i++) overflow[i] = 0x80;
        Assert.IsFalse(prefixed(overflow, 8, out _, out _));
        Assert.IsFalse(prefixed(new byte[] { 0xFF, 0x80 }, 8, out _, out _));

        var literal = (Wave42FatGapTestSupport.TryReadStringLiteralDelegate)Delegate.CreateDelegate(typeof(Wave42FatGapTestSupport.TryReadStringLiteralDelegate),
            typeof(QpackDecoder).GetMethod("TryReadStringLiteral", Wave42FatGapTestSupport.PrivateStatic)!);
        Assert.IsFalse(literal(ReadOnlySpan<byte>.Empty, out _, out _));
        Assert.IsFalse(literal(new byte[] { 0x05, (byte)'a' }, out _, out _));
        Assert.IsTrue(literal(new byte[] { 0x03, (byte)'a', (byte)'b', (byte)'c' }, out var plain, out _));
        Assert.AreEqual("abc", plain);

        var huffman = (Wave42FatGapTestSupport.HuffmanDecodeDelegate)Delegate.CreateDelegate(typeof(Wave42FatGapTestSupport.HuffmanDecodeDelegate),
            typeof(QpackDecoder).GetMethod("HuffmanDecode", Wave42FatGapTestSupport.PrivateStatic)!);
        Assert.AreEqual("", huffman(ReadOnlySpan<byte>.Empty));

        using var proxy = new ProxyServer(false, false, false);
        var stripMem = typeof(Http2OriginConnection).GetMethod("StripDataFramingMemory", Wave42FatGapTestSupport.PrivateStatic)!;
        var unpadded = new byte[] { 1, 2, 3 };
        var mem = (ReadOnlyMemory<byte>)stripMem.Invoke(null, [unpadded, 3, (Http2FrameFlag)0])!;
        CollectionAssert.AreEqual(unpadded, mem.ToArray());
        var padded = new byte[] { 1, 9, 0 };
        mem = (ReadOnlyMemory<byte>)stripMem.Invoke(null, [padded, 3, Http2FrameFlag.Padded])!;
        CollectionAssert.AreEqual(new byte[] { 9 }, mem.ToArray());
        mem = (ReadOnlyMemory<byte>)stripMem.Invoke(null, [Array.Empty<byte>(), 0, Http2FrameFlag.Padded])!;
        Assert.AreEqual(0, mem.Length);
    }
}
#pragma warning restore TWP001
