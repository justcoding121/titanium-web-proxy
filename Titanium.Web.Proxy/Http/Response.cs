using System;
using System.Text;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Shared;

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
        public int ResponseStatusCode { get; set; }

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
        public string ContentEncoding => ResponseHeaders.GetHeaderValueOrNull("content-encoding")?.Trim();

        /// <summary>
        /// Http version
        /// </summary>
        public Version HttpVersion { get; set; }

        /// <summary>
        /// Has response body?
        /// </summary>
        public bool HasBody
        {
            get
            {
                //Has body only if response is chunked or content length >0
                //If none are true then check if connection:close header exist, if so write response until server or client terminates the connection
                if (IsChunked || ContentLength > 0 || !ResponseKeepAlive)
                {
                    return true;
                }

                //has response if connection:keep-alive header exist and when version is http/1.0
                //Because in Http 1.0 server can return a response without content-length (expectation being client would read until end of stream)
                if (ResponseKeepAlive && HttpVersion.Minor == 0)
                {
                    return true;
                }

                return false;
            }
        }

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
        public string ContentType => ResponseHeaders.GetHeaderValueOrNull("content-type");

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
        public HeaderCollection ResponseHeaders { get; private set; } = new HeaderCollection();

        /// <summary>
        /// Response body content as byte array
        /// </summary>
        internal byte[] ResponseBody { get; set; }

        /// <summary>
        /// Response body as string
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
        /// Gets the resposne status.
        /// </summary>
        public string ResponseStatus => $"HTTP/{HttpVersion?.Major}.{HttpVersion?.Minor} {ResponseStatusCode} {ResponseStatusDescription}";

        /// <summary>
        /// Gets the header text.
        /// </summary>
        public string HeaderText
        {
            get
            {
                var sb = new StringBuilder();
                sb.AppendLine(ResponseStatus);
                foreach (var header in ResponseHeaders)
                {
                    sb.AppendLine(header.ToString());
                }

                sb.AppendLine();
                return sb.ToString();
            }
        }

        internal static void ParseResponseLine(string httpStatus, out Version version, out int statusCode, out string statusDescription)
        {
            var httpResult = httpStatus.Split(ProxyConstants.SpaceSplit, 3);
            if (httpResult.Length != 3)
            {
                throw new Exception("Invalid HTTP status line: " + httpStatus);
            }

            string httpVersion = httpResult[0];

            version = HttpHeader.Version11;
            if (string.Equals(httpVersion, "HTTP/1.0", StringComparison.OrdinalIgnoreCase))
            {
                version = HttpHeader.Version10;
            }

            statusCode = int.Parse(httpResult[1]);
            statusDescription = httpResult[2];
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        public Response()
        {
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
