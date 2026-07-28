#if NET6_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Text;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.Http3.Qpack;

/// <summary>
///     Static-only QPACK decoder (RFC 9204). No dynamic table is maintained; the Required Insert Count
///     in every HEADERS prefix is expected to be 0. Any block referencing the dynamic table will be
///     decoded correctly for name-only references, but value lookups are limited to the static table.
/// </summary>
internal static class QpackDecoder
{
    /// <summary>
    ///     Decodes a QPACK-encoded header block (the payload of an HTTP/3 HEADERS frame) into a list of
    ///     (name, value) pairs.
    /// </summary>
    /// <param name="data">The raw QPACK header block bytes.</param>
    /// <returns>Decoded header fields in wire order.</returns>
    /// <exception cref="Http3ConnectionException">On QPACK decompression failure.</exception>
    public static List<(string Name, string Value)> Decode(ReadOnlySpan<byte> data)
    {
        // Required Insert Count prefix (2 integers: Required Insert Count + S+Delta Base).
        // For static-only encoding, both are 0, encoded as a single 0x00 0x00 header prefix.
        if (data.Length < 2)
            throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed,
                "QPACK header block too short (missing Required Insert Count prefix).");

        // Parse Required Insert Count (encoded as a prefixed integer with 8-bit prefix)
        if (!TryReadPrefixedInt(data, 8, out var requiredInsertCount, out var consumed))
            throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Invalid Required Insert Count.");
        data = data[consumed..];

        // Parse S bit and Delta Base (S=0, Delta Base with 7-bit prefix).
        if (data.IsEmpty)
            throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Missing Base field.");
        var sAndBase = data[0];
        var sBit = (sAndBase & 0x80) != 0;
        if (!TryReadPrefixedInt(data, 7, out var deltaBase, out consumed))
            throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Invalid Delta Base.");
        data = data[consumed..];

        // We only support Required Insert Count == 0 (static-only). Non-zero means dynamic table entries
        // were inserted and should be referenced — we cannot decompress those without a dynamic table.
        // In practice, well-behaved static-only encoders (including ourselves) always send 0.
        if (requiredInsertCount != 0)
            throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed,
                $"Dynamic QPACK table not supported: Required Insert Count = {requiredInsertCount}.");

        var headers = new List<(string, string)>();

        while (!data.IsEmpty)
        {
            var b = data[0];

            if ((b & 0x80) != 0)
            {
                // Indexed Header Field — S=1 means static table, S=0 means dynamic table
                var isStatic = (b & 0x40) != 0;
                if (!TryReadPrefixedInt(data, 6, out var index, out consumed))
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Invalid indexed field index.");
                data = data[consumed..];

                if (!isStatic)
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed,
                        "Dynamic table indexed field not supported.");

                if (index >= (ulong)QpackStaticTable.Entries.Length)
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed,
                        $"Static table index {index} out of range.");

                var entry = QpackStaticTable.Entries[index];
                headers.Add((entry.Name, entry.Value));
            }
            else if ((b & 0x40) != 0)
            {
                // Literal Header Field With Name Reference
                var isStatic = (b & 0x10) != 0;
                var neverIndex = (b & 0x20) != 0;
                if (!TryReadPrefixedInt(data, 4, out var nameIndex, out consumed))
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Invalid name ref index.");
                data = data[consumed..];

                string name;
                if (isStatic)
                {
                    if (nameIndex >= (ulong)QpackStaticTable.Entries.Length)
                        throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed,
                            $"Static table name index {nameIndex} out of range.");
                    name = QpackStaticTable.Entries[nameIndex].Name;
                }
                else
                {
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed,
                        "Dynamic table name reference not supported.");
                }

                if (!TryReadStringLiteral(data, out var value, out consumed))
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Invalid literal value.");
                data = data[consumed..];

                headers.Add((name, value));
            }
            else if ((b & 0x20) != 0)
            {
                // Literal Header Field Without Name Reference (0b001xxxxx)
                var neverIndex = (b & 0x10) != 0;
                data = data[1..]; // skip pattern byte
                // Name literal
                if (!TryReadStringLiteral(data, out var name, out consumed))
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Invalid literal name.");
                data = data[consumed..];
                // Value literal
                if (!TryReadStringLiteral(data, out var value, out consumed))
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Invalid literal value.");
                data = data[consumed..];
                headers.Add((name, value));
            }
            else
            {
                throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed,
                    $"Unknown QPACK representation byte 0x{b:X2}.");
            }
        }

        return headers;
    }

    private static bool TryReadPrefixedInt(ReadOnlySpan<byte> data, int prefixBits, out ulong value, out int consumed)
    {
        if (data.IsEmpty)
        {
            value = 0;
            consumed = 0;
            return false;
        }

        var mask = (byte)((1 << prefixBits) - 1);
        var initial = (ulong)(data[0] & mask);
        consumed = 1;
        if (initial < (ulong)mask)
        {
            value = initial;
            return true;
        }

        // Multi-byte integer continuation (RFC 7541 §5.1 integer encoding)
        ulong m = 0;
        ulong result = (ulong)mask;
        while (consumed < data.Length)
        {
            var next = data[consumed++];
            result += ((ulong)(next & 0x7F)) << (int)m;
            m += 7;
            if ((next & 0x80) == 0)
            {
                value = result;
                return true;
            }
            if (m >= 63)
            {
                value = 0;
                return false;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryReadStringLiteral(ReadOnlySpan<byte> data, out string result, out int consumed)
    {
        result = string.Empty;
        consumed = 0;
        if (data.IsEmpty) return false;

        var huffman = (data[0] & 0x80) != 0;
        if (!TryReadPrefixedInt(data, 7, out var length, out var headerLen))
            return false;

        consumed = headerLen + (int)length;
        if (consumed > data.Length) return false;

        var strData = data.Slice(headerLen, (int)length);
        result = huffman
            ? HuffmanDecode(strData)
            : Encoding.Latin1.GetString(strData);
        return true;
    }

    private static string HuffmanDecode(ReadOnlySpan<byte> data)
    {
        // QPACK uses the same Huffman table as HPACK (RFC 7541 Appendix B, referenced by RFC 9204).
        var decoded = Titanium.Web.Proxy.Http2.Hpack.HuffmanDecoder.Instance.Decode(data.ToArray());
        return Encoding.Latin1.GetString(decoded.Span);
    }
}
#endif
