using System;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicPacketProtectorTests
{
    private static readonly byte[] DestinationId = Convert.FromHexString("8394c8f03e515708");

    private static readonly byte[] SourceId = [0x51, 0x51, 0x51, 0x51];

    private static readonly byte[] Payload = [0x01, 0x02, 0x03, 0x04];

    [TestMethod]
    public void AShortPacketRoundTripsAndRejectsAShortBuffer()
    {
        using var protector = QuicPacketProtector.FromInitial(DestinationId, client: true);
        byte[] wide = new byte[64];
        int written = QuicPackets.SealShort(wide, DestinationId, 0x0201, Payload, protector);
        Assert.AreEqual("488394C8F03E515708796BCF948CAE233B638AE58CA8BB6F06FBAC23F62C54", Convert.ToHexString(wide.AsSpan(0, written)));
        byte[] exact = wide[..written];
        byte[] fitted = new byte[written];
        Assert.AreEqual(written, QuicPackets.SealShort(fitted, DestinationId, 0x0201, Payload, protector));
        CollectionAssert.AreEqual(exact, fitted);
        Assert.AreEqual(0, QuicPackets.SealShort(new byte[written - 1], DestinationId, 0x0201, Payload, protector));
        Assert.AreEqual(0, QuicPackets.SealShort(new byte[written], DestinationId, 0x0201, ReadOnlySpan<byte>.Empty, protector));

        int offset = 1 + DestinationId.Length;
        byte[] plain = new byte[Payload.Length];
        Assert.IsTrue(protector.Open(exact, offset, largest: 0, out ulong number, plain, out int plainLength));
        Assert.AreEqual(0x0201UL, number);
        Assert.AreEqual(Payload.Length, plainLength);
        CollectionAssert.AreEqual(Payload, plain[..plainLength]);

        byte[] damaged = (byte[])exact.Clone();
        damaged[^1] ^= 0x01;
        Assert.IsFalse(protector.Open(damaged, offset, largest: 0, out _, plain, out _));
        Assert.IsFalse(protector.Open(exact, packetNumberOffset: 0, largest: 0, out _, plain, out _));
        Assert.IsFalse(protector.Open(exact.AsSpan(0, offset + 19), offset, largest: 0, out _, plain, out _));
        Assert.IsFalse(protector.Open(exact, offset, QuicPacketNumber.MaxValue, out _, plain, out _));
        Assert.IsFalse(protector.Open((byte[])exact.Clone(), offset, largest: 0, out _, new byte[Payload.Length - 1], out _));

        byte[] direct = new byte[written];
        direct[0] = 0x41;
        Assert.AreEqual(written, protector.Seal(direct, 1 + DestinationId.Length + 2, 0x0201, Payload));
        Assert.AreEqual(0, protector.Seal(new byte[written - 1], 1 + DestinationId.Length + 2, 0x0201, Payload));

        byte[] two = [0xAB, 0xCD];
        byte[] tiny = new byte[1 + DestinationId.Length + 2 + two.Length + 16];
        Assert.AreEqual(tiny.Length, QuicPackets.SealShort(tiny, DestinationId, 0x0201, two, protector));
        Assert.IsTrue(protector.Open(tiny, offset, largest: 0, out ulong tinyNumber, new byte[two.Length], out int tinyLength));
        Assert.AreEqual(0x0201UL, tinyNumber);
        Assert.AreEqual(two.Length, tinyLength);

        byte[] header = new byte[32];
        Assert.AreEqual(0, protector.Seal(header, headerLength: 0, packetNumber: 1, Payload));
        header[0] = 0x00;
        Assert.AreEqual(0, protector.Seal(header, headerLength: 1, packetNumber: 1, Payload));

        protector.Dispose();
        protector.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => protector.Seal(header, 1, 1, Payload));
    }

    [TestMethod]
    public void ALongPacketPadsToTheMinimumAndCarriesAToken()
    {
        using var protector = QuicPacketProtector.FromSecret(new byte[32]);
        byte[] initial = new byte[256];
        int bare = QuicPackets.SealLong(initial, QuicPacketKind.Initial, DestinationId, SourceId, 0x0302, Payload, protector, minimumDatagram: 0);
        Assert.AreEqual("C100000001088394C8F03E51570804515151510016D58CAA7475044467EF51033490BF0654C909E24285F4", Convert.ToHexString(initial.AsSpan(0, bare)));
        byte[] plain = new byte[Payload.Length];
        Assert.IsTrue(protector.Open(initial.AsSpan(0, bare), bare - Payload.Length - 16 - 2, largest: 0, out ulong number, plain, out int plainLength));
        Assert.AreEqual(0x0302UL, number);
        Assert.AreEqual(Payload.Length, plainLength);
        CollectionAssert.AreEqual(Payload, plain);
        byte[] token = [0xAA, 0xBB];
        byte[] tokenPacket = new byte[256];
        int withToken = QuicPackets.SealLong(tokenPacket, QuicPacketKind.Initial, DestinationId, SourceId, 7, Payload, protector, 0, token);
        Assert.AreEqual("C900000001088394C8F03E515708045151515102AABB1644E3FFD6FB7A11FF42596F6A9E5B84F247E87B91FF00", Convert.ToHexString(tokenPacket.AsSpan(0, withToken)));
        byte[] handshakePacket = new byte[256];
        int handshake = QuicPackets.SealLong(handshakePacket, QuicPacketKind.Handshake, DestinationId, SourceId, 7, Payload, protector, 0);
        Assert.AreEqual("EC00000001088394C8F03E5157080451515151160DC5FFD6FB7A5A2BC402BF7020DA2CBFDC256AA3B845", Convert.ToHexString(handshakePacket.AsSpan(0, handshake)));
        byte[] fittedLong = new byte[bare];
        Assert.AreEqual(bare, QuicPackets.SealLong(fittedLong, QuicPacketKind.Initial, DestinationId, SourceId, 0x0302, Payload, protector, 0));

        int padded = QuicPackets.SealLong(new byte[2048], QuicPacketKind.Initial, DestinationId, SourceId, 7, Payload, protector, 1200);
        Assert.IsTrue(padded >= 1200);
        Assert.AreEqual(0, QuicPackets.SealLong(new byte[32], QuicPacketKind.Handshake, DestinationId, SourceId, 7, Payload, protector, 0));

        byte[] crowded = new byte[64];
        Assert.IsTrue(QuicPackets.SealLong(new byte[256], QuicPacketKind.Handshake, DestinationId, SourceId, 9, crowded, protector, 0) > 0);
        Assert.IsTrue(QuicPackets.SealShort(new byte[128], new byte[20], 1, Payload, protector) > 0);
        Assert.IsTrue(QuicPackets.SealShort(new byte[64], ReadOnlySpan<byte>.Empty, 1, Payload, protector) > 0);
    }
}
