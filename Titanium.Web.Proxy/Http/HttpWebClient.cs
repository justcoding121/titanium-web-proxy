
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
    public class Request
    {
        public string Method { get; set; }

        public Uri RequestUri { get; set; }

        public string Version { get; set; }

        public List<HttpHeader> RequestHeaders { get; set; }

        public string RequestStatus { get; set; }

        public int RequestContentLength { get; set; }

        public bool RequestSendChunked { get; set; }

        public string RequestContentType { get; set; }

        public bool RequestKeepAlive { get; set; }

        public string RequestHost { get; set; }

        public Request()
        {
            this.RequestHeaders = new List<HttpHeader>();
        }
    }

    public class Response
    {

        public List<HttpHeader> ResponseHeaders { get; set; }

        public string ResponseCharacterSet { get; set; }

        public string ResponseContentEncoding { get; set; }

        public System.Version ResponseProtocolVersion { get; set; }

        public string ResponseStatusCode { get; set; }

        public string ResponseStatusDescription { get; set; }

        public bool ResponseKeepAlive { get; set; }

        public string ResponseContentType { get; set; }

        public Response()
        {
            this.ResponseHeaders = new List<HttpHeader>();
        }

        public int ContentLength { get; set; }
    }

    public class HttpWebSession
    {
        private const string Space = " ";

        public bool IsSecure
        {
            get
            {
                return this.Request.RequestUri.Scheme == Uri.UriSchemeHttps;
            }
        }

        public Request Request { get; set; }
        public Response Response { get; set; }
        public TcpConnection Client { get; set; }

        public void SetConnection(TcpConnection Connection)
        {
            Client = Connection;
            ServerStreamReader = Client.ServerStreamReader;
        }

        public HttpWebSession()
        {
            this.Request = new Request();
            this.Response = new Response();
            
      
        }

        public CustomBinaryReader ServerStreamReader { get; set; }

        public async Task SendRequest()
        {
            Stream stream = Client.Stream;

            StringBuilder requestLines = new StringBuilder();

            requestLines.AppendLine(string.Join(" ", new string[3]
              {
                this.Request.Method,
                this.Request.RequestUri.PathAndQuery,
                this.Request.Version
              }));

            foreach (HttpHeader httpHeader in this.Request.RequestHeaders)
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

        public void ReceiveResponse()
        {         
            var httpResult = ServerStreamReader.ReadLine().Split(new char[] { ' ' }, 3);

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

            this.Response.ResponseProtocolVersion = version;
            this.Response.ResponseStatusCode = httpResult[1];
            string status = httpResult[2];

            this.Response.ResponseStatusDescription = status;

            List<string> responseLines = ServerStreamReader.ReadAllLines();

            for (int index = 0; index < responseLines.Count; ++index)
            {
                string[] strArray = responseLines[index].Split(new char[] { ':' }, 2);
                this.Response.ResponseHeaders.Add(new HttpHeader(strArray[0], strArray[1]));
            }
        }



    }


}
