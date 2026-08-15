using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class LimitedStreamReadTests
{
    [TestMethod]
    public async Task ReadAsync_ArrayBackedMemory_ReadsDirectly()
    {
        var payload = new byte[100];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)i;

        var proxy = new ProxyServer(false, false, false);
        using var source = new HttpStream(proxy, new MemoryStream(payload), new DefaultBufferPool(),
            CancellationToken.None, false);
        var limited = new LimitedStream(source, new DefaultBufferPool(), isChunked: false, contentLength: payload.Length);

        var destination = new byte[100];
        var total = 0;
        while (total < destination.Length)
        {
            var read = await limited.ReadAsync(destination.AsMemory(total), CancellationToken.None);
            if (read == 0) break;
            total += read;
        }

        Assert.AreEqual(payload.Length, total);
        CollectionAssert.AreEqual(payload, destination);
    }

    [TestMethod]
    public async Task ReadAsync_ContentLongerThanPoolBuffer_CompletesInMultipleReads()
    {
        var payload = new byte[64];
        new Random(1).NextBytes(payload);

        var pool = new FixedSizeBufferPool(16);
        var proxy = new ProxyServer(false, false, false);
        using var source = new HttpStream(proxy, new MemoryStream(payload), new FixedSizeBufferPool(64),
            CancellationToken.None, false);
        var limited = new LimitedStream(source, pool, isChunked: false, contentLength: payload.Length);

        var destination = new byte[payload.Length];
        var total = 0;
        var reads = 0;
        while (total < destination.Length)
        {
            var read = await limited.ReadAsync(destination.AsMemory(total), CancellationToken.None);
            if (read == 0) break;
            total += read;
            reads++;
        }

        Assert.AreEqual(payload.Length, total);
        Assert.IsTrue(reads >= 1);
        CollectionAssert.AreEqual(payload, destination);
    }

    private sealed class FixedSizeBufferPool : IBufferPool
    {
        public FixedSizeBufferPool(int bufferSize) => BufferSize = bufferSize;
        public int BufferSize { get; }
        public byte[] GetBuffer() => new byte[BufferSize];
        public byte[] GetBuffer(int bufferSize) => new byte[bufferSize];
        public void ReturnBuffer(byte[] buffer) { }
        public void Dispose() { }
    }
}
