using System;

namespace Titanium.Quic;

internal enum QuicFrameKind : byte
{
    Padding = 0,
    Ping = 1,
    Ack = 2,
    ResetStream = 3,
    StopSending = 4,
    Crypto = 5,
    NewToken = 6,
    Stream = 7,
    MaxData = 8,
    MaxStreamData = 9,
    MaxStreams = 10,
    DataBlocked = 11,
    StreamDataBlocked = 12,
    StreamsBlocked = 13,
    NewConnectionId = 14,
    RetireConnectionId = 15,
    PathChallenge = 16,
    PathResponse = 17,
    ConnectionClose = 18,
    HandshakeDone = 19,
    Datagram = 20,
}

internal struct QuicAckRange
{
    internal ulong Gap;

    internal ulong Length;
}

/// <summary>One decoded frame. Spans point at the packet payload.</summary>
internal ref struct QuicFrame
{
    internal QuicFrameKind Kind;

    internal ulong StreamId;

    internal ulong ErrorCode;

    internal ulong Offset;

    internal ulong LargestAcknowledged;

    internal ulong AckDelay;

    internal ulong Limit;

    internal ulong Sequence;

    internal ulong RetirePriorTo;

    internal ulong TriggeringFrame;

    internal ulong Ect0;

    internal ulong Ect1;

    internal ulong Ce;

    internal bool Fin;

    internal bool HasLength;

    internal bool Ecn;

    internal bool Unidirectional;

    internal bool ApplicationClose;

    internal int AckRangeCount;

    internal ReadOnlySpan<byte> Data;

    internal ReadOnlySpan<byte> Token;
}

