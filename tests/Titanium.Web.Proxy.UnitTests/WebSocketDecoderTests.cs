using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit tests for <see cref="WebSocketDecoder" />, the frame decoder exposed to API users via
///     <c>SessionEventArgs.WebSocketDecoderSend</c> / <c>WebSocketDecoderReceive</c> to turn the raw bytes
///     relayed after a WebSocket upgrade (see <c>WebSocketHandler.HandleWebSocketUpgrade</c>) back into
///     individual <see cref="WebSocketFrame" />s, per RFC 6455 section 5.2.
/// </summary>
[TestClass]
public class WebSocketDecoderTests
{
    private static WebSocketDecoder CreateDecoder(int bufferSize = 8192, long maxFramePayloadBytes = long.MaxValue)
    {
        return new WebSocketDecoder(new FakeBufferPool(bufferSize), maxFramePayloadBytes);
    }

    [TestMethod]
    public void Decode_SingleUnmaskedTextFrame_DeliveredInOneShot_YieldsCorrectFrame()
    {
        var decoder = CreateDecoder();
        var raw = BuildFrame(WebsocketOpCode.Text, Encoding.UTF8.GetBytes("hello"));

        var frames = decoder.Decode(raw, 0, raw.Length).ToList();

        Assert.AreEqual(1, frames.Count);
        Assert.IsTrue(frames[0].IsFinal);
        Assert.AreEqual(WebsocketOpCode.Text, frames[0].OpCode);
        Assert.AreEqual("hello", frames[0].GetText());
    }

    [TestMethod]
    public void Decode_MaskedTextFrame_UnmasksPayloadCorrectly()
    {
        // Per RFC 6455 section 5.1, every frame sent from client to server (the "send" direction from the
        // proxy's point of view) must be masked.
        var decoder = CreateDecoder();
        var raw = BuildFrame(WebsocketOpCode.Text, Encoding.UTF8.GetBytes("masked payload"), mask: true);

        var frames = decoder.Decode(raw, 0, raw.Length).ToList();

        Assert.AreEqual(1, frames.Count);
        Assert.AreEqual("masked payload", frames[0].GetText());
    }

    [TestMethod]
    public void Decode_MaskedPayload_LengthNotMultipleOfFour_UnmasksTrailingBytesCorrectly()
    {
        // The decoder unmasks in 4-byte (uint) chunks for speed and handles the 1-3 leftover trailing
        // bytes separately - exercise a payload length that forces that trailing-byte path.
        var decoder = CreateDecoder();
        var payload = Encoding.UTF8.GetBytes("seven!!"); // 7 bytes: 1 full uint chunk + 3 trailing bytes
        Assert.AreEqual(7, payload.Length);
        var raw = BuildFrame(WebsocketOpCode.Binary, payload, mask: true, maskKey: 0xAABBCCDD);

        var frames = decoder.Decode(raw, 0, raw.Length).ToList();

        Assert.AreEqual(1, frames.Count);
        CollectionAssert.AreEqual(payload, frames[0].Data.ToArray());
    }

    [TestMethod]
    public void Decode_MultipleFramesInSingleBuffer_YieldsAllFramesInOrder()
    {
        var decoder = CreateDecoder();
        var frame1 = BuildFrame(WebsocketOpCode.Text, Encoding.UTF8.GetBytes("first"));
        var frame2 = BuildFrame(WebsocketOpCode.Text, Encoding.UTF8.GetBytes("second"));
        var raw = frame1.Concat(frame2).ToArray();

        var frames = decoder.Decode(raw, 0, raw.Length).ToList();

        Assert.AreEqual(2, frames.Count);
        Assert.AreEqual("first", frames[0].GetText());
        Assert.AreEqual("second", frames[1].GetText());
    }

    [TestMethod]
    public void Decode_FragmentedMessage_NonFinalFrame_ReportsIsFinalFalse()
    {
        var decoder = CreateDecoder();
        var raw = BuildFrame(WebsocketOpCode.Text, Encoding.UTF8.GetBytes("part1"), fin: false);

        var frames = decoder.Decode(raw, 0, raw.Length).ToList();

        Assert.AreEqual(1, frames.Count);
        Assert.IsFalse(frames[0].IsFinal);
        Assert.AreEqual(WebsocketOpCode.Text, frames[0].OpCode);
    }

