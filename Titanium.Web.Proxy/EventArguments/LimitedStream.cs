using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using StreamExtended.Helpers;
using StreamExtended.Network;

namespace Titanium.Web.Proxy.EventArguments
{
    internal class LimitedStream : Stream
    {
        private readonly CustomBufferedStream baseStream;
        private readonly CustomBinaryReader baseReader;
        private readonly bool isChunked;

        private bool readChunkTrail;
        private long bytesRemaining;

        public LimitedStream(CustomBufferedStream baseStream, CustomBinaryReader baseReader, bool isChunked, long contentLength)
        {
            this.baseStream = baseStream;
            this.baseReader = baseReader;
            this.isChunked = isChunked;
            bytesRemaining = isChunked ? 0 : contentLength;
        }

        private void GetNextChunk()
        {
            if (readChunkTrail)
            {
                // read the chunk trail of the previous chunk
                string s = baseReader.ReadLineAsync().Result;
            }

            readChunkTrail = true;

            string chunkHead = baseReader.ReadLineAsync().Result;
            int idx = chunkHead.IndexOf(";");
            if (idx >= 0)
            {
                // remove chunk extension
                chunkHead = chunkHead.Substring(0, idx);
            }

            int chunkSize = int.Parse(chunkHead, NumberStyles.HexNumber);
            bytesRemaining = chunkSize;

            if (chunkSize == 0)
            {
                bytesRemaining = -1;

                //chunk trail
                string s = baseReader.ReadLineAsync().Result;
            }
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (bytesRemaining == -1)
            {
                return 0;
            }

            if (bytesRemaining == 0)
            {
                if (isChunked)
                {
                    GetNextChunk();
                }
                else
                {
                    bytesRemaining = -1;
                }
            }

            if (bytesRemaining == -1)
            {
                return 0;
            }

            int toRead = (int)Math.Min(count, bytesRemaining);
            int res = baseStream.Read(buffer, offset, toRead);
            bytesRemaining -= res;

            return res;
        }

        public async Task Finish()
        {
            var buffer = BufferPool.GetBuffer(baseReader.Buffer.Length);
            try
            {
                while (bytesRemaining != -1)
                {
                    await ReadAsync(buffer, 0, buffer.Length);
                }
            }
            finally
            {
                BufferPool.ReturnBuffer(buffer);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }
}
