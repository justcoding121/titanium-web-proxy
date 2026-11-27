using System;

namespace Titanium.Tls;

/// <summary>
/// Handshake header (type + 24-bit length) and a reassembler for messages split across records
/// or packed into one record. The default cap is 128 KiB. Nothing larger than 1 MiB is accepted.
/// </summary>
internal static class HandshakeFraming
{
    internal const int DefaultMaxMessageBytes = 131072;
    internal const int HardMaxMessageBytes = 1 << 20;
    internal const int HeaderLength = 4;

    internal static bool TryReadHeader(ReadOnlySpan<byte> message, out HandshakeType type, out int bodyLength)
    {
        type = default;
        bodyLength = 0;
        if (message.Length < HeaderLength)
        {
            return false;
        }

        type = (HandshakeType)message[0];
        bodyLength = (message[1] << 16) | (message[2] << 8) | message[3];
        return true;
    }

    internal static bool TrySplitMessage(ReadOnlySpan<byte> message, out HandshakeType type, out ReadOnlySpan<byte> body, out HandshakeAlert alert)
    {
        type = default;
        body = default;
        alert = default;
        if (!TryReadHeader(message, out type, out int bodyLength))
        {
            alert = HandshakeAlert.DecodeError;
            return false;
        }

        if (bodyLength > HardMaxMessageBytes || message.Length != HeaderLength + bodyLength)
        {
            alert = HandshakeAlert.DecodeError;
            return false;
        }

        body = message.Slice(HeaderLength, bodyLength);
        return true;
    }

    internal static bool TryUnwrapHandshakeRecord(ReadOnlySpan<byte> record, out ReadOnlySpan<byte> payload, out bool incomplete, out HandshakeAlert alert)
    {
        payload = default;
        incomplete = false;
        alert = default;
        if (record.Length < TlsRecordProtector.HeaderLength)
        {
            incomplete = record.Length > 0 && record[0] == 22;
            alert = incomplete ? default : HandshakeAlert.DecodeError;
            return false;
        }

        if (record[0] != 22 || record[1] != 3 || (record[2] != 1 && record[2] != 3))
        {
            alert = HandshakeAlert.DecodeError;
            return false;
        }

        int length = (record[3] << 8) | record[4];
        if (length == 0 || length > TlsRecordProtector.MaxCiphertext)
        {
            alert = HandshakeAlert.DecodeError;
            return false;
        }

        if (record.Length < TlsRecordProtector.HeaderLength + length)
        {
            incomplete = true;
            return false;
        }

        payload = record.Slice(TlsRecordProtector.HeaderLength, length);
        return true;
    }
}

internal sealed class HandshakeReassembler
{
    private readonly byte[] _buffer;
    private readonly int _maxMessage;
    private int _length;

    internal HandshakeReassembler(int maxMessageBytes = HandshakeFraming.DefaultMaxMessageBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessageBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxMessageBytes, HandshakeFraming.HardMaxMessageBytes);
        _maxMessage = maxMessageBytes;
        _buffer = new byte[maxMessageBytes + HandshakeFraming.HeaderLength];
    }

    internal bool TryAppend(ReadOnlySpan<byte> fragment, out HandshakeAlert alert)
    {
        alert = default;
        if (fragment.IsEmpty)
        {
            return true;
        }

        if ((long)_length + fragment.Length > _buffer.Length)
        {
            alert = HandshakeAlert.DecodeError;
            return false;
        }

        fragment.CopyTo(_buffer.AsSpan(_length));
        _length += fragment.Length;
        if (_length >= HandshakeFraming.HeaderLength)
        {
            int bodyLength = (_buffer[1] << 16) | (_buffer[2] << 8) | _buffer[3];
            if (bodyLength > _maxMessage)
            {
                alert = HandshakeAlert.DecodeError;
                return false;
            }
        }

        return true;
    }

    internal bool TryTake(Span<byte> bodyDestination, out HandshakeType type, out int bodyLength, out bool needMore, out HandshakeAlert alert)
    {
        type = default;
        bodyLength = 0;
        needMore = false;
        alert = default;
        if (_length < HandshakeFraming.HeaderLength)
        {
            needMore = true;
            return true;
        }

        int declared = (_buffer[1] << 16) | (_buffer[2] << 8) | _buffer[3];
        if (declared > _maxMessage)
        {
            alert = HandshakeAlert.DecodeError;
            return false;
        }

        if (_length < HandshakeFraming.HeaderLength + declared)
        {
            needMore = true;
            return true;
        }

        if (bodyDestination.Length < declared)
        {
            throw new TlsProtocolViolation("The destination is shorter than the handshake body.");
        }

        type = (HandshakeType)_buffer[0];
        _buffer.AsSpan(HandshakeFraming.HeaderLength, declared).CopyTo(bodyDestination);
        int total = HandshakeFraming.HeaderLength + declared;
        int tail = _length - total;
        if (tail > 0)
        {
            Buffer.BlockCopy(_buffer, total, _buffer, 0, tail);
        }

        _length = tail;
        bodyLength = declared;
        return true;
    }
}