    [TestMethod]
    public void Decode_FrameSplitAcrossTwoCalls_BuffersPartialDataAndYieldsOnceComplete()
    {
        var decoder = CreateDecoder();
        var raw = BuildFrame(WebsocketOpCode.Text, Encoding.UTF8.GetBytes("split across reads"));

        var splitPoint = raw.Length - 3;
        var firstFrames = decoder.Decode(raw, 0, splitPoint).ToList();
        Assert.AreEqual(0, firstFrames.Count, "No frame should be produced until all of its bytes have arrived.");

        var secondFrames = decoder.Decode(raw, splitPoint, raw.Length - splitPoint).ToList();
        Assert.AreEqual(1, secondFrames.Count);
        Assert.AreEqual("split across reads", secondFrames[0].GetText());
    }

    [TestMethod]
    public void Decode_FrameArrivingOneByteAtATime_StillDecodesCorrectly()
    {
        var decoder = CreateDecoder();
        var raw = BuildFrame(WebsocketOpCode.Text, Encoding.UTF8.GetBytes("trickle"), mask: true);

        var received = new List<WebSocketFrame>();
        for (var i = 0; i < raw.Length; i++)
            received.AddRange(decoder.Decode(raw, i, 1));

        Assert.AreEqual(1, received.Count);
        Assert.AreEqual("trickle", received[0].GetText());
    }

    [TestMethod]
    public void Decode_ExtendedLength16Bit_PayloadOver125Bytes_ParsesFullPayload()
    {
        var decoder = CreateDecoder();
        var payload = new byte[500];
        new Random(42).NextBytes(payload);
        var raw = BuildFrame(WebsocketOpCode.Binary, payload);

        var frames = decoder.Decode(raw, 0, raw.Length).ToList();

        Assert.AreEqual(1, frames.Count);
        CollectionAssert.AreEqual(payload, frames[0].Data.ToArray());
    }

    [TestMethod]
    public void Decode_ExtendedLength64Bit_PayloadOver64KB_ParsesFullPayload()
    {
        var decoder = CreateDecoder(bufferSize: 1024);
        var payload = new byte[70_000];
        new Random(7).NextBytes(payload);
        var raw = BuildFrame(WebsocketOpCode.Binary, payload, mask: true);

        var frames = decoder.Decode(raw, 0, raw.Length).ToList();

        Assert.AreEqual(1, frames.Count);
        CollectionAssert.AreEqual(payload, frames[0].Data.ToArray());
    }

    [TestMethod]
    [DataRow(WebsocketOpCode.ConnectionClose)]
    [DataRow(WebsocketOpCode.Ping)]
    [DataRow(WebsocketOpCode.Pong)]
    [DataRow(WebsocketOpCode.Continuation)]
    public void Decode_ControlAndContinuationOpCodes_AreRoundTrippedCorrectly(WebsocketOpCode opCode)
    {
        var decoder = CreateDecoder();
        var raw = BuildFrame(opCode, Array.Empty<byte>());

        var frames = decoder.Decode(raw, 0, raw.Length).ToList();

        Assert.AreEqual(1, frames.Count);
        Assert.AreEqual(opCode, frames[0].OpCode);
    }

    [TestMethod]
    public void Decode_PartialFrameThatOutgrowsInternalBuffer_ResizesInsteadOfThrowing()
    {
        // Regression test: WebSocketDecoder grows its internal reassembly buffer by doubling its previous
        // capacity, but only when that's actually enough to fit the newly-arrived data alongside whatever
        // partial frame was already buffered. Constructing the decoder with a tiny initial buffer (via a
        // fake, small-BufferSize pool - mirroring how the real decoder is sized off
        // SessionEventArgsBase.BufferPool at construction time) makes it trivial to force a single
        // Decode() call to require far more than double the current capacity in one jump.
        var decoder = CreateDecoder(bufferSize: 4);
        var payload = new byte[200];
        new Random(1).NextBytes(payload);
        var raw = BuildFrame(WebsocketOpCode.Binary, payload, mask: true);

        // First call: deliver only the first 2 bytes (opcode/flags + the "use 16-bit extended length"
        // marker byte) - not enough to even know the real payload size yet, so it's buffered as-is
        // without needing to grow past the initial 4-byte capacity.
        var firstFrames = decoder.Decode(raw, 0, 2).ToList();
        Assert.AreEqual(0, firstFrames.Count);

        // Second call: deliver the rest of the frame (well over double the 4-byte initial capacity) in a
        // single call. This must not throw.
        var secondFrames = decoder.Decode(raw, 2, raw.Length - 2).ToList();

        Assert.AreEqual(1, secondFrames.Count);
        CollectionAssert.AreEqual(payload, secondFrames[0].Data.ToArray());
    }

