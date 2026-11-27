using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicFrameTests
{
    [TestMethod]
    public void EveryFrameRoundTrips()
    {
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.Padding });
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.Ping });
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.ResetStream, StreamId = 4, ErrorCode = 9, Limit = 20 });
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.StopSending, StreamId = 8, ErrorCode = 3 });
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.Crypto, Offset = 3, Data = [0x0a, 0x0b] }, QuicPacketKind.OneRtt);
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.NewToken, Data = [0x11, 0x22] });
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.MaxData, Limit = 1000 });
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.MaxStreamData, StreamId = 4, Limit = 80 });
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.MaxStreams, Limit = 6 });
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.MaxStreams, Unidirectional = true, Limit = 2 });
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.DataBlocked, Limit = 40 });
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.StreamDataBlocked, StreamId = 4, Limit = 40 });
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.StreamsBlocked, Limit = 6 });
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.StreamsBlocked, Unidirectional = true, Limit = 1 });
        byte[] token = new byte[16];
        token[0] = 0x7e;
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.NewConnectionId, Sequence = 4, RetirePriorTo = 1, Data = [0xaa, 0xbb], Token = token });
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.RetireConnectionId, Sequence = 2 });
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.PathChallenge, Data = [1, 2, 3, 4, 5, 6, 7, 8] });
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.PathResponse, Data = [8, 7, 6, 5, 4, 3, 2, 1] });
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.ConnectionClose, ErrorCode = 0x07, TriggeringFrame = 0x08, Data = "bye"u8.ToArray() });
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.ConnectionClose, ApplicationClose = true, ErrorCode = 9 });
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.HandshakeDone });
        RoundTrip(new QuicFrame { Kind = QuicFrameKind.Datagram, HasLength = true, Data = [0x44] });
    }

    [TestMethod]
    public void AckWithEcnRoundTrips()
    {
        QuicAckRange[] ranges =
        [
            new() { Length = 2 },
            new() { Gap = 0, Length = 1 },
        ];
        var frame = new QuicFrame
        {
            Kind = QuicFrameKind.Ack,
            LargestAcknowledged = 10,
            AckDelay = 5,
            AckRangeCount = 2,
            Ecn = true,
            Ect0 = 1,
            Ect1 = 2,
            Ce = 3,
        };
        ReadBack read = WriteAndRead(frame, ranges, QuicPacketKind.OneRtt);
        Assert.AreEqual(0UL, read.Error);
        Assert.AreEqual(10UL, read.LargestAcknowledged);
        Assert.AreEqual(5UL, read.AckDelay);
        Assert.AreEqual(2, read.AckRangeCount);
        Assert.AreEqual(2UL, read.Ranges[0].Length);
        Assert.AreEqual(0UL, read.Ranges[1].Gap);
        Assert.AreEqual(1UL, read.Ranges[1].Length);
        Assert.IsTrue(read.Ecn);
        Assert.AreEqual(1UL, read.Ect0);
        Assert.AreEqual(2UL, read.Ect1);
        Assert.AreEqual(3UL, read.Ce);
    }

    [TestMethod]
    public void StreamRoundTripsWithAndWithoutLength()
    {
        ReadBack withLength = WriteAndRead(new QuicFrame
        {
            Kind = QuicFrameKind.Stream,
            StreamId = 4,
            Offset = 9,
            Fin = true,
            HasLength = true,
            Data = [0x0c, 0x0d],
        }, [], QuicPacketKind.OneRtt);
        Assert.AreEqual(0UL, withLength.Error);
        Assert.IsTrue(withLength.HasLength);
        Assert.IsTrue(withLength.Fin);
        Assert.AreEqual(9UL, withLength.Offset);
        CollectionAssert.AreEqual(new byte[] { 0x0c, 0x0d }, withLength.Data);

        ReadBack without = WriteAndRead(new QuicFrame
        {
            Kind = QuicFrameKind.Stream,
            StreamId = 4,
            HasLength = false,
            Data = [0x61, 0x62],
        }, [], QuicPacketKind.ZeroRtt);
        Assert.AreEqual(0UL, without.Error);
        Assert.IsFalse(without.HasLength);
        Assert.AreEqual(0UL, without.Offset);
        CollectionAssert.AreEqual(new byte[] { 0x61, 0x62 }, without.Data);
    }

    [TestMethod]
    public void DatagramWithoutLengthConsumesThePacket()
    {
        ReadBack read = WriteAndRead(new QuicFrame
        {
            Kind = QuicFrameKind.Datagram,
            HasLength = false,
            Data = [0x09, 0x08],
        }, [], QuicPacketKind.OneRtt);
        Assert.AreEqual(0UL, read.Error);
        Assert.IsFalse(read.HasLength);
        CollectionAssert.AreEqual(new byte[] { 0x09, 0x08 }, read.Data);
    }

    [TestMethod]
    public void CryptoInOneRttIsLegalAndAckInInitialIsLegal()
    {
        ReadBack crypto = WriteAndRead(new QuicFrame { Kind = QuicFrameKind.Crypto, Offset = 0, Data = [0x01] }, [], QuicPacketKind.OneRtt);
        Assert.AreEqual(QuicFrameKind.Crypto, crypto.Kind);
        Assert.AreEqual(0UL, crypto.Error);

        ReadBack ack = WriteAndRead(new QuicFrame
        {
            Kind = QuicFrameKind.Ack,
            LargestAcknowledged = 1,
            AckRangeCount = 1,
        }, [new QuicAckRange { Length = 0 }], QuicPacketKind.Initial);
        Assert.AreEqual(QuicFrameKind.Ack, ack.Kind);
        Assert.AreEqual(0UL, ack.Error);
    }

    [TestMethod]
    public void AckInZeroRttAndStreamInInitialAreProtocolViolations()
    {
        AssertRejected(new QuicFrame
        {
            Kind = QuicFrameKind.Ack,
            LargestAcknowledged = 1,
            AckRangeCount = 1,
        }, [new QuicAckRange { Length = 0 }], QuicPacketKind.ZeroRtt, QuicFrameReader.ProtocolViolation);
        AssertRejected(new QuicFrame
        {
            Kind = QuicFrameKind.Stream,
            StreamId = 0,
            HasLength = true,
            Data = [0x01],
        }, [], QuicPacketKind.Initial, QuicFrameReader.ProtocolViolation);
    }

    [TestMethod]
    public void UnknownTypeAndNonMinimalTypeAndEmptyPayloadAreErrors()
    {
        AssertRaw([0x1f], QuicPacketKind.OneRtt, QuicFrameReader.FrameEncodingError);
        AssertRaw([0x40, 0x01], QuicPacketKind.OneRtt, QuicFrameReader.ProtocolViolation);
        AssertRaw([], QuicPacketKind.Initial, QuicFrameReader.ProtocolViolation);
    }

    [TestMethod]
    public void AckAndStreamThatDoNotFitAreFrameEncodingErrors()
    {
        AssertRaw([0x02, 0x00, 0x00, 0x00, 0x01], QuicPacketKind.OneRtt, QuicFrameReader.FrameEncodingError);
        AssertRaw([0x02, 0x64, 0x00, 0x20, 0x00], QuicPacketKind.OneRtt, QuicFrameReader.FrameEncodingError);
        AssertRejected(new QuicFrame
        {
            Kind = QuicFrameKind.Stream,
            StreamId = 0,
            Offset = QuicVarInt.MaxValue,
            HasLength = true,
            Data = [0x01],
        }, [], QuicPacketKind.OneRtt, QuicFrameReader.FrameEncodingError);
        AssertRaw([0x07, 0x00], QuicPacketKind.OneRtt, QuicFrameReader.FrameEncodingError);
    }

    [TestMethod]
    public void FrameEdgesAreEncodingOrProtocolErrors()
    {
        AssertRaw([0x07, 0x00], QuicPacketKind.OneRtt, QuicFrameReader.FrameEncodingError);
        AssertRaw([0x1a, 1, 2, 3, 4, 5, 6, 7], QuicPacketKind.OneRtt, QuicFrameReader.FrameEncodingError);
        AssertRaw([0x1e], QuicPacketKind.Initial, QuicFrameReader.ProtocolViolation);
        AssertRaw([0x1d, 0x00, 0x00], QuicPacketKind.Initial, QuicFrameReader.ProtocolViolation);
        AssertRaw([0x08, 0x00, 0x01], QuicPacketKind.Handshake, QuicFrameReader.ProtocolViolation);
        AssertRaw([0x03, 0x00, 0x00, 0x00, 0x00], QuicPacketKind.OneRtt, QuicFrameReader.FrameEncodingError);

        byte[] over = new byte[9];
        over[0] = 0x12;
        Assert.AreEqual(8, QuicVarInt.Encode((1UL << 60) + 1, over.AsSpan(1)));
        AssertRaw(over, QuicPacketKind.OneRtt, QuicFrameReader.FrameEncodingError);

        byte[] retired = new byte[21];
        retired[0] = 0x18;
        retired[2] = 0x01;
        retired[3] = 0x01;
        retired[4] = 0xaa;
        AssertRaw(retired, QuicPacketKind.OneRtt, QuicFrameReader.FrameEncodingError);

        byte[] emptyId = new byte[4];
        emptyId[0] = 0x18;
        emptyId[1] = 0x01;
        AssertRaw(emptyId, QuicPacketKind.OneRtt, QuicFrameReader.FrameEncodingError);

        byte[] crypto = new byte[11];
        crypto[0] = 0x06;
        Assert.AreEqual(8, QuicVarInt.Encode(QuicVarInt.MaxValue, crypto.AsSpan(1)));
        crypto[9] = 0x01;
        crypto[10] = 0x01;
        AssertRaw(crypto, QuicPacketKind.OneRtt, QuicFrameReader.FrameEncodingError);
    }

    [TestMethod]
    public void AFrameFitsItsOwnLengthAndNotOneByteLess()
    {
        byte[] id = new byte[20];
        id[0] = 0x20;
        byte[] token = new byte[16];
        token[0] = 0x21;
        Fits(new QuicFrame { Kind = QuicFrameKind.Stream, StreamId = 4, HasLength = true, Data = [0x0c] });
        Fits(new QuicFrame { Kind = QuicFrameKind.Datagram, HasLength = true, Data = [0x44, 0x45] });
        Fits(new QuicFrame { Kind = QuicFrameKind.Datagram, HasLength = false, Data = [0x09] });
        Fits(new QuicFrame { Kind = QuicFrameKind.Crypto, Offset = 3, Data = [0x0a, 0x0b] });
        Fits(new QuicFrame { Kind = QuicFrameKind.NewToken, Data = [0x11] });
        Fits(new QuicFrame { Kind = QuicFrameKind.NewConnectionId, Sequence = 4, RetirePriorTo = 1, Data = id, Token = token });
        Fits(new QuicFrame { Kind = QuicFrameKind.ConnectionClose, ErrorCode = 7, TriggeringFrame = 8, Data = "bye"u8.ToArray() });
        Fits(new QuicFrame { Kind = QuicFrameKind.PathChallenge, Data = [1, 2, 3, 4, 5, 6, 7, 8] });
        Fits(new QuicFrame { Kind = QuicFrameKind.MaxStreams, Limit = QuicFrameReader.MaxStreamsLimit });
        Fits(new QuicFrame { Kind = QuicFrameKind.StreamsBlocked, Unidirectional = true, Limit = QuicFrameReader.MaxStreamsLimit });

        var tooLong = new QuicFrame { Kind = QuicFrameKind.NewConnectionId, Sequence = 1, Data = new byte[21], Token = token };
        Assert.AreEqual(0, QuicFrameWriter.Write(tooLong, [], new byte[64]));
    }

    [TestMethod]
    public void TwoFramesAreReadInOrder()
    {
        byte[] payload = [0x01, 0x00];
        var reader = new QuicFrameReader(payload, QuicPacketKind.Initial);
        var ranges = new QuicAckRange[1];
        Assert.IsTrue(reader.TryNext(ranges, out QuicFrame first));
        Assert.AreEqual(QuicFrameKind.Ping, first.Kind);
        Assert.IsTrue(reader.TryNext(ranges, out QuicFrame second));
        Assert.AreEqual(QuicFrameKind.Padding, second.Kind);
        Assert.IsFalse(reader.TryNext(ranges, out _));
        Assert.IsTrue(reader.Completed);
    }

    private static void Fits(QuicFrame frame)
    {
        byte[] wide = new byte[128];
        int written = QuicFrameWriter.Write(frame, [], wide);
        Assert.IsTrue(written > 1, frame.Kind.ToString());
        Assert.AreEqual(written, QuicFrameWriter.Write(frame, [], wide.AsSpan(0, written)), frame.Kind.ToString());
        Assert.AreEqual(0, QuicFrameWriter.Write(frame, [], wide.AsSpan(0, written - 1)), frame.Kind.ToString());
        var reader = new QuicFrameReader(wide.AsSpan(0, written), QuicPacketKind.OneRtt);
        Assert.IsTrue(reader.TryNext(new QuicAckRange[QuicFrameReader.MaxAckRanges], out QuicFrame decoded), frame.Kind.ToString());
        Assert.AreEqual(frame.Kind, decoded.Kind);
        Assert.AreEqual(frame.Limit, decoded.Limit);
        Assert.IsTrue(reader.Completed || reader.Error == 0);
    }

    private static void RoundTrip(QuicFrame frame, QuicPacketKind packet = QuicPacketKind.OneRtt)
    {
        ReadBack read = WriteAndRead(frame, frame.Kind == QuicFrameKind.Ack ? [] : [], packet);
        Assert.AreEqual(0UL, read.Error, frame.Kind.ToString());
        Assert.IsTrue(read.Completed, frame.Kind.ToString());
        Assert.AreEqual(frame.Kind, read.Kind);
        Assert.AreEqual(frame.StreamId, read.StreamId);
        Assert.AreEqual(frame.ErrorCode, read.ErrorCode);
        Assert.AreEqual(frame.Offset, read.Offset);
        Assert.AreEqual(frame.Limit, read.Limit);
        Assert.AreEqual(frame.Sequence, read.Sequence);
        Assert.AreEqual(frame.RetirePriorTo, read.RetirePriorTo);
        Assert.AreEqual(frame.TriggeringFrame, read.TriggeringFrame);
        Assert.AreEqual(frame.Fin, read.Fin);
        Assert.AreEqual(frame.HasLength || frame.Kind == QuicFrameKind.Crypto, read.HasLength);
        Assert.AreEqual(frame.Unidirectional, read.Unidirectional);
        Assert.AreEqual(frame.ApplicationClose, read.ApplicationClose);
        CollectionAssert.AreEqual(frame.Data.ToArray(), read.Data, frame.Kind.ToString());
        CollectionAssert.AreEqual(frame.Token.ToArray(), read.Token, frame.Kind.ToString());
    }

    private static void AssertRejected(QuicFrame frame, QuicAckRange[] ranges, QuicPacketKind packet, ulong error)
    {
        ReadBack read = WriteAndRead(frame, ranges, packet);
        Assert.AreEqual(error, read.Error);
        Assert.IsFalse(read.Completed);
    }

    private static void AssertRaw(byte[] payload, QuicPacketKind packet, ulong error)
    {
        var reader = new QuicFrameReader(payload, packet);
        Assert.IsFalse(reader.TryNext(new QuicAckRange[QuicFrameReader.MaxAckRanges], out _));
        Assert.AreEqual(error, reader.Error);
    }

    private static ReadBack WriteAndRead(QuicFrame frame, QuicAckRange[] ranges, QuicPacketKind packet)
    {
        var buffer = new byte[128];
        int written = QuicFrameWriter.Write(frame, ranges, buffer);
        Assert.IsTrue(written > 0, frame.Kind.ToString());
        var reader = new QuicFrameReader(buffer.AsSpan(0, written), packet);
        var gotRanges = new QuicAckRange[QuicFrameReader.MaxAckRanges];
        var read = new ReadBack { Ranges = gotRanges, Data = [], Token = [] };
        if (!reader.TryNext(gotRanges, out QuicFrame decoded))
        {
            read.Error = reader.Error;
            return read;
        }

        read.Kind = decoded.Kind;
        read.StreamId = decoded.StreamId;
        read.ErrorCode = decoded.ErrorCode;
        read.Offset = decoded.Offset;
        read.Limit = decoded.Limit;
        read.Sequence = decoded.Sequence;
        read.RetirePriorTo = decoded.RetirePriorTo;
        read.TriggeringFrame = decoded.TriggeringFrame;
        read.LargestAcknowledged = decoded.LargestAcknowledged;
        read.AckDelay = decoded.AckDelay;
        read.Ect0 = decoded.Ect0;
        read.Ect1 = decoded.Ect1;
        read.Ce = decoded.Ce;
        read.Fin = decoded.Fin;
        read.HasLength = decoded.HasLength;
        read.Ecn = decoded.Ecn;
        read.Unidirectional = decoded.Unidirectional;
        read.ApplicationClose = decoded.ApplicationClose;
        read.AckRangeCount = decoded.AckRangeCount;
        read.Data = decoded.Data.ToArray();
        read.Token = decoded.Token.ToArray();
        Assert.IsFalse(reader.TryNext(gotRanges, out _));
        read.Error = reader.Error;
        read.Completed = reader.Completed;
        return read;
    }

    private sealed class ReadBack
    {
        internal QuicFrameKind Kind;

        internal ulong StreamId;

        internal ulong ErrorCode;

        internal ulong Offset;

        internal ulong Limit;

        internal ulong Sequence;

        internal ulong RetirePriorTo;

        internal ulong TriggeringFrame;

        internal ulong LargestAcknowledged;

        internal ulong AckDelay;

        internal ulong Ect0;

        internal ulong Ect1;

        internal ulong Ce;

        internal bool Fin;

        internal bool HasLength;

        internal bool Ecn;

        internal bool Unidirectional;

        internal bool ApplicationClose;

        internal int AckRangeCount;

        internal QuicAckRange[] Ranges = [];

        internal byte[] Data = [];

        internal byte[] Token = [];

        internal ulong Error;

        internal bool Completed;
    }
}
