using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.StreamExtended.BufferPool;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Benchmarks.Support;

/// <summary>
///     Feeds a fixed in-memory byte buffer through the real, internal
///     <see cref="HttpStream.ReadLineInternalAsync" /> line reader - the exact method
///     <c>HttpClientStream</c>/<c>HttpServerStream</c> delegate to on the wire - so header and
///     chunk-size-line benchmarks measure the production parsing code path rather than a
///     benchmark-only reimplementation of it. Reusable across iterations via <see cref="Reset" />
///     so each BenchmarkDotNet invocation only pays for parsing, not buffer setup.
/// </summary>
internal sealed class InMemoryLineStream : ILineStream
{
    private readonly byte[] data;
    private readonly IBufferPool bufferPool;
    private int position;

    public InMemoryLineStream(byte[] data, IBufferPool bufferPool)
    {
        this.data = data;
        this.bufferPool = bufferPool;
    }

    public bool DataAvailable => position < data.Length;

    public ValueTask<bool> FillBufferAsync(CancellationToken cancellationToken = default)
        => new(position < data.Length);

    public byte ReadByteFromBuffer() => data[position++];

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
        => HttpStream.ReadLineInternalAsync(this, bufferPool, cancellationToken);

    public void Reset() => position = 0;
}
