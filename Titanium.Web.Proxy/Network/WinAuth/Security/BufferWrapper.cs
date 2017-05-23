namespace Titanium.Web.Proxy.Network.WinAuth.Security
{
    using System;

    internal struct BufferWrapper
    {
        internal byte[] Buffer;
        internal Common.SecurityBufferType BufferType;

        internal BufferWrapper(byte[] buffer, Common.SecurityBufferType bufferType)
        {
            if (buffer == null || buffer.Length == 0)
            {
                throw new ArgumentException("buffer cannot be null or 0 length");
            }

            Buffer = buffer;
            BufferType = bufferType;
        }
    };
}
