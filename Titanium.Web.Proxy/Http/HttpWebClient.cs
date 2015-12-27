
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http
{
    public class HttpWebClient
    {
        private const string Space = " ";

        public string Method { get; set; }

        public Uri RequestUri { get; set; }

        public string Version { get; set; }

        public List<HttpHeader> RequestHeaders { get; set; }

        public bool IsSecure
        {
            get
            {
                return this.RequestUri.Scheme == Uri.UriSchemeHttps;
            }
        }

        public TcpClient Client { get; set; }

        public string RequestStatus { get; set; }

        public List<HttpHeader> ResponseHeaders { get; set; }

        public int RequestContentLength { get; set; }

        public bool RequestSendChunked { get; set; }

        public HttpWebClient()
        {
            this.RequestHeaders = new List<HttpHeader>();
            this.ResponseHeaders = new List<HttpHeader>();
        }

        public CustomBinaryReader ServerStreamReader { get; set; }

        public async Task SendRequest()
        {
            Stream stream = Client.GetStream();

            StringBuilder requestLines = new StringBuilder();

            requestLines.AppendLine(string.Join(" ", new string[3]
              {
                this.Method,
                this.RequestUri.AbsolutePath,
                this.Version
              }));

            foreach (HttpHeader httpHeader in this.RequestHeaders)
            {
                requestLines.AppendLine(httpHeader.Name + ':' + httpHeader.Value);

            }
            requestLines.AppendLine();
            requestLines.AppendLine();

            string request = requestLines.ToString();
            byte[] requestBytes = Encoding.ASCII.GetBytes(request);
            await AsyncExtensions.WriteAsync((Stream)stream, requestBytes, 0, requestBytes.Length);
            await AsyncExtensions.FlushAsync((Stream)stream);
        }

        public  void ReceiveResponse()
        {
            Stream stream = Client.GetStream();
            ServerStreamReader = new CustomBinaryReader(stream, Encoding.ASCII);
            var httpResult = ServerStreamReader.ReadLine().Split(' ');

            var httpVersion = httpResult[0];

            Version version;
            if (httpVersion == "HTTP/1.1")
            {
                version = new Version(1, 1);
            }
            else
            {
                version = new Version(1, 0);
            }

            this.ResponseProtocolVersion = version;
            this.ResponseStatusCode = httpResult[1];
            string status = httpResult[2];
            for (int i = 3; i < httpResult.Length; i++)
            {
                status = status + Space + httpResult[i];
            }
            this.ResponseStatusDescription = status;

            List<string> responseLines = ServerStreamReader.ReadAllLines();
         
            for (int index = 0; index < responseLines.Count; ++index)
            {
                string[] strArray = responseLines[index].Split(':');
                this.ResponseHeaders.Add(new HttpHeader(strArray[0], strArray[1]));
            }
        }



        public string RequestContentType { get; set; }

        public bool RequestKeepAlive { get; set; }

        public string RequestHost { get; set; }

        public string ResponseCharacterSet { get; set; }

        public string ResponseContentEncoding { get; set; }

        public System.Version ResponseProtocolVersion { get; set; }

        public string ResponseStatusCode { get; set; }

        public string ResponseStatusDescription { get; set; }

        public bool ResponseKeepAlive { get; set; }

        public string ResponseContentType { get; set; }

    }


}
