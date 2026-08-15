using System;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.StreamExtended.Network;

/// <summary>
///     Copies the source stream to destination stream.
///     But this let users to peek and read the copying process.
/// </summary>
internal class CopyStream : ILineStream, IDisposable
{
    private readonly byte[] buffer;

    private readonly IBufferPool bufferPool;
    private readonly IHttpStreamReader reader;

    private readonly IHttpStreamWriter writer;

    private int bufferLength;

    private bool disposed;

    public CopyStream(IHttpStreamReader reader, IHttpStreamWriter writer, IBufferPool bufferPool)
    {
        this.reader = reader;
        this.writer = writer;
        buffer = bufferPool.GetBuffer();
        this.bufferPool = bufferPool;
    }

    public long ReadBytes { get; private set; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposed) return;

        // Only return pooled buffers on the explicit Dispose path. There is no finalizer:
        // ArrayPool.Return from a finalizer would touch managed objects after an undefined GC order.
        if (disposing)
        {
            bufferPool.ReturnBuffer(buffer);
        }

        disposed = true;
    }

    /// <summary>
    ///     True when the source still has buffered bytes <em>and</em> this copy buffer can accept
    ///     another byte. Returning false when the copy buffer is full forces callers that use the
    ///     usual <c>DataAvailable || FillBufferAsync</c> loop into <see cref="FillBufferAsync" />,
    ///     which flushes before reading more — preventing <see cref="ReadByteFromBuffer" /> overflow
    ///     when the source's rented array is larger than this copy buffer.
    /// </summary>
    public bool DataAvailable => reader.DataAvailable && bufferLength < buffer.Length;

    public async ValueTask<bool> FillBufferAsync(CancellationToken cancellationToken = default)
    {
        // Flush before pulling more source data. ArrayPool may rent a larger array for the
        // underlying HttpStream than for this copy buffer, so Available can exceed buffer.Length;
        // FlushAsync also runs whenever the copy buffer is full (see ReadByteFromBuffer).
        await FlushAsync(cancellationToken);

        // If the source still has buffered bytes, we are ready to read again without filling.
        // Calling FillBufferAsync on a full HttpStream buffer returns false (no room to read more),
        // which would incorrectly look like EOF while unread source data remains.
        if (reader.DataAvailable) return true;

        return await reader.FillBufferAsync(cancellationToken);
    }

    public byte ReadByteFromBuffer()
    {
        if (bufferLength >= buffer.Length)
            throw new InvalidOperationException(
                "CopyStream buffer is full; call FillBufferAsync (which flushes) before reading more bytes.");

        var b = reader.ReadByteFromBuffer();
        buffer[bufferLength++] = b;
        ReadBytes++;
        return b;
    }

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        return HttpStream.ReadLineInternalAsync(this, bufferPool, cancellationToken,
            ProxyResourceLimits.Default.MaxHeaderLineBytes);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        // send out the current data from from the buffer
        if (bufferLength > 0)
        {
            await writer.WriteAsync(buffer, 0, bufferLength, cancellationToken);
            bufferLength = 0;
        }
    }
}
