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
///     to cut syscalls.
///     <para>
///         When constructed without a lock the drain is the exclusive socket writer (exclusive drain model):
///         it never takes a lock around <c>WriteAsync</c>. Pass a <see cref="SemaphoreSlim" /> only
///         when mixed direct-locked writes still exist on the same stream (client MITM path).
///     </para>
/// </summary>
internal sealed class Http2FrameWriter : IAsyncDisposable
{
    // Enough for HEADERS + a full 256 KiB known-CL body (sixteen 16 KiB DATA frames) in one
    // SslStream write after flatten. Smaller budgets force several coalesce passes mid-body and
    // left cool 256 KiB H2→H1 at ~0.84× while 64 KiB was already at parity with an 80 KiB budget.
    private const int CoalesceByteBudget = 288 * 1024;
    private const int CoalesceMaxFrames = 64;

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
        drainTask = Task.Run(() => DrainAsync(cts.Token), cts.Token);
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

    private async Task DrainAsync(CancellationToken cancellationToken) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
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
                            await WriteLockedAsync(first.AsMemory(), cancellationToken).ConfigureAwait(false);
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
                            await WriteLockedAsync(first.AsMemory(), cancellationToken).ConfigureAwait(false);
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

                                await WriteLockedAsync(coalesced.AsMemory(0, total), cancellationToken).ConfigureAwait(false);
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

    private async Task WriteLockedAsync(ReadOnlyMemory<byte> memory, CancellationToken cancellationToken)
    {
        if (writeLock != null)
            await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(memory, cancellationToken).ConfigureAwait(false);
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

        // Complete first so the drain writes already-queued frames. Cancelling immediately
        // races WaitToReadAsync and drops leftovers (bytes never hit the socket).
        channel.Writer.TryComplete();
        try
        {
            await drainTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            try { await cts.CancelAsync(); }
            catch { /* ignore */ }

            try { await drainTask.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false); }
            catch { /* drain may fault if socket already closed */ }
        }
        catch
        {
            /* drain may fault if socket already closed */
        }

        try { await cts.CancelAsync(); }
        catch { /* ignore */ }

        cts.Dispose();
    }
}
