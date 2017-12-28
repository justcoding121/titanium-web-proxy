using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StreamExtended.Network;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Http
{
    internal static class HeaderParser
    {
        internal static async Task ReadHeaders(CustomBinaryReader reader, HeaderCollection headerCollection)
        {
            var nonUniqueResponseHeaders = headerCollection.NonUniqueHeaders;
            var headers = headerCollection.Headers;

            string tmpLine;
            while (!string.IsNullOrEmpty(tmpLine = await reader.ReadLineAsync()))
            {
                var header = tmpLine.Split(ProxyConstants.ColonSplit, 2);

                var newHeader = new HttpHeader(header[0], header[1]);

                //if header exist in non-unique header collection add it there
                if (nonUniqueResponseHeaders.ContainsKey(newHeader.Name))
                {
                    nonUniqueResponseHeaders[newHeader.Name].Add(newHeader);
                }
                //if header is already in unique header collection then move both to non-unique collection
                else if (headers.ContainsKey(newHeader.Name))
                {
                    var existing = headers[newHeader.Name];

                    var nonUniqueHeaders = new List<HttpHeader>
                    {
                        existing,
                        newHeader
                    };

                    nonUniqueResponseHeaders.Add(newHeader.Name, nonUniqueHeaders);
                    headers.Remove(newHeader.Name);
                }
                //add to unique header collection
                else
                {
                    headers.Add(newHeader.Name, newHeader);
                }
            }
        }

        /// <summary>
        /// Increase size of buffer and copy existing content to new buffer
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="size"></param>
        private static void ResizeBuffer(ref byte[] buffer, long size)
        {
            var newBuffer = new byte[size];
            Buffer.BlockCopy(buffer, 0, newBuffer, 0, buffer.Length);
            buffer = newBuffer;
        }
    }
}