/// <summary>Pulls frames from one unprotected payload. An error stops the reader.</summary>
internal ref struct QuicFrameReader
{
    internal const int MaxAckRanges = 32;

    internal const ulong FrameEncodingError = 0x07;

    internal const ulong ProtocolViolation = 0x0a;

    internal const ulong MaxStreamsLimit = 1UL << 60;

    private ReadOnlySpan<byte> _rest;

    private readonly QuicPacketKind _packet;

    private bool _sawFrame;

    private bool _failed;

    internal QuicFrameReader(ReadOnlySpan<byte> payload, QuicPacketKind packet)
    {
        _rest = payload;
        _packet = packet;
    }

    internal ulong Error { get; private set; }

    internal bool Completed => Error == 0 && _rest.IsEmpty && _sawFrame;

    internal bool TryNext(Span<QuicAckRange> ranges, out QuicFrame frame)
    {
        frame = default;
        if (_failed)
        {
            return false;
        }

        if (_rest.IsEmpty)
        {
            if (!_sawFrame)
            {
                return Fail(ProtocolViolation);
            }

            return false;
        }

        if (!QuicVarInt.TryDecode(_rest, out ulong type, out int used))
        {
            return Fail(FrameEncodingError);
        }

        if (used != Minimal(type))
        {
            return Fail(ProtocolViolation);
        }

        _rest = _rest[used..];
        QuicFrame decoded = default;
        if (!ReadBody(type, ranges, ref decoded))
        {
            frame = default;
            return Error == 0 ? Fail(FrameEncodingError) : false;
        }

        if (!Allowed(decoded, _packet))
        {
            frame = default;
            return Fail(ProtocolViolation);
        }

        frame = decoded;
        _sawFrame = true;
        return true;
    }

    private bool ReadBody(ulong type, Span<QuicAckRange> ranges, ref QuicFrame frame)
    {
        switch (type)
        {
            case 0x00:
                frame.Kind = QuicFrameKind.Padding;
                return true;
            case 0x01:
                frame.Kind = QuicFrameKind.Ping;
                return true;
            case 0x02:
            case 0x03:
                return ReadAck(type == 0x03, ranges, ref frame);
            case 0x04:
                frame.Kind = QuicFrameKind.ResetStream;
                return Take(out frame.StreamId) && Take(out frame.ErrorCode) && Take(out frame.Limit);
            case 0x05:
                frame.Kind = QuicFrameKind.StopSending;
                return Take(out frame.StreamId) && Take(out frame.ErrorCode);
            case 0x06:
                frame.Kind = QuicFrameKind.Crypto;
                frame.HasLength = true;
                return Take(out frame.Offset) && TakeBytes(out frame.Data) && EndFits(frame.Offset, frame.Data.Length);
            case 0x07:
                frame.Kind = QuicFrameKind.NewToken;
                return TakeBytes(out frame.Data) && !frame.Data.IsEmpty;
            case >= 0x08 and <= 0x0f:
                return ReadStream(type, ref frame);
            case 0x10:
                frame.Kind = QuicFrameKind.MaxData;
                return Take(out frame.Limit);
            case 0x11:
                frame.Kind = QuicFrameKind.MaxStreamData;
                return Take(out frame.StreamId) && Take(out frame.Limit);
            case 0x12:
            case 0x13:
                frame.Kind = QuicFrameKind.MaxStreams;
                frame.Unidirectional = type == 0x13;
                return Take(out frame.Limit) && frame.Limit <= MaxStreamsLimit;
            case 0x14:
                frame.Kind = QuicFrameKind.DataBlocked;
                return Take(out frame.Limit);
            case 0x15:
                frame.Kind = QuicFrameKind.StreamDataBlocked;
                return Take(out frame.StreamId) && Take(out frame.Limit);
            case 0x16:
            case 0x17:
                frame.Kind = QuicFrameKind.StreamsBlocked;
                frame.Unidirectional = type == 0x17;
                return Take(out frame.Limit) && frame.Limit <= MaxStreamsLimit;
            case 0x18:
                return ReadNewConnectionId(ref frame);
            case 0x19:
                frame.Kind = QuicFrameKind.RetireConnectionId;
                return Take(out frame.Sequence);
            case 0x1a:
                frame.Kind = QuicFrameKind.PathChallenge;
                return TakeFixed(8, out frame.Data);
            case 0x1b:
                frame.Kind = QuicFrameKind.PathResponse;
                return TakeFixed(8, out frame.Data);
            case 0x1c:
            case 0x1d:
                return ReadClose(type == 0x1d, ref frame);
            case 0x1e:
                frame.Kind = QuicFrameKind.HandshakeDone;
                return true;
            case 0x30:
            case 0x31:
                frame.Kind = QuicFrameKind.Datagram;
                frame.HasLength = type == 0x31;
                return frame.HasLength ? TakeBytes(out frame.Data) : TakeRest(out frame.Data);
            default:
                return Fail(FrameEncodingError);
        }
    }

    private bool ReadAck(bool ecn, Span<QuicAckRange> ranges, ref QuicFrame frame)
    {
        frame.Kind = QuicFrameKind.Ack;
        frame.Ecn = ecn;
        if (!Take(out frame.LargestAcknowledged) || !Take(out frame.AckDelay) || !Take(out ulong count) || !Take(out ulong first))
        {
            return false;
        }

        if (count >= (ulong)MaxAckRanges || first > frame.LargestAcknowledged || ranges.Length < (int)count + 1)
        {
            return Fail(FrameEncodingError);
        }

        ranges[0] = new QuicAckRange { Length = first };
        ulong smallest = frame.LargestAcknowledged - first;
        for (int i = 0; i < (int)count; i++)
        {
            if (!Take(out ulong gap) || !Take(out ulong length))
            {
                return false;
            }

            if (gap > smallest || smallest - gap < 2 || length > smallest - gap - 2)
            {
                return Fail(FrameEncodingError);
            }

            ulong largest = smallest - gap - 2;
            smallest = largest - length;
            ranges[i + 1] = new QuicAckRange { Gap = gap, Length = length };
        }

        frame.AckRangeCount = (int)count + 1;
        if (!ecn)
        {
            return true;
        }

        return Take(out frame.Ect0) && Take(out frame.Ect1) && Take(out frame.Ce);
    }

    private bool ReadStream(ulong type, ref QuicFrame frame)
    {
        frame.Kind = QuicFrameKind.Stream;
        frame.Fin = (type & 0x01) != 0;
        frame.HasLength = (type & 0x02) != 0;
        bool hasOffset = (type & 0x04) != 0;
        if (!Take(out frame.StreamId))
        {
            return false;
        }

        if (hasOffset && !Take(out frame.Offset))
        {
            return false;
        }

        if (!(frame.HasLength ? TakeBytes(out frame.Data) : TakeRest(out frame.Data)))
        {
            return false;
        }

        return EndFits(frame.Offset, frame.Data.Length);
    }

    private bool ReadNewConnectionId(ref QuicFrame frame)
    {
        frame.Kind = QuicFrameKind.NewConnectionId;
        if (!Take(out frame.Sequence) || !Take(out frame.RetirePriorTo) || _rest.IsEmpty)
        {
            return false;
        }

        int length = _rest[0];
        _rest = _rest[1..];
        if (length is < 1 or > QuicDatagramReader.MaxConnectionIdLength || frame.RetirePriorTo > frame.Sequence)
        {
            return Fail(FrameEncodingError);
        }

        return TakeFixed(length, out frame.Data) && TakeFixed(16, out frame.Token);
    }

    private bool ReadClose(bool application, ref QuicFrame frame)
    {
        frame.Kind = QuicFrameKind.ConnectionClose;
        frame.ApplicationClose = application;
        if (!Take(out frame.ErrorCode))
        {
            return false;
        }

        if (!application && !Take(out frame.TriggeringFrame))
        {
            return false;
        }

        return TakeBytes(out frame.Data);
    }

    private bool EndFits(ulong offset, int length)
    {
        if (offset > QuicVarInt.MaxValue - (ulong)length)
        {
            return Fail(FrameEncodingError);
        }

        return true;
    }

    private bool Take(out ulong value)
    {
        if (!QuicVarInt.TryDecode(_rest, out value, out int used))
        {
            value = 0;
            return Fail(FrameEncodingError);
        }

        _rest = _rest[used..];
        return true;
    }

    private bool TakeBytes(out ReadOnlySpan<byte> data)
    {
        data = default;
        if (!Take(out ulong length))
        {
            return false;
        }

        if (length > (ulong)_rest.Length)
        {
            return Fail(FrameEncodingError);
        }

        data = _rest[..(int)length];
        _rest = _rest[(int)length..];
        return true;
    }

    private bool TakeFixed(int length, out ReadOnlySpan<byte> data)
    {
        data = default;
        if (_rest.Length < length)
        {
            return Fail(FrameEncodingError);
        }

        data = _rest[..length];
        _rest = _rest[length..];
        return true;
    }

    private bool TakeRest(out ReadOnlySpan<byte> data)
    {
        data = _rest;
        _rest = default;
        return true;
    }

    private bool Fail(ulong error)
    {
        Error = error;
        _failed = true;
        return false;
    }

    private static int Minimal(ulong value)
    {
        if (value <= 63)
        {
            return 1;
        }

        if (value <= 16_383)
        {
            return 2;
        }

        if (value <= 1_073_741_823)
        {
            return 4;
        }

        return 8;
    }

    private static bool Allowed(in QuicFrame frame, QuicPacketKind packet)
    {
        bool initial = packet == QuicPacketKind.Initial;
        bool handshake = packet == QuicPacketKind.Handshake;
        bool zero = packet == QuicPacketKind.ZeroRtt;
        bool one = packet == QuicPacketKind.OneRtt;
        if (!initial && !handshake && !zero && !one)
        {
            return false;
        }

        bool early = zero || one;
        switch (frame.Kind)
        {
            case QuicFrameKind.Padding:
            case QuicFrameKind.Ping:
                return true;
            case QuicFrameKind.Ack:
            case QuicFrameKind.Crypto:
                return !zero;
            case QuicFrameKind.ConnectionClose:
                return frame.ApplicationClose ? early : true;
            case QuicFrameKind.NewToken:
            case QuicFrameKind.PathResponse:
            case QuicFrameKind.HandshakeDone:
            case QuicFrameKind.RetireConnectionId:
                return one;
            default:
                return early;
        }
    }
}

