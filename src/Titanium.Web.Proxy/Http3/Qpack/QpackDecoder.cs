using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.Http3.Qpack;

/// <summary>
///     QPACK decoder (RFC 9204). Supports both static-table-only mode and dynamic-table mode.
///     When <see cref="QpackContext" /> is provided, decodes the Required Insert Count prefix and
///     checks it against the inbound dynamic table's current insert count. Falls back to
///     static-only when context is null.
///     <para>
///         Deliberately never blocks waiting for missing dynamic-table entries: this proxy always
///         advertises <c>SETTINGS_QPACK_BLOCKED_STREAMS = 0</c> (see <c>Http3Connection.SendServerSettingsAsync</c>),
///         which is a promise to the peer that no stream on this connection will ever be held open
///         waiting for encoder-stream insertions to catch up (RFC 9204 §2.1.2). A block whose Required
///         Insert Count is not yet satisfied at decode time is therefore always a connection error -
///         either the peer sent HEADERS before the corresponding encoder-stream instructions (a
///         genuine ordering violation), or is relying on blocked-stream semantics this connection
///         never offered - not a legitimate race to wait out.
///     </para>
/// </summary>
internal static class QpackDecoder
{
    /// <summary>
    ///     Decodes a QPACK-encoded header block synchronously (static-table-only mode, no context).
    ///     Throws <see cref="Http3ConnectionException" /> if the block references the dynamic table.
    /// </summary>
    public static List<(string Name, string Value)> Decode(ReadOnlySpan<byte> data) =>
        DecodeCore(data, context: null);

    /// <summary>
    ///     Decodes a QPACK-encoded header block, optionally using the dynamic table from
    ///     <paramref name="context" />. If the block's Required Insert Count exceeds the inbound
    ///     dynamic table's current insert count, throws <see cref="Http3ConnectionException" />
    ///     with <see cref="Http3ErrorCode.QpackDecompressionFailed" /> immediately rather than
    ///     waiting for it to be satisfied - see the type-level remarks for why blocking would
    ///     contradict this connection's own SETTINGS advertisement.
    /// </summary>
    public static Task<List<(string Name, string Value)>> DecodeAsync(
        ReadOnlyMemory<byte> data, QpackContext? context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (context != null &&
            data.Length >= 1 &&
            TryReadPrefixedInt(data.Span, 8, out ulong encodedRic, out _) &&
            encodedRic > 0)
        {
            // Decode the RequiredInsertCount per RFC 9204 §4.5.1.1.
            var requiredInsertCount = DecodeRequiredInsertCount(
                encodedRic, context.InboundDecoderTable.InsertCount, context.MaxTableCapacityFromPeer);
            if (requiredInsertCount > context.InboundDecoderTable.InsertCount)
                throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed,
                    $"QPACK header block requires insert count {requiredInsertCount}, but the inbound " +
                    $"dynamic table only has {context.InboundDecoderTable.InsertCount} entries. This " +
                    "connection advertised SETTINGS_QPACK_BLOCKED_STREAMS = 0, so the block cannot be " +
                    "held open waiting for it to be satisfied.");
        }

