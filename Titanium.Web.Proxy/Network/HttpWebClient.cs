
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

namespace Titanium.Web.Proxy.Network
{
    public class Request
    {
        public string Method { get; internal set; }
        public Uri RequestUri { get; internal set; }
        public string HttpVersion { get; internal set; }

        public string Status { get; internal set; }
        public int ContentLength { get; internal set; }
        public bool SendChunked { get; internal set; }
        public string ContentType { get; internal set; }
        public bool KeepAlive { get; internal set; }
        public string Hostname { get; internal set; }

        public string Url { get; internal set; }

        internal Encoding Encoding { get; set; }
        internal bool IsAlive { get; set; }
        internal bool CancelRequest { get; set; }
        internal byte[] RequestBody { get; set; }
        internal string RequestBodyString { get; set; }
        internal bool RequestBodyRead { get; set; }
        internal bool UpgradeToWebSocket { get; set; }
        public List<HttpHeader> RequestHeaders { get; internal set; }
        internal bool RequestLocked { get; set; }

        public Request()
        {
            this.RequestHeaders = new List<HttpHeader>();
        }

    }

    public class Response
    {

        internal Encoding Encoding { get; set; }
        internal Stream ResponseStream { get; set; }
        internal byte[] ResponseBody { get; set; }
        internal string ResponseBodyString { get; set; }
        internal bool ResponseBodyRead { get; set; }
        internal bool ResponseLocked { get; set; }
        public List<HttpHeader> ResponseHeaders { get; internal set; }
        internal string CharacterSet { get; set; }
        internal string ContentEncoding { get; set; }
        internal string HttpVersion { get; set; }
        public string ResponseStatusCode { get; internal set; }
        public string ResponseStatusDescription { get; internal set; }
        internal bool ResponseKeepAlive { get; set; }
        public string ContentType { get; internal set; }
        internal int ContentLength { get; set; }
        internal bool IsChunked { get; set; }

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
        internal TcpConnection ProxyClient { get; set; }

        public void SetConnection(TcpConnection Connection)
        {
            Connection.LastAccess = DateTime.Now;
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
                this.Request.HttpVersion
              }));

            foreach (HttpHeader httpHeader in this.Request.RequestHeaders)
            {
                requestLines.AppendLine(httpHeader.Name + ':' + httpHeader.Value);
            }

            requestLines.AppendLine();

            string request = requestLines.ToString();
            byte[] requestBytes = Encoding.ASCII.GetBytes(request);
            stream.Write(requestBytes, 0, requestBytes.Length);
            stream.Flush();
        }

        public void ReceiveResponse()
        {
            var httpResult = ProxyClient.ServerStreamReader.ReadLine().Split(new char[] { ' ' }, 3);

            if(string.IsNullOrEmpty(httpResult[0]))
            {
                var s = ProxyClient.ServerStreamReader.ReadLine();
            }

            this.Response.HttpVersion = httpResult[0];
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
