
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
using System.Linq;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy.Network
{
    public class Request
    {
        public string Method { get; set; }
        public Uri RequestUri { get; set; }
        public string HttpVersion { get; set; }

        internal string Host
        {
            get
            {
                var host = RequestHeaders.FirstOrDefault(x => x.Name.ToLower() == "host");
                if (host != null)
                    return host.Value;
                return null;
            }
            set
            {
                var host = RequestHeaders.FirstOrDefault(x => x.Name.ToLower() == "host");
                if (host != null)
                    host.Value = value;
                else
                    RequestHeaders.Add(new HttpHeader("Host", value));
            }
        }

        public int ContentLength
        {
            get
            {
                var header = RequestHeaders.FirstOrDefault(x => x.Name.ToLower() == "content-length");

                if (header == null)
                    return 0;

                int contentLen;
                int.TryParse(header.Value, out contentLen);
                if (contentLen != 0)
                    return contentLen;

                return 0;
            }
            set
            {
                var header = RequestHeaders.FirstOrDefault(x => x.Name.ToLower() == "content-length");

                if (header != null)
                    header.Value = value.ToString();
                else
                    RequestHeaders.Add(new HttpHeader("content-length", value.ToString()));
            }
        }

        public string ContentType
        {
            get
            {
                var header = RequestHeaders.FirstOrDefault(x => x.Name.ToLower() == "content-type");
                if (header != null)
                    return header.Value;
                return null;
            }
            set
            {
                var header = RequestHeaders.FirstOrDefault(x => x.Name.ToLower() == "content-type");

                if (header != null)
                    header.Value = value.ToString();
                else
                    RequestHeaders.Add(new HttpHeader("content-type", value.ToString()));
            }

        }

        public bool SendChunked
        {
            get
            {
                var header = RequestHeaders.FirstOrDefault(x => x.Name.ToLower() == "transfer-encoding");
                if (header != null) return header.Value.ToLower().Contains("chunked");
                return false;
            }
        }

        public string Url { get { return RequestUri.OriginalString; } }

        internal Encoding Encoding { get { return this.GetEncoding(); } }

        internal bool CancelRequest { get; set; }

        internal byte[] RequestBody { get; set; }
        internal string RequestBodyString { get; set; }
        internal bool RequestBodyRead { get; set; }
        internal bool RequestLocked { get; set; }

        internal bool UpgradeToWebSocket
        {
            get
            {
                var header = RequestHeaders.FirstOrDefault(x => x.Name.ToLower() == "upgrade");
                if (header == null)
                    return false;

                if (header.Value.ToLower() == "websocket")
                    return true;

                return false;

            }
        }

        public List<HttpHeader> RequestHeaders { get; set; }


        public Request()
        {
            this.RequestHeaders = new List<HttpHeader>();
        }

    }

    public class Response
    {
        public string ResponseStatusCode { get; set; }
        public string ResponseStatusDescription { get; set; }

        internal Encoding Encoding { get { return this.GetResponseEncoding(); } }

        internal string CharacterSet
        {
            get
            {

                if (this.ContentType.Contains(";"))
                {

                    return this.ContentType.Split(';')[1].Substring(9).Trim();
                }
                return null;

            }
        }
        internal string ContentEncoding
        {
            get
            {
                var header = this.ResponseHeaders.FirstOrDefault(x => x.Name.ToLower().Equals("content-encoding"));

                if (header != null)
                {
                    return header.Value.Trim().ToLower();
                }

                return null;
            }
        }

        internal string HttpVersion { get; set; }
        internal bool ResponseKeepAlive
        {
            get
            {
                var header = this.ResponseHeaders.FirstOrDefault(x => x.Name.ToLower().Equals("connection"));

                if (header != null && header.Value.ToLower().Contains("close"))
                {
                    return false;
                }

                return true;

            }
        }

        public string ContentType
        {
            get
            {
                var header = this.ResponseHeaders.FirstOrDefault(x => x.Name.ToLower().Equals("content-type"));

                if (header != null)
                {
                    if (header.Value.Contains(";"))
                    {

                        return header.Value.Split(';')[0].Trim();
                    }
                    else
                        return header.Value.ToLower().Trim();
                }

                return null;

            }
        }

        internal int ContentLength
        {
            get
            {
                var header = this.ResponseHeaders.FirstOrDefault(x => x.Name.ToLower().Equals("content-length"));

                if (header != null)
                {
                    return int.Parse(header.Value.Trim());
                }

                return -1;

            }
        }

        internal bool IsChunked
        {
            get
            {
                var header = this.ResponseHeaders.FirstOrDefault(x => x.Name.ToLower().Equals("transfer-encoding"));

                if (header != null && header.Value.ToLower().Contains("chunked"))
                {
                    return true;
                }

                return false;

            }
        }

        public List<HttpHeader> ResponseHeaders { get; set; }

        internal Stream ResponseStream { get; set; }
        internal byte[] ResponseBody { get; set; }
        internal string ResponseBodyString { get; set; }
        internal bool ResponseBodyRead { get; set; }
        internal bool ResponseLocked { get; set; }


        public Response()
        {
            this.ResponseHeaders = new List<HttpHeader>();
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
            //return if this is already read
            if (this.Response.ResponseStatusCode != null) return;

            var httpResult = ProxyClient.ServerStreamReader.ReadLine().Split(new char[] { ' ' }, 3);

            if (string.IsNullOrEmpty(httpResult[0]))
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
