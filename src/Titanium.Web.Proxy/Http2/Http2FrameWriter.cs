using System;
using System.Buffers;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     Single dedicated writer for one HTTP/2 socket direction. Producers enqueue already-framed
///     rented buffers; one background task drains the channel, optionally coalescing contiguous
///     writes into one <see cref="System.IO.Stream.WriteAsync(System.ReadOnlyMemory{byte}, CancellationToken)" />
///     to cut syscalls. When a <see cref="SemaphoreSlim" /> is supplied, drain acquires it around each
///     socket write so control-frame paths that still use the lock cannot interleave bytes.
/// </summary>
internal sealed class Http2FrameWriter : IAsyncDisposable
{
    private const int CoalesceByteBudget = 32 * 1024;
    private const int CoalesceMaxFrames = 32;

    private readonly Channel<ArraySegment<byte>> channel;
    private readonly System.IO.Stream output;
    private readonly SemaphoreSlim? writeLock;
    private readonly CancellationTokenSource cts = new();
    private readonly Task drainTask;
    private int disposed;

    public Http2FrameWriter(System.IO.Stream output, SemaphoreSlim? writeLock = null)
    {
        this.output = output;
        this.writeLock = writeLock;
        channel = Channel.CreateUnbounded<ArraySegment<byte>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        drainTask = Task.Run(() => DrainAsync(cts.Token));
    }

    /// <summary>
    ///     Enqueues a rented buffer for write. Ownership transfers; the buffer is returned to
    ///     <see cref="ArrayPool{T}.Shared" /> after the write (or on writer fault/dispose).
    /// </summary>
    public void EnqueueRented(byte[] rented, int length)
    {
        if (disposed != 0)
        {
            ArrayPool<byte>.Shared.Return(rented);
            return;
        }

        if (!channel.Writer.TryWrite(new ArraySegment<byte>(rented, 0, length)))
            ArrayPool<byte>.Shared.Return(rented);
    }

    public Task Completion => drainTask;

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        var reader = channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var first))
                {
                    try
                    {
                        if (!reader.TryPeek(out _))
                        {
                            await WriteLockedAsync(first.AsMemory()).ConfigureAwait(false);
                            ArrayPool<byte>.Shared.Return(first.Array!);
                            continue;
                        }

                        var total = first.Count;
                        var frames = new ArraySegment<byte>[CoalesceMaxFrames];
                        frames[0] = first;
                        var count = 1;
                        while (count < CoalesceMaxFrames
                               && total < CoalesceByteBudget
                               && reader.TryRead(out var next))
                        {
                            frames[count++] = next;
                            total += next.Count;
                        }

                        if (count == 1)
                        {
                            await WriteLockedAsync(first.AsMemory()).ConfigureAwait(false);
                            ArrayPool<byte>.Shared.Return(first.Array!);
                        }
                        else
                        {
                            var coalesced = ArrayPool<byte>.Shared.Rent(total);
                            try
                            {
                                var pos = 0;
                                for (var i = 0; i < count; i++)
                                {
                                    frames[i].AsSpan().CopyTo(coalesced.AsSpan(pos));
                                    pos += frames[i].Count;
                                    ArrayPool<byte>.Shared.Return(frames[i].Array!);
                                    frames[i] = default;
                                }

                                await WriteLockedAsync(coalesced.AsMemory(0, total)).ConfigureAwait(false);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(coalesced);
                                for (var i = 0; i < count; i++)
                                {
                                    if (frames[i].Array != null)
                                        ArrayPool<byte>.Shared.Return(frames[i].Array!);
                                }
                            }
                        }
                    }
                    catch
                    {
                        if (first.Array != null)
                            ArrayPool<byte>.Shared.Return(first.Array);
                        throw;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        finally
        {
            while (reader.TryRead(out var leftover))
            {
                if (leftover.Array != null)
                    ArrayPool<byte>.Shared.Return(leftover.Array);
            }
        }
    }

    private async Task WriteLockedAsync(ReadOnlyMemory<byte> memory)
    {
        if (writeLock != null)
            await writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(memory, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            writeLock?.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        channel.Writer.TryComplete();
        try { cts.Cancel(); }
        catch { /* ignore */ }

        try { await drainTask.ConfigureAwait(false); }
        catch { /* drain may fault if socket already closed */ }

        cts.Dispose();
    }
}
