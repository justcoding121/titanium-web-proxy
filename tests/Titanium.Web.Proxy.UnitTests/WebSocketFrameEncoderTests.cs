using System;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class WebSocketFrameEncoderTests
{
    [TestMethod]
    public void Encode_Masked_RoundTrips_Through_Decoder()
    {
        var payload = Encoding.UTF8.GetBytes("mask-me");
        var wire = WebSocketFrameEncoder.Encode(WebsocketOpCode.Text, payload, mask: true, maskKey: 0x11223344);

        var decoder = new WebSocketDecoder(new FakeBufferPool(8192));
        var frame = decoder.Decode(wire, 0, wire.Length).Single();

        Assert.AreEqual(WebsocketOpCode.Text, frame.OpCode);
        Assert.IsTrue(frame.IsFinal);
        CollectionAssert.AreEqual(payload, frame.Data.ToArray());
    }

    [TestMethod]
    public void Encode_Unmasked_RoundTrips_Through_Decoder()
    {
        var payload = Encoding.UTF8.GetBytes("plain");
        var wire = WebSocketFrameEncoder.Encode(WebsocketOpCode.Binary, payload, mask: false);

        var decoder = new WebSocketDecoder(new FakeBufferPool(8192));
        var frame = decoder.Decode(wire, 0, wire.Length).Single();

        Assert.AreEqual(WebsocketOpCode.Binary, frame.OpCode);
        CollectionAssert.AreEqual(payload, frame.Data.ToArray());
    }

    [TestMethod]
    public void Encode_Fragmented_And_Control_Frames()
    {
        var part1 = WebSocketFrameEncoder.Encode(WebsocketOpCode.Text, Encoding.UTF8.GetBytes("ab"),
            mask: false, isFinal: false);
        var part2 = WebSocketFrameEncoder.Encode(WebsocketOpCode.Continuation, Encoding.UTF8.GetBytes("cd"),
            mask: false, isFinal: true);
        var ping = WebSocketFrameEncoder.Encode(WebsocketOpCode.Ping, Encoding.UTF8.GetBytes("p"), mask: false);
        var close = WebSocketFrameEncoder.Encode(WebsocketOpCode.ConnectionClose, Array.Empty<byte>(),
            mask: false);

        var decoder = new WebSocketDecoder(new FakeBufferPool(8192));
        var all = part1.Concat(part2).Concat(ping).Concat(close).ToArray();
        var frames = decoder.Decode(all, 0, all.Length).ToList();

        Assert.AreEqual(4, frames.Count);
        Assert.IsFalse(frames[0].IsFinal);
        Assert.AreEqual(WebsocketOpCode.Text, frames[0].OpCode);
        Assert.IsTrue(frames[1].IsFinal);
        Assert.AreEqual(WebsocketOpCode.Continuation, frames[1].OpCode);
        Assert.AreEqual(WebsocketOpCode.Ping, frames[2].OpCode);
        Assert.AreEqual(WebsocketOpCode.ConnectionClose, frames[3].OpCode);
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
