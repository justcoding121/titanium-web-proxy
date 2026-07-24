using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

/// <summary>
///     Frame-level read/write helpers shared by <see cref="Http2RawOriginServer" />, built directly on the
///     proxy's own internal <see cref="Http2FrameType" />/<see cref="Http2FrameFlag" />/HPACK
///     <see cref="Encoder" />/<see cref="Decoder" /> types (accessible here via InternalsVisibleTo) so tests
///     get real, protocol-accurate framing without re-implementing HPACK.
/// </summary>
internal static class Http2RawFrame
{
    public readonly record struct Frame(Http2FrameType Type, int StreamId, Http2FrameFlag Flags, byte[] Payload);

    public static async Task WriteAsync(Stream stream, Http2FrameType type, int streamId, Http2FrameFlag flags,
        byte[] payload)
    {
        var header = new byte[9];
        var length = payload.Length;
        header[0] = (byte)((length >> 16) & 0xff);
        header[1] = (byte)((length >> 8) & 0xff);
        header[2] = (byte)(length & 0xff);
        header[3] = (byte)type;
        header[4] = (byte)flags;
        header[5] = (byte)((streamId >> 24) & 0x7f);
        header[6] = (byte)((streamId >> 16) & 0xff);
        header[7] = (byte)((streamId >> 8) & 0xff);
        header[8] = (byte)(streamId & 0xff);

        await stream.WriteAsync(header, 0, header.Length);
        if (length > 0)
        {
            await stream.WriteAsync(payload, 0, length);
        }
    }

    public static async Task<Frame> ReadAsync(Stream stream)
    {
        var header = new byte[9];
        await ReadExactAsync(stream, header, 0, header.Length);

        int length = (header[0] << 16) + (header[1] << 8) + header[2];
        var type = (Http2FrameType)header[3];
        var flags = (Http2FrameFlag)header[4];
        int streamId = ((header[5] & 0x7f) << 24) + (header[6] << 16) + (header[7] << 8) + header[8];

        var payload = new byte[length];
        if (length > 0)
        {
            await ReadExactAsync(stream, payload, 0, length);
        }

        return new Frame(type, streamId, flags, payload);
    }

    public static async Task ReadExactAsync(Stream stream, byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer, offset, count);
            if (read == 0)
            {
                throw new EndOfStreamException("The peer closed the connection before the expected bytes arrived.");
            }

            offset += read;
            count -= read;
        }
    }

    /// <summary>
    ///     Encodes the given pseudo-headers (sent first, without static-table name reuse suppression - not
    ///     needed for a short-lived, single-purpose test encoder) followed by regular headers, using a fresh
    ///     <see cref="Encoder" />. A fresh encoder per connection is fine here: unlike the proxy itself, these
    ///     tests do not need to exercise dynamic-table reuse across many messages.
    /// </summary>
    public static byte[] EncodeHeaderBlock(Encoder encoder, IEnumerable<(string Name, string Value)> pseudoHeaders,
        IEnumerable<(string Name, string Value)> headers)
    {
        var ms = new MemoryStream();
        var writer = new BinaryWriter(ms);

        foreach (var (name, value) in pseudoHeaders)
        {
            encoder.EncodeHeader(writer, name.GetByteString(), value.GetByteString(), false,
                HpackUtil.IndexType.None, false);
        }

        foreach (var (name, value) in headers)
        {
            encoder.EncodeHeader(writer, name.GetByteString(), value.GetByteString());
        }

        return ms.ToArray();
    }

    private sealed class RecordingHeaderListener : IHeaderListener
    {
        public readonly List<(string Name, string Value)> Headers = new();

        public void AddHeader(ByteString name, ByteString value, bool sensitive)
        {
            Headers.Add((name.GetString(), value.GetString()));
        }
    }

    public static List<(string Name, string Value)> DecodeHeaderBlock(Decoder decoder, byte[] compressed)
    {
        var listener = new RecordingHeaderListener();
        decoder.Decode(new BinaryReader(new MemoryStream(compressed)), listener);
        decoder.EndHeaderBlock();
        return listener.Headers;
    }

    /// <summary>
    ///     One accepted, already-TLS/ALPN/preface-established raw HTTP/2 connection.
    /// </summary>
    public sealed class Connection
    {
        private readonly Stream stream;
        private readonly Encoder encoder = new(4096);
        private readonly Decoder decoder = new(8192, 4096);

        public Connection(Stream stream)
        {
            this.stream = stream;
        }

        public Task WriteFrameAsync(Http2FrameType type, int streamId, Http2FrameFlag flags, byte[] payload)
        {
            return WriteAsync(stream, type, streamId, flags, payload);
        }

        public Task<Frame> ReadFrameAsync()
        {
            return ReadAsync(stream);
        }

        public byte[] EncodeHeaders(IEnumerable<(string Name, string Value)> pseudoHeaders,
            IEnumerable<(string Name, string Value)> headers)
        {
            return EncodeHeaderBlock(encoder, pseudoHeaders, headers);
        }

        /// <summary>
        ///     Same as <see cref="EncodeHeaders" /> but first emits an HPACK Dynamic Table Size Update
        ///     (RFC 7541 §6.3) growing this connection's encoder to <paramref name="newMaxHeaderTableSize" />,
        ///     as a real HTTP/2 stack does once it starts using more of the header-table budget the peer's
        ///     SETTINGS_HEADER_TABLE_SIZE allows - used to deterministically reproduce/regress the proxy's
        ///     HPACK decoder-sizing bug (it must size its decoder from what it forwarded to this peer as the
        ///     *other* peer's declared table size, not from what this peer itself declared about its own
        ///     receive budget).
        /// </summary>
        public byte[] EncodeHeadersWithTableSizeUpdate(int newMaxHeaderTableSize,
            IEnumerable<(string Name, string Value)> pseudoHeaders, IEnumerable<(string Name, string Value)> headers)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            encoder.SetMaxHeaderTableSize(writer, newMaxHeaderTableSize);

            foreach (var (name, value) in pseudoHeaders)
            {
                encoder.EncodeHeader(writer, name.GetByteString(), value.GetByteString(), false,
                    HpackUtil.IndexType.None, false);
            }

            foreach (var (name, value) in headers)
            {
                encoder.EncodeHeader(writer, name.GetByteString(), value.GetByteString());
            }

            return ms.ToArray();
        }

        public List<(string Name, string Value)> DecodeHeaders(byte[] compressed)
        {
            return DecodeHeaderBlock(decoder, compressed);
        }

        /// <summary>
        ///     Sends an initial (possibly empty) SETTINGS frame, as any real HTTP/2 endpoint must as its
        ///     first frame - the real client on the other side of the proxy relay expects one before it will
        ///     consider the connection usable.
        /// </summary>
        public Task SendInitialSettingsAsync()
        {
            return WriteFrameAsync(Http2FrameType.Settings, 0, 0, Array.Empty<byte>());
        }

        /// <summary>
        ///     Same as <see cref="SendInitialSettingsAsync()" /> but declares a specific
        ///     SETTINGS_HEADER_TABLE_SIZE (RFC 7540 §6.5.2) instead of omitting it (i.e. relying on the
        ///     protocol default of 4096) - real browsers (e.g. Chrome) advertise a larger value here, which
        ///     the proxy must forward transparently to the other leg for that leg's HPACK decoder sizing.
        /// </summary>
        public Task SendInitialSettingsAsync(int headerTableSize)
        {
            var payload = new byte[6];
            payload[0] = (byte)(((int)Http2SettingsId.HeaderTableSize >> 8) & 0xff);
            payload[1] = (byte)((int)Http2SettingsId.HeaderTableSize & 0xff);
            payload[2] = (byte)((headerTableSize >> 24) & 0xff);
            payload[3] = (byte)((headerTableSize >> 16) & 0xff);
            payload[4] = (byte)((headerTableSize >> 8) & 0xff);
            payload[5] = (byte)(headerTableSize & 0xff);
            return WriteFrameAsync(Http2FrameType.Settings, 0, 0, payload);
        }

        /// <summary>
        ///     Reads frames until the request HEADERS block (assumed to fit in one frame - true for the
        ///     small test requests these tests send) and any DATA frames are fully consumed through
        ///     END_STREAM. SETTINGS/WINDOW_UPDATE/PING frames encountered along the way are ignored.
        /// </summary>
        public async Task<(int StreamId, List<(string Name, string Value)> Headers, byte[] Body)> ReadRequestAsync()
        {
            int streamId = -1;
            List<(string Name, string Value)> requestHeaders = null;
            var body = new MemoryStream();

            while (true)
            {
                var frame = await ReadFrameAsync();
                if (frame.Type == Http2FrameType.Headers)
                {
                    streamId = frame.StreamId;
                    requestHeaders = DecodeHeaders(frame.Payload);
                    if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    {
                        break;
                    }
                }
                else if (frame.Type == Http2FrameType.Data && frame.StreamId == streamId)
                {
                    body.Write(frame.Payload, 0, frame.Payload.Length);
                    if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    {
                        break;
                    }
                }
                else if (frame.Type == Http2FrameType.Continuation && frame.StreamId == streamId)
                {
                    // not expected for the small test requests these tests send; ignore defensively.
                }
            }

            return (streamId, requestHeaders, body.ToArray());
        }

        /// <summary>
        ///     Reads the next HEADERS (or PUSH_PROMISE) frame - skipping any interleaved SETTINGS,
        ///     WINDOW_UPDATE, PING or GOAWAY frames encountered while waiting for it, since those are
        ///     transparently relayed by the proxy and are not what the caller is looking for - then keeps
        ///     reading CONTINUATION frames until END_HEADERS, reassembling and decoding the full header
        ///     block. This mirrors (a simplified, test-only version of) the reassembly the proxy itself does
        ///     in <c>Http2Helper.CopyHttp2FrameAsync</c>, so it can be used on either side of the proxy to
        ///     observe the result of that reassembly/re-splitting.
        /// </summary>
        public async Task<(int StreamId, List<(string Name, string Value)> Headers, bool EndStream)>
            ReadHeaderBlockAsync()
        {
            Frame frame;
            do
            {
                frame = await ReadFrameAsync();
            } while (frame.Type != Http2FrameType.Headers && frame.Type != Http2FrameType.PushPromise);

            var streamId = frame.StreamId;
            var endStream = (frame.Flags & Http2FrameFlag.EndStream) != 0;
            var compressed = new MemoryStream();
            compressed.Write(frame.Payload, 0, frame.Payload.Length);

            while ((frame.Flags & Http2FrameFlag.EndHeaders) == 0)
            {
                frame = await ReadFrameAsync();
                if (frame.Type != Http2FrameType.Continuation || frame.StreamId != streamId)
                {
                    throw new InvalidOperationException(
                        $"Expected a CONTINUATION frame for stream {streamId} but got {frame.Type} for stream {frame.StreamId}.");
                }

                compressed.Write(frame.Payload, 0, frame.Payload.Length);
            }

            return (streamId, DecodeHeaders(compressed.ToArray()), endStream);
        }

        /// <summary>
        ///     Writes one already-HPACK-encoded header block as a HEADERS frame followed by as many
        ///     CONTINUATION frames as needed so that no single frame's payload exceeds
        ///     <paramref name="maxFrameSize" />, letting tests deliberately force the proxy's inbound
        ///     HEADERS/CONTINUATION reassembly path regardless of the encoded block's actual size.
        /// </summary>
        public async Task WriteHeaderBlockAsync(int streamId, byte[] compressed, bool endStream,
            int maxFrameSize = 16384)
        {
            var pos = 0;
            var first = true;
            do
            {
                var chunkLength = Math.Min(maxFrameSize, compressed.Length - pos);
                var isLast = pos + chunkLength >= compressed.Length;

                var flags = (Http2FrameFlag)0;
                if (isLast) flags |= Http2FrameFlag.EndHeaders;
                if (first && endStream) flags |= Http2FrameFlag.EndStream;

                var chunk = compressed.AsSpan(pos, chunkLength).ToArray();
                await WriteFrameAsync(first ? Http2FrameType.Headers : Http2FrameType.Continuation, streamId, flags,
                    chunk);

                pos += chunkLength;
                first = false;
            } while (pos < compressed.Length);
        }
    }
}
