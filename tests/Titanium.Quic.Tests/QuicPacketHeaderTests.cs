using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicPacketHeaderTests
{
    [TestMethod]
    public void LongHeadersRoundTripEachType()
    {
        byte[] initial = LongPacket(0x00, token: [9, 9], packetNumber: [1, 2], payload: [3, 4, 5]);
        AssertPacket(initial, QuicPacketKind.Initial, destination: [0xAA], source: [0xBB], packetNumber: [1, 2], payload: [3, 4, 5], token: [9, 9]);

        byte[] zeroRtt = LongPacket(0x01, token: null, packetNumber: [4], payload: [5]);
        AssertPacket(zeroRtt, QuicPacketKind.ZeroRtt, destination: [0xAA], source: [0xBB], packetNumber: [4], payload: [5], token: []);

        byte[] handshake = LongPacket(0x02, token: null, packetNumber: [6, 7, 8, 9], payload: [10]);
        AssertPacket(handshake, QuicPacketKind.Handshake, destination: [0xAA], source: [0xBB], packetNumber: [6, 7, 8, 9], payload: [10], token: []);

        byte[] tag = new byte[16];
        tag[0] = 0x7E;
        byte[] retry = Retry([1, 2, 3], tag);
        AssertPacket(retry, QuicPacketKind.Retry, destination: [0xAA], source: [0xBB], packetNumber: [], payload: [], token: [1, 2, 3]);
        var reader = new QuicDatagramReader(retry, shortDestinationIdLength: 0);
        Assert.IsTrue(reader.TryRead(out QuicPacket packet));
        CollectionAssert.AreEqual(tag, packet.IntegrityTag.ToArray());
        Assert.IsFalse(reader.TryRead(out _));
    }

    [TestMethod]
    public void ShortHeaderUsesTheSuppliedConnectionIdLength()
    {
        byte[] datagram = [0x46, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
        var reader = new QuicDatagramReader(datagram, shortDestinationIdLength: 8);
        Assert.IsTrue(reader.TryRead(out QuicPacket packet));
        Assert.AreEqual(QuicPacketKind.OneRtt, packet.Kind);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, packet.DestinationConnectionId.ToArray());
        CollectionAssert.AreEqual(new byte[] { 9, 10, 11 }, packet.PacketNumber.ToArray());
        Assert.AreEqual(0, packet.Payload.Length);
        Assert.IsTrue(packet.FixedBit);
        Assert.IsFalse(packet.Spin);
        Assert.IsTrue(packet.KeyPhase);
        Assert.IsFalse(reader.TryRead(out _));
    }

    [TestMethod]
    public void CoalescedInitialAndHandshakeStopBeforeAShortHeader()
    {
        var bytes = new List<byte>();
        bytes.AddRange(LongPacket(0x00, token: [], packetNumber: [1], payload: [2]));
        bytes.AddRange(LongPacket(0x02, token: null, packetNumber: [3], payload: [4]));
        bytes.AddRange(new byte[] { 0x41, 9, 8, 7 });
        var reader = new QuicDatagramReader(bytes.ToArray(), shortDestinationIdLength: 1);
        Assert.IsTrue(reader.TryRead(out QuicPacket initial));
        Assert.AreEqual(QuicPacketKind.Initial, initial.Kind);
        CollectionAssert.AreEqual(new byte[] { 2 }, initial.Payload.ToArray());
        Assert.IsTrue(reader.TryRead(out QuicPacket handshake));
        Assert.AreEqual(QuicPacketKind.Handshake, handshake.Kind);
        CollectionAssert.AreEqual(new byte[] { 4 }, handshake.Payload.ToArray());
        Assert.IsTrue(reader.TryRead(out QuicPacket oneRtt));
        Assert.AreEqual(QuicPacketKind.OneRtt, oneRtt.Kind);
        CollectionAssert.AreEqual(new byte[] { 9 }, oneRtt.DestinationConnectionId.ToArray());
        CollectionAssert.AreEqual(new byte[] { 8, 7 }, oneRtt.PacketNumber.ToArray());
        Assert.IsFalse(reader.TryRead(out _));
    }

    [TestMethod]
    public void VersionNegotiationCarriesSupportedVersions()
    {
        var bytes = new List<byte> { 0x80, 0, 0, 0, 0, 1, 0xAA, 1, 0xBB };
        bytes.AddRange(new byte[] { 0, 0, 0, 1, 0x6b, 0x33, 0x43, 0xcf });
        var reader = new QuicDatagramReader(bytes.ToArray(), shortDestinationIdLength: 0);
        Assert.IsTrue(reader.TryRead(out QuicPacket packet));
        Assert.AreEqual(QuicPacketKind.VersionNegotiation, packet.Kind);
        Assert.AreEqual(0u, packet.Version);
        CollectionAssert.AreEqual(new byte[] { 0xAA }, packet.DestinationConnectionId.ToArray());
        CollectionAssert.AreEqual(new byte[] { 0xBB }, packet.SourceConnectionId.ToArray());
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 1, 0x6b, 0x33, 0x43, 0xcf }, packet.SupportedVersions.ToArray());
        Assert.IsFalse(reader.TryRead(out _));
    }

    [TestMethod]
    public void VersionNegotiationAllowsAConnectionIdLongerThanTwenty()
    {
        var bytes = new List<byte> { 0x80, 0, 0, 0, 0, 21 };
        bytes.AddRange(new byte[21]);
        bytes.Add(0);
        bytes.AddRange(new byte[] { 0, 0, 0, 1 });
        Assert.IsTrue(new QuicDatagramReader(bytes.ToArray(), 0).TryRead(out QuicPacket packet));
        Assert.AreEqual(21, packet.DestinationConnectionId.Length);
    }

    [TestMethod]
    public void HeaderEdgesKeepTheLengthAndTheBits()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new QuicDatagramReader([0x40], -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new QuicDatagramReader([0x40], 21));

        byte[] cid = new byte[20];
        cid[0] = 0x11;
        var shortHeader = new List<byte> { 0x40 };
        shortHeader.AddRange(cid);
        shortHeader.Add(0x05);
        Assert.IsTrue(new QuicDatagramReader(shortHeader.ToArray(), 20).TryRead(out QuicPacket packet));
        Assert.AreEqual(20, packet.DestinationConnectionId.Length);
        Assert.AreEqual(1, packet.PacketNumber.Length);
        Assert.IsFalse(packet.Spin);
        Assert.IsFalse(packet.KeyPhase);
        Assert.IsFalse(new QuicDatagramReader(shortHeader.ToArray().AsSpan(0, shortHeader.Count - 1), 20).TryRead(out _));

        Assert.IsTrue(new QuicDatagramReader([0x00, 0x01], 0).TryRead(out packet));
        Assert.IsFalse(packet.FixedBit);
        Assert.IsTrue(new QuicDatagramReader([0x60, 0x09, 0x01], 1).TryRead(out packet));
        Assert.IsTrue(packet.Spin);
        Assert.IsFalse(packet.KeyPhase);

        var versions = new List<byte> { 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 };
        Assert.IsTrue(new QuicDatagramReader(versions.ToArray(), 0).TryRead(out packet));
        Assert.AreEqual(4, packet.SupportedVersions.Length);
        Assert.IsFalse(new QuicDatagramReader(versions.GetRange(0, versions.Count - 2).ToArray(), 0).TryRead(out _));
        versions.AddRange(new byte[] { 0, 2 });
        Assert.IsFalse(new QuicDatagramReader(versions.ToArray(), 0).TryRead(out _));

        byte[] unknown = LongPacket(0x00, token: [], packetNumber: [1], payload: [2]);
        unknown[4] = 2;
        Assert.IsFalse(new QuicDatagramReader(unknown, 0).TryRead(out _));

        byte[] probe = new byte[1200];
        probe[0] = 0xC0;
        probe[1] = 0x57;
        probe[2] = 0x41;
        probe[3] = 0x49;
        probe[4] = 0x54;
        byte[] reply = new byte[64];
        Assert.IsTrue(QuicDatagramReader.TryVersionNegotiation(probe, reply, out int written));
        Assert.AreEqual(11, written);
        Assert.AreEqual(0, reply[1]);
        Assert.AreEqual(0, reply[4]);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 1 }, reply.AsSpan(7, 4).ToArray());
        Assert.IsFalse(QuicDatagramReader.TryVersionNegotiation(probe.AsSpan(0, 1199), reply, out _));

        var long20 = new List<byte> { 0xE0, 0, 0, 0, 1, 20 };
        long20.AddRange(new byte[20]);
        long20.Add(0);
        long20.Add(1);
        long20.Add(9);
        Assert.IsTrue(new QuicDatagramReader(long20.ToArray(), 0).TryRead(out packet));
        Assert.AreEqual(20, packet.DestinationConnectionId.Length);

        var longSource = new List<byte> { 0xE0, 0, 0, 0, 1, 0, 21 };
        longSource.AddRange(new byte[21]);
        Assert.IsFalse(new QuicDatagramReader(longSource.ToArray(), 0).TryRead(out _));

        Assert.IsTrue(new QuicDatagramReader([0xE1, 0, 0, 0, 1, 0, 0, 2, 0x11, 0x22], 0).TryRead(out packet));
        Assert.AreEqual(2, packet.PacketNumber.Length);
        Assert.AreEqual(0, packet.Payload.Length);
        Assert.IsFalse(new QuicDatagramReader([0xE1, 0, 0, 0, 1, 0, 0, 1, 0x11, 0x22], 0).TryRead(out _));
    }

    [TestMethod]
    public void TruncatedAndOversizedHeadersAreRejected()
    {
        Assert.IsFalse(new QuicDatagramReader([0xC0, 0, 0, 0], 0).TryRead(out _));
        byte[] initial = LongPacket(0x00, token: [], packetNumber: [1, 2], payload: [3]);
        Assert.IsFalse(new QuicDatagramReader(initial.AsSpan(0, initial.Length - 1), 0).TryRead(out _));
        var oversized = new List<byte> { 0xC0, 0, 0, 0, 1, 21 };
        oversized.AddRange(new byte[21]);
        oversized.Add(0);
        Assert.IsFalse(new QuicDatagramReader(oversized.ToArray(), 0).TryRead(out _));
        Assert.IsFalse(new QuicDatagramReader([], 0).TryRead(out _));
    }

    private static void AssertPacket(byte[] datagram, QuicPacketKind kind, byte[] destination, byte[] source, byte[] packetNumber, byte[] payload, byte[] token)
    {
        var reader = new QuicDatagramReader(datagram, shortDestinationIdLength: 0);
        Assert.IsTrue(reader.TryRead(out QuicPacket packet));
        Assert.AreEqual(kind, packet.Kind);
        Assert.AreEqual(QuicDatagramReader.Version1, packet.Version);
        CollectionAssert.AreEqual(destination, packet.DestinationConnectionId.ToArray());
        CollectionAssert.AreEqual(source, packet.SourceConnectionId.ToArray());
        CollectionAssert.AreEqual(packetNumber, packet.PacketNumber.ToArray());
        CollectionAssert.AreEqual(payload, packet.Payload.ToArray());
        CollectionAssert.AreEqual(token, packet.Token.ToArray());
        Assert.IsFalse(reader.TryRead(out _));
    }

    private static byte[] LongPacket(byte type, byte[]? token, byte[] packetNumber, byte[] payload)
    {
        var bytes = new List<byte>
        {
            (byte)(0xC0 | (type << 4) | (packetNumber.Length - 1)),
            0, 0, 0, 1,
            1, 0xAA,
            1, 0xBB,
        };
        if (token is not null)
        {
            bytes.AddRange(Var(token.Length));
            bytes.AddRange(token);
        }

        bytes.AddRange(Var(packetNumber.Length + payload.Length));
        bytes.AddRange(packetNumber);
        bytes.AddRange(payload);
        return bytes.ToArray();
    }

    private static byte[] Retry(byte[] token, byte[] tag)
    {
        var bytes = new List<byte> { 0xF0, 0, 0, 0, 1, 1, 0xAA, 1, 0xBB };
        bytes.AddRange(token);
        bytes.AddRange(tag);
        return bytes.ToArray();
    }

    private static byte[] Var(int value)
    {
        var buffer = new byte[8];
        int written = QuicVarInt.Encode((ulong)value, buffer);
        return buffer.AsSpan(0, written).ToArray();
    }
}
