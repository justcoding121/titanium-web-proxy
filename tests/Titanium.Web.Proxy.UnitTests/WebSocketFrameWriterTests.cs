using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class WebSocketFrameWriterTests
{
    [TestMethod]
    public async Task WriteTextAsync_WritesMaskedFrame_InvokesCallback_AndRoundTrips()
    {
        using var ms = new MemoryStream();
        using var writeLock = new SemaphoreSlim(1, 1);
        var written = 0;
        var writer = new WebSocketFrameWriter(ms, mask: true, writeLock, (_, _, count) => written += count);

        await writer.WriteTextAsync("hello-ws");

        Assert.IsTrue(written > 0);
        var wire = ms.ToArray();
        var decoder = new WebSocketDecoder(new FakeBufferPool(8192));
        var frame = decoder.Decode(wire, 0, wire.Length).Single();

        Assert.AreEqual(WebsocketOpCode.Text, frame.OpCode);
        Assert.AreEqual("hello-ws", Encoding.UTF8.GetString(frame.Data.ToArray()));
    }

    [TestMethod]
    public async Task WriteAsync_SerializesConcurrentWriters()
    {
        using var ms = new MemoryStream();
        using var writeLock = new SemaphoreSlim(1, 1);
        var writer = new WebSocketFrameWriter(ms, mask: false, writeLock, null);

        await Task.WhenAll(
            writer.WriteAsync(WebsocketOpCode.Text, Encoding.UTF8.GetBytes("one")),
            writer.WriteAsync(WebsocketOpCode.Text, Encoding.UTF8.GetBytes("two")),
            writer.WriteAsync(WebsocketOpCode.Text, Encoding.UTF8.GetBytes("three")));

        var decoder = new WebSocketDecoder(new FakeBufferPool(8192));
        var wire = ms.ToArray();
        var frames = decoder.Decode(wire, 0, wire.Length).ToList();
        Assert.AreEqual(3, frames.Count);
        CollectionAssert.AreEquivalent(
            new[] { "one", "two", "three" },
            frames.Select(f => Encoding.UTF8.GetString(f.Data.ToArray())).ToArray());
    }

    private sealed class FakeBufferPool : IBufferPool
    {
        public FakeBufferPool(int bufferSize) => BufferSize = bufferSize;
        public int BufferSize { get; }
        public byte[] GetBuffer() => new byte[BufferSize];
        public byte[] GetBuffer(int bufferSize) => new byte[bufferSize];
        public void ReturnBuffer(byte[] buffer) { }
        public void Dispose() { }
    }
}