internal static class QuicFrameWriter
{
    internal static int Write(in QuicFrame frame, ReadOnlySpan<QuicAckRange> ranges, Span<byte> destination)
    {
        int at = frame.Kind switch
        {
            QuicFrameKind.Padding => Put(0x00, destination),
            QuicFrameKind.Ping => Put(0x01, destination),
            QuicFrameKind.Ack => WriteAck(frame, ranges, destination),
            QuicFrameKind.ResetStream => Put(0x04, frame.StreamId, frame.ErrorCode, frame.Limit, destination),
            QuicFrameKind.StopSending => Put(0x05, frame.StreamId, frame.ErrorCode, destination),
            QuicFrameKind.Crypto => WritePrefixed(0x06, frame.Offset, frame.Data, destination),
            QuicFrameKind.NewToken => WritePrefixed(0x07, frame.Data, destination),
            QuicFrameKind.Stream => WriteStream(frame, destination),
            QuicFrameKind.MaxData => Put(0x10, frame.Limit, destination),
            QuicFrameKind.MaxStreamData => Put(0x11, frame.StreamId, frame.Limit, destination),
            QuicFrameKind.MaxStreams => Put(frame.Unidirectional ? 0x13UL : 0x12, frame.Limit, destination),
            QuicFrameKind.DataBlocked => Put(0x14, frame.Limit, destination),
            QuicFrameKind.StreamDataBlocked => Put(0x15, frame.StreamId, frame.Limit, destination),
            QuicFrameKind.StreamsBlocked => Put(frame.Unidirectional ? 0x17UL : 0x16, frame.Limit, destination),
            QuicFrameKind.NewConnectionId => WriteNewConnectionId(frame, destination),
            QuicFrameKind.RetireConnectionId => Put(0x19, frame.Sequence, destination),
            QuicFrameKind.PathChallenge => WriteFixed(0x1a, frame.Data, 8, destination),
            QuicFrameKind.PathResponse => WriteFixed(0x1b, frame.Data, 8, destination),
            QuicFrameKind.ConnectionClose => WriteClose(frame, destination),
            QuicFrameKind.HandshakeDone => Put(0x1e, destination),
            QuicFrameKind.Datagram => WriteDatagram(frame, destination),
            _ => 0,
        };
        return at;
    }

