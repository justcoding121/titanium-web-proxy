using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicPacketNumberTests
{
    [TestMethod]
    public void AppendixExamplesSelectTheStatedWidth()
    {
        var buffer = new byte[4];
        Assert.IsTrue(QuicPacketNumber.TryEncode(0xac5c02, 0xabe8b3, buffer, out int written));
        Assert.AreEqual(2, written);
        CollectionAssert.AreEqual(new byte[] { 0x5c, 0x02 }, buffer.AsSpan(0, written).ToArray());

        Assert.IsTrue(QuicPacketNumber.TryEncode(0xace8fe, 0xabe8b3, buffer, out written));
        Assert.AreEqual(3, written);
        CollectionAssert.AreEqual(new byte[] { 0xac, 0xe8, 0xfe }, buffer.AsSpan(0, written).ToArray());
    }

    [TestMethod]
    public void AppendixDecodeRecoversTheFullNumber()
    {
        Assert.IsTrue(QuicPacketNumber.TryDecode(0xa82f30ea, [0x9b, 0x32], out ulong full));
        Assert.AreEqual(0xa82f9b32UL, full);
    }

    [TestMethod]
    public void TwoByteBoundaryReconstructsOnEitherSide()
    {
        Assert.IsTrue(QuicPacketNumber.TryDecode(0xFFFF, [0x00, 0x00], out ulong ahead));
        Assert.AreEqual(0x10000UL, ahead);
        Assert.IsTrue(QuicPacketNumber.TryDecode(0x10000, [0xFF, 0xFF], out ulong behind));
        Assert.AreEqual(0xFFFFUL, behind);
    }

    [TestMethod]
    public void FirstPacketNumberIsZeroWhenNothingHasBeenProcessed()
    {
        Assert.IsTrue(QuicPacketNumber.TryEncode(0, null, new byte[4], out int written));
        Assert.AreEqual(1, written);
        Assert.IsTrue(QuicPacketNumber.TryDecode(null, [0], out ulong full));
        Assert.AreEqual(0UL, full);
    }

    [TestMethod]
    public void ANumberOutsideTheWindowIsRejected()
    {
        Assert.IsFalse(QuicPacketNumber.TryDecode(null, [], out _));
        Assert.IsFalse(QuicPacketNumber.TryDecode(null, new byte[5], out _));
        Assert.IsFalse(QuicPacketNumber.TryDecode(QuicPacketNumber.MaxValue, [0], out _));
        Assert.IsFalse(QuicPacketNumber.TryDecode(QuicPacketNumber.MaxValue - 1, [0x00], out _));
        Assert.IsFalse(QuicPacketNumber.TryEncode(1UL << 32, 0, new byte[8], out _));
        Assert.IsFalse(QuicPacketNumber.TryEncode(QuicPacketNumber.MaxValue + 1, null, new byte[8], out _));
    }

    [TestMethod]
    public void EncodeFitsTheExactWidthAndRejectsAnEqualAck()
    {
        Assert.IsFalse(QuicPacketNumber.TryEncode(5, 5, new byte[4], out _));
        Assert.IsTrue(QuicPacketNumber.TryEncode(5, 4, new byte[1], out int written));
        Assert.AreEqual(1, written);
        Assert.IsFalse(QuicPacketNumber.TryEncode(256, 0, new byte[1], out _));
        Assert.IsTrue(QuicPacketNumber.TryEncode(256, 0, new byte[2], out written));
        Assert.AreEqual(2, written);
        Assert.IsTrue(QuicPacketNumber.TryDecode(4, [5], out ulong full));
        Assert.AreEqual(5UL, full);
    }

    [TestMethod]
    public void EncodedNumbersDecodeBackToTheSameValue()
    {
        ulong[] numbers = [1, 63, 256, 0x734f, 0x10000, 0xa82f9b32];
        var buffer = new byte[4];
        foreach (ulong number in numbers)
        {
            ulong? acked = number == 0 ? null : number - 1;
            Assert.IsTrue(QuicPacketNumber.TryEncode(number, acked, buffer, out int written), number.ToString());
            Assert.IsTrue(QuicPacketNumber.TryDecode(acked, buffer.AsSpan(0, written), out ulong decoded), number.ToString());
            Assert.AreEqual(number, decoded);
        }
    }
}
