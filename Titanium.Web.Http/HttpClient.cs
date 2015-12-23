using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Instrumentation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Titanium.Web.Http
{
    public class HttpClient
    {
        const string Space = " ";

        public string Method { get; set; }
        public Uri Uri { get; set; }
        public string Version { get; set; }
        public List<HttpHeader> RequestHeaders { get; set; }

        public bool IsSecure
        {
            get { return this.Uri.Scheme == Uri.UriSchemeHttps; }
        }

        public TcpClient Client { get; set; }

        public Stream Stream { get; set; }


        public HttpClient()
        {
            RequestHeaders = new List<HttpHeader>();
            ResponseHeaders = new List<HttpHeader>();
        }

        public async Task<Stream> GetStream()
        {
            if (Stream == null)
            {
                Client = new TcpClient(Uri.Host, Uri.Port);
                Stream = Client.GetStream();

                if (IsSecure)
                {
                    SslStream sslStream = null;
                    try
                    {
                        sslStream = new SslStream(Stream);
                        await sslStream.AuthenticateAsClientAsync(Uri.Host);
                        Stream = sslStream;
                    }
                    catch
                    {
                        if (sslStream != null)
                            sslStream.Dispose();

                        throw;
                    }

                }
            }
            return Stream;
        }

        public async Task SendRequest()
        {
            await GetStream();

            var requestLines = new StringBuilder();

            requestLines.Append(string.Join(Space, Method, Uri.AbsolutePath, Version));
            requestLines.AppendLine();


            foreach (var header in RequestHeaders)
            {
                requestLines.Append(header.Name + ':' + header.Value);
                requestLines.AppendLine();
                requestLines.AppendLine();
            }
            var request = requestLines.ToString();

            var requestBytes = Encoding.ASCII.GetBytes(request);

            await Stream.WriteAsync(requestBytes, 0, requestBytes.Length);
            await Stream.FlushAsync();


        }

        public string Status { get; set; }
        public List<HttpHeader> ResponseHeaders { get; set; }

        public async Task ReceiveResponse()
        {

            var responseLines = await HttpStreamReader.ReadAllLines(Stream);
            var responseStatus = responseLines[0].Split(' ');
            Status = responseStatus[1] + Space + responseStatus[2];

            for (int i = 1; i < responseLines.Count; i++)
            {
                var header = responseLines[i].Split(':');
                ResponseHeaders.Add(new HttpHeader(header[0], header[1]));
            }



        }


        public void Abort()
        {
            throw new NotImplementedException();
        }

        public int ContentLength { get; set; }

        public bool SendChunked { get; set; }

        
    }

}
