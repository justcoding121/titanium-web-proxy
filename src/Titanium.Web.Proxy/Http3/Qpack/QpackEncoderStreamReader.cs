#pragma warning disable CA1416
using System;
using System.Net.Quic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http3.Qpack;

/// <summary>
///     Reads RFC 9204 §3.2 QPACK encoder stream instructions from the peer's unidirectional encoder
///     stream and applies them to <see cref="QpackContext.InboundDecoderTable" />.
///     Supported instructions:
///     <list type="bullet">
///       <item>Insert With Name Reference (static or dynamic table)</item>
///       <item>Insert With Literal Name</item>
///       <item>Duplicate</item>
///       <item>Set Dynamic Table Capacity</item>
///     </list>
/// </summary>
internal static class QpackEncoderStreamReader
{
    /// <summary>
    ///     Continuously reads and applies encoder instructions until the stream ends or
    ///     <paramref name="ct" /> is cancelled.
    /// </summary>
    internal static async Task ProcessAsync(QuicStream stream, QpackContext context, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var remaining = ReadOnlyMemory<byte>.Empty;

        while (!ct.IsCancellationRequested)
        {
            // Refill buffer when the current window is consumed.
            if (remaining.IsEmpty)
            {
                int read = await stream.ReadAsync(buffer, ct);
                if (read == 0) return; // stream ended
                remaining = buffer.AsMemory(0, read);
            }

            var span = remaining.Span;
            var b = span[0];

            if ((b & 0x80) != 0)
            {
                // Insert With Name Reference: 1 S T Index(6) + value literal
                bool isStatic = (b & 0x40) != 0;
                if (!TryReadPrefixedInt(span, 6, out ulong nameIndex, out int consumed)) { remaining = await RefillAsync(stream, buffer, ct); continue; }
                remaining = remaining[consumed..];
                span = remaining.Span;

                if (!TryReadStringLiteral(span, out string value, out consumed)) { remaining = await RefillAsync(stream, buffer, ct); continue; }
                remaining = remaining[consumed..];

                string name;
                if (isStatic)
                {
                    if (nameIndex < (ulong)QpackStaticTable.Entries.Length)
                        name = QpackStaticTable.Entries[nameIndex].Name;
                    else
                        continue; // invalid static index — skip
                }
                else
                {
                    if (!context.InboundDecoderTable.TryGetByAbsoluteIndex(nameIndex, out name, out _))
                        continue; // evicted entry — skip
                }

                context.InboundDecoderTable.Insert(name, value, context.InFlightMinAbsoluteIndex);
                context.NotifyInsert();
            }
            else if ((b & 0x40) != 0)
            {
                // Set Dynamic Table Capacity: 01 Capacity(6)
                if (!TryReadPrefixedInt(span, 6, out ulong newCapacity, out int consumed)) { remaining = await RefillAsync(stream, buffer, ct); continue; }
                remaining = remaining[consumed..];

                context.InboundDecoderTable.SetCapacity((uint)Math.Min(newCapacity, uint.MaxValue));
            }
            else if ((b & 0x20) != 0)
            {
                // Insert With Literal Name: 001 N Name-literal Value-literal
                if (remaining.Length < 1) { remaining = await RefillAsync(stream, buffer, ct); continue; }
                remaining = remaining[1..]; // skip pattern byte
                span = remaining.Span;

                if (!TryReadStringLiteral(span, out string name, out int consumed)) { remaining = await RefillAsync(stream, buffer, ct); continue; }
                remaining = remaining[consumed..];
                span = remaining.Span;

                if (!TryReadStringLiteral(span, out string value, out consumed)) { remaining = await RefillAsync(stream, buffer, ct); continue; }
                remaining = remaining[consumed..];

                context.InboundDecoderTable.Insert(name, value, context.InFlightMinAbsoluteIndex);
                context.NotifyInsert();
            }
            else
            {
                // Duplicate: 000 Index(5)
                if (!TryReadPrefixedInt(span, 5, out ulong dupIndex, out int consumed)) { remaining = await RefillAsync(stream, buffer, ct); continue; }
                remaining = remaining[consumed..];

                if (context.InboundDecoderTable.TryGetByAbsoluteIndex(dupIndex, out string dupName, out string dupValue))
                {
                    context.InboundDecoderTable.Insert(dupName, dupValue, context.InFlightMinAbsoluteIndex);
                    context.NotifyInsert();
                }
            }
        }
    }

    private static async Task<ReadOnlyMemory<byte>> RefillAsync(QuicStream stream, byte[] buffer, CancellationToken ct)
    {
        int read = await stream.ReadAsync(buffer, ct);
        return read == 0 ? ReadOnlyMemory<byte>.Empty : buffer.AsMemory(0, read);
    }

    private static bool TryReadPrefixedInt(ReadOnlySpan<byte> data, int prefixBits, out ulong value, out int consumed)
    {
        if (data.IsEmpty) { value = 0; consumed = 0; return false; }
        var mask = (byte)((1 << prefixBits) - 1);
        var initial = (ulong)(data[0] & mask);
        consumed = 1;
        if (initial < (ulong)mask) { value = initial; return true; }
        ulong m = 0, result = (ulong)mask;
        while (consumed < data.Length)
        {
            var next = data[consumed++];
            result += ((ulong)(next & 0x7F)) << (int)m;
            m += 7;
            if ((next & 0x80) == 0) { value = result; return true; }
            if (m >= 63) { value = 0; return false; }
        }
        value = 0; return false;
    }

    private static bool TryReadStringLiteral(ReadOnlySpan<byte> data, out string result, out int consumed)
    {
        result = string.Empty; consumed = 0;
        if (data.IsEmpty) return false;
        var huffman = (data[0] & 0x80) != 0;
        if (!TryReadPrefixedInt(data, 7, out ulong length, out int headerLen)) return false;
        consumed = headerLen + (int)length;
        if (consumed > data.Length) return false;
        var strData = data.Slice(headerLen, (int)length);
        result = huffman
            ? Encoding.Latin1.GetString(Titanium.Web.Proxy.Http2.Hpack.HuffmanDecoder.Instance.Decode(strData.ToArray()).Span)
            : Encoding.Latin1.GetString(strData);
        return true;
    }
}
#pragma warning restore CA1416
