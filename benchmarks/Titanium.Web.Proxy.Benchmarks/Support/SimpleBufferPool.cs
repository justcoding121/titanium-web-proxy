using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.Benchmarks.Support;

/// <summary>
///     Minimal <see cref="IBufferPool" /> for benchmarks that need one to drive the real internal
///     line/parse code paths (<see cref="Titanium.Web.Proxy.Network.Streams.HttpStream.ReadLineInternalAsync" />
///     etc.) without depending on the library's internal <c>DefaultBufferPool</c>. Not pooling at all is
///     deliberate: allocation cost is exactly one of the things these benchmarks measure via
///     <c>[MemoryDiagnoser]</c>, and a real deployment always uses <c>DefaultBufferPool</c>, not this type.
/// </summary>
internal sealed class SimpleBufferPool : IBufferPool
{
    public int BufferSize { get; } = 8192;

    public byte[] GetBuffer() => new byte[BufferSize];

    public byte[] GetBuffer(int bufferSize) => new byte[bufferSize];

    public void ReturnBuffer(byte[] buffer)
    {
    }

    public void Dispose()
    {
    }
}
