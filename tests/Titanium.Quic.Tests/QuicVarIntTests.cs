using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicVarIntTests
{
    [TestMethod]
    public void BoundariesRoundTripInTheShortestForm()
    {
        AssertRoundTrip(0, 1);
        AssertRoundTrip(63, 1);
        AssertRoundTrip(64, 2);
        AssertRoundTrip(16_383, 2);
        AssertRoundTrip(16_384, 4);
        AssertRoundTrip(1_073_741_823, 4);
        AssertRoundTrip(1_073_741_824, 8);
        AssertRoundTrip(QuicVarInt.MaxValue, 8);
    }

    [TestMethod]
    public void LongerEncodingStillDecodes()
    {
        byte[] encoded = [0x40, 0x19];
        Assert.IsTrue(QuicVarInt.TryDecode(encoded, out ulong value, out int consumed));
        Assert.AreEqual(25UL, value);
        Assert.AreEqual(2, consumed);
    }

    [TestMethod]
    public void TruncatedBufferDoesNotDecode()
    {
        Assert.IsFalse(QuicVarInt.TryDecode([], out _, out _));
        Assert.IsFalse(QuicVarInt.TryDecode([0x40], out _, out _));
        Assert.IsFalse(QuicVarInt.TryDecode([0x80, 0x00, 0x00], out _, out _));
        Assert.IsFalse(QuicVarInt.TryDecode([0xC0, 0, 0, 0, 0, 0, 0], out _, out _));
    }

    [TestMethod]
    public void ValuePastTheMaximumIsRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => QuicVarInt.Encode(QuicVarInt.MaxValue + 1, new byte[8]));
    }

    [TestMethod]
    public void ShortDestinationWritesNothing()
    {
        var destination = new byte[1];
        Assert.AreEqual(0, QuicVarInt.Encode(64, destination));
        Assert.AreEqual(0, destination[0]);
    }

    private static void AssertRoundTrip(ulong value, int expectedLength)
    {
        var buffer = new byte[8];
        int written = QuicVarInt.Encode(value, buffer);
        Assert.AreEqual(expectedLength, written);
        Assert.IsTrue(QuicVarInt.TryDecode(buffer.AsSpan(0, written), out ulong decoded, out int consumed));
        Assert.AreEqual(value, decoded);
        Assert.AreEqual(written, consumed);
    }
}
