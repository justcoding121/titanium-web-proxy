using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit coverage for QUIC variable-length integer encoding/decoding per RFC 9000 §16.
/// </summary>
[TestClass]
public class Http3VarIntTests
{
    // ── Decoding via TryRead ──────────────────────────────────────────────────────

    [TestMethod]
    public void TryRead_SingleByteZero_DecodesCorrectly()
    {
        byte[] data = [0x00];
        var ok = Http3VarInt.TryRead(data, out var value, out var consumed);
        Assert.IsTrue(ok);
        Assert.AreEqual(0UL, value);
        Assert.AreEqual(1, consumed);
    }

    [TestMethod]
    public void TryRead_SingleByteMax_DecodesCorrectly()
    {
        byte[] data = [0x3F]; // 63, maximum 1-byte value
        var ok = Http3VarInt.TryRead(data, out var value, out var consumed);
        Assert.IsTrue(ok);
        Assert.AreEqual(63UL, value);
        Assert.AreEqual(1, consumed);
    }

    [TestMethod]
    public void TryRead_TwoByteEncoding_DecodesCorrectly()
    {
        byte[] data = [0x40, 0x00]; // 0 encoded as 2 bytes
        var ok = Http3VarInt.TryRead(data, out var value, out var consumed);
        Assert.IsTrue(ok);
        Assert.AreEqual(0UL, value);
        Assert.AreEqual(2, consumed);
    }

    [TestMethod]
    public void TryRead_FourByteEncoding_DecodesCorrectly()
    {
        byte[] data = [0x80, 0x00, 0x00, 0x01]; // value = 1 in 4-byte form
        var ok = Http3VarInt.TryRead(data, out var value, out var consumed);
        Assert.IsTrue(ok);
        Assert.AreEqual(1UL, value);
        Assert.AreEqual(4, consumed);
    }

    [TestMethod]
    public void TryRead_EmptySpan_ReturnsFalse()
    {
        var ok = Http3VarInt.TryRead(ReadOnlySpan<byte>.Empty, out _, out _);
        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void TryRead_InsufficientBytesForTwoByteEncoding_ReturnsFalse()
    {
        byte[] data = [0x40]; // prefix says 2 bytes but only 1 present
        var ok = Http3VarInt.TryRead(data, out _, out _);
        Assert.IsFalse(ok);
    }

    // ── Encoding via Write ────────────────────────────────────────────────────────

    [TestMethod]
    public void Write_SmallValue_UsesSingleByte()
    {
        byte[] buf = new byte[8];
        var written = Http3VarInt.Write(buf.AsSpan(), 63);
        Assert.AreEqual(1, written);
        Assert.AreEqual(0x3F, buf[0]);
    }

    [TestMethod]
    public void Write_MediumValue_UsesTwoBytes()
    {
        byte[] buf = new byte[8];
        var written = Http3VarInt.Write(buf.AsSpan(), 64); // just above 1-byte limit
        Assert.AreEqual(2, written);
    }

    [TestMethod]
    public void Write_LargeValue_UsesFourBytes()
    {
        byte[] buf = new byte[8];
        var written = Http3VarInt.Write(buf.AsSpan(), 16384); // just above 2-byte limit
        Assert.AreEqual(4, written);
    }

    // ── Round-trip ───────────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(0UL)]
    [DataRow(63UL)]
    [DataRow(64UL)]
    [DataRow(16383UL)]
    [DataRow(16384UL)]
    [DataRow(1073741823UL)]
    [DataRow(1073741824UL)]
    [DataRow(4611686018427387903UL)] // max representable value
    public void RoundTrip_Symmetric(ulong value)
    {
        byte[] buf = new byte[8];
        var written = Http3VarInt.Write(buf.AsSpan(), value);
        var ok = Http3VarInt.TryRead(buf.AsSpan()[..written], out var decoded, out var bytesRead);

        Assert.IsTrue(ok);
        Assert.AreEqual(value, decoded);
        Assert.AreEqual(written, bytesRead);
    }
}
