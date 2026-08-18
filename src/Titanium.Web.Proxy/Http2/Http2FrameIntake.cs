using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     Socket-backed HTTP/2 frame intake: one large
///     <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)" /> can satisfy many subsequent
///     9-byte headers + payloads (SocketsHttpHandler / Kestrel leftover-buffer pattern without a
///     <c>ReadOnlySequence</c> retrofit). Shared by inbound MITM relay and origin
///     <see cref="Http2OriginConnection" />.
/// </summary>
/// <remarks>
///     Default capacity is 64 KiB — large enough that a typical TLS record / TCP read yields multiple
///     frames before the next Fill, matching the inbound MITM path comment historically paired with
///     this type. Frames larger than the capacity must use <see cref="ReadExactAsync"/> (chunked copy).
/// </remarks>
internal sealed class Http2FrameIntake
{
    private const int DefaultCapacity = 64 * 1024;
    private readonly Stream input;
    private readonly byte[] buf;
    private int start;
    private int end;

    public Http2FrameIntake(Stream input, int capacity = DefaultCapacity)
    {
        this.input = input;
        buf = GC.AllocateUninitializedArray<byte>(capacity);
    }

    /// <summary>Bytes currently buffered and not yet consumed.</summary>
    public int Available => end - start;

    /// <summary>Contiguous unread bytes in the intake buffer (SocketsHttpHandler <c>ActiveSpan</c>).</summary>
    public ReadOnlySpan<byte> ActiveSpan => buf.AsSpan(start, Available);

    /// <summary>Contiguous unread bytes as <see cref="ReadOnlyMemory{T}"/> for pipe writes.</summary>
    public ReadOnlyMemory<byte> ActiveMemory => buf.AsMemory(start, Available);

    /// <summary>Underlying capacity of the leftover buffer.</summary>
    public int Capacity => buf.Length;

    /// <summary>
    ///     Ensures at least <paramref name="count"/> bytes are buffered contiguously.
    ///     Returns <see langword="false"/> on EOF, or when <paramref name="count"/> exceeds capacity
    ///     (caller must fall back to <see cref="ReadExactAsync"/>).
    /// </summary>
    public async ValueTask<bool> EnsureAsync(int count, CancellationToken cancellationToken)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0)
            return true;
        if (count > buf.Length)
            return false;

        while (Available < count)
        {
            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
                return false;
        }

        return true;
    }

    /// <summary>Consumes <paramref name="count"/> bytes from the front of the active buffer.</summary>
    public void Advance(int count)
    {
        if (count < 0 || count > Available)
            throw new ArgumentOutOfRangeException(nameof(count));
        start += count;
        if (start == end)
        {
            start = 0;
            end = 0;
        }
    }

    public async ValueTask<bool> ReadExactAsync(byte[] dest, int destOffset, int count,
        CancellationToken cancellationToken)
    {
        if (count == 0)
            return true;

        var copied = 0;
        while (copied < count)
        {
            if (Available == 0 && !await FillAsync(cancellationToken).ConfigureAwait(false))
                return false;

            var n = Math.Min(count - copied, Available);
            Buffer.BlockCopy(buf, start, dest, destOffset + copied, n);
            start += n;
            copied += n;
        }

        if (start == end)
        {
            start = 0;
            end = 0;
        }

        return true;
    }

    public async ValueTask DiscardAsync(int length, CancellationToken cancellationToken)
    {
        var remaining = length;
        while (remaining > 0)
        {
            if (Available == 0 && !await FillAsync(cancellationToken).ConfigureAwait(false))
                return;

            var n = Math.Min(remaining, Available);
            start += n;
            remaining -= n;
        }

        if (start == end)
        {
            start = 0;
            end = 0;
        }
    }

    private async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
    {
        if (start == end)
        {
            start = 0;
            end = 0;
        }
        else if (start > 0 && end == buf.Length)
        {
            var available = end - start;
            Buffer.BlockCopy(buf, start, buf, 0, available);
            start = 0;
            end = available;
        }

        if (end == buf.Length)
        {
            // Compact so a single oversized need can still progress via ForceRead-sized chunks.
            if (start > 0)
            {
                var available = end - start;
                Buffer.BlockCopy(buf, start, buf, 0, available);
                start = 0;
                end = available;
            }

            if (end == buf.Length)
                return false;
        }

        var read = await input.ReadAsync(buf.AsMemory(end, buf.Length - end), cancellationToken)
            .ConfigureAwait(false);
        if (read == 0)
            return false;

        end += read;
        return true;
    }
}
