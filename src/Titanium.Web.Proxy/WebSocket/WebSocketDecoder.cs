using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy;

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

        var copied = false;
        if (bufferLength > 0)
        {
            // already have remaining data
            buffer = CopyToBuffer(buffer);
            copied = true;
        }

        while (true)
        {
            var data1 = buffer.Span;
            if (!IsDataEnough(data1)) break;

            var opCode = (WebsocketOpCode)(data1[0] & 0xf);
            var isFinal = (data1[0] & 0x80) != 0;
            var b = data1[1];
            long size = b & 0x7f;

            // todo: size > int.Max??

            var masked = (b & 0x80) != 0;

            var idx = 2;
            if (size > 125)
            {
                if (size == 126)
                {
                    size = (data1[2] << 8) + data1[3];
                    idx = 4;
                }
                else
                {
                    size = ((long)data1[2] << 56) + ((long)data1[3] << 48) + ((long)data1[4] << 40) +
                           ((long)data1[5] << 32) +
                           ((long)data1[6] << 24) + (data1[7] << 16) + (data1[8] << 8) + data1[9];
                    idx = 10;
                }
            }

            if (data1.Length < idx + size) break;

            if (masked)
            {
                //mask = (uint)(((long)data1[idx++] << 24) + (data1[idx++] << 16) + (data1[idx++] << 8) + data1[idx++]);
                //mask = (uint)(data1[idx++] + (data1[idx++] << 8) + (data1[idx++] << 16) + ((long)data1[idx++] << 24));
                var uData = MemoryMarshal.Cast<byte, uint>(data1.Slice(idx, (int)size + 4));
                idx += 4;

                var mask = uData[0];
                var size1 = size;
                if (size > 4)
                {
                    uData = uData.Slice(1);
                    for (var i = 0; i < uData.Length; i++) uData[i] = uData[i] ^ mask;

                    size1 -= uData.Length * 4;
                }

                if (size1 > 0)
                {
                    var pos = (int)(idx + size - size1);
                    data1[pos] ^= (byte)mask;

                    if (size1 > 1) data1[pos + 1] ^= (byte)(mask >> 8);

                    if (size1 > 2) data1[pos + 2] ^= (byte)(mask >> 16);
                }
            }

            var frameData = buffer.Slice(idx, (int)size);
            var frame = new WebSocketFrame { IsFinal = isFinal, Data = frameData, OpCode = opCode };
            yield return frame;

            buffer = buffer.Slice((int)(idx + size));
        }

        if (!copied && buffer.Length > 0) CopyToBuffer(buffer);

        if (copied)
        {
            if (buffer.Length == 0)
            {
                bufferLength = 0;
            }
            else
            {
                buffer.CopyTo(this.buffer);
                bufferLength = buffer.Length;
            }
        }
    }

    private Memory<byte> CopyToBuffer(ReadOnlyMemory<byte> data)
    {
        var requiredLength = bufferLength + data.Length;
        if (requiredLength > buffer.Length) Array.Resize(ref buffer, (int)Math.Min(requiredLength, buffer.Length * 2));

        data.CopyTo(buffer.AsMemory((int)bufferLength));
        bufferLength += data.Length;
        return buffer.AsMemory(0, (int)bufferLength);
    }

    private static bool IsDataEnough(ReadOnlySpan<byte> data)
    {
        var length = data.Length;
        if (length < 2)
            return false;

        var size = data[1];
        if ((size & 0x80) != 0) // masked
            length -= 4;

        size &= 0x7f;

        if (size == 126)
        {
            if (length < 2) return false;
        }
        else if (size == 127)
        {
            if (length < 10) return false;
        }

        return length >= size;
    }
}