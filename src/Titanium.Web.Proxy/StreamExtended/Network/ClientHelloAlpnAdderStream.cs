using System.Diagnostics;
using System.IO;
using System.Threading;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.StreamExtended.Network
{
    public class ClientHelloAlpnAdderStream : Stream
    {
        private readonly CustomBufferedStream stream;
        private readonly IBufferPool bufferPool;

        private bool called;

        public ClientHelloAlpnAdderStream(CustomBufferedStream stream, IBufferPool bufferPool)
        {
            this.stream = stream;
        }

        public override void Flush()
        {
            stream.Flush();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return stream.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            stream.SetLength(value);
        }

        [DebuggerStepThrough]
        public override int Read(byte[] buffer, int offset, int count)
        {
            return stream.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (called)
            {
                stream.Write(buffer, offset, count);
                return;
            }

            called = true;
            var ms = new MemoryStream(buffer, offset, count);

            //this can be non async, because reads from a memory stream
            var cts = new CancellationTokenSource();
            var clientHello = SslTools.PeekClientHello(new CustomBufferedStream(ms, bufferPool, (int)ms.Length), bufferPool, cts.Token).Result;
            if (clientHello != null)
            {
                // 0x00 0x10: ALPN identifier
                // 0x00 0x0e: length of ALPN data
                // 0x00 0x0c: length of ALPN data again:)
                var dataToAdd = new byte[]
                {
                    0x0, 0x10, 0x0, 0xE, 0x0, 0xC,
                    2, (byte)'h', (byte)'2',
                    8, (byte)'h', (byte)'t', (byte)'t', (byte)'p', (byte)'/', (byte)'1', (byte)'.', (byte)'1'
                };

                int newByteCount = clientHello.Extensions == null ? dataToAdd.Length + 2 : dataToAdd.Length;
                var buffer2 = new byte[buffer.Length + newByteCount];

                for (int i = 0; i < buffer.Length; i++)
                {
                    buffer2[i] = buffer[i];
                }

                //this is a hacky solution, but works
                int length = (buffer[offset + 3] << 8) + buffer[offset + 4];
                length += newByteCount;
                buffer2[offset + 3] = (byte)(length >> 8);
                buffer2[offset + 4] = (byte)length;

                length = (buffer[offset + 6] << 16) + (buffer[offset + 7] << 8) + buffer[offset + 8];
                length += newByteCount;
                buffer2[offset + 6] = (byte)(length >> 16);
                buffer2[offset + 7] = (byte)(length >> 8);
                buffer2[offset + 8] = (byte)length;

                int pos = offset + clientHello.EntensionsStartPosition;
                int endPos = offset + clientHello.ClientHelloLength;
                if (clientHello.Extensions != null)
                {
                    // update ALPN length
                    length = (buffer[pos] << 8) + buffer[pos + 1];
                    length += newByteCount;
                    buffer2[pos] = (byte)(length >> 8);
                    buffer2[pos + 1] = (byte)length;
                }
                else
                {
                    // add ALPN length
                    length = dataToAdd.Length;
                    buffer2[pos] = (byte)(length >> 8);
                    buffer2[pos + 1] = (byte)length;
                    endPos += 2;
                }

                for (int i = 0; i < dataToAdd.Length; i++)
                {
                    buffer2[endPos + i] = dataToAdd[i];
                }

                // copy the reamining data if any
                for (int i = clientHello.ClientHelloLength; i < count; i++)
                {
                    buffer2[offset + newByteCount + i] = buffer[offset + i];
                }

                buffer = buffer2;
                count += newByteCount;
            }

            stream.Write(buffer, offset, count);
        }

        public override bool CanRead => stream.CanRead;

        public override bool CanSeek => stream.CanSeek;

        public override bool CanWrite => stream.CanWrite;

        public override long Length => stream.Length;

        public override long Position
        {
            get => stream.Position;
            set => stream.Position = value;
        }
    }
}