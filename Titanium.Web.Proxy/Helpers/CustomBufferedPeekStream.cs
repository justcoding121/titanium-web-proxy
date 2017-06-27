using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Helpers
{
    class CustomBufferedPeekStream
    {
        private readonly CustomBufferedStream baseStream;
        private int position;

        public CustomBufferedPeekStream(CustomBufferedStream baseStream, int startPosition = 0)
        {
            this.baseStream = baseStream;
            position = startPosition;
        }

        public int Available => baseStream.Available - position;

        public async Task<bool> EnsureBufferLength(int length)
        {
            var val = await baseStream.PeekByteAsync(position + length - 1);
            return val != -1;
        }

        public byte ReadByte()
        {
            return baseStream.PeekByteFromBuffer(position++);
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
