using System;
using System.ComponentModel;
using System.Text;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Http
{
    /// <summary>
    /// Http(s) response object
    /// </summary>
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public class Response
    {
        /// <summary>
        /// Cached response body content as byte array
        /// </summary>
        private byte[] body;

        /// <summary>
        /// Cached response body as string
        /// </summary>
        private string bodyString;

        /// <summary>
        /// Response Status Code.
        /// </summary>
        public int StatusCode { get; set; }

        /// <summary>
        /// Response Status description.
        /// </summary>
        public string StatusDescription { get; set; }

        /// <summary>
        /// Encoding used in response
        /// </summary>
        public Encoding Encoding => HttpHelper.GetEncodingFromContentType(ContentType);

        /// <summary>
        /// Content encoding for this response
        /// </summary>
        public string ContentEncoding => Headers.GetHeaderValueOrNull("content-encoding")?.Trim();

        /// <summary>
        /// Http version
        /// </summary>
        public Version HttpVersion { get; set; }

        /// <summary>
        /// Keeps the response body data after the session is finished
        /// </summary>
        public bool KeepBody { get; set; }

        /// <summary>
        /// Has response body?
        /// </summary>
        public bool HasBody
        {
            get
            {
                //Has body only if response is chunked or content length >0
                //If none are true then check if connection:close header exist, if so write response until server or client terminates the connection
                if (IsChunked || ContentLength > 0 || !KeepAlive)
                {
                    return true;
                }

                //has response if connection:keep-alive header exist and when version is http/1.0
                //Because in Http 1.0 server can return a response without content-length (expectation being client would read until end of stream)
                if (KeepAlive && HttpVersion.Minor == 0)
                {
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Keep the connection alive?
        /// </summary>
        public bool KeepAlive
        {
            get
            {
                string headerValue = Headers.GetHeaderValueOrNull("connection");

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
        public string ContentType => Headers.GetHeaderValueOrNull("content-type");

        /// <summary>
        /// Length of response body
        /// </summary>
        public long ContentLength
        {
            get
            {
                string headerValue = Headers.GetHeaderValueOrNull("content-length");

                if (headerValue == null)
                {
                    return -1;
                }

                long.TryParse(headerValue, out long contentLen);
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
                    Headers.SetOrAddHeaderValue("content-length", value.ToString());
                    IsChunked = false;
                }
                else
                {
                    Headers.RemoveHeader("content-length");
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
                string headerValue = Headers.GetHeaderValueOrNull("transfer-encoding");
                return headerValue != null && headerValue.ContainsIgnoreCase("chunked");
            }
            set
            {
                if (value)
                {
                    Headers.SetOrAddHeaderValue("transfer-encoding", "chunked");
                    ContentLength = -1;
                }
                else
                {
                    Headers.RemoveHeader("transfer-encoding");
                }
            }
        }

        /// <summary>
        /// Collection of all response headers
        /// </summary>
        public HeaderCollection Headers { get; } = new HeaderCollection();

        internal void EnsureBodyAvailable()
        {
            if (!IsBodyRead)
            {
                throw new Exception("Response body is not read yet. " +
                                    "Use SessionEventArgs.GetResponseBody() or SessionEventArgs.GetResponseBodyAsString() " +
                                    "method to read the response body.");
            }
        }

        /// <summary>
        /// Response body as byte array
        /// </summary>
        [Browsable(false)]
        public byte[] Body
        {
            get
            {
                EnsureBodyAvailable();
                return body;
            }
            internal set
            {
                body = value;
                bodyString = null;
            }
        }

        /// <summary>
        /// Response body as string
        /// Use the encoding specified in response to decode the byte[] data to string
        /// </summary>
        [Browsable(false)]
        public string BodyString => bodyString ?? (bodyString = Encoding.GetString(Body));

        /// <summary>
        /// Was response body read by user
        /// </summary>
        public bool IsBodyRead { get; internal set; }

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
        public string Status => $"HTTP/{HttpVersion?.Major}.{HttpVersion?.Minor} {StatusCode} {StatusDescription}";

        /// <summary>
        /// Gets the header text.
        /// </summary>
        public string HeaderText
        {
            get
            {
                var sb = new StringBuilder();
                sb.AppendLine(Status);
                foreach (var header in Headers)
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
        /// Finish the session
        /// </summary>
        internal void FinishSession()
        {
            if (!KeepBody)
            {
                body = null;
                bodyString = null;
            }
        }

        public override string ToString()
        {
            return HeaderText;
        }
    }
}
