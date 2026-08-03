using System;
using System.Buffers;

namespace Titanium.Web.Proxy.StreamExtended.BufferPool;

/// <summary>
///     A concrete IBufferPool implementation backed by the shared <see cref="System.Buffers.ArrayPool{T}" />.
///     It is thread-safe and handles both fixed and variable size buffer requests.
///     Note: rented buffers may be larger than the requested size (ArrayPool bucketing) and are not
///     cleared on return, so callers must not assume the buffer length equals the requested size.
/// </summary>
internal sealed class DefaultBufferPool : IBufferPool
{
    /// <summary>
    ///     Buffer size in bytes used throughout this proxy.
    ///     Default value is 8192 bytes.
    /// </summary>
    public int BufferSize { get; set; } = 8192;

    /// <summary>
    ///     Gets a buffer with a default size.
    /// </summary>
    /// <returns></returns>
    public byte[] GetBuffer()
    {
        return ArrayPool<byte>.Shared.Rent(BufferSize);
    }

    /// <summary>
    ///     Gets a buffer.
    /// </summary>
    /// <param name="bufferSize">Size of the buffer.</param>
    /// <returns></returns>
    public byte[] GetBuffer(int bufferSize)
    {
        return ArrayPool<byte>.Shared.Rent(bufferSize);
    }

    /// <summary>
    ///     Returns the buffer.
    /// </summary>
    /// <param name="buffer">The buffer.</param>
    public void ReturnBuffer(byte[] buffer)
    {
        ArrayPool<byte>.Shared.Return(buffer);
    }

    public void Dispose() // NOSONAR CA1822 -- IDisposable contract requires an instance member.
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        // Nothing to dispose; required for IBufferPool.
    }
}