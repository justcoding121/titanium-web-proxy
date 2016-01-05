
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

        public string RequestStatus { get; set; }
        public int RequestContentLength { get; set; }
        public bool RequestSendChunked { get; set; }
        public string RequestContentType { get; set; }
        public bool RequestKeepAlive { get; set; }
        public string RequestHost { get; set; }

        public string RequestUrl { get; internal set; }
        public string RequestHostname { get; internal set; }

        internal Encoding RequestEncoding { get; set; }
        internal Version RequestHttpVersion { get; set; }
        internal bool RequestIsAlive { get; set; }
        internal bool CancelRequest { get; set; }
        internal byte[] RequestBody { get; set; }
        internal string RequestBodyString { get; set; }
        internal bool RequestBodyRead { get; set; }
        public bool UpgradeToWebSocket { get; set; }
        public List<HttpHeader> RequestHeaders { get; internal set; }
        internal bool RequestLocked { get; set; }

        public Request()
        {
            this.RequestHeaders = new List<HttpHeader>();
        }

    }

    public class Response
    {

        internal Encoding ResponseEncoding { get; set; }
        internal Stream ResponseStream { get; set; }
        internal byte[] ResponseBody { get; set; }
        internal string ResponseBodyString { get; set; }
        internal bool ResponseBodyRead { get; set; }
        internal bool ResponseLocked { get; set; }
        public List<HttpHeader> ResponseHeaders { get; internal set; }
        public string ResponseCharacterSet { get; set; }
        public string ResponseContentEncoding { get; set; }
        public System.Version ResponseProtocolVersion { get; set; }
        public string ResponseStatusCode { get; set; }
        public string ResponseStatusDescription { get; set; }
        public bool ResponseKeepAlive { get; set; }
        public string ResponseContentType { get; set; }
        public int ContentLength { get; set; }
        public bool IsChunked { get; set; }

        public Response()
        {
            this.ResponseHeaders = new List<HttpHeader>();
            this.ResponseKeepAlive = true;
        }



        
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
        public TcpConnection ProxyClient { get; set; }

        public void SetConnection(TcpConnection Connection)
        {
            ProxyClient = Connection;
        }

        public HttpWebSession()
        {
            this.Request = new Request();
            this.Response = new Response();


        }



        public void SendRequest()
        {
            Stream stream = ProxyClient.Stream;

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
            //requestLines.AppendLine();

            string request = requestLines.ToString();
            byte[] requestBytes = Encoding.ASCII.GetBytes(request);
            stream.Write(requestBytes, 0, requestBytes.Length);
            stream.Flush();
        }

        public void ReceiveResponse()
        {
            var httpResult = ProxyClient.ServerStreamReader.ReadLine().Split(new char[] { ' ' }, 3);

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

            List<string> responseLines = ProxyClient.ServerStreamReader.ReadAllLines();

            for (int index = 0; index < responseLines.Count; ++index)
            {
                string[] strArray = responseLines[index].Split(new char[] { ':' }, 2);
                this.Response.ResponseHeaders.Add(new HttpHeader(strArray[0], strArray[1]));
            }
        }



    }


}
