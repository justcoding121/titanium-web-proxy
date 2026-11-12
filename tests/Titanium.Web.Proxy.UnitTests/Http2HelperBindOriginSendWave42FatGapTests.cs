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
public class Http2HelperBindOriginSendWave42FatGapTests
{


    [TestMethod]
    public async Task Http2Helper_BindOriginAndSendMemoryHelpers()
    {
        using var proxy = new ProxyServer(false, false, false) { EnableRequestTimingCapture = true };
        using var session = Wave42FatGapTestSupport.MakeSession(proxy);
        Assert.IsNotNull(session.Timing);
        using var shell = await Wave42FatGapTestSupport.CreateShellAsync(proxy);
        typeof(Http2Helper).GetMethod("BindOriginForHttp2Stream", Wave42FatGapTestSupport.PrivateStatic)!
            .Invoke(null, [session, shell.ServerConnection]);
        typeof(Http2Helper).GetMethod("BindOriginForHttp2Stream", Wave42FatGapTestSupport.PrivateStatic)!
            .Invoke(null, [session, shell.ServerConnection]); // reused path

        var getMem = typeof(Http2Helper).GetMethod("GetMemoryStreamMemory", Wave42FatGapTestSupport.PrivateStatic)!
            .CreateDelegate<Wave42FatGapTestSupport.GetMemoryStreamMemoryDelegate>();
        using (var expandable = new MemoryStream())
        {
            expandable.Write("abc"u8);
            CollectionAssert.AreEqual("abc"u8.ToArray(), getMem(expandable).ToArray());
        }

        using (var fixedBuf = new MemoryStream(new byte[8], 0, 8, writable: true, publiclyVisible: false))
        {
            fixedBuf.Write("xy"u8);
            var mem = getMem(fixedBuf); // TryGetBuffer fails → ToArray path (Length stays capacity)
            Assert.IsTrue(mem.Length >= 2);
            Assert.AreEqual((byte)'x', mem.Span[0]);
            Assert.AreEqual((byte)'y', mem.Span[1]);
        }

        var asVt = typeof(Http2Helper).GetMethod("AsValueTask", Wave42FatGapTestSupport.PrivateStatic)!;
        await (ValueTask)asVt.Invoke(null, [Task.CompletedTask])!;

        using var syncOut = new MemoryStream();
        var writeTwo = typeof(Http2Helper).GetMethod("WriteTwoAsync", Wave42FatGapTestSupport.PrivateStatic)!;
        await (ValueTask)writeTwo.Invoke(null,
            [syncOut, new ReadOnlyMemory<byte>([1, 2]), new ReadOnlyMemory<byte>([3]), CancellationToken.None])!;
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, syncOut.ToArray());

        await using var deferred = new Wave42FatGapTestSupport.DeferredFirstWriteStream();
        var slow = (ValueTask)writeTwo.Invoke(null,
            [deferred, new ReadOnlyMemory<byte>([9]), new ReadOnlyMemory<byte>([8]), CancellationToken.None])!;
        deferred.Unblock();
        await slow;
        CollectionAssert.AreEqual(new byte[] { 9, 8 }, deferred.Written.ToArray());

        Span<byte> dest = stackalloc byte[16];
        var writePref = typeof(Http2Helper).GetMethod("WriteHpackPrefixedInt", Wave42FatGapTestSupport.PrivateStatic)!
            .CreateDelegate<Wave42FatGapTestSupport.WriteHpackPrefixedIntDelegate>();
        Assert.AreEqual(1, writePref(dest, 0x00, 7, 10UL));
        Assert.IsTrue(writePref(dest, 0x00, 7, 300UL) >= 2);

        var litBytes = typeof(Http2Helper).GetMethods(Wave42FatGapTestSupport.PrivateStatic)
            .First(m => m.Name == "WriteHpackAsciiStringLiteral"
                        && m.GetParameters()[1].ParameterType == typeof(ReadOnlySpan<byte>))
            .CreateDelegate<Wave42FatGapTestSupport.WriteHpackAsciiLiteralBytesDelegate>();
        Assert.AreEqual(4, litBytes(dest, "abc"u8));

        var litStr = typeof(Http2Helper).GetMethods(Wave42FatGapTestSupport.PrivateStatic)
            .First(m => m.Name == "WriteHpackAsciiStringLiteral"
                        && m.GetParameters()[1].ParameterType == typeof(string))
            .CreateDelegate<Wave42FatGapTestSupport.WriteHpackAsciiLiteralStringDelegate>();
        Assert.AreEqual(3, litStr(dest, "xy"));
    }
}
#pragma warning restore TWP001
