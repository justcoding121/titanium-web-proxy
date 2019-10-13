using System;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Http
{
    internal static class HeaderParser
    {
        internal static async Task ReadHeaders(ICustomStreamReader reader, HeaderCollection headerCollection,
            CancellationToken cancellationToken)
        {
            string? tmpLine;
            while (!string.IsNullOrEmpty(tmpLine = await reader.ReadLineAsync(cancellationToken)))
            {
                int colonIndex = tmpLine!.IndexOf(':');
                if (colonIndex == -1)
                {
                    throw new Exception("Header line should contain a colon character.");
                }

                string headerName = tmpLine.AsSpan(0, colonIndex).ToString();
                string headerValue = tmpLine.AsSpan(colonIndex + 1).ToString();
                headerCollection.AddHeader(headerName, headerValue);
            }
        }

        /// <summary>
        ///     Increase size of buffer and copy existing content to new buffer
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="size"></param>
        private static void resizeBuffer(ref byte[] buffer, long size)
        {
            var newBuffer = new byte[size];
            Buffer.BlockCopy(buffer, 0, newBuffer, 0, buffer.Length);
            buffer = newBuffer;
        }
    }
}
