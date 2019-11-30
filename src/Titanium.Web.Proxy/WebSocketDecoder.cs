using System;
using System.Collections.Generic;
using System.Linq;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy
{
    public class WebSocketDecoder
    {
        private byte[] buffer;

        private long bufferLength;

        internal WebSocketDecoder(IBufferPool bufferPool)
        {
            buffer = new byte[bufferPool.BufferSize];
        }

        public IEnumerable<WebSocketFrame> Decode(byte[] data, int offset, int count)
        {
            var buffer = data.AsMemory(offset, count);

            bool copied = false;
            if (bufferLength > 0)
            {
                // already have remaining data
                buffer = copyToBuffer(buffer);
                copied = true;
            }

            while (true)
            {
                var data1 = buffer.Span;
                if (!isDataEnough(data1))
                {
                    break;
                }

                var opCode = (WebsocketOpCode)(data1[0] & 0xf);
                byte b = data1[1];
                long size = b & 0x7f;

                // todo: size > int.Max??
                
                bool masked = (b & 0x80) != 0;

                int idx = 2;
                if (size > 125)
                {
                    if (size == 126)
                    {
                        size = (data1[2] << 8) + data1[3];
                        idx = 4;
                    }
                    else
                    {
                        size = ((long)data1[2] << 56) + ((long)data1[3] << 48) + ((long)data1[4] << 40) + ((long)data1[5] << 32) +
                               ((long)data1[6] << 24) + (data1[7] << 16) + (data1[8] << 8) + data1[9];
                        idx = 10;
                    }
                }

                uint mask = 0;
                if (masked)
                {
                    //mask = (uint)(((long)data1[idx++] << 24) + (data1[idx++] << 16) + (data1[idx++] << 8) + data1[idx++]);
                    mask = (uint)(data1[idx++] + (data1[idx++] << 8) + (data1[idx++] << 16) + ((long)data1[idx++] << 24));
                }

                if (masked)
                {
                    uint m = mask;
                    for (int i = 0; i < size; i++)
                    {
                        data[i + idx] = (byte)(data1[i + idx] ^ (byte)mask);

                        m >>= 8;

                        if (m == 0)
                            m = mask;
                    }
                }

                var frameData = buffer.Slice(idx, (int)size);
                var frame = new WebSocketFrame { Data = frameData, OpCode = opCode };
                yield return frame;

                buffer = buffer.Slice((int)(idx + size));
            }

            if (!copied && buffer.Length > 0)
            {
                copyToBuffer(buffer);
            }
        }

        private Memory<byte> copyToBuffer(ReadOnlyMemory<byte> data)
        {
            long requiredLength = bufferLength + data.Length;
            if (requiredLength > buffer.Length)
            {
                Array.Resize(ref buffer, (int)Math.Min(requiredLength, buffer.Length * 2));
            }

            data.CopyTo(buffer.AsMemory((int)bufferLength));
            bufferLength += data.Length;
            return buffer.AsMemory(0, (int)bufferLength);
        }

        private static bool isDataEnough(ReadOnlySpan<byte> data)
        {
            int length = data.Length;
            if (length < 2)
                return false;

            byte size = data[1];
            if ((size & 0x80) != 0) // masked
                length -= 4;

            size &= 0x7f;

            if (size == 126)
            {
                if (length < 2)
                {
                    return false;
                }
            }
            else if (size == 127)
            {
                if (length < 10)
                {
                    return false;
                }
            }

            return length >= size;
        }
    }
}
