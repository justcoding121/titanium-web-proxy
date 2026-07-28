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
///     waits (via <see cref="QpackContext.AwaitInsertCountAsync" />) if the dynamic table does not
///     yet have the required number of entries. Falls back to static-only when context is null.
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
    ///     <paramref name="context" />. When Required Insert Count &gt; 0, waits until the inbound
    ///     dynamic table has the required number of entries or <paramref name="ct" /> is cancelled.
    /// </summary>
    public static async Task<List<(string Name, string Value)>> DecodeAsync(
        ReadOnlySpan<byte> data, QpackContext? context, CancellationToken ct)
    {
        if (context != null)
        {
            // Peek at the Required Insert Count before decoding the full block.
            if (data.Length >= 1 && TryReadPrefixedInt(data, 8, out ulong encodedRic, out _) && encodedRic > 0)
            {
                // Decode the RequiredInsertCount per RFC 9204 §4.5.1.1.
                var requiredInsertCount = DecodeRequiredInsertCount(
                    encodedRic, context.InboundDecoderTable.InsertCount, context.MaxTableCapacityFromPeer);
                if (requiredInsertCount > 0)
                    await context.AwaitInsertCountAsync(requiredInsertCount, ct);
            }
        }
        return DecodeCore(data, context);
    }

    private static ulong DecodeRequiredInsertCount(ulong encodedRic, ulong insertCount, uint maxTableCapacity)
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
                // Literal Header Field Without Name Reference (0b001xxxxx)
                data = data[1..];
                if (!TryReadStringLiteral(data, out var name, out consumed))
                    throw new Http3ConnectionException(Http3ErrorCode.QpackDecompressionFailed, "Invalid literal name.");
                data = data[consumed..];
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
