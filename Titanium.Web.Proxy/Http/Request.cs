using System;
using System.Collections.Generic;
using System.Text;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http
{
    /// <summary>
    /// A HTTP(S) request object
    /// </summary>
    public class Request : IDisposable
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
                var hasHeader = RequestHeaders.ContainsKey("host");
                return hasHeader ? RequestHeaders["host"].Value : null;
            }
            set
            {
                var hasHeader = RequestHeaders.ContainsKey("host");
                if (hasHeader)
                {
                    RequestHeaders["host"].Value = value;
                }
                else
                {
                    RequestHeaders.Add("Host", new HttpHeader("Host", value));
                }
            }
        }

        /// <summary>
        /// Request content encoding
        /// </summary>
        internal string ContentEncoding
        {
            get
            {
                var hasHeader = RequestHeaders.ContainsKey("content-encoding");

                if (hasHeader)
                {
                    return RequestHeaders["content-encoding"].Value;
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
                var hasHeader = RequestHeaders.ContainsKey("content-length");

                if (hasHeader == false)
                {
                    return -1;
                }

                var header = RequestHeaders["content-length"];

                long contentLen;
                long.TryParse(header.Value, out contentLen);
                if (contentLen >= 0)
                {
                    return contentLen;
                }

                return -1;
            }
            set
            {
                var hasHeader = RequestHeaders.ContainsKey("content-length");

                var header = RequestHeaders["content-length"];

                if (value >= 0)
                {
                    if (hasHeader)
                    {
                        header.Value = value.ToString();
                    }
                    else
                    {
                        RequestHeaders.Add("content-length", new HttpHeader("content-length", value.ToString()));
                    }

                    IsChunked = false;
                }
                else
                {
                    if (hasHeader)
                    {
                        RequestHeaders.Remove("content-length");
                    }
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
                var hasHeader = RequestHeaders.ContainsKey("content-type");

                if (hasHeader)
                {
                    var header = RequestHeaders["content-type"];
                    return header.Value;
                }

                return null;
            }
            set
            {
                var hasHeader = RequestHeaders.ContainsKey("content-type");

                if (hasHeader)
                {
                    var header = RequestHeaders["content-type"];
                    header.Value = value;
                }
                else
                {
                    RequestHeaders.Add("content-type", new HttpHeader("content-type", value));
                }
            }
        }

        /// <summary>
        /// Is request body send as chunked bytes
        /// </summary>
        public bool IsChunked
        {
            get
            {
                var hasHeader = RequestHeaders.ContainsKey("transfer-encoding");

                if (hasHeader)
                {
                    var header = RequestHeaders["transfer-encoding"];

                    return header.Value.ContainsIgnoreCase("chunked");
                }

                return false;
            }
            set
            {
                var hasHeader = RequestHeaders.ContainsKey("transfer-encoding");

                if (value)
                {
                    if (hasHeader)
                    {
                        var header = RequestHeaders["transfer-encoding"];
                        header.Value = "chunked";
                    }
                    else
                    {
                        RequestHeaders.Add("transfer-encoding", new HttpHeader("transfer-encoding", "chunked"));
                    }

                    ContentLength = -1;
                }
                else
                {
                    if (hasHeader)
                    {
                        RequestHeaders.Remove("transfer-encoding");
                    }
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
                var hasHeader = RequestHeaders.ContainsKey("expect");

                if (!hasHeader) return false;
                var header = RequestHeaders["expect"];

                return header.Value.Equals("100-continue");
            }
        }

        /// <summary>
        /// Request Url
        /// </summary>
        public string Url => RequestUri.OriginalString;

        /// <summary>
        /// Encoding for this request
        /// </summary>
        internal Encoding Encoding => this.GetEncoding();

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
                var hasHeader = RequestHeaders.ContainsKey("upgrade");

                if (hasHeader == false)
                {
                    return false;
                }

                var header = RequestHeaders["upgrade"];

                return header.Value.Equals("websocket", StringComparison.CurrentCultureIgnoreCase);
            }
        }

        /// <summary>
        /// Unique Request header collection
        /// </summary>
        public Dictionary<string, HttpHeader> RequestHeaders { get; set; }

        /// <summary>
        /// Non Unique headers
        /// </summary>
        public Dictionary<string, List<HttpHeader>> NonUniqueRequestHeaders { get; set; }

        /// <summary>
        /// Does server responsed positively for 100 continue request
        /// </summary>
        public bool Is100Continue { get; internal set; }

        /// <summary>
        /// Server responsed negatively for the request for 100 continue
        /// </summary>
        public bool ExpectationFailed { get; internal set; }

        /// <summary>
        /// Constructor.
        /// </summary>
        public Request()
        {
            RequestHeaders = new Dictionary<string, HttpHeader>(StringComparer.OrdinalIgnoreCase);
            NonUniqueRequestHeaders = new Dictionary<string, List<HttpHeader>>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Dispose off 
        /// </summary>
        public void Dispose()
        {
            //not really needed since GC will collect it
            //but just to be on safe side

            RequestHeaders = null;
            NonUniqueRequestHeaders = null;

            RequestBody = null;
            RequestBody = null;
        }
    }
}
