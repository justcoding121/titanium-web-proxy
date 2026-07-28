using System;
using System.Collections.Generic;
using System.Text;

namespace Titanium.Web.Proxy.Http3.Qpack;

/// <summary>
///     Static-only QPACK encoder (RFC 9204). The encoder never populates the dynamic table; all
///     representations use either static-table indexed references or literal-without-indexing.
///     This means every HEADERS block has a zero Required Insert Count, which allows all QPACK decoder
///     streams to remain empty and QPACK_BLOCKED_STREAMS to be 0.
/// </summary>
internal static class QpackEncoder
{
    /// <summary>
    ///     Encodes a list of header fields into a QPACK header block.
    ///     The output is suitable as the payload of an HTTP/3 HEADERS frame.
    /// </summary>
    public static byte[] Encode(IEnumerable<(string Name, string Value)> headers)
    {
        var body = new System.IO.MemoryStream();

        foreach (var (name, value) in headers)
        {
            var lowerName = name; // names from the proxy are already lowercase

            // Try exact match (name + value) in the static table first.
            int nameOnlyIndex = -1;
            bool foundExact = false;
            for (var i = 0; i < QpackStaticTable.Entries.Length; i++)
            {
                var entry = QpackStaticTable.Entries[i];
                if (!string.Equals(entry.Name, lowerName, StringComparison.Ordinal)) continue;
                if (nameOnlyIndex < 0) nameOnlyIndex = i;
                if (string.Equals(entry.Value, value, StringComparison.Ordinal))
                {
                    // Indexed Header Field representation (S=1, 6-bit prefix)
                    // Pattern: 1 1 S T Index
                    //          1 1 1 (static) Index(6)
                    WriteIndexed(body, (ulong)i);
                    foundExact = true;
                    break;
                }
            }
            if (foundExact) continue;

            if (nameOnlyIndex >= 0)
            {
                // Literal Header Field With Name Reference (static table name, literal value)
                // Pattern: 0 1 N S T Index(4) | value literal
                WriteLiteralWithStaticNameRef(body, (ulong)nameOnlyIndex, value);
            }
            else
            {
                // Literal Header Field Without Name Reference
                // Pattern: 0 0 1 N (Name + Value literals)
                WriteLiteralNewName(body, lowerName, value);
            }
        }

        // Prepend 2-byte QPACK prefix: Required Insert Count = 0, S=0, Delta Base = 0.
        var result = new byte[2 + body.Length];
        result[0] = 0x00; // Required Insert Count = 0
        result[1] = 0x00; // S=0, Delta Base = 0
        body.GetBuffer().AsSpan(0, (int)body.Length).CopyTo(result.AsSpan(2));
        return result;
    }

    // Indexed Header Field: 1 1 S=1(static) Index(6)
    // Byte 0: 1 1 1 xxxxxx where xxxxxx = index (if <=63) or first 6 bits + continuation
    private static void WriteIndexed(System.IO.MemoryStream buf, ulong index)
    {
        // Pattern byte: 0b11 (high 2 bits) + S=1 (bit 6) → 0b1110_0000 = 0xC0 + 6-bit prefix int
        WritePrefixedInt(buf, 0xC0, 6, index);
    }

    // Literal With Name Reference: 0 1 N=0 S=1(static) T=0 Index(4)
    // Byte 0: 0b0101_xxxx where xxxx = first 4 bits of static index
    private static void WriteLiteralWithStaticNameRef(System.IO.MemoryStream buf, ulong nameIndex, string value)
    {
        // Pattern: 0b0101_0000 = 0x50 (N=0, S=1) with 4-bit prefix
        WritePrefixedInt(buf, 0x50, 4, nameIndex);
        WriteStringLiteral(buf, value);
    }

    // Literal Without Name Reference: 0 0 1 N=0 Name-length Name Value-length Value
    // Byte 0: 0b0010_0000 = 0x20 (N=0)
    private static void WriteLiteralNewName(System.IO.MemoryStream buf, string name, string value)
    {
        buf.WriteByte(0x20); // 0b0010_0000
        WriteStringLiteral(buf, name);
        WriteStringLiteral(buf, value);
    }

    /// <summary>Writes an RFC 7541 §5.1 prefixed integer into the stream.</summary>
    private static void WritePrefixedInt(System.IO.MemoryStream buf, byte patternByte, int prefixBits, ulong value)
    {
        var mask = (uint)((1 << prefixBits) - 1);
        if (value < mask)
        {
            buf.WriteByte((byte)(patternByte | (byte)value));
            return;
        }

        buf.WriteByte((byte)(patternByte | (byte)mask));
        value -= mask;
        while (value >= 0x80)
        {
            buf.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        buf.WriteByte((byte)value);
    }

    /// <summary>
    ///     Writes an RFC 7541 §5.2 string literal (length-prefixed, no Huffman encoding).
    ///     We always emit raw bytes (H=0) to keep the implementation simple.
    /// </summary>
    private static void WriteStringLiteral(System.IO.MemoryStream buf, string s)
    {
        var bytes = Encoding.Latin1.GetBytes(s);
        // H=0 flag + 7-bit length
        WritePrefixedInt(buf, 0x00, 7, (ulong)bytes.Length);
        buf.Write(bytes, 0, bytes.Length);
    }
}
