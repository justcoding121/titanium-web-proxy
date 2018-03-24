using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using StreamExtended.Helpers;
using StreamExtended.Network;

namespace Titanium.Web.Proxy.EventArguments
{
    internal class ChunkedStream : Stream
    {
        private readonly CustomBufferedStream baseStream;
        private readonly CustomBinaryReader baseReader;

        private bool readChunkTrail;
        private int chunkBytesRemaining;

        public ChunkedStream(CustomBufferedStream baseStream, CustomBinaryReader baseReader)
        {
            this.baseStream = baseStream;
            this.baseReader = baseReader;
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
            chunkBytesRemaining = chunkSize;

            if (chunkSize == 0)
            {
                chunkBytesRemaining = -1;

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
            if (chunkBytesRemaining == -1)
            {
                return 0;
            }

            if (chunkBytesRemaining == 0)
            {
                GetNextChunk();
            }

            if (chunkBytesRemaining == -1)
            {
                return 0;
            }

            int toRead = Math.Min(count, chunkBytesRemaining);
            int res = baseStream.Read(buffer, offset, toRead);
            chunkBytesRemaining -= res;

            return res;
        }

        public async Task Finish()
        {
            var buffer = BufferPool.GetBuffer(baseReader.Buffer.Length);
            try
            {
                while (chunkBytesRemaining != -1)
                {
                    await ReadAsync(buffer, 0, buffer.Length);
                }
            }
            finally
            {
                BufferPool.ReturnBuffer(buffer);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (chunkBytesRemaining != -1)
            {
                ;
            }

            base.Dispose(disposing);
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
