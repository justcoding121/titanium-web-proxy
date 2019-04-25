using System.Collections.Concurrent;

namespace StreamExtended
{

    /// <summary>
    ///     A concrete IBufferPool implementation using a thread-safe stack.
    ///     Works well when all consumers ask for buffers with the same size.
    ///     If your application would use variable size buffers consider implementing IBufferPool using System.Buffers library from Microsoft.
    /// </summary>
    public class DefaultBufferPool : IBufferPool
    {
        private readonly ConcurrentStack<byte[]> buffers = new ConcurrentStack<byte[]>();

        /// <summary>
        /// Gets a buffer.
        /// </summary>
        /// <param name="bufferSize">Size of the buffer.</param>
        /// <returns></returns>
        public byte[] GetBuffer(int bufferSize)
        {
            if (!buffers.TryPop(out var buffer) || buffer.Length != bufferSize)
            {
                buffer = new byte[bufferSize];
            }

            return buffer;
        }

        /// <summary>
        /// Returns the buffer.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        public void ReturnBuffer(byte[] buffer)
        {
            if (buffer != null)
            {
                buffers.Push(buffer);
            }
        }

        public void Dispose()
        {
            buffers.Clear();
        }
    }
}
