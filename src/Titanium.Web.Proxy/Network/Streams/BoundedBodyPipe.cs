using System;
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
    ///     Initializes a new <see cref="BoundedBodyPipe"/> with the given byte limit.
    ///     <paramref name="maxBytes"/> of 0 means unlimited.
    /// </summary>
    internal BoundedBodyPipe(long maxBytes = 0)
    {
        this.maxBytes = maxBytes;
        // For unlimited pipes, disable backpressure (pauseWriterThreshold: 0) so that WriteAsync
        // never blocks waiting for a reader. For bounded pipes, cap backpressure at 512 KB or the
        // byte limit, whichever is smaller, to give callers early cancellation feedback.
        pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: maxBytes > 0 ? Math.Min(maxBytes, 512 * 1024) : 0,
            resumeWriterThreshold: maxBytes > 0 ? Math.Min(maxBytes, 256 * 1024) : 0,
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
    /// </summary>
    internal async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (maxBytes > 0)
        {
            var newTotal = totalWritten + buffer.Length;
            if (newTotal > maxBytes)
            {
                await pipe.Writer.CompleteAsync(new BodySizeLimitExceededException(
                    $"Body exceeds the configured limit of {maxBytes:N0} bytes."));
                throw new BodySizeLimitExceededException(
                    $"Body byte count {newTotal:N0} exceeds the limit of {maxBytes:N0}.");
            }
        }

        var result = await pipe.Writer.WriteAsync(buffer, cancellationToken);
        totalWritten += buffer.Length;

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
        await pipe.Reader.CopyToAsync(destination, cancellationToken);
        await pipe.Reader.CompleteAsync();
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
