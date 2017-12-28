using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Network
{
    public class CustomBufferedPeekStream : IBufferedStream
    {
        private readonly CustomBufferedStream baseStream;

        internal int Position { get; private set; }

        public CustomBufferedPeekStream(CustomBufferedStream baseStream, int startPosition = 0)
        {
            this.baseStream = baseStream;
            Position = startPosition;
        }

        internal int Available => baseStream.Available - Position;

        bool IBufferedStream.DataAvailable => Available > 0;

        internal async Task<bool> EnsureBufferLength(int length)
        {
            var val = await baseStream.PeekByteAsync(Position + length - 1);
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

        Task<bool> IBufferedStream.FillBufferAsync()
        {
            return baseStream.FillBufferAsync();
        }

        byte IBufferedStream.ReadByteFromBuffer()
        {
            return ReadByte();
        }

        Task<int> IBufferedStream.ReadAsync(byte[] buffer, int offset, int count)
        {
            return baseStream.ReadAsync(buffer, offset, count);
        }
    }
}