    private static int WriteAck(in QuicFrame frame, ReadOnlySpan<QuicAckRange> ranges, Span<byte> destination)
    {
        if (frame.AckRangeCount < 1 || ranges.Length < frame.AckRangeCount)
        {
            return 0;
        }

        int at = Put(frame.Ecn ? 0x03UL : 0x02, frame.LargestAcknowledged, frame.AckDelay, destination);
        if (at == 0)
        {
            return 0;
        }

        int countAndFirst = Put((ulong)(frame.AckRangeCount - 1), ranges[0].Length, destination[at..]);
        if (countAndFirst == 0)
        {
            return 0;
        }

        at += countAndFirst;
        if (at == 0)
        {
            return 0;
        }

        for (int i = 1; i < frame.AckRangeCount; i++)
        {
            int used = Put(ranges[i].Gap, ranges[i].Length, destination[at..]);
            if (used == 0)
            {
                return 0;
            }

            at += used;
        }

        if (!frame.Ecn)
        {
            return at;
        }

        int ecn = Put(frame.Ect0, frame.Ect1, frame.Ce, destination[at..]);
        return ecn == 0 ? 0 : at + ecn;
    }

    private static int WriteStream(in QuicFrame frame, Span<byte> destination)
    {
        ulong type = 0x08;
        if (frame.Fin)
        {
            type |= 0x01;
        }

        if (frame.HasLength)
        {
            type |= 0x02;
        }

        if (frame.Offset != 0)
        {
            type |= 0x04;
        }

        int at = Put(type, frame.StreamId, destination);
        if (at == 0)
        {
            return 0;
        }

        if (frame.Offset != 0)
        {
            int used = Put(frame.Offset, destination[at..]);
            if (used == 0)
            {
                return 0;
            }

            at += used;
        }

        if (frame.HasLength)
        {
            int used = QuicVarInt.Encode((ulong)frame.Data.Length, destination[at..]);
            if (used == 0)
            {
                return 0;
            }

            at += used;
        }

        if (destination.Length < at + frame.Data.Length)
        {
            return 0;
        }

        frame.Data.CopyTo(destination[at..]);
        return at + frame.Data.Length;
    }