        return Task.FromResult(DecodeCore(data.Span, context));
    }

    internal static ulong DecodeRequiredInsertCount(ulong encodedRic, ulong insertCount, uint maxTableCapacity)
    {
        if (encodedRic == 0) return 0;
        var maxEntries = (ulong)(maxTableCapacity / 32);
        if (maxEntries == 0) return 0;
        var fullRange = 2 * maxEntries;
        var maxValue = insertCount + maxEntries;
        var candidate = maxValue / fullRange * fullRange + encodedRic - 1;
        if (candidate > maxValue) candidate -= fullRange;
        return candidate;
    }

    private static List<(string Name, string Value)> DecodeCore(ReadOnlySpan<byte> data, QpackContext? context)
    {
        if (data.Length < 2)
            throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed,
                "QPACK header block too short (missing Required Insert Count prefix).");

        // Parse Required Insert Count
        if (!TryReadPrefixedInt(data, 8, out var requiredInsertCount, out var consumed))
            throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Invalid Required Insert Count.");
        data = data[consumed..];

        // Parse S bit and Delta Base
        if (data.IsEmpty)
            throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Missing Base field.");
        if (!TryReadPrefixedInt(data, 7, out var deltaBase, out consumed))
            throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Invalid Delta Base.");
        data = data[consumed..];

        if (requiredInsertCount != 0 && context == null)
            throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed,
                $"Dynamic QPACK table not supported: Required Insert Count = {requiredInsertCount}.");

        var headers = new List<(string, string)>();

        while (!data.IsEmpty)
        {
            var b = data[0];

            if ((b & 0x80) != 0)
            {
                // Indexed Header Field — S=1 static, S=0 dynamic
                var isStatic = (b & 0x40) != 0;
                if (!TryReadPrefixedInt(data, 6, out var index, out consumed))
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Invalid indexed field index.");
                data = data[consumed..];

                if (isStatic)
                {
                    if (index >= (ulong)QpackStaticTable.Entries.Length)
                        throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed,
                            $"Static table index {index} out of range.");
                    var entry = QpackStaticTable.Entries[index];
                    headers.Add((entry.Name, entry.Value));
                }
                else
                {
                    if (context?.InboundDecoderTable.TryGetByAbsoluteIndex(index, out string dynName, out string dynValue) == true)
                        headers.Add((dynName, dynValue));
                    else
                        throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed,
                            $"Dynamic table absolute index {index} not found.");
                }
            }
            else if ((b & 0x40) != 0)
            {
                // Literal Header Field With Name Reference
                var isStatic = (b & 0x10) != 0;
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
                    if (context?.InboundDecoderTable.TryGetByAbsoluteIndex(nameIndex, out string dynName, out _) == true)
                        name = dynName;
                    else
                        throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed,
                            $"Dynamic table name index {nameIndex} not found.");
                }

                if (!TryReadStringLiteral(data, out var value, out consumed))
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Invalid literal value.");
                data = data[consumed..];

                headers.Add((name, value));
            }
            else if ((b & 0x20) != 0)
            {
                // Literal Field Line With Literal Name (RFC 9204 §4.5.6):
                // 0 0 1 N H NameLen(3+) — name length is the 3-bit prefix of this byte.
                var nameHuffman = (b & 0x08) != 0;
                if (!TryReadPrefixedInt(data, 3, out var nameLength, out consumed))
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Invalid literal name length.");
                data = data[consumed..];
                if (nameLength > (ulong)data.Length)
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Truncated literal name.");
                var nameBytes = data[..(int)nameLength];
                data = data[(int)nameLength..];
                string name;
                try
                {
                    name = nameHuffman ? HuffmanDecode(nameBytes) : Encoding.Latin1.GetString(nameBytes);
                }
                catch (Exception)
                {
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed,
                        "Invalid Huffman-coded literal name.");
                }

                if (!TryReadStringLiteral(data, out var value, out consumed))
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Invalid literal value.");
                data = data[consumed..];
                headers.Add((name, value));
            }
            else if ((b & 0x10) != 0)
            {
                // Indexed Header Field (post-base, dynamic): 0 0 0 1 Index(4)
                if (!TryReadPrefixedInt(data, 4, out var index, out consumed))
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Invalid post-base index.");
                data = data[consumed..];

                if (context?.InboundDecoderTable.TryGetByAbsoluteIndex(index, out string pbName, out string pbValue) == true)
                    headers.Add((pbName, pbValue));
                else
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed,
                        $"Post-base dynamic index {index} not found.");
            }
            else
            {
                // Literal Header Field With Post-Base Name Reference: 0 0 0 0 N Index(3)
                if (!TryReadPrefixedInt(data, 3, out var nameIndex, out consumed))
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Invalid post-base name ref.");
                data = data[consumed..];

                string nameFromDyn;
                if (context?.InboundDecoderTable.TryGetByAbsoluteIndex(nameIndex, out string pbName, out _) == true)
                    nameFromDyn = pbName;
                else
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed,
                        $"Post-base dynamic name index {nameIndex} not found.");

                if (!TryReadStringLiteral(data, out var value, out consumed))
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Invalid post-base literal value.");
                data = data[consumed..];
                headers.Add((nameFromDyn, value));
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
