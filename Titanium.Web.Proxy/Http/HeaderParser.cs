using System.Collections.Generic;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Http
{
    internal static class HeaderParser
    {
        internal static async Task ReadHeaders(CustomBinaryReader reader,
            Dictionary<string, List<HttpHeader>> nonUniqueResponseHeaders,
            Dictionary<string, HttpHeader> headers)
        {
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
                //if header is alread in unique header collection then move both to non-unique collection
                else if (headers.ContainsKey(newHeader.Name))
                {
                    var existing = headers[newHeader.Name];

                    var nonUniqueHeaders = new List<HttpHeader> { existing, newHeader };

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
    }
}
