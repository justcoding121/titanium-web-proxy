namespace Titanium.Web.Proxy.Http2
{
    internal class Http2FrameHeader
    {
        public int Length;

        public Http2FrameType Type;

        public Http2FrameFlag Flags;

        public int StreamId;

        public byte[] Buffer;

        public byte[] CopyToBuffer()
        {
            int length = Length;
            var buf = Buffer;
            buf[0] = (byte)((length >> 16) & 0xff);
            buf[1] = (byte)((length >> 8) & 0xff);
            buf[2] = (byte)(length & 0xff);
            buf[3] = (byte)Type;
            buf[4] = (byte)Flags;
            return buf;
        }
    }
}
