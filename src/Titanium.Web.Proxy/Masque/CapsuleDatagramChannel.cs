using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Masque;

/// <summary>
///     HTTP/2 capsule transport. A later QUIC datagram transport implements the same interface.
/// </summary>
internal sealed class CapsuleDatagramChannel : IHttpDatagramChannel
{
    private readonly ChannelReader<ReadOnlyMemory<byte>> inbound;
    private readonly Stream outbound;
    private readonly CapsuleReader reader = new();
    private readonly Channel<Pooled> toClient = Channel.CreateBounded<Pooled>(new BoundedChannelOptions(64)
    {
        // Wait + TryWrite drops the new datagram when the queue is full. DropWrite also drops it,
        // but TryWrite still returns true, so the tunnel cannot count the loss.
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = true,
    });
    private readonly byte[] encode = new byte[2048];

    public CapsuleDatagramChannel(ChannelReader<ReadOnlyMemory<byte>> inbound, Stream outbound)
    {
        this.inbound = inbound;
        this.outbound = outbound;
    }

    public int ParserDrops => reader.Dropped;

    public async ValueTask RunClientToTargetAsync(DatagramSend send, CancellationToken cancellationToken)
    {
        while (await inbound.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (inbound.TryRead(out var chunk))
            {
                reader.Feed(chunk.Span, payload => send(payload));
            }
        }
    }

    public bool TryQueueToClient(ReadOnlySpan<byte> payload)
    {
        var rented = ArrayPool<byte>.Shared.Rent(payload.Length);
        payload.CopyTo(rented);
        if (toClient.Writer.TryWrite(new Pooled(rented, payload.Length)))
        {
            return true;
        }

        ArrayPool<byte>.Shared.Return(rented);
        return false;
    }

    public async ValueTask WriteQueuedAsync(CancellationToken cancellationToken)
    {
        while (await toClient.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (toClient.Reader.TryRead(out var item))
            {
                try
                {
                    var written = CapsuleReader.WriteDatagram(encode, item.Bytes.AsSpan(0, item.Length));
                    if (written > 0)
                    {
                        await outbound.WriteAsync(encode.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(item.Bytes);
                }
            }
        }
    }

    public bool TryRead(out ReadOnlyMemory<byte> payload)
    {
        payload = default;
        return false;
    }

    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
        inbound.WaitToReadAsync(cancellationToken);

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var written = CapsuleReader.WriteDatagram(encode, payload.Span);
        if (written < 0)
        {
            return ValueTask.CompletedTask;
        }

        return outbound.WriteAsync(encode.AsMemory(0, written), cancellationToken);
    }

    private readonly record struct Pooled(byte[] Bytes, int Length);
}

internal delegate void DatagramSend(ReadOnlySpan<byte> payload);
