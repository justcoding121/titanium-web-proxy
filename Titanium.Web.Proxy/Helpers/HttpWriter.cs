using System.IO;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Helpers
{
    abstract class HttpWriter : StreamWriter
    {
        protected HttpWriter(Stream stream, int bufferSize, bool leaveOpen) 
            : base(stream, Encoding.ASCII, bufferSize, leaveOpen)
        {
            NewLine = ProxyConstants.NewLine;
        }

        public void WriteHeaders(HeaderCollection headers, bool flush = true)
        {
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    header.WriteToStream(this);
                }
            }

            WriteLine();
            if (flush)
            {
                Flush();
            }
        }

        /// <summary>
        /// Write the headers to client
        /// </summary>
        /// <param name="headers"></param>
        /// <param name="flush"></param>
        /// <returns></returns>
        public async Task WriteHeadersAsync(HeaderCollection headers, bool flush = true)
        {
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    await header.WriteToStreamAsync(this);
                }
            }

            await WriteLineAsync();
            if (flush)
            {
                await FlushAsync();
            }
        }
    }
}
