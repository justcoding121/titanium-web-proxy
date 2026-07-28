using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http3.Qpack;

/// <summary>
///     Per-connection QPACK state, holding two independent dynamic tables (one inbound, one outbound)
///     plus the synchronization primitives required by the RFC 9204 encoder/decoder stream protocol.
///     Implements <see cref="IAsyncDisposable" /> so that background loops and channels are
///     gracefully drained when the connection closes.
/// </summary>
internal sealed class QpackContext : IAsyncDisposable
{
    private const int DecoderAckChannelCapacity = 1000;

    // ── Inbound pair (client=encoder, proxy=decoder) ───────────────────────────
    /// <summary>
    ///     Table populated by the remote peer's QPACK encoder stream instructions. Used to decode
    ///     request HEADERS frames that reference the dynamic table.
    /// </summary>
    internal QpackDynamicTable InboundDecoderTable { get; }

    // ── Outbound pair (proxy=encoder, client=decoder) ──────────────────────────
    /// <summary>
    ///     Table populated by our own QPACK encoder stream insertions. Used to encode response
    ///     HEADERS frames with dynamic-table references.
    /// </summary>
    internal QpackDynamicTable OutboundEncoderTable { get; }

    /// <summary>
    ///     Maximum table capacity advertised by the peer in <c>SETTINGS_QPACK_MAX_TABLE_CAPACITY</c>.
    ///     The encoder must not insert entries that would exceed this limit.
    /// </summary>
    internal uint MaxTableCapacityFromPeer { get; set; }

    /// <summary>
    ///     Serializes concurrent HEADERS-block encoder stream writes so that insertion instructions
    ///     from different request streams do not interleave on the single unidirectional encoder stream.
    /// </summary>
    internal SemaphoreSlim EncoderStreamWriteLock { get; } = new(1, 1);

    /// <summary>
    ///     Bounded channel of serialized decoder-stream instructions (Section Ack, Insert Count Increment).
    ///     Bounded to prevent unbounded growth if the peer stalls reading acks.
    ///     <see cref="QpackDecoderStreamWriter" /> drains this channel.
    /// </summary>
    internal Channel<byte[]> DecoderAckChannel { get; } =
        Channel.CreateBounded<byte[]>(new BoundedChannelOptions(DecoderAckChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropNewest,
            SingleWriter = false,
            SingleReader = true
        });

    /// <summary>
    ///     Tracks the lowest absolute dynamic-table index referenced by each in-flight (not-yet-
    ///     acknowledged) HEADERS block. Keyed by QUIC stream ID. Eviction skips any entry whose
    ///     absolute index is ≤ a value in this dictionary.
    /// </summary>
    internal ConcurrentDictionary<long, ulong> InFlightMinAbsoluteIndex { get; } = new();

    /// <summary>
    ///     When true, the peer set <c>SETTINGS_QPACK_MAX_TABLE_CAPACITY = 0</c>, which means we must
    ///     not reference the outbound dynamic table in any HEADERS block we send.
    /// </summary>
    internal bool OutboundTableDisabled { get; private set; }

    // Used by AwaitInsertCountAsync to be notified when the InboundDecoderTable's insert count grows.
    private readonly SemaphoreSlim _insertNotifier = new(0);

    internal QpackContext(uint tableCapacity)
    {
        InboundDecoderTable = new QpackDynamicTable(tableCapacity);
        OutboundEncoderTable = new QpackDynamicTable(tableCapacity);
    }

    /// <summary>
    ///     Called by <see cref="QpackEncoderStreamReader" /> after each successful table insertion to
    ///     release threads blocked in <see cref="AwaitInsertCountAsync" />.
    /// </summary>
    internal void NotifyInsert() => _insertNotifier.Release();

    /// <summary>
    ///     Waits until <c>InboundDecoderTable.InsertCount &gt;= <paramref name="requiredInsertCount" /></c>
    ///     or <paramref name="ct" /> is cancelled. Cancellation prevents deadlock when the connection
    ///     closes before the Required Insert Count is satisfied.
    /// </summary>
    internal async Task AwaitInsertCountAsync(ulong requiredInsertCount, CancellationToken ct)
    {
        while (InboundDecoderTable.InsertCount < requiredInsertCount)
        {
            ct.ThrowIfCancellationRequested();
            // Wait for the next insert notification (released by NotifyInsert).
            await _insertNotifier.WaitAsync(ct);
        }
    }

    /// <summary>
    ///     Queues a Section Acknowledgment instruction (RFC 9204 §3.2.6) for the given stream ID.
    ///     The instruction is dropped (not thrown) if the decoder ack channel is full.
    /// </summary>
    internal void EnqueueSectionAck(long streamId)
    {
        // Section Acknowledgment: 1-bit pattern 1, then Stream ID as a 7-bit prefix integer.
        var instruction = EncodeVarIntInstruction(0x80, 7, (ulong)streamId);
        DecoderAckChannel.Writer.TryWrite(instruction); // BoundedChannelFullMode.DropNewest on full
    }

    /// <summary>
    ///     Disables outbound dynamic-table encoding for this connection.
    ///     Called when the peer sends <c>SETTINGS_QPACK_MAX_TABLE_CAPACITY = 0</c>.
    /// </summary>
    internal void DisableOutboundTable() => OutboundTableDisabled = true;

    private static byte[] EncodeVarIntInstruction(byte patternByte, int prefixBits, ulong value)
    {
        var buf = new System.IO.MemoryStream(2);
        var mask = (uint)((1 << prefixBits) - 1);
        if (value < mask)
        {
            buf.WriteByte((byte)(patternByte | (byte)value));
        }
        else
        {
            buf.WriteByte((byte)(patternByte | (byte)mask));
            value -= mask;
            while (value >= 0x80)
            {
                buf.WriteByte((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }
            buf.WriteByte((byte)value);
        }
        return buf.ToArray();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        DecoderAckChannel.Writer.TryComplete();
        EncoderStreamWriteLock.Dispose();
        _insertNotifier.Dispose();
        InboundDecoderTable.Dispose();
        OutboundEncoderTable.Dispose();
        await Task.CompletedTask; // satisfy async signature; heavy cleanup is synchronous
    }
}