    private static int WriteDatagram(in QuicFrame frame, Span<byte> destination)
    {
        if (!frame.HasLength)
        {
            return WriteTail(0x30, frame.Data, destination);
        }

        int at = Put(0x31, (ulong)frame.Data.Length, destination);
        if (at == 0 || destination.Length < at + frame.Data.Length)
        {
            return 0;
        }

        frame.Data.CopyTo(destination[at..]);
        return at + frame.Data.Length;
    }

    private static int WriteNewConnectionId(in QuicFrame frame, Span<byte> destination)
    {
        if (frame.Data.Length is < 1 or > QuicDatagramReader.MaxConnectionIdLength || frame.Token.Length != 16)
        {
            return 0;
        }

        int at = Put(0x18, frame.Sequence, frame.RetirePriorTo, destination);
        if (at == 0 || destination.Length < at + 1 + frame.Data.Length + 16)
        {
            return 0;
        }

        destination[at++] = (byte)frame.Data.Length;
        frame.Data.CopyTo(destination[at..]);
        at += frame.Data.Length;
        frame.Token.CopyTo(destination[at..]);
        return at + 16;
    }

    private static int WriteClose(in QuicFrame frame, Span<byte> destination)
    {
        int at = frame.ApplicationClose
            ? Put(0x1d, frame.ErrorCode, destination)
            : Put(0x1c, frame.ErrorCode, frame.TriggeringFrame, destination);
        if (at == 0)
        {
            return 0;
        }

        int used = QuicVarInt.Encode((ulong)frame.Data.Length, destination[at..]);
        if (used == 0 || destination.Length < at + used + frame.Data.Length)
        {
            return 0;
        }

        at += used;
        frame.Data.CopyTo(destination[at..]);
        return at + frame.Data.Length;
    }

    private static int WritePrefixed(ulong type, ulong offset, ReadOnlySpan<byte> data, Span<byte> destination)
    {
        int at = Put(type, offset, (ulong)data.Length, destination);
        if (at == 0 || destination.Length < at + data.Length)
        {
            return 0;
        }

        data.CopyTo(destination[at..]);
        return at + data.Length;
    }

    private static int WritePrefixed(ulong type, ReadOnlySpan<byte> data, Span<byte> destination)
    {
        int at = Put(type, (ulong)data.Length, destination);
        if (at == 0 || destination.Length < at + data.Length)
        {
            return 0;
        }

        data.CopyTo(destination[at..]);
        return at + data.Length;
    }

    private static int WriteFixed(ulong type, ReadOnlySpan<byte> data, int length, Span<byte> destination)
    {
        if (data.Length != length)
        {
            return 0;
        }

        return WriteTail(type, data, destination);
    }

    private static int WriteTail(ulong type, ReadOnlySpan<byte> data, Span<byte> destination)
    {
        int at = Put(type, destination);
        if (at == 0 || destination.Length < at + data.Length)
        {
            return 0;
        }

        data.CopyTo(destination[at..]);
        return at + data.Length;
    }

    private static int Put(ulong value, Span<byte> destination)
    {
        return QuicVarInt.Encode(value, destination);
    }

    private static int Put(ulong first, ulong second, Span<byte> destination)
    {
        int at = Put(first, destination);
        if (at == 0)
        {
            return 0;
        }

        int used = Put(second, destination[at..]);
        return used == 0 ? 0 : at + used;
    }

    private static int Put(ulong first, ulong second, ulong third, Span<byte> destination)
    {
        int at = Put(first, second, destination);
        if (at == 0)
        {
            return 0;
        }

        int used = Put(third, destination[at..]);
        return used == 0 ? 0 : at + used;
    }

    private static int Put(ulong first, ulong second, ulong third, ulong fourth, Span<byte> destination)
    {
        int at = Put(first, second, third, destination);
        if (at == 0)
        {
            return 0;
        }

        int used = Put(fourth, destination[at..]);
        return used == 0 ? 0 : at + used;
    }
}
