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
///     this type.
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

    private int Available => end - start;

    public async ValueTask<bool> ReadExactAsync(byte[] dest, int destOffset, int count,
        CancellationToken cancellationToken)
    {
        if (count == 0)
            return true;

        var copied = 0;
        while (copied < count)
        {
            if (Available == 0 && !await FillAsync(cancellationToken))
                return false;

            var n = Math.Min(count - copied, Available);
            Buffer.BlockCopy(buf, start, dest, destOffset + copied, n);
            start += n;
            copied += n;
        }

        return true;
    }

    public async ValueTask DiscardAsync(int length, CancellationToken cancellationToken)
    {
        var remaining = length;
        while (remaining > 0)
        {
            if (Available == 0 && !await FillAsync(cancellationToken))
                return;

            var n = Math.Min(remaining, Available);
            start += n;
            remaining -= n;
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
            // Should not happen for frames ≤ MaxAcceptableFrameSize with 64 KiB capacity, but
            // compact so a single oversized need can still progress via ForceRead-sized chunks.
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

        var read = await input.ReadAsync(buf.AsMemory(end, buf.Length - end), cancellationToken);
        if (read == 0)
            return false;

        end += read;
        return true;
    }
}
