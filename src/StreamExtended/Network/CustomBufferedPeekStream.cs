using System.Threading;
using System.Threading.Tasks;

namespace StreamExtended.Network
{
    internal class CustomBufferedPeekStream : ICustomStreamReader
    {
        private readonly IBufferPool bufferPool;
        private readonly ICustomStreamReader baseStream;

        internal int Position { get; private set; }

        internal CustomBufferedPeekStream(ICustomStreamReader baseStream, IBufferPool bufferPool, int startPosition = 0)
        {
            this.bufferPool = bufferPool;
            this.baseStream = baseStream;
            Position = startPosition;
        }

        int ICustomStreamReader.BufferSize => baseStream.BufferSize;

        /// <summary>
        /// Gets a value indicating whether data is available.
        /// </summary>
        bool ICustomStreamReader.DataAvailable => Available > 0;

        /// <summary>
        /// Gets the available data size.
        /// </summary>
        public int Available => baseStream.Available - Position;

        internal async Task<bool> EnsureBufferLength(int length, CancellationToken cancellationToken)
        {
            var val = await baseStream.PeekByteAsync(Position + length - 1, cancellationToken);
            return val != -1;
        }

        internal byte ReadByte()
        {
            return baseStream.PeekByteFromBuffer(Position++);
        }

        internal int ReadInt16()
        {
            int i1 = ReadByte();
            int i2 = ReadByte();
            return (i1 << 8) + i2;
        }

        internal int ReadInt24()
        {
            int i1 = ReadByte();
            int i2 = ReadByte();
            int i3 = ReadByte();
            return (i1 << 16) + (i2 << 8) + i3;
        }

        internal byte[] ReadBytes(int length)
        {
            var buffer = new byte[length];
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = ReadByte();
            }

            return buffer;
        }

        /// <summary>
        /// Fills the buffer asynchronous.
        /// </summary>
        /// <returns></returns>
        Task<bool> ICustomStreamReader.FillBufferAsync(CancellationToken cancellationToken)
        {
            return baseStream.FillBufferAsync(cancellationToken);
        }

        /// <summary>
        /// Peeks a byte from buffer.
        /// </summary>
        /// <param name="index">The index.</param>
        /// <returns></returns>
        byte ICustomStreamReader.PeekByteFromBuffer(int index)
        {
            return baseStream.PeekByteFromBuffer(index);
        }

        /// <summary>
        /// Peeks bytes asynchronous.
        /// </summary>
        /// <param name="buffer">The buffer to copy.</param>
        /// <param name="offset">The offset where copying.</param>
        /// <param name="index">The index.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        Task<int> ICustomStreamReader.PeekBytesAsync(byte[] buffer, int offset, int index, int size, CancellationToken cancellationToken)
        {
            return baseStream.PeekBytesAsync(buffer, offset, index, size, cancellationToken);
        }

        /// <summary>
        /// Peeks a byte asynchronous.
        /// </summary>
        /// <param name="index">The index.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        Task<int> ICustomStreamReader.PeekByteAsync(int index, CancellationToken cancellationToken)
        {
            return baseStream.PeekByteAsync(index, cancellationToken);
        }

        /// <summary>
        /// Reads a byte from buffer.
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exception">Buffer is empty</exception>
        byte ICustomStreamReader.ReadByteFromBuffer()
        {
            return ReadByte();
        }

        int ICustomStreamReader.Read(byte[] buffer, int offset, int count)
        {
            return baseStream.Read(buffer, offset, count);
        }

        /// <summary>
        /// Reads the asynchronous.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        /// <param name="offset">The offset.</param>
        /// <param name="count">The count.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        Task<int> ICustomStreamReader.ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return baseStream.ReadAsync(buffer, offset, count, cancellationToken);
        }

        /// <summary>
        /// Read a line from the byte stream
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<string> ICustomStreamReader.ReadLineAsync(CancellationToken cancellationToken)
        {
            return CustomBufferedStream.ReadLineInternalAsync(this, bufferPool, cancellationToken);
        }

    }
}
