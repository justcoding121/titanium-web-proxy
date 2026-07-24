using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy;

/// <summary>
///     Reassembles raw bytes relayed after a WebSocket upgrade (see
///     <c>SessionEventArgs.WebSocketDecoderSend</c>/<c>WebSocketDecoderReceive</c>, fed from the session's
///     <c>DataSent</c>/<c>DataReceived</c> events) into individual <see cref="WebSocketFrame" />s, handling
///     frames split across multiple reads and unmasking masked (client-to-server) payloads. Use one
///     instance per direction of a single connection - frames may span calls, so state from one call feeds
///     the next.
/// </summary>
public class WebSocketDecoder
{
    private byte[] buffer;

    private long bufferLength;

    internal WebSocketDecoder(IBufferPool bufferPool)
    {
        buffer = new byte[bufferPool.BufferSize];
    }

    /// <summary>
    ///     Decodes as many complete frames as <paramref name="data" /> currently contains (0 or more - any
    ///     trailing partial frame is buffered internally and completed by a later call).
    /// </summary>
    /// <remarks>
    ///     Every yielded <see cref="WebSocketFrame" />'s <see cref="WebSocketFrame.Data" /> is a zero-copy
    ///     slice of either <paramref name="data" /> itself or this decoder's internal reassembly buffer -
    ///     see the remarks on <see cref="WebSocketFrame" />. In particular, do not retain frames across
    ///     separate calls to this method on the same decoder instance without first copying out their data.
    /// </remarks>
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

            // The completeness check must also account for the 4-byte masking key (present right before
            // the payload whenever the mask bit is set) - otherwise, once just enough bytes have arrived
            // to cover the header/extended-length/payload but not yet the mask key, the slice below would
            // read past the end of the currently available data.
            var maskKeyLength = masked ? 4 : 0;
            if (data1.Length < idx + size + maskKeyLength) break;

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
        if (requiredLength > buffer.Length) Array.Resize(ref buffer, (int)Math.Max(requiredLength, buffer.Length * 2));

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