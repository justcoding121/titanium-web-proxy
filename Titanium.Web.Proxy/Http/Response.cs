using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http
{
    /// <summary>
    /// Http(s) response object
    /// </summary>
    public class Response : IDisposable
    {
        /// <summary>
        /// Response Status Code.
        /// </summary>
        public string ResponseStatusCode { get; set; }

        /// <summary>
        /// Response Status description.
        /// </summary>
        public string ResponseStatusDescription { get; set; }

        internal Encoding Encoding => this.GetResponseCharacterEncoding();

        /// <summary>
        /// Content encoding for this response
        /// </summary>
        internal string ContentEncoding
        {
            get
            {
                var hasHeader = ResponseHeaders.ContainsKey("content-encoding");

                if (!hasHeader) return null;
                var header = ResponseHeaders["content-encoding"];

                return header.Value.Trim();
            }
        }

        internal Version HttpVersion { get; set; }

        /// <summary>
        /// Keep the connection alive?
        /// </summary>
        internal bool ResponseKeepAlive
        {
            get
            {
                var hasHeader = ResponseHeaders.ContainsKey("connection");

                if (hasHeader)
                {
                    var header = ResponseHeaders["connection"];

                    if (header.Value.ContainsIgnoreCase("close"))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Content type of this response
        /// </summary>
        public string ContentType
        {
            get
            {
                var hasHeader = ResponseHeaders.ContainsKey("content-type");

                if (hasHeader)
                {
                    var header = ResponseHeaders["content-type"];

                    return header.Value;
                }

                return null;
            }
        }

        /// <summary>
        /// Length of response body
        /// </summary>
        internal long ContentLength
        {
            get
            {
                var hasHeader = ResponseHeaders.ContainsKey("content-length");

                if (hasHeader == false)
                {
                    return -1;
                }

                var header = ResponseHeaders["content-length"];

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
                var hasHeader = ResponseHeaders.ContainsKey("content-length");

                if (value >= 0)
                {
                    if (hasHeader)
                    {
                        var header = ResponseHeaders["content-length"];
                        header.Value = value.ToString();
                    }
                    else
                    {
                        ResponseHeaders.Add("content-length", new HttpHeader("content-length", value.ToString()));
                    }

                    IsChunked = false;
                }
                else
                {
                    if (hasHeader)
                    {
                        ResponseHeaders.Remove("content-length");
                    }
                }
            }
        }

        /// <summary>
        /// Response transfer-encoding is chunked?
        /// </summary>
        internal bool IsChunked
        {
            get
            {
                var hasHeader = ResponseHeaders.ContainsKey("transfer-encoding");

                if (hasHeader)
                {
                    var header = ResponseHeaders["transfer-encoding"];

                    if (header.Value.ContainsIgnoreCase("chunked"))
                    {
                        return true;
                    }
                }

                return false;
            }
            set
            {
                var hasHeader = ResponseHeaders.ContainsKey("transfer-encoding");

                if (value)
                {
                    if (hasHeader)
                    {
                        var header = ResponseHeaders["transfer-encoding"];
                        header.Value = "chunked";
                    }
                    else
                    {
                        ResponseHeaders.Add("transfer-encoding", new HttpHeader("transfer-encoding", "chunked"));
                    }

                    ContentLength = -1;
                }
                else
                {
                    if (hasHeader)
                    {
                        ResponseHeaders.Remove("transfer-encoding");
                    }
                }
            }
        }

        /// <summary>
        /// Collection of all response headers
        /// </summary>
        public Dictionary<string, HttpHeader> ResponseHeaders { get; set; }

        /// <summary>
        /// Non Unique headers
        /// </summary>
        public Dictionary<string, List<HttpHeader>> NonUniqueResponseHeaders { get; set; }


        /// <summary>
        /// Response network stream
        /// </summary>
        public Stream ResponseStream { get; set; }

        /// <summary>
        /// response body contenst as byte array
        /// </summary>
        internal byte[] ResponseBody { get; set; }

        /// <summary>
        /// response body as string
        /// </summary>
        internal string ResponseBodyString { get; set; }

        internal bool ResponseBodyRead { get; set; }
        internal bool ResponseLocked { get; set; }

        /// <summary>
        /// Is response 100-continue
        /// </summary>
        public bool Is100Continue { get; internal set; }

        /// <summary>
        /// expectation failed returned by server?
        /// </summary>
        public bool ExpectationFailed { get; internal set; }

        /// <summary>
        /// Constructor.
        /// </summary>
        public Response()
        {
            ResponseHeaders = new Dictionary<string, HttpHeader>(StringComparer.OrdinalIgnoreCase);
            NonUniqueResponseHeaders = new Dictionary<string, List<HttpHeader>>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Dispose off 
        /// </summary>
        public void Dispose()
        {
            //not really needed since GC will collect it
            //but just to be on safe side

            ResponseHeaders = null;
            NonUniqueResponseHeaders = null;

            ResponseBody = null;
            ResponseBodyString = null;
        }
    }
}
