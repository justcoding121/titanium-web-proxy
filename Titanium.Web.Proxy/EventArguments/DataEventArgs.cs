using System;

namespace Titanium.Web.Proxy.EventArguments
{
    public class DataEventArgs : EventArgs
    {
        public byte[] Buffer { get; }

        public int Offset { get; }

        public int Count { get; }

        public DataEventArgs(byte[] buffer, int offset, int count)
        {
            Buffer = buffer;
            Offset = offset;
            Count = count;
        }
    }
}