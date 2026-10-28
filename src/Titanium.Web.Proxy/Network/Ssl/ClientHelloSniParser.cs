using System;
using System.Text;

namespace Titanium.Web.Proxy.Network.Ssl;

/// <summary>
///     Reads the SNI host name out of the first TLS record of a connection without decrypting anything.
///     Every read is bounds checked against the bytes supplied, and only the returned name is allocated.
/// </summary>
internal static class ClientHelloSniParser
{
    /// <summary>Outcome of <see cref="TryGetServerName" />.</summary>
    internal enum ParseResult
    {
        /// <summary>The ClientHello carries a host_name server_name extension.</summary>
        Found,

        /// <summary>A complete ClientHello arrived and names no host (no extension, or one the parser cannot use).</summary>
        NoSni,

        /// <summary>The first bytes are not a TLS handshake record that starts a ClientHello.</summary>
        NotTls,

        /// <summary>The record is well formed so far, but more bytes are needed to finish it.</summary>
        NeedMoreData,
    }

    /// <summary>TLS record header: type, version (2), length (2).</summary>
    internal const int RecordHeaderLength = 5;

    /// <summary>Largest TLS plaintext record body (2^14).</summary>
    internal const int MaxRecordLength = 16 * 1024;

    /// <summary>Bytes needed to hold the largest first record, header included.</summary>
    internal const int MaxRecordWithHeader = MaxRecordLength + RecordHeaderLength;

    private const byte HandshakeRecord = 0x16;
    private const byte ClientHelloType = 0x01;
    private const int ServerNameExtension = 0x0000;
    private const byte HostNameType = 0x00;
    private const int MaxHostNameLength = 255;

    /// <summary>
    ///     Parses the first TLS record. <paramref name="serverName" /> is set only for <see cref="ParseResult.Found" />.
    ///     A ClientHello split across several records is reported as <see cref="ParseResult.NoSni" />.
    /// </summary>
    public static bool TryGetServerName(ReadOnlySpan<byte> record, out string? serverName, out ParseResult result)
    {
        serverName = null;
        result = Parse(record, out serverName);
        return result == ParseResult.Found;
    }

    private static ParseResult Parse(ReadOnlySpan<byte> record, out string? serverName)
    {
        serverName = null;
        if (record.Length == 0)
        {
            return ParseResult.NeedMoreData;
        }

        if (record[0] != HandshakeRecord)
        {
            return ParseResult.NotTls;
        }

        if (record.Length >= 2 && record[1] != 0x03)
        {
            return ParseResult.NotTls;
        }

        if (record.Length < RecordHeaderLength)
        {
            return ParseResult.NeedMoreData;
        }

        var recordLength = (record[3] << 8) | record[4];
        if (recordLength is 0 or > MaxRecordLength)
        {
            return ParseResult.NotTls;
        }

        // A handshake message starts with its type, so one byte settles the record kind early.
        if (record.Length > RecordHeaderLength && record[RecordHeaderLength] != ClientHelloType)
        {
            return ParseResult.NotTls;
        }

        if (record.Length < RecordHeaderLength + recordLength)
        {
            return ParseResult.NeedMoreData;
        }

        var body = record.Slice(RecordHeaderLength, recordLength);
        return ParseHandshake(body, out serverName);
    }

    private static ParseResult ParseHandshake(ReadOnlySpan<byte> body, out string? serverName)
    {
        serverName = null;
        if (body.Length < 4 || body[0] != ClientHelloType)
        {
            return ParseResult.NoSni;
        }

        var handshakeLength = (body[1] << 16) | (body[2] << 8) | body[3];
        if (handshakeLength > body.Length - 4)
        {
            // The message continues in a later record. Reassembling it is not worth the buffering here.
            return ParseResult.NoSni;
        }

        var hello = body.Slice(4, handshakeLength);

        // client_version (2) + random (32)
        var offset = 34;
        if (!TrySkipVector8(hello, ref offset) ||
            !TrySkipVector16(hello, ref offset) ||
            !TrySkipVector8(hello, ref offset))
        {
            return ParseResult.NoSni;
        }

        if (!TryReadUInt16(hello, ref offset, out var extensionsLength) || extensionsLength > hello.Length - offset)
        {
            return ParseResult.NoSni;
        }

        return ScanExtensions(hello.Slice(offset, extensionsLength), out serverName);
    }

    private static ParseResult ScanExtensions(ReadOnlySpan<byte> extensions, out string? serverName)
    {
        serverName = null;
        var offset = 0;
        while (extensions.Length - offset >= 4)
        {
            TryReadUInt16(extensions, ref offset, out var type);
            TryReadUInt16(extensions, ref offset, out var length);
            if (length > extensions.Length - offset)
            {
                return ParseResult.NoSni;
            }

            if (type == ServerNameExtension)
            {
                return ReadServerNameList(extensions.Slice(offset, length), out serverName);
            }

            offset += length;
        }

        return ParseResult.NoSni;
    }

    private static ParseResult ReadServerNameList(ReadOnlySpan<byte> data, out string? serverName)
    {
        serverName = null;
        var offset = 0;
        if (!TryReadUInt16(data, ref offset, out var listLength) || listLength > data.Length - offset)
        {
            return ParseResult.NoSni;
        }

        var list = data.Slice(offset, listLength);
        offset = 0;
        while (list.Length - offset >= 3)
        {
            var nameType = list[offset++];
            TryReadUInt16(list, ref offset, out var nameLength);
            if (nameLength > list.Length - offset)
            {
                return ParseResult.NoSni;
            }

            if (nameType == HostNameType)
            {
                return DecodeHostName(list.Slice(offset, nameLength), out serverName);
            }

            offset += nameLength;
        }

        return ParseResult.NoSni;
    }

    private static ParseResult DecodeHostName(ReadOnlySpan<byte> name, out string? serverName)
    {
        serverName = null;

        // One trailing dot names the same host. Dropping it keeps "a.example.com." on the "a.example.com" route.
        if (name.Length > 0 && name[^1] == (byte)'.')
        {
            name = name[..^1];
        }

        if (name.Length is 0 or > MaxHostNameLength)
        {
            return ParseResult.NoSni;
        }

        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] is <= 0x20 or >= 0x7F)
            {
                return ParseResult.NoSni;
            }
        }

        serverName = Encoding.ASCII.GetString(name);
        return ParseResult.Found;
    }

    private static bool TryReadUInt16(ReadOnlySpan<byte> data, ref int offset, out int value)
    {
        if (data.Length - offset < 2)
        {
            value = 0;
            return false;
        }

        value = (data[offset] << 8) | data[offset + 1];
        offset += 2;
        return true;
    }

    private static bool TrySkipVector8(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset >= data.Length)
        {
            return false;
        }

        var length = data[offset];
        if (length > data.Length - offset - 1)
        {
            return false;
        }

        offset += 1 + length;
        return true;
    }

    private static bool TrySkipVector16(ReadOnlySpan<byte> data, ref int offset)
    {
        if (!TryReadUInt16(data, ref offset, out var length) || length > data.Length - offset)
        {
            return false;
        }

        offset += length;
        return true;
    }
}