    [TestMethod]
    public void Decode_FrameDataFromInternalBuffer_IsOverwrittenByALaterDecodeCall()
    {
        // Characterization test for a documented sharp edge (see the remarks on WebSocketFrame and
        // WebSocketDecoder.Decode): a frame whose bytes had to be buffered internally (because it arrived
        // split across two Decode calls) exposes Data as a zero-copy slice of the decoder's own
        // reassembly buffer. That buffer is reused by later calls, so retaining the WebSocketFrame past
        // the call that produced it - instead of consuming/copying its Data immediately - is a misuse of
        // the API that silently corrupts the previously-observed content, as asserted below.
        var decoder = CreateDecoder();
        var frameA = BuildFrame(WebsocketOpCode.Text, Encoding.UTF8.GetBytes("first-message"));

        // Split frameA so the second call goes through the internal-buffer ("copied") reassembly path,
        // making its yielded Data alias `this.buffer` rather than the caller's own array.
        var splitPoint = frameA.Length - 2;
        Assert.AreEqual(0, decoder.Decode(frameA, 0, splitPoint).Count());
        var decodedFrameA = decoder.Decode(frameA, splitPoint, frameA.Length - splitPoint).Single();
        Assert.AreEqual("first-message", decodedFrameA.GetText(), "Sanity check before the buffer is reused.");

        // Decode a second, unrelated frame through the very same decoder instance. Since frameA's bytes
        // were fully consumed (the decoder's internal bufferLength resets to 0), this reuses the same
        // internal buffer starting from the same offset frameA's Data used to point into.
        var frameB = BuildFrame(WebsocketOpCode.Text, Encoding.UTF8.GetBytes("second-msg"));
        Assert.AreEqual(0, decoder.Decode(frameB, 0, frameB.Length - 2).Count());
        _ = decoder.Decode(frameB, frameB.Length - 2, 2).Single();

        // The long-retained reference to frameA's Data now observes frameB's raw wire bytes instead - it
        // no longer reads back "first-message". This is the exact bug this test would have caught: naively
        // collecting WebSocketFrame instances (rather than their extracted text/bytes) for later use.
        Assert.AreNotEqual("first-message", decodedFrameA.GetText());
    }

    [TestMethod]
    public void Decode_64BitLength_ReservedHighBitSet_ThrowsBeforeBuffering()
    {
        // RFC 6455 section 5.2: "the most significant bit MUST be 0" for the 64-bit extended length.
        // Build only the 10-byte header (opcode/flags + length-marker byte + 8-byte extended length) with
        // the reserved bit set and no payload at all - if validation happened only after the full
        // (attacker-declared) length had arrived, this call would instead just report "not enough data
        // yet" and wait forever.
        var decoder = CreateDecoder();
        var header = new byte[]
        {
            (byte)(0x80 | (byte)WebsocketOpCode.Binary), // FIN=1, opcode=Binary
            127, // unmasked, 64-bit extended length follows
            0x80, 0, 0, 0, 0, 0, 0, 1 // reserved high bit set; low bits declare length=1
        };

        var ex = Assert.ThrowsExactly<WebSocketProtocolException>(
            () => decoder.Decode(header, 0, header.Length).ToList());
        Assert.AreEqual((ushort)1002, ex.CloseCode);
    }

    [TestMethod]
    public void Decode_64BitLength_ExceedsIntMaxValue_ThrowsBeforeBuffering()
    {
        // A structurally valid (reserved bit clear) but still unbufferable length: WebSocketFrame.Data
        // is ultimately sliced with a 32-bit length, so this can never be honored regardless of any
        // configured per-frame limit.
        var decoder = CreateDecoder();
        var header = new byte[]
        {
            (byte)(0x80 | (byte)WebsocketOpCode.Binary),
            127,
            0, 0, 0, 1, 0, 0, 0, 0 // (long)1 << 32 = 4,294,967,296, well over int.MaxValue
        };

        var ex = Assert.ThrowsExactly<WebSocketProtocolException>(
            () => decoder.Decode(header, 0, header.Length).ToList());
        Assert.AreEqual((ushort)1002, ex.CloseCode);
    }

