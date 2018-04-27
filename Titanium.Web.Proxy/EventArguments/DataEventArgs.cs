using System;

namespace Titanium.Web.Proxy.EventArguments
{
    /// <summary>
    /// Wraps the data sent/received by a proxy server instance.
    /// </summary>
    public class DataEventArgs : EventArgs
    {
        internal DataEventArgs(byte[] buffer, int offset, int count)
        {
            Buffer = buffer;
            Offset = offset;
            Count = count;
        }

        /// <summary>
        /// The buffer with data.
        /// </summary>
        public byte[] Buffer { get; }

        /// <summary>
        /// Offset in Buffer where valid data begins.
        /// </summary>
        public int Offset { get; }

        /// <summary>
        /// Length from offset in Buffer with valid data.
        /// </summary>
        public int Count { get; }
    }
}
