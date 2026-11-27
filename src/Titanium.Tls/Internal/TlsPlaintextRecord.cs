using System;

namespace Titanium.Tls;

/// <summary>Unprotected TLS records used before the handshake keys exist.</summary>
internal static class TlsPlaintextRecord
{
    internal static bool TryReadHeader(ReadOnlySpan<byte> input, out byte contentType, out int version, out int length, out TlsRecordAlert alert)
    {
        contentType = 0;
        version = 0;
        length = 0;
        alert = TlsRecordAlert.UnexpectedMessage;
        if (input.Length < TlsRecordProtector.HeaderLength)
        {
            return false;
        }

        contentType = input[0];
        version = (input[1] << 8) | input[2];
        length = (input[3] << 8) | input[4];
        if (version != 0x0301 && version != 0x0303)
        {
            return false;
        }

        if (length > TlsRecordProtector.MaxCiphertext)
        {
            alert = TlsRecordAlert.RecordOverflow;
            return false;
        }

        if (contentType != 20 && contentType != 21 && contentType != 22 && contentType != 23)
        {
            return false;
        }

        alert = default;
        return true;
    }

    internal static bool TryAcceptChangeCipherSpec(ReadOnlySpan<byte> record, ref int accepted)
    {
        if (!TryReadHeader(record, out byte contentType, out _, out int length, out _))
        {
            return false;
        }

        if (contentType != 20 || length != 1 || record.Length < TlsRecordProtector.HeaderLength + 1 || record[5] != 1)
        {
            return false;
        }

        if (accepted != 0)
        {
            return false;
        }

        accepted = 1;
        return true;
    }
}
