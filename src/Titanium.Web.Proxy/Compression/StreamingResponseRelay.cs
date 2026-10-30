using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Compression;

/// <summary>
///     Compresses an origin body into a client sink while it is relayed (<c>responseCompression.mode:
///     streaming</c>). One origin read is one compression flush, so a chunked origin that trickles data (for
///     example a long-poll or a progressive HTML page) is delivered as it arrives, not when the body ends.
///     Memory is bounded by the read buffer, the encoder window and one coalescing buffer, whatever the body size:
///     the sink write is awaited before the next origin read, so a slow client slows the origin down.
/// </summary>
internal static class StreamingResponseRelay
{
    /// <summary>Compressed bytes are handed to the sink in pieces of at most this size, or at each flush.</summary>
    internal const int SinkBufferBytes = 16 * 1024;

    /// <summary>The read buffer size used for the origin side.</summary>
    internal const int ReadBufferBytes = 32 * 1024;

    /// <summary>
    ///     Reads <paramref name="origin" /> to its end, compresses it with <paramref name="algorithm" /> and writes
    ///     the result to <paramref name="sink" />. Returns false, having stopped early and written nothing more,
    ///     when <paramref name="clientGone" /> reports that the client has gone away; the caller then closes the
    ///     origin connection. Throws <see cref="IOException" /> when the origin delivers fewer bytes than
    ///     <paramref name="expectedLength" /> (when it is not negative).
    /// </summary>
    /// <param name="origin">The decoded origin body (framing removed).</param>
    /// <param name="sink">Receives the compressed bytes. It is not completed or disposed here.</param>
    /// <param name="algorithm">Gzip or Brotli.</param>
    /// <param name="level">The compression level.</param>
    /// <param name="readBuffer">A caller-owned buffer used to read the origin.</param>
    /// <param name="expectedLength">The declared Content-Length, or -1 when the body has no declared length.</param>
    /// <param name="onOriginBytes">Observes each piece of origin bytes (data events, the response cache tee).</param>
    /// <param name="clientGone">Polled after each sink write.</param>
    /// <param name="cancellationToken">Cancels the relay.</param>
    internal static async Task<bool> RelayAsync(Stream origin, Stream sink, HttpCompression algorithm,
        CompressionLevel level, byte[] readBuffer, long expectedLength, Action<byte[], int, int>? onOriginBytes,
        Func<bool>? clientGone, CancellationToken cancellationToken)
    {
        var coalescer = new CoalescingSink(sink);
        var encoder = CompressionFactory.Create(algorithm, coalescer, level);
        var completed = false;
        try
        {
            long total = 0;
            while (true)
            {
                var read = await origin.ReadAsync(readBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                total += read;
                onOriginBytes?.Invoke(readBuffer, 0, read);

                await encoder.WriteAsync(readBuffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await encoder.FlushAsync(cancellationToken).ConfigureAwait(false);

                if (clientGone is not null && clientGone())
                    return false;
            }

            if (expectedLength >= 0 && total != expectedLength)
                throw new IOException(
                    $"The origin ended the body after {total} of {expectedLength} declared bytes.");

            // Writes the encoder trailer, then drains the coalescing buffer.
            await encoder.DisposeAsync().ConfigureAwait(false);
            await coalescer.FlushAsync(cancellationToken).ConfigureAwait(false);
            completed = true;
            return !(clientGone is not null && clientGone());
        }
        finally
        {
            if (!completed)
            {
                // Dropping the sink keeps the encoder's trailer from being written to a dead or half-written
                // response.
                coalescer.Abandon();
            }

            try
            {
                await encoder.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The abandoned sink took the trailer; nothing to report.
            }

            coalescer.Release();
        }
    }

    /// <summary>
    ///     Gathers the encoder's many small writes into pieces of up to <see cref="SinkBufferBytes" /> so each
    ///     reaches the sink (one HTTP chunk or a few HTTP/2 DATA frames) as a single write, and sends whatever is
    ///     pending when the encoder flushes. Write-only; synchronous members are not supported.
    /// </summary>
    private sealed class CoalescingSink : Stream
    {
        private readonly Stream inner;
        private byte[]? buffer;
        private int count;
        private bool abandoned;

        internal CoalescingSink(Stream inner)
        {
            this.inner = inner;
            buffer = ArrayPool<byte>.Shared.Rent(SinkBufferBytes);
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void Abandon()
        {
            abandoned = true;
            count = 0;
        }

        internal void Release()
        {
            var rented = buffer;
            buffer = null;
            count = 0;
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
        {
            if (abandoned || data.IsEmpty || buffer is null)
                return;

            if (count + data.Length > buffer.Length)
            {
                await SendPendingAsync(cancellationToken).ConfigureAwait(false);

                if (data.Length >= buffer.Length)
                {
                    await inner.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            data.CopyTo(buffer.AsMemory(count));
            count += data.Length;
        }

        public override Task WriteAsync(byte[] data, int offset, int length, CancellationToken cancellationToken) =>
            WriteAsync(data.AsMemory(offset, length), cancellationToken).AsTask();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (abandoned)
                return;

            await SendPendingAsync(cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask SendPendingAsync(CancellationToken cancellationToken)
        {
            if (count == 0 || buffer is null)
                return;

            var length = count;
            count = 0;
            await inner.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }

        public override void Flush() => throw new NotSupportedException("Use FlushAsync.");

        public override void Write(byte[] data, int offset, int length) =>
            throw new NotSupportedException("Use WriteAsync.");

        public override int Read(byte[] data, int offset, int length) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
