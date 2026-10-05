using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Network.Streams;

/// <summary>
///     A bounded, cancellation-aware pipe wrapping <see cref="System.IO.Pipelines.Pipe"/>
///     for streaming HTTP body bytes between producer and consumer tasks. Enforces a
///     configurable maximum total byte count; once exceeded the writer is faulted with
///     <see cref="BodySizeLimitExceededException"/>.
/// </summary>
internal sealed class BoundedBodyPipe : IDisposable
{
    private readonly long maxBytes;
    private readonly Pipe pipe;
    private long totalWritten;
    private bool disposed;

    /// <summary>
    ///     Invoked on the consumer thread with the number of body bytes just copied out of the pipe.
    ///     Used to return HTTP/2 stream flow-control credit as the client actually reads.
    /// </summary>
    internal Action<int>? OnBytesConsumed { get; set; }

    /// <summary>
    ///     Invoked once after the reader is completed (success or failure), so the caller can flush
    ///     any credit still owed for padding or a short final read.
    /// </summary>
    internal Action? OnReadCompleted { get; set; }

    /// <summary>
    ///     Initializes a new <see cref="BoundedBodyPipe"/> with the given byte limit.
    ///     <paramref name="maxBytes"/> of 0 means unlimited.
    /// </summary>
    /// <param name="applyBackpressure">
    ///     When <see langword="false"/>, <c>WriteAsync</c> never waits for a reader. Origin response
    ///     pipes use this so the shared HTTP/2 read loop cannot stall sibling streams. Memory is then
    ///     bounded by receive flow control, not by a pause threshold.
    /// </param>
    internal BoundedBodyPipe(long maxBytes = 0, bool applyBackpressure = true)
    {
        this.maxBytes = maxBytes;
        // pauseWriterThreshold 0 still pauses once the pipe holds data (unconsumed >= 0). Origin
        // response pipes pass applyBackpressure: false and use a threshold the stream window cannot
        // reach, so ReadLoop's WriteAsync completes synchronously.
        long pause;
        long resume;
        if (!applyBackpressure)
        {
            pause = long.MaxValue;
            resume = long.MaxValue - 1;
        }
        else if (maxBytes > 0)
        {
            pause = Math.Min(maxBytes, 512 * 1024);
            resume = Math.Min(maxBytes / 2, 256 * 1024);
            if (resume >= pause) resume = Math.Max(0, pause - 1);
        }
        else
        {
            pause = 0;
            resume = 0;
        }

        pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: pause,
            resumeWriterThreshold: resume,
            useSynchronizationContext: false));
    }

    /// <summary>Gets the reader end of the pipe (consumer).</summary>
    internal PipeReader Reader => pipe.Reader;

    /// <summary>Gets the writer end of the pipe (producer).</summary>
    internal PipeWriter Writer => pipe.Writer;

    /// <summary>Total bytes written so far.</summary>
    internal long TotalWritten => totalWritten;

    /// <summary>
    ///     Writes a chunk of body bytes to the pipe, enforcing the byte limit.
    ///     Throws <see cref="BodySizeLimitExceededException"/> if the limit is exceeded.
    ///     Completes synchronously when the underlying pipe does not apply backpressure
    ///     (unlimited pipes use <c>pauseWriterThreshold: 0</c>).
    /// </summary>
    internal ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (maxBytes > 0)
        {
            var newTotal = totalWritten + buffer.Length;
            if (newTotal > maxBytes)
            {
                var ex = new BodySizeLimitExceededException(
                    $"Body byte count {newTotal:N0} exceeds the limit of {maxBytes:N0}.");
                pipe.Writer.Complete(ex);
                return ValueTask.FromException(ex);
            }
        }

        var flushVt = pipe.Writer.WriteAsync(buffer, cancellationToken);
        if (flushVt.IsCompletedSuccessfully)
        {
            var result = flushVt.Result;
            totalWritten += buffer.Length;
            if (result.IsCanceled)
                return ValueTask.FromException(new OperationCanceledException(cancellationToken));
            return default;
        }

        return WriteAsyncSlow(flushVt, buffer.Length, cancellationToken);
    }

    private async ValueTask WriteAsyncSlow(ValueTask<FlushResult> flushVt, int byteCount,
        CancellationToken cancellationToken)
    {
        var result = await flushVt.ConfigureAwait(false);
        totalWritten += byteCount;
        if (result.IsCanceled)
            throw new OperationCanceledException(cancellationToken);
    }

    /// <summary>
    ///     Signals that no more bytes will be written. Consumers will see
    ///     <see cref="PipeReader.ReadAsync"/> return with <c>IsCompleted=true</c>
    ///     after draining all remaining data.
    /// </summary>
    internal void CompleteWriter(Exception? exception = null) => pipe.Writer.Complete(exception);

    /// <summary>
    ///     Signals that the consumer has finished reading. The producer will observe
    ///     <c>FlushResult.IsCompleted=true</c> on the next write.
    /// </summary>
    internal void CompleteReader(Exception? exception = null) => pipe.Reader.Complete(exception);

    /// <summary>
    ///     Copies all remaining bytes from this pipe's reader to <paramref name="destination"/>,
    ///     respecting <paramref name="cancellationToken"/>. Completes the reader when done.
    /// </summary>
    internal async Task CopyToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        try
        {
            while (true)
            {
                var result = await pipe.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                if (buffer.Length > 0)
                {
                    foreach (var segment in buffer)
                    {
                        await destination.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
                        if (segment.Length > 0)
                            OnBytesConsumed?.Invoke(segment.Length);
                    }

                    pipe.Reader.AdvanceTo(buffer.End);
                }
                else
                {
                    pipe.Reader.AdvanceTo(buffer.Start);
                }

                if (result.IsCompleted || result.IsCanceled)
                    break;
            }
        }
        finally
        {
            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            OnReadCompleted?.Invoke();
        }
    }

    /// <summary>
    ///     Reads exactly <paramref name="destination"/>.Length bytes (or fewer if the writer
    ///     completes early). Avoids <see cref="MemoryStream"/> + <c>ToArray</c> on the tiny
    ///     fixed-length origin body path. Completes the reader when done.
    /// </summary>
    /// <returns>Number of bytes written into <paramref name="destination"/>.</returns>
    internal async Task<int> ReadExactAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var offset = 0;
        while (offset < destination.Length)
        {
            var result = await pipe.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (buffer.Length == 0)
            {
                pipe.Reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                    break;
                continue;
            }

            var toCopy = (int)Math.Min(buffer.Length, destination.Length - offset);
            buffer.Slice(0, toCopy).CopyTo(destination.Span.Slice(offset, toCopy));
            offset += toCopy;
            if (toCopy > 0)
                OnBytesConsumed?.Invoke(toCopy);
            pipe.Reader.AdvanceTo(buffer.GetPosition(toCopy));
            if (result.IsCompleted && offset < destination.Length)
                break;
        }

        await pipe.Reader.CompleteAsync().ConfigureAwait(false);
        OnReadCompleted?.Invoke();
        return offset;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pipe.Writer.Complete();
        pipe.Reader.Complete();
    }
}

/// <summary>
///     Thrown when a body exceeds the configured <see cref="BoundedBodyPipe"/> size limit.
/// </summary>
internal sealed class BodySizeLimitExceededException : IOException
{
    internal BodySizeLimitExceededException(string message) : base(message) { }
}
