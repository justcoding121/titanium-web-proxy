using System;

namespace Titanium.Quic;

internal enum QuicPacketKind : byte
{
    Initial = 0,
    ZeroRtt = 1,
    Handshake = 2,
    Retry = 3,
    VersionNegotiation = 4,
    OneRtt = 5,
}

/// <summary>One QUIC packet sliced out of a datagram. Spans point at the datagram.</summary>
internal ref struct QuicPacket
{
    internal QuicPacketKind Kind;

    internal byte FirstByte;

    internal uint Version;

    internal ReadOnlySpan<byte> DestinationConnectionId;

    internal ReadOnlySpan<byte> SourceConnectionId;

    internal ReadOnlySpan<byte> Token;

    internal ReadOnlySpan<byte> PacketNumber;

    internal ReadOnlySpan<byte> Payload;

    internal ReadOnlySpan<byte> IntegrityTag;

    internal ReadOnlySpan<byte> SupportedVersions;

    internal int TotalLength;

    internal bool Spin;

    internal bool KeyPhase;

    internal bool FixedBit => (FirstByte & 0x40) != 0;

    internal static bool ReservedBitsSet(byte first) => (first & 0x80) != 0 ? (first & 0x0c) != 0 : (first & 0x18) != 0;

    internal int PacketNumberLength => PacketNumber.Length;
}

/// <summary>Reads coalesced packets. A short header, a Retry, or a Version Negotiation packet ends the datagram.</summary>
internal ref struct QuicDatagramReader
{
    internal const int MaxConnectionIdLength = 20;

    internal const uint Version1 = 0x0000_0001;

    private ReadOnlySpan<byte> _remaining;

    private readonly int _shortDestinationIdLength;

    private bool _finished;

    internal QuicDatagramReader(ReadOnlySpan<byte> datagram, int shortDestinationIdLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(shortDestinationIdLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(shortDestinationIdLength, MaxConnectionIdLength);
        _remaining = datagram;
        _shortDestinationIdLength = shortDestinationIdLength;
    }

    internal bool TryRead(out QuicPacket packet)
    {
        packet = default;
        if (_finished || _remaining.IsEmpty)
        {
            return false;
        }

        if (!TryParse(_remaining, _shortDestinationIdLength, out packet) || packet.TotalLength <= 0 || packet.TotalLength > _remaining.Length)
        {
            return false;
        }

        _remaining = _remaining[packet.TotalLength..];
        if (packet.Kind is QuicPacketKind.OneRtt or QuicPacketKind.Retry or QuicPacketKind.VersionNegotiation)
        {
            _finished = true;
        }

        return true;
    }

    internal static bool TryParse(ReadOnlySpan<byte> source, int shortDestinationIdLength, out QuicPacket packet)
    {
        packet = default;
        if (source.IsEmpty)
        {
            return false;
        }

        if ((source[0] & 0x80) == 0)
        {
            return TryParseShort(source, shortDestinationIdLength, out packet);
        }

        return TryParseLong(source, out packet);
    }

    private static bool TryParseShort(ReadOnlySpan<byte> source, int shortDestinationIdLength, out QuicPacket packet)
    {
        packet = default;
        if (shortDestinationIdLength is < 0 or > MaxConnectionIdLength)
        {
            return false;
        }

        int packetNumberLength = (source[0] & 0x03) + 1;
        int header = 1 + shortDestinationIdLength + packetNumberLength;
        if (source.Length < header)
        {
            return false;
        }

        packet.Kind = QuicPacketKind.OneRtt;
        packet.FirstByte = source[0];
        packet.DestinationConnectionId = source.Slice(1, shortDestinationIdLength);
        packet.PacketNumber = source.Slice(1 + shortDestinationIdLength, packetNumberLength);
        packet.Payload = source[header..];
        packet.Spin = (source[0] & 0x20) != 0;
        packet.KeyPhase = (source[0] & 0x04) != 0;
        packet.TotalLength = source.Length;
        return true;
    }

    private static bool TryParseLong(ReadOnlySpan<byte> source, out QuicPacket packet)
    {
        packet = default;
        if (source.Length < 7)
        {
            return false;
        }

        uint version = ((uint)source[1] << 24) | ((uint)source[2] << 16) | ((uint)source[3] << 8) | source[4];
        int offset = 5;
        int destinationLength = source[offset++];
        if (version != 0 && destinationLength > MaxConnectionIdLength)
        {
            return false;
        }

        if (source.Length < offset + destinationLength + 1)
        {
            return false;
        }

        ReadOnlySpan<byte> destination = source.Slice(offset, destinationLength);
        offset += destinationLength;
        int sourceLength = source[offset++];
        if (version != 0 && sourceLength > MaxConnectionIdLength)
        {
            return false;
        }

        if (source.Length < offset + sourceLength)
        {
            return false;
        }

        ReadOnlySpan<byte> sourceId = source.Slice(offset, sourceLength);
        offset += sourceLength;
        packet.FirstByte = source[0];
        packet.Version = version;
        packet.DestinationConnectionId = destination;
        packet.SourceConnectionId = sourceId;
        if (version == 0)
        {
            int versionsBytes = source.Length - offset;
            if (versionsBytes < 4 || (versionsBytes & 3) != 0)
            {
                return false;
            }

            packet.Kind = QuicPacketKind.VersionNegotiation;
            packet.SupportedVersions = source[offset..];
            packet.TotalLength = source.Length;
            return true;
        }

        if (version != Version1)
        {
            return false;
        }

        int type = (source[0] >> 4) & 0x03;
        if (type == 3)
        {
            if (source.Length - offset < 16)
            {
                return false;
            }

            packet.Kind = QuicPacketKind.Retry;
            packet.Token = source.Slice(offset, source.Length - offset - 16);
            packet.IntegrityTag = source[^16..];
            packet.TotalLength = source.Length;
            return true;
        }

        if (type == 0)
        {
            if (!QuicVarInt.TryDecode(source[offset..], out ulong tokenLength, out int tokenLengthBytes) || tokenLength > int.MaxValue)
            {
                return false;
            }

            offset += tokenLengthBytes;
            if (source.Length < offset + (int)tokenLength)
            {
                return false;
            }

            packet.Token = source.Slice(offset, (int)tokenLength);
            offset += (int)tokenLength;
        }

        if (!QuicVarInt.TryDecode(source[offset..], out ulong length, out int lengthBytes) || length > int.MaxValue)
        {
            return false;
        }

        offset += lengthBytes;
        int packetNumberLength = (source[0] & 0x03) + 1;
        if (length < (ulong)packetNumberLength || source.Length < offset + (int)length)
        {
            return false;
        }

        packet.Kind = (QuicPacketKind)type;
        packet.PacketNumber = source.Slice(offset, packetNumberLength);
        packet.Payload = source.Slice(offset + packetNumberLength, (int)length - packetNumberLength);
        packet.TotalLength = offset + (int)length;
        return true;
    }
}
