using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Helpers
{
    internal static class BufferPool
    {
        private static readonly ConcurrentQueue<byte[]> buffers = new ConcurrentQueue<byte[]>();

        internal static byte[] GetBuffer(int bufferSize)
        {
            byte[] buffer;
            if (!buffers.TryDequeue(out buffer) || buffer.Length != bufferSize)
            {
                buffer = new byte[bufferSize];
            }

            return buffer;
        }

        internal static void ReturnBuffer(byte[] buffer)
        {
            if (buffer != null)
            {
                buffers.Enqueue(buffer);
            }
        }
    }
}
