using System;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
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

    public bool DataAvailable => reader.DataAvailable;

    public async ValueTask<bool> FillBufferAsync(CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken);
        return await reader.FillBufferAsync(cancellationToken);
    }

    public byte ReadByteFromBuffer()
    {
        var b = reader.ReadByteFromBuffer();
        buffer[bufferLength++] = b;
        ReadBytes++;
        return b;
    }

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        return HttpStream.ReadLineInternalAsync(this, bufferPool, cancellationToken);
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

    protected virtual void Dispose(bool disposing)
    {
        if (disposed) return;

        // Return the pooled buffer on both the normal Dispose and the finalizer path.
        // ArrayPool.Return is thread-safe, and the buffer/bufferPool references remain
        // reachable via this instance until it is collected, so this is safe from a finalizer.
        // This prevents leaking a rented buffer if the stream is ever finalized without disposal.
        bufferPool.ReturnBuffer(buffer);

        disposed = true;
    }

    ~CopyStream()
    {
        Logging.ProxyDiagnostics.ReportUndisposedFinalizer(null, nameof(CopyStream));

        Dispose(false);
    }
}