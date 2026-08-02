using System;
using System.IO;
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
    ///     <para>
    ///         Instructions are not guaranteed to align with QUIC read boundaries - a single
    ///         <c>stream.ReadAsync</c> call can return a buffer that ends mid-instruction. Any bytes
    ///         left over after parsing every complete instruction out of the current buffer are
    ///         carried forward into <c>pending</c> and prefixed onto the next read, rather than
    ///         discarded: dropping them would desynchronize this reader from the encoder's actual
    ///         instruction stream for the rest of the connection, corrupting every subsequent
    ///         dynamic-table insertion silently.
    ///     </para>
    /// </summary>
    internal static async Task ProcessAsync(Stream stream, QpackContext context, CancellationToken ct)
    {
        var readBuffer = new byte[4096];
        var pending = Array.Empty<byte>();

        while (!ct.IsCancellationRequested)
        {
            // Drain every complete instruction already sitting in `pending` before reading more.
            while (pending.Length > 0 && TryParseOneInstruction(pending, context, out var consumed))
            {
                pending = consumed >= pending.Length ? Array.Empty<byte>() : pending[consumed..];
            }

            int read = await stream.ReadAsync(readBuffer, ct);
            if (read == 0)
            {
                if (pending.Length > 0)
                    // RFC 9204 §3.2: the encoder stream carries a sequence of instructions; ending it
                    // with an incomplete one is a genuine protocol violation, not a benign EOF.
                    throw new Http3ConnectionException(Http3ErrorCode.QpackEncoderStreamError,
                        "The QPACK encoder stream ended with a truncated instruction.");
                return;
            }

            if (pending.Length == 0)
            {
                pending = readBuffer.AsSpan(0, read).ToArray();
            }
            else
            {
                var combined = new byte[pending.Length + read];
                pending.CopyTo(combined, 0);
                readBuffer.AsSpan(0, read).CopyTo(combined.AsSpan(pending.Length));
                pending = combined;
            }
        }
    }

    /// <summary>
    ///     Attempts to parse and apply exactly one instruction from the start of <paramref name="data" />.
    ///     Returns <see langword="false" /> (with <paramref name="consumed" /> left at 0) when
    ///     <paramref name="data" /> does not yet contain a complete instruction - the caller must wait
    ///     for more bytes and retry with the same, unconsumed <paramref name="data" /> prefixed onto
    ///     whatever arrives next. A reference to an evicted/out-of-range table entry is applied as a
    ///     silent no-op (matching the pre-existing, deliberately lenient behavior here) rather than
    ///     torn down as a connection error, since the entry may simply have been evicted by the time
    ///     this reader catches up - only a truncated instruction at end-of-stream is fatal.
    ///     Exposed internally for unit tests that feed crafted instruction bytes without a live QUIC stream.
    /// </summary>
    internal static bool TryParseOneInstruction(ReadOnlySpan<byte> data, QpackContext context, out int consumed)
    {
        consumed = 0;
        if (data.IsEmpty) return false;

        var b = data[0];

        if ((b & 0x80) != 0)
        {
            // Insert With Name Reference: 1 S T Index(6) + value literal
            var isStatic = (b & 0x40) != 0;
            if (!TryReadPrefixedInt(data, 6, out var nameIndex, out var headerConsumed)) return false;
            if (!TryReadStringLiteral(data[headerConsumed..], out var value, out var valueConsumed)) return false;
            consumed = headerConsumed + valueConsumed;

            string name;
            if (isStatic)
            {
                if (nameIndex < (ulong)QpackStaticTable.Entries.Length)
                    name = QpackStaticTable.Entries[nameIndex].Name;
                else
                    return true; // invalid static index — skip (still consumes the full instruction)
            }
            else
            {
                if (!context.InboundDecoderTable.TryGetByAbsoluteIndex(nameIndex, out name, out _))
                    return true; // evicted entry — skip
            }

            context.InboundDecoderTable.Insert(name, value, context.InFlightMinAbsoluteIndex);
            context.NotifyInsert();
            return true;
        }

        if ((b & 0x40) != 0)
        {
            // Set Dynamic Table Capacity: 01 Capacity(6)
            if (!TryReadPrefixedInt(data, 6, out var newCapacity, out consumed)) { consumed = 0; return false; }
            context.InboundDecoderTable.SetCapacity((uint)Math.Min(newCapacity, uint.MaxValue));
            return true;
        }

        if ((b & 0x20) != 0)
        {
            // Insert With Literal Name: 001 N Name-literal Value-literal
            if (data.Length < 2) return false;
            if (!TryReadStringLiteral(data[1..], out var name, out var nameConsumed)) return false;
            if (!TryReadStringLiteral(data[(1 + nameConsumed)..], out var value, out var valueConsumed)) return false;
            consumed = 1 + nameConsumed + valueConsumed;

            context.InboundDecoderTable.Insert(name, value, context.InFlightMinAbsoluteIndex);
            context.NotifyInsert();
            return true;
        }

        // Duplicate: 000 Index(5)
        if (!TryReadPrefixedInt(data, 5, out var dupIndex, out consumed)) { consumed = 0; return false; }

        if (context.InboundDecoderTable.TryGetByAbsoluteIndex(dupIndex, out var dupName, out var dupValue))
        {
            context.InboundDecoderTable.Insert(dupName, dupValue, context.InFlightMinAbsoluteIndex);
            context.NotifyInsert();
        }

        return true;
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
            if (m >= 63) { value = 0; consumed = 0; return false; }
        }
        value = 0; consumed = 0; return false;
    }

    private static bool TryReadStringLiteral(ReadOnlySpan<byte> data, out string result, out int consumed)
    {
        result = string.Empty; consumed = 0;
        if (data.IsEmpty) return false;
        var huffman = (data[0] & 0x80) != 0;
        if (!TryReadPrefixedInt(data, 7, out var length, out var headerLen)) return false;
        var total = headerLen + (int)length;
        if (total > data.Length) return false;
        consumed = total;
        var strData = data.Slice(headerLen, (int)length);
        result = huffman
            ? Encoding.Latin1.GetString(Titanium.Web.Proxy.Http2.Hpack.HuffmanDecoder.Instance.Decode(strData.ToArray()).Span)
            : Encoding.Latin1.GetString(strData);
        return true;
    }
}
