using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http3.Qpack;

/// <summary>
///     QPACK encoder (RFC 9204). Supports both static-table-only mode and dynamic-table mode.
///     When <see cref="QpackContext" /> is provided and not disabled, the encoder will attempt to
///     reference dynamic-table entries for repeated headers; otherwise all blocks use static-table
///     references or literals. The 2-byte prefix (Required Insert Count + Base) is computed per
///     RFC 9204 §4.5.1 and emitted at the front of every HEADERS block.
/// </summary>
internal static class QpackEncoder
{
    /// <summary>
    ///     Encodes a list of header fields into a QPACK header block using static-table-only mode.
    ///     Equivalent to calling <see cref="Encode(IEnumerable{ValueTuple{string,string}},QpackContext?)" />
    ///     with a null context.
    /// </summary>
    public static byte[] Encode(IEnumerable<(string Name, string Value)> headers) =>
        Encode(headers, context: null);

    /// <summary>
    ///     Encodes a list of header fields into a QPACK header block.
    ///     When <paramref name="context" /> is non-null and the outbound table is not disabled, the
    ///     encoder will reference dynamic-table entries. The Required Insert Count prefix is encoded per
    ///     RFC 9204 §4.5.1.1.
    /// </summary>
    public static byte[] Encode(IEnumerable<(string Name, string Value)> headers, QpackContext? context) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        var body = new MemoryStream();
        var outboundTable = context != null && !context.OutboundTableDisabled && context.MaxTableCapacityFromPeer > 0
            ? context.OutboundEncoderTable
            : null;

        ulong maxRequiredInsertCount = 0;

        foreach (var (name, value) in headers)
        {
            var lowerName = name; // names from the proxy are already lowercase

            // 1. Try exact match in the static table.
            int nameOnlyStaticIndex = -1;
            bool foundExact = false;
            for (var i = 0; i < QpackStaticTable.Entries.Length; i++)
            {
                var entry = QpackStaticTable.Entries[i];
                if (!string.Equals(entry.Name, lowerName, StringComparison.Ordinal)) continue;
                if (nameOnlyStaticIndex < 0) nameOnlyStaticIndex = i;
                if (string.Equals(entry.Value, value, StringComparison.Ordinal))
                {
                    // Indexed Header Field (static): 1 1 S=1 Index(6)
                    WriteIndexed(body, (ulong)i);
                    foundExact = true;
                    break;
                }
            }
            if (foundExact) continue;

            // 2. Try dynamic table (when enabled).
            if (outboundTable != null && outboundTable.TryFind(lowerName, value, out ulong dynAbsIdx, out bool dynExact))
            {
                if (dynExact)
                {
                    WriteDynamicIndexed(body, dynAbsIdx);
                    maxRequiredInsertCount = Math.Max(maxRequiredInsertCount, dynAbsIdx + 1);
                }
                else
                {
                    WriteLiteralWithDynamicNameRef(body, dynAbsIdx, value);
                    maxRequiredInsertCount = Math.Max(maxRequiredInsertCount, dynAbsIdx + 1);
                }
                continue;
            }

            // 3. Literal with static name reference.
            if (nameOnlyStaticIndex >= 0)
            {
                WriteLiteralWithStaticNameRef(body, (ulong)nameOnlyStaticIndex, value);
                continue;
            }

            // 4. Literal without name reference.
            WriteLiteralNewName(body, lowerName, value);
        }

        // Encode the 2-byte QPACK prefix per RFC 9204 §4.5.1.
        byte ricByte, sByte;
        if (maxRequiredInsertCount == 0 || outboundTable == null)
        {
            ricByte = 0x00; // Required Insert Count = 0
            sByte = 0x00;   // S=0, Delta Base = 0
        }
        else
        {
            var encodedRic = EncodeRequiredInsertCount(maxRequiredInsertCount, context!.MaxTableCapacityFromPeer);
            ricByte = (byte)(encodedRic & 0xFF);
            sByte = 0x00; // S=0, Delta Base = 0 (post-base indexing not used)
        }

        var result = new byte[2 + body.Length];
        result[0] = ricByte;
        result[1] = sByte;
        body.GetBuffer().AsSpan(0, (int)body.Length).CopyTo(result.AsSpan(2));
        return result;
    }

    /// <summary>
    ///     Encodes the Required Insert Count per RFC 9204 §4.5.1.1.
    ///     Encoded = (count % (2 * MaxEntries)) + 1, where MaxEntries = floor(MaxTableCapacity / 32).
    /// </summary>
    internal static ulong EncodeRequiredInsertCount(ulong requiredInsertCount, uint maxTableCapacity)
    {
        var maxEntries = (ulong)(maxTableCapacity / 32);
        if (maxEntries == 0) maxEntries = 1;
        return (requiredInsertCount % (2 * maxEntries)) + 1;
    }

    // Indexed Header Field (static): 1 1 S=1 Index(6) — pattern 0xC0
    private static void WriteIndexed(MemoryStream buf, ulong index)
    {
        WritePrefixedInt(buf, 0xC0, 6, index);
    }

    // Indexed Header Field (dynamic, post-base): 0 0 0 1 Index(4) — pattern 0x10
    private static void WriteDynamicIndexed(MemoryStream buf, ulong absoluteIndex)
    {
        WritePrefixedInt(buf, 0x10, 4, absoluteIndex);
    }

    // Literal Header Field With Name Reference (static): 0 1 N=0 S=1 Index(4) — pattern 0x50
    private static void WriteLiteralWithStaticNameRef(MemoryStream buf, ulong nameIndex, string value)
    {
        WritePrefixedInt(buf, 0x50, 4, nameIndex);
        WriteStringLiteral(buf, value);
    }

    // Literal Header Field With Name Reference (dynamic, post-base): 0 0 0 0 N=0 Index(3) — pattern 0x00
    private static void WriteLiteralWithDynamicNameRef(MemoryStream buf, ulong absoluteIndex, string value)
    {
        WritePrefixedInt(buf, 0x00, 3, absoluteIndex);
        WriteStringLiteral(buf, value);
    }

    // Literal Field Line With Literal Name (RFC 9204 §4.5.6):
    //   0 0 1 N H NameLen(3+) | Name | H ValueLen(7+) | Value
    // Name length occupies the 3-bit prefix of the first byte — it is NOT a separate string literal.
    private static void WriteLiteralNewName(MemoryStream buf, string name, string value)
    {
        var nameBytes = Encoding.Latin1.GetBytes(name);
        // pattern 0x20 => 001 N=0 H=0, then 3-bit-prefixed name length
        WritePrefixedInt(buf, 0x20, 3, (ulong)nameBytes.Length);
        buf.Write(nameBytes, 0, nameBytes.Length);
        WriteStringLiteral(buf, value);
    }

    /// <summary>Writes an RFC 7541 §5.1 prefixed integer into the stream.</summary>
    private static void WritePrefixedInt(MemoryStream buf, byte patternByte, int prefixBits, ulong value)
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
    private static void WriteStringLiteral(MemoryStream buf, string s)
    {
        var bytes = Encoding.Latin1.GetBytes(s);
        WritePrefixedInt(buf, 0x00, 7, (ulong)bytes.Length);
        buf.Write(bytes, 0, bytes.Length);
    }
}
