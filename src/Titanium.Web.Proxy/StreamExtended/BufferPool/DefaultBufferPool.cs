using System.Buffers;
using System.Collections.Concurrent;

namespace Titanium.Web.Proxy.StreamExtended.BufferPool
{

    /// <summary>
    ///     A concrete IBufferPool implementation using a thread-safe stack.
    ///     Works well when all consumers ask for buffers with the same size.
    ///     If your application would use variable size buffers consider implementing IBufferPool using System.Buffers library from Microsoft.
    /// </summary>
    public class DefaultBufferPool : IBufferPool
    {
        /// <summary>
        /// Gets a buffer.
        /// </summary>
        /// <param name="bufferSize">Size of the buffer.</param>
        /// <returns></returns>
        public byte[] GetBuffer(int bufferSize)
        {
            return ArrayPool<byte>.Shared.Rent(bufferSize);
        }

        /// <summary>
        /// Returns the buffer.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        public void ReturnBuffer(byte[] buffer)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        public void Dispose()
        {
        }
    }
}
