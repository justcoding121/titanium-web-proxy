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

    private readonly long maxFramePayloadBytes;

    /// <param name="bufferPool">Sizes the initial internal reassembly buffer.</param>
    /// <param name="maxFramePayloadBytes">
    ///     Upper bound on a single frame's declared payload length, validated before any of that frame's
    ///     bytes are buffered. Defaults to <see cref="long.MaxValue" /> (no caller-configured limit; the
    ///     structural <see cref="int.MaxValue" /> bound below still applies) so existing call sites that
    ///     do not have a policy limit to enforce keep their previous behavior.
    /// </param>
    internal WebSocketDecoder(IBufferPool bufferPool, long maxFramePayloadBytes = long.MaxValue)
    {
        buffer = new byte[bufferPool.BufferSize];
        this.maxFramePayloadBytes = maxFramePayloadBytes;
    }

    /// <summary>
    ///     Decodes as many complete frames as <paramref name="data" /> currently contains (0 or more - any
    ///     trailing partial frame is buffered internally and completed by a later call).
    /// </summary>
    /// <remarks>
    ///     Every yielded <see cref="WebSocketFrame" />'s <see cref="WebSocketFrame.Data" /> is an owned
    ///     copy of the unmasked payload, safe to retain after this method returns. Remaining incomplete
    ///     frame bytes stay in this decoder's internal reassembly buffer for a later call.
    /// </remarks>
    public IEnumerable<WebSocketFrame> Decode(byte[] data, int offset, int count) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        var decodeBuffer = data.AsMemory(offset, count);

        var copied = false;
        if (bufferLength > 0)
        {
            // already have remaining data
            decodeBuffer = CopyToBuffer(decodeBuffer);
            copied = true;
        }

        while (true)
        {
            var data1 = decodeBuffer.Span;
            if (!IsDataEnough(data1)) break;

            var opCode = (WebsocketOpCode)(data1[0] & 0xf);
            var isFinal = (data1[0] & 0x80) != 0;
            var b = data1[1];
            long size = b & 0x7f;

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
                    // RFC 6455 section 5.2: "the most significant bit MUST be 0" for the 64-bit extended
                    // payload length. Check the raw byte before doing arithmetic on it below - a set high
                    // bit would otherwise just flip the sign of the shifted value rather than fail loudly.
                    if ((data1[2] & 0x80) != 0)
                        throw new WebSocketProtocolException(
                            "WebSocket frame declared a 64-bit payload length with the reserved high bit set.",
                            1002);

                    size = ((long)data1[2] << 56) + ((long)data1[3] << 48) + ((long)data1[4] << 40) +
                           ((long)data1[5] << 32) +
                           ((long)data1[6] << 24) + (data1[7] << 16) + (data1[8] << 8) + data1[9];
                    idx = 10;

                    // WebSocketFrame.Data is ultimately sliced with a 32-bit length (below), so a
                    // structurally valid but oversized 64-bit length can never be honored regardless of
                    // any configured limit.
                    if (size > int.MaxValue)
                        throw new WebSocketProtocolException(
                            $"WebSocket frame declared a payload length of {size:N0} bytes, which exceeds " +
                            $"the maximum of {int.MaxValue:N0} bytes this decoder can buffer.",
                            1002);
                }
            }

            // Validate the declared length before buffering a single byte of payload (see the
            // maxFramePayloadBytes remarks on the constructor): the completeness check just below waits
            // for the full declared length to arrive, and CopyToBuffer grows the reassembly buffer
            // without an upper bound to accumulate a frame that has not fully arrived yet. Checking here,
            // rather than after the frame is fully reassembled, is what keeps an attacker who declares an
            // oversized length and trickles bytes in slowly from forcing unbounded allocation.
            if (size > maxFramePayloadBytes)
                throw new WebSocketProtocolException(
                    $"WebSocket frame payload of {size:N0} bytes exceeds the configured limit of " +
                    $"{maxFramePayloadBytes:N0} bytes.",
                    1009);

            // The completeness check must also account for the 4-byte masking key (present right before
            // the payload whenever the mask bit is set) - otherwise, once just enough bytes have arrived
            // to cover the header/extended-length/payload but not yet the mask key, the slice below would
            // read past the end of the currently available data.
            var maskKeyLength = masked ? 4 : 0;
            if (data1.Length < idx + size + maskKeyLength) break;

            if (masked)
            {
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

            var frameData = decodeBuffer.Slice(idx, (int)size).ToArray();
            var frame = new WebSocketFrame { IsFinal = isFinal, Data = frameData, OpCode = opCode };
            yield return frame;

            decodeBuffer = decodeBuffer.Slice((int)(idx + size));
        }

        if (!copied && decodeBuffer.Length > 0) CopyToBuffer(decodeBuffer);

        if (copied)
        {
            if (decodeBuffer.Length == 0)
            {
                bufferLength = 0;
            }
            else
            {
                decodeBuffer.CopyTo(buffer);
                bufferLength = decodeBuffer.Length;
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

    /// <summary>
    ///     Reports whether enough bytes have arrived to parse the frame's length-encoding header (the
    ///     base 2 bytes, plus a 2- or 8-byte extended length field, plus a 4-byte mask key when present) -
    ///     not whether the payload itself has fully arrived. The payload-completeness check is separate
    ///     (in <see cref="Decode" />, once the real declared length is known) so that this gate can run,
    ///     and the declared-length validation immediately after it, using only the header bytes.
    /// </summary>
    /// <remarks>
    ///     A previous version of this check compared the bytes available against the raw 126/127
    ///     length-code marker byte instead of the actual number of header bytes needed (e.g. requiring
    ///     127 total bytes to be available before even attempting to parse a 64-bit extended length,
    ///     regardless of how small the header itself is). That accidentally left the declared-length
    ///     validation in <see cref="Decode" /> unreachable for a header delivered with little or no
    ///     payload alongside it - exactly the slow-trickle delivery pattern an attacker would use to
    ///     force unbounded reassembly-buffer growth while "waiting for more data".
    /// </remarks>
    private static bool IsDataEnough(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return false;

        var masked = (data[1] & 0x80) != 0;
        var sizeMarker = data[1] & 0x7f;

        var headerLength = 2;
        if (sizeMarker == 126) headerLength += 2;
        else if (sizeMarker == 127) headerLength += 8;

        if (masked) headerLength += 4;

        return data.Length >= headerLength;
    }
}