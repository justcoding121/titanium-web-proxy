using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy.Http
{
    /// <summary>
    /// A HTTP(S) request object
    /// </summary>
    public class Request
    {
        /// <summary>
        /// Request Method
        /// </summary>
        public string Method { get; set; }

        /// <summary>
        /// Request HTTP Uri
        /// </summary>
        public Uri RequestUri { get; set; }

        /// <summary>
        /// Request Http Version
        /// </summary>
        public Version HttpVersion { get; set; }

        /// <summary>
        /// Request Http hostanem
        /// </summary>
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

        /// <summary>
        /// Request content encoding
        /// </summary>
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

        /// <summary>
        /// Request content-length
        /// </summary>
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

        /// <summary>
        /// Request content-type
        /// </summary>
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

        /// <summary>
        /// Is request body send as chunked bytes
        /// </summary>
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

        /// <summary>
        /// Does this request has a 100-continue header?
        /// </summary>
        public bool ExpectContinue
        {
            get
            {
                var header = RequestHeaders.FirstOrDefault(x => x.Name.ToLower() == "expect");
                if (header != null) return header.Value.Equals("100-continue");
                return false;
            }
        }

        /// <summary>
        /// Request Url
        /// </summary>
        public string Url { get { return RequestUri.OriginalString; } }

        /// <summary>
        /// Encoding for this request
        /// </summary>
        internal Encoding Encoding { get { return this.GetEncoding(); } }
        /// <summary>
        /// Terminates the underlying Tcp Connection to client after current request
        /// </summary>
        internal bool CancelRequest { get; set; }

        /// <summary>
        /// Request body as byte array
        /// </summary>
        internal byte[] RequestBody { get; set; }

        /// <summary>
        /// request body as string
        /// </summary>
        internal string RequestBodyString { get; set; }


        internal bool RequestBodyRead { get; set; }
        internal bool RequestLocked { get; set; }

        /// <summary>
        /// Does this request has an upgrade to websocket header?
        /// </summary>
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

        /// <summary>
        /// Request heade collection
        /// </summary>
        public List<HttpHeader> RequestHeaders { get; set; }

        /// <summary>
        /// Does server responsed positively for 100 continue request
        /// </summary>
        public bool Is100Continue { get; internal set; }

        /// <summary>
        /// Server responsed negatively for the request for 100 continue
        /// </summary>
        public bool ExpectationFailed { get; internal set; }

        public Request()
        {
            this.RequestHeaders = new List<HttpHeader>();
        }

    }
}
