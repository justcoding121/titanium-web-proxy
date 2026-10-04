using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     HTTP/3 frame as read from or written to a stream (typically a QUIC stream).
///     Format: <c>Type (VarInt) | Length (VarInt) | Payload (Length bytes)</c> (RFC 9114 §7.1).
///     Payload buffers are rented from <see cref="ArrayPool{T}"/> when non-empty; call
///     <see cref="ReturnPayload"/> when finished with <see cref="Payload"/> (idempotent).
/// </summary>
internal sealed class Http3Frame
{
    private byte[]? rentedPayload;

    public ulong Type { get; }
    public ReadOnlyMemory<byte> Payload { get; }

    private Http3Frame(ulong type, ReadOnlyMemory<byte> payload, byte[]? rented)
    {
        Type = type;
        Payload = payload;
        rentedPayload = rented;
    }

    /// <summary>
    ///     Returns a rented payload buffer to <see cref="ArrayPool{T}"/>. Safe to call more than once.
    ///     After this, <see cref="Payload"/> must not be read.
    /// </summary>
    public void ReturnPayload()
    {
        var buffer = Interlocked.Exchange(ref rentedPayload, null);
        if (buffer != null)
            ArrayPool<byte>.Shared.Return(buffer);
    }

    /// <summary>
    ///     Reads one HTTP/3 frame from <paramref name="stream" />.
    ///     Returns <see langword="null" /> when the stream is cleanly closed (end-of-data).
    /// </summary>
    /// <exception cref="Http3ConnectionException">On malformed frame (e.g., huge payload).</exception>
    public static async ValueTask<Http3Frame?> ReadAsync(
        Stream stream,
        long maxPayloadBytes,
        CancellationToken cancellationToken)
    {
        var frameType = await Http3VarInt.ReadAsync(stream, cancellationToken);
        if (frameType is null) return null;

        var payloadLength = await Http3VarInt.ReadAsync(stream, cancellationToken)
            ?? throw new Http3ConnectionException(Http3ErrorCode.FrameError, "Unexpected end of stream reading frame length.");

        if (maxPayloadBytes > 0 && (long)payloadLength > maxPayloadBytes)
            throw new Http3ConnectionException(Http3ErrorCode.ExcessiveLoad,
                $"HTTP/3 frame payload {payloadLength} bytes exceeds limit {maxPayloadBytes}.");

        if (payloadLength == 0)
            return new Http3Frame(frameType.Value, ReadOnlyMemory<byte>.Empty, null);

        var length = (int)payloadLength;
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(rented.AsMemory(offset, length - offset), cancellationToken);
                if (read == 0)
                    throw new Http3ConnectionException(Http3ErrorCode.FrameError,
                        $"Unexpected end of stream reading frame payload (expected {payloadLength}, got {offset}).");
                offset += read;
            }

