using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>
        /// Encoding used in response
        /// </summary>
        public Encoding Encoding => this.GetResponseCharacterEncoding();

        /// <summary>
        /// Content encoding for this response
        /// </summary>
        public string ContentEncoding
        {
            get
            {
                return ResponseHeaders.GetHeaderValueOrNull("content-encoding")?.Trim();
            }
        }

        /// <summary>
        /// Http version
        /// </summary>
        public Version HttpVersion { get; set; }

        /// <summary>
        /// Keep the connection alive?
        /// </summary>
        public bool ResponseKeepAlive
        {
            get
            {
                string headerValue = ResponseHeaders.GetHeaderValueOrNull("connection");

                if (headerValue != null)
                {
                    if (headerValue.ContainsIgnoreCase("close"))
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
                return ResponseHeaders.GetHeaderValueOrNull("content-type");
            }
        }

        /// <summary>
        /// Length of response body
        /// </summary>
        public long ContentLength
        {
            get
            {
                string headerValue = ResponseHeaders.GetHeaderValueOrNull("content-length");

                if (headerValue == null)
                {
                    return -1;
                }

                long contentLen;
                long.TryParse(headerValue, out contentLen);
                if (contentLen >= 0)
                {
                    return contentLen;
                }

                return -1;
            }
            set
            {
                if (value >= 0)
                {
                    ResponseHeaders.SetOrAddHeaderValue("content-length", value.ToString());
                    IsChunked = false;
                }
                else
                {
                    ResponseHeaders.RemoveHeader("content-length");
                }
            }
        }

        /// <summary>
        /// Response transfer-encoding is chunked?
        /// </summary>
        public bool IsChunked
        {
            get
            {
                string headerValue = ResponseHeaders.GetHeaderValueOrNull("transfer-encoding");
                return headerValue != null && headerValue.ContainsIgnoreCase("chunked");
            }
            set
            {
                if (value)
                {
                    ResponseHeaders.SetOrAddHeaderValue("transfer-encoding", "chunked");
                    ContentLength = -1;
                }
                else
                {
                    ResponseHeaders.RemoveHeader("transfer-encoding");
                }
            }
        }

        /// <summary>
        /// Collection of all response headers
        /// </summary>
        public HeaderCollection ResponseHeaders { get; set; }

        /// <summary>
        /// response body content as byte array
        /// </summary>
        internal byte[] ResponseBody { get; set; }

        /// <summary>
        /// response body as string
        /// </summary>
        internal string ResponseBodyString { get; set; }

        /// <summary>
        /// Was response body read by user
        /// </summary>
        internal bool ResponseBodyRead { get; set; }

        /// <summary>
        /// Is response is no more modifyable by user (user callbacks complete?)
        /// </summary>
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
            ResponseHeaders = new HeaderCollection();
        }

        /// <summary>
        /// Dispose off 
        /// </summary>
        public void Dispose()
        {
            //not really needed since GC will collect it
            //but just to be on safe side

            ResponseHeaders = null;

            ResponseBody = null;
            ResponseBodyString = null;
        }
    }
}
