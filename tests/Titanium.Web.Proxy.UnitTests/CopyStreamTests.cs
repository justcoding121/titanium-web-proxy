using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.StreamExtended.BufferPool;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class CopyStreamTests
{
    [TestMethod]
    public async Task ReadByteFromBuffer_WhenSourceAvailableExceedsCopyBuffer_FlushesViaFillBufferAsync()
    {
        // Source HttpStream rents a large buffer; CopyStream rents a tiny one. ArrayPool-style
        // size mismatch used to overflow CopyStream.buffer when DataAvailable stayed true.
        var payload = Encoding.ASCII.GetBytes(new string('x', 64) + "\r\n");
        var proxy = new ProxyServer(false, false, false);
        using var source = new HttpStream(proxy, new MemoryStream(payload), new FixedSizeBufferPool(64),
            CancellationToken.None, false);

        var destination = new MemoryStream();
        var writer = new HttpStream(proxy, destination, new FixedSizeBufferPool(64), CancellationToken.None, true);
        using var copy = new CopyStream(source, writer, new FixedSizeBufferPool(8));

        // Drain via the usual DataAvailable || FillBufferAsync loop used by ReadLine / multipart.
        while (copy.DataAvailable || await copy.FillBufferAsync())
            copy.ReadByteFromBuffer();

        await copy.FlushAsync();
        Assert.AreEqual(payload.Length, copy.ReadBytes);
        CollectionAssert.AreEqual(payload, destination.ToArray());
    }

    [TestMethod]
    public async Task ReadByteFromBuffer_WhenCopyBufferFull_ThrowsClearError()
    {
        var payload = Encoding.ASCII.GetBytes("abcdefghij");
        var proxy = new ProxyServer(false, false, false);
        using var source = new HttpStream(proxy, new MemoryStream(payload), new FixedSizeBufferPool(32),
            CancellationToken.None, false);

        var writer = new HttpStream(proxy, new MemoryStream(), new FixedSizeBufferPool(32), CancellationToken.None,
            true);
        using var copy = new CopyStream(source, writer, new FixedSizeBufferPool(4));

        Assert.IsTrue(await copy.FillBufferAsync());
        for (var i = 0; i < 4; i++)
            copy.ReadByteFromBuffer();

        // Buffer full: DataAvailable is false so callers must FillBufferAsync (which flushes).
        Assert.IsFalse(copy.DataAvailable);
        Assert.ThrowsExactly<InvalidOperationException>(() => copy.ReadByteFromBuffer());
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