    [TestMethod]
    public void Decode_DeclaredLengthExceedsConfiguredLimit_ThrowsBeforeBufferingPayload()
    {
        // Only the header has arrived (10 bytes); the declared 10 000-byte payload has not, and never
        // will in this test. A caller-configured limit smaller than the declared length must reject the
        // frame the moment the length is known, rather than growing the reassembly buffer while waiting
        // for the rest of an oversized frame that a legitimate peer might trickle in slowly.
        var decoder = CreateDecoder(maxFramePayloadBytes: 1024);
        var header = new byte[]
        {
            (byte)(0x80 | (byte)WebsocketOpCode.Binary),
            127,
            0, 0, 0, 0, 0, 0, 0x27, 0x10 // 10_000
        };

        var ex = Assert.ThrowsExactly<WebSocketProtocolException>(
            () => decoder.Decode(header, 0, header.Length).ToList());
        Assert.AreEqual((ushort)1009, ex.CloseCode);
    }

    [TestMethod]
    public void Decode_16BitLength_ExceedsConfiguredLimit_ThrowsBeforeBufferingPayload()
    {
        var decoder = CreateDecoder(maxFramePayloadBytes: 100);
        var raw = BuildFrame(WebsocketOpCode.Binary, new byte[500]);

        // Deliver only the 4-byte header (opcode/flags + 126-marker + 16-bit length) - the limit check
        // must fire without any of the 500-byte payload having arrived.
        var ex = Assert.ThrowsExactly<WebSocketProtocolException>(
            () => decoder.Decode(raw, 0, 4).ToList());
        Assert.AreEqual((ushort)1009, ex.CloseCode);
    }

    [TestMethod]
    public void Decode_DeclaredLengthWithinConfiguredLimit_StillDecodesNormally()
    {
        var decoder = CreateDecoder(maxFramePayloadBytes: 1024);
        var raw = BuildFrame(WebsocketOpCode.Text, Encoding.UTF8.GetBytes("within limit"));

        var frames = decoder.Decode(raw, 0, raw.Length).ToList();

        Assert.AreEqual(1, frames.Count);
        Assert.AreEqual("within limit", frames[0].GetText());
    }

    /// <summary>
    ///     Hand-builds a raw WebSocket frame (RFC 6455 section 5.2) with the given opcode/payload, choosing
    ///     the 7-bit/16-bit/64-bit length encoding automatically based on the payload size.
    /// </summary>
    private static byte[] BuildFrame(WebsocketOpCode opCode, byte[] payload, bool mask = false, bool fin = true,
        uint maskKey = 0x11223344)
    {
        var bytes = new List<byte> { (byte)((fin ? 0x80 : 0x00) | (byte)opCode) };

        var maskBit = mask ? (byte)0x80 : (byte)0x00;
        var length = payload.Length;
        if (length <= 125)
        {
            bytes.Add((byte)(maskBit | length));
        }
        else if (length <= 65535)
        {
            bytes.Add((byte)(maskBit | 126));
            bytes.Add((byte)(length >> 8));
            bytes.Add((byte)length);
        }
        else
        {
            bytes.Add((byte)(maskBit | 127));
            for (var i = 7; i >= 0; i--) bytes.Add((byte)((long)length >> (i * 8)));
        }

        byte[]? maskKeyBytes = null;
        if (mask)
        {
            maskKeyBytes = new[]
            {
                (byte)maskKey, (byte)(maskKey >> 8), (byte)(maskKey >> 16), (byte)(maskKey >> 24)
            };
            bytes.AddRange(maskKeyBytes);
        }

        var payloadBytes = (byte[])payload.Clone();
        if (mask)
            for (var i = 0; i < payloadBytes.Length; i++)
                payloadBytes[i] ^= maskKeyBytes![i % 4];

        bytes.AddRange(payloadBytes);
        return bytes.ToArray();
    }

    /// <summary>
    ///     Minimal <see cref="IBufferPool" /> that lets tests control the decoder's initial internal
    ///     buffer size (<see cref="WebSocketDecoder" /> captures <c>BufferSize</c> once, at construction).
    /// </summary>
    private sealed class FakeBufferPool : IBufferPool
    {
        public FakeBufferPool(int bufferSize)
        {
            BufferSize = bufferSize;
        }

        public int BufferSize { get; }

        public byte[] GetBuffer()
        {
            return new byte[BufferSize];
        }

        public byte[] GetBuffer(int bufferSize)
        {
            return new byte[bufferSize];
        }

        public void ReturnBuffer(byte[] buffer)
        {
        }

        public void Dispose()
        {
        }
    }
}
