using System;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicRetryTests
{
    private static readonly byte[] OriginalDestinationConnectionId = Convert.FromHexString("8394c8f03e515708");

    private static readonly byte[] Sample = Convert.FromHexString(
        "ff000000010008f067a5502a4262b5746f6b656e04a265ba2eff4d829058fb3f0f2496ba");

    [TestMethod]
    public void PublishedKeyMatchesTheExpandLabel()
    {
        byte[] secret = Convert.FromHexString("d9c9943e6101fd200021506bcc02814c73030f25c79d71ce876eca876e6fca8e");
        var key = new byte[16];
        var iv = new byte[12];
        var unused = new byte[16];
        QuicInitial.DeriveTraffic(HashAlgorithmName.SHA256, secret, key, iv, unused);
        CollectionAssert.AreEqual(QuicRetry.Key.ToArray(), key);
        CollectionAssert.AreEqual(QuicRetry.Nonce.ToArray(), iv);
    }

    [TestMethod]
    public void AppendixRetrySampleVerifies()
    {
        var tag = new byte[16];
        Assert.IsTrue(QuicRetry.TryWriteTag(OriginalDestinationConnectionId, Sample[..^16], tag));
        CollectionAssert.AreEqual(Sample[^16..], tag);
        Assert.IsTrue(QuicRetry.TryVerify(OriginalDestinationConnectionId, Sample));
    }

    [TestMethod]
    public void AFlippedTagIsDiscarded()
    {
        byte[] packet = (byte[])Sample.Clone();
        packet[^1] ^= 0x01;
        Assert.IsFalse(QuicRetry.TryVerify(OriginalDestinationConnectionId, packet));
    }

    [TestMethod]
    public void ADifferentOriginalDestinationConnectionIdIsDiscarded()
    {
        byte[] other = (byte[])OriginalDestinationConnectionId.Clone();
        other[0] ^= 0x01;
        Assert.IsFalse(QuicRetry.TryVerify(other, Sample));
    }

    [TestMethod]
    public void ShortInputsAndTheConnectionIdLimitAreEdges()
    {
        var tag = new byte[16];
        ReadOnlySpan<byte> body = Sample.AsSpan(0, Sample.Length - 16);
        Assert.IsFalse(QuicRetry.TryWriteTag(OriginalDestinationConnectionId, body, tag.AsSpan(0, 15)));
        Assert.IsFalse(QuicRetry.TryVerify(OriginalDestinationConnectionId, Sample.AsSpan(0, 15)));

        Assert.IsTrue(QuicRetry.TryWriteTag(OriginalDestinationConnectionId, ReadOnlySpan<byte>.Empty, tag));
        Assert.IsTrue(QuicRetry.TryVerify(OriginalDestinationConnectionId, tag));

        byte[] twenty = new byte[20];
        Assert.IsTrue(QuicRetry.TryWriteTag(twenty, body, tag));
        byte[] packet = new byte[body.Length + tag.Length];
        body.CopyTo(packet);
        tag.CopyTo(packet.AsSpan(body.Length));
        Assert.IsTrue(QuicRetry.TryVerify(twenty, packet));

        byte[] tooLong = new byte[21];
        Assert.IsFalse(QuicRetry.TryWriteTag(tooLong, body, tag));
        Assert.IsFalse(QuicRetry.TryVerify(tooLong, Sample));
    }
}