            return new Http3Frame(frameType.Value, rented.AsMemory(0, length), rented);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }

    /// <summary>
    ///     Writes a frame (type + length + payload) to <paramref name="stream" />.
    ///     Coalesces the VarInt header (and small payloads) into a single socket write —
    ///     single-span header flush pattern (VarInt header + small payload coalesced into one write).
    ///     When <paramref name="completeWrites"/> is true and <paramref name="stream"/> is a
    ///     <see cref="QuicStream"/>, STREAM data and FIN share one MsQuic write. Callers must still
    ///     <c>FlushAsync</c> (Darwin skip-Flush is banned).
    /// </summary>
    public static ValueTask WriteAsync(
        Stream stream,
        ulong frameType,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken,
        bool completeWrites = false,
        Http3FrameScratch? scratch = null)
    {
        // Max VarInt is 8 bytes each for type + length.
        const int headerCap = 16;
        if (scratch != null
            && payload.Length <= Http3FrameScratch.Capacity - headerCap
            && scratch.TryAcquire())
        {
            return WriteScratchAsync(stream, frameType, payload, completeWrites, scratch, cancellationToken);
        }

        if (payload.Length <= 256)
        {
            var rented = ArrayPool<byte>.Shared.Rent(headerCap + payload.Length);
            var typeLen = Http3VarInt.Write(rented, frameType);
            var lengthLen = Http3VarInt.Write(rented.AsSpan(typeLen), (ulong)payload.Length);
            var headerLen = typeLen + lengthLen;
            if (!payload.IsEmpty)
                payload.Span.CopyTo(rented.AsSpan(headerLen));
            var total = headerLen + payload.Length;
            // Always await before ArrayPool.Return. Returning on IsCompletedSuccessfully without
            // consuming the ValueTask let QuicStream/MsQuic keep the Memory while the next frame
            // Rent reused the same array — H3_FRAME_ERROR on streamed H3 bodies (≥16 KiB chunks /
            // H3→H2 above the 8 KiB origin buffer). Introduced by e781b009 ValueTask fast-path.
            return AwaitWriteAndReturnAsync(
                WriteBufferAsync(stream, rented.AsMemory(0, total), completeWrites, cancellationToken),
                rented);
        }

        // Large DATA: one write for the header, then the payload buffer as-is (avoid a huge copy).
        return WriteLargeAsync(stream, frameType, payload, completeWrites, cancellationToken);
    }

    /// <summary>
    ///     Copies the frame into <paramref name="scratch"/> and does not release it until the
    ///     <see cref="QuicStream"/> write has been consumed. Sync completion calls <c>GetResult</c>
    ///     before release so MsQuic cannot still hold the memory (the e781b009 ArrayPool bug).
    ///     A second write while this one is in flight must not call <see cref="Http3FrameScratch.TryAcquire"/>
    ///     successfully; callers fall back to <see cref="ArrayPool{T}"/>.
    /// </summary>
    private static ValueTask WriteScratchAsync(
        Stream stream,
        ulong frameType,
        ReadOnlyMemory<byte> payload,
        bool completeWrites,
        Http3FrameScratch scratch,
        CancellationToken cancellationToken)
    {
        int total;
        try
        {
            var span = scratch.Buffer.AsSpan();
            var typeLen = Http3VarInt.Write(span, frameType);
            var lengthLen = Http3VarInt.Write(span.Slice(typeLen), (ulong)payload.Length);
            var headerLen = typeLen + lengthLen;
            if (!payload.IsEmpty)
                payload.Span.CopyTo(span.Slice(headerLen));
            total = headerLen + payload.Length;
        }
        catch
        {
            scratch.Release();
            throw;
        }

        ValueTask write;
        try
        {
            write = WriteBufferAsync(stream, scratch.Buffer.AsMemory(0, total), completeWrites, cancellationToken);
        }
        catch
        {
            scratch.Release();
            throw;
        }

        if (write.IsCompletedSuccessfully)
        {
            try
            {
                write.GetAwaiter().GetResult();
            }
            finally
            {
                scratch.Release();
            }

            return default;
        }

        return AwaitScratchAsync(write, scratch);
    }

    private static async ValueTask AwaitScratchAsync(ValueTask write, Http3FrameScratch scratch)
    {
        try
        {
            await write.ConfigureAwait(false);
        }
        finally
        {
            scratch.Release();
        }
    }

    private static async ValueTask WriteLargeAsync(
        Stream stream,
        ulong frameType,
        ReadOnlyMemory<byte> payload,
        bool completeWrites,
        CancellationToken cancellationToken)
    {
        const int headerCap = 16;
        var headerBytes = ArrayPool<byte>.Shared.Rent(headerCap);
        try
        {
            var typeLen = Http3VarInt.Write(headerBytes, frameType);
            var lengthLen = Http3VarInt.Write(headerBytes.AsSpan(typeLen), (ulong)payload.Length);
            await stream.WriteAsync(headerBytes.AsMemory(0, typeLen + lengthLen), cancellationToken)
                .ConfigureAwait(false);
            await WriteBufferAsync(stream, payload, completeWrites, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBytes);
        }
    }

    private static async ValueTask AwaitWriteAndReturnAsync(ValueTask writeVt, byte[] rented)
    {
        try
        {
            await writeVt.ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    ///     Writes a zero-payload frame (used for GOAWAY and some SETTINGS without parameters).
    /// </summary>
    public static ValueTask WriteAsync(
        Stream stream,
        ulong frameType,
        CancellationToken cancellationToken)
        => WriteAsync(stream, frameType, ReadOnlyMemory<byte>.Empty, cancellationToken);

    /// <summary>
    ///     Writes HEADERS then DATA as a single stream write when the combined frames fit a modest
    ///     buffer. Used for already-buffered medium/large bodies (lossy / bodies ≥ 16 KiB) so the
    ///     UDP path does not emit a header-only datagram before the body. Tiny GET stays on the
    ///     separate-write path (HEADERS+DATA coalesce there hurt CI).
    /// </summary>
    public static ValueTask WriteHeadersAndDataAsync(
        Stream stream,
        ReadOnlyMemory<byte> headersPayload,
        ReadOnlyMemory<byte> dataPayload,
        CancellationToken cancellationToken,
        bool completeWrites = false)
    {
        const int headerCap = 16;
        var total = headerCap + headersPayload.Length + headerCap + dataPayload.Length;
        var rented = ArrayPool<byte>.Shared.Rent(total);
        var o = 0;
        o += Http3VarInt.Write(rented.AsSpan(o), Http3FrameType.Headers);
        o += Http3VarInt.Write(rented.AsSpan(o), (ulong)headersPayload.Length);
        if (!headersPayload.IsEmpty)
        {
            headersPayload.Span.CopyTo(rented.AsSpan(o));
            o += headersPayload.Length;
        }

        o += Http3VarInt.Write(rented.AsSpan(o), Http3FrameType.Data);
        o += Http3VarInt.Write(rented.AsSpan(o), (ulong)dataPayload.Length);
        if (!dataPayload.IsEmpty)
        {
            dataPayload.Span.CopyTo(rented.AsSpan(o));
            o += dataPayload.Length;
        }

        // Same ValueTask-consume-before-Return rule as WriteAsync (see comment there).
        return AwaitWriteAndReturnAsync(
            WriteBufferAsync(stream, rented.AsMemory(0, o), completeWrites, cancellationToken),
            rented);
    }

#pragma warning disable CA1416 // QuicStream.WriteAsync(completeWrites) is gated on the runtime stream type.
    /// <summary>
    ///     When <paramref name="completeWrites"/> is set, pack STREAM payload + FIN on
    ///     <see cref="QuicStream"/>; other streams ignore the flag (unit tests use MemoryStream).
    /// </summary>
    private static ValueTask WriteBufferAsync(
        Stream stream,
        ReadOnlyMemory<byte> buffer,
        bool completeWrites,
        CancellationToken cancellationToken)
    {
        if (completeWrites && stream is QuicStream quic)
            return quic.WriteAsync(buffer, completeWrites: true, cancellationToken);
        return stream.WriteAsync(buffer, cancellationToken);
    }
#pragma warning restore CA1416
}

/// <summary>
///     Single-owner buffer for small HTTP/3 frames on one stream. The buffer stays owned until
///     <see cref="Release"/> runs, which is only after the write ValueTask has been consumed.
///     Do not return this object to the pool while <see cref="TryAcquire"/> is held.
///     Writes on one stream are sequential (each <c>WriteAsync</c> is awaited). A concurrent
///     writer fails <see cref="TryAcquire"/> and uses <see cref="ArrayPool{T}"/> instead.
/// </summary>
internal sealed class Http3FrameScratch
{
    internal const int Capacity = 1024;

    private static readonly ConcurrentBag<Http3FrameScratch> Pool = new();

    private readonly byte[] buffer = new byte[Capacity];
    private int busy;
    private int checkedOut;

    internal byte[] Buffer => buffer;

    internal static Http3FrameScratch Rent()
    {
        if (!Pool.TryTake(out var scratch))
            scratch = new Http3FrameScratch();
        scratch.checkedOut = 1;
        scratch.busy = 0;
        return scratch;
    }

    /// <summary>
    ///     Returns the scratch to the pool. If a write is still in flight the instance is dropped
    ///     instead of being reused, so MsQuic cannot observe a recycled buffer.
    /// </summary>
    internal void Return()
    {
        if (Volatile.Read(ref busy) != 0)
            return;
        if (Interlocked.Exchange(ref checkedOut, 0) != 1)
            return;
        Pool.Add(this);
    }

    internal bool TryAcquire() => Interlocked.CompareExchange(ref busy, 1, 0) == 0;

    internal void Release() => Volatile.Write(ref busy, 0);
}
