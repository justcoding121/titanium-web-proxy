using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Helpers
{
    class CustomBufferedPeekStream
    {
        private readonly CustomBufferedStream baseStream;

        public int Position { get; private set; }

        public CustomBufferedPeekStream(CustomBufferedStream baseStream, int startPosition = 0)
        {
            this.baseStream = baseStream;
            Position = startPosition;
        }

        public int Available => baseStream.Available - Position;

        public async Task<bool> EnsureBufferLength(int length)
        {
            var val = await baseStream.PeekByteAsync(Position + length - 1);
            return val != -1;
        }

        public byte ReadByte()
        {
            return baseStream.PeekByteFromBuffer(Position++);
        }

        public int ReadInt16()
        {
            int i1 = ReadByte();
            int i2 = ReadByte();
            return (i1 << 8) + i2;
        }

        public int ReadInt24()
        {
            int i1 = ReadByte();
            int i2 = ReadByte();
            int i3 = ReadByte();
            return (i1 << 16) + (i2 << 8) + i3;
        }

        public byte[] ReadBytes(int length)
        {
            var buffer = new byte[length];
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = ReadByte();
            }

            return buffer;
        }
    }
}
