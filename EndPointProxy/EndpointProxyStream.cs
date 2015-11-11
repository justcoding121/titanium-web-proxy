using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EndPointProxy
{
    public class EndpointProxyStream : Stream
    {
        private Stream _requestStream;

        public EndpointProxyStream(Stream requestStream)
        {
            _requestStream = requestStream;
        }

        public override bool CanRead
        {
            get
            {
                return _requestStream.CanRead;
            }
        }

        public override bool CanSeek
        {
            get
            {
                return _requestStream.CanSeek;
            }
        }

        public override bool CanWrite
        {
            get
            {
                return _requestStream.CanWrite;
            }
        }

        public override long Length
        {
            get
            {
                return _requestStream.Length;
            }
        }

        public override long Position
        {
            get
            {
                return _requestStream.Position;
            }

            set
            {
                _requestStream.Position = value;
            }
        }

        public override void Flush()
        {
            _requestStream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _requestStream.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return _requestStream.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            _requestStream.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _requestStream.Write(buffer, offset, count);
        }
    }
}
