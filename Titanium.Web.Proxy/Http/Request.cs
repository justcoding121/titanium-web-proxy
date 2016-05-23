using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy.Http
{
    public class Request
    {
        public string Method { get; set; }
        public Uri RequestUri { get; set; }
        public Version HttpVersion { get; set; }

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

        internal string ContentEncoding
        {
            get
            {
                var header = this.RequestHeaders.FirstOrDefault(x => x.Name.ToLower().Equals("content-encoding"));

                if (header != null)
                {
                    return header.Value.Trim();
                }

                return null;
            }
        }

        public long ContentLength
        {
            get
            {
                var header = RequestHeaders.FirstOrDefault(x => x.Name.ToLower() == "content-length");

                if (header == null)
                    return -1;

                long contentLen;
                long.TryParse(header.Value, out contentLen);
                if (contentLen >=0)
                    return contentLen;

                return -1;
            }
            set
            {

                var header = RequestHeaders.FirstOrDefault(x => x.Name.ToLower() == "content-length");

                if (value >= 0)
                {
                    if (header != null)
                        header.Value = value.ToString();
                    else
                        RequestHeaders.Add(new HttpHeader("content-length", value.ToString()));

                    IsChunked = false;
                }
                else
                {
                    if (header != null)
                        this.RequestHeaders.Remove(header);
                }
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

        public bool IsChunked
        {
            get
            {
                var header = RequestHeaders.FirstOrDefault(x => x.Name.ToLower() == "transfer-encoding");
                if (header != null) return header.Value.ToLower().Contains("chunked");
                return false;
            }
            set
            {
                var header = RequestHeaders.FirstOrDefault(x => x.Name.ToLower() == "transfer-encoding");

                if (value)
                {
                    if (header != null)
                    {
                        header.Value = "chunked";
                    }
                    else
                        RequestHeaders.Add(new HttpHeader("transfer-encoding", "chunked"));

                    this.ContentLength = -1;
                }
                else
                {
                    if (header != null)
                        RequestHeaders.Remove(header);
                }
            }
        }

        public bool ExpectContinue
        {
            get
            {
                var header = RequestHeaders.FirstOrDefault(x => x.Name.ToLower() == "expect");
                if (header != null) return header.Value.Equals("100-continue");
                return false;
            }
        }

        public string Url { get { return RequestUri.OriginalString; } }

        internal Encoding Encoding { get { return this.GetEncoding(); } }
        /// <summary>
        /// Terminates the underlying Tcp Connection to client after current request
        /// </summary>
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
        public bool Is100Continue { get; internal set; }
        public bool ExpectationFailed { get; internal set; }

        public Request()
        {
            this.RequestHeaders = new List<HttpHeader>();
        }

    }
}
