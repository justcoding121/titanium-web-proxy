using System;
using System.ComponentModel;
using System.Text;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Http
{
    /// <summary>
    ///     Http(s) response object
    /// </summary>
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public class Response : RequestResponseBase
    {
        /// <summary>
        ///     Constructor.
        /// </summary>
        public Response()
        {
        }

        /// <summary>
        ///     Constructor.
        /// </summary>
        public Response(byte[] body)
        {
            Body = body;
        }

        /// <summary>
        ///     Response Status Code.
        /// </summary>
        public int StatusCode { get; set; }

        /// <summary>
        ///     Response Status description.
        /// </summary>
        public string StatusDescription { get; set; }

        /// <summary>
        ///     Has response body?
        /// </summary>
        public override bool HasBody
        {
            get
            {
                long contentLength = ContentLength;

                // If content length is set to 0 the response has no body
                if (contentLength == 0)
                {
                    return false;
                }

                // Has body only if response is chunked or content length >0
                // If none are true then check if connection:close header exist, if so write response until server or client terminates the connection
                if (IsChunked || contentLength > 0 || !KeepAlive)
                {
                    return true;
                }

                // has response if connection:keep-alive header exist and when version is http/1.0
                // Because in Http 1.0 server can return a response without content-length (expectation being client would read until end of stream)
                if (KeepAlive && HttpVersion == HttpHeader.Version10)
                {
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        ///     Keep the connection alive?
        /// </summary>
        public bool KeepAlive
        {
            get
            {
                string headerValue = Headers.GetHeaderValueOrNull(KnownHeaders.Connection);

                if (headerValue != null)
                {
                    if (headerValue.EqualsIgnoreCase(KnownHeaders.ConnectionClose))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>
        ///     Is response 100-continue
        /// </summary>
        public bool Is100Continue { get; internal set; }

        /// <summary>
        ///     expectation failed returned by server?
        /// </summary>
        public bool ExpectationFailed { get; internal set; }

        /// <summary>
        ///     Gets the header text.
        /// </summary>
        public override string HeaderText
        {
            get
            {
                var sb = new StringBuilder();
                sb.AppendLine(CreateResponseLine(HttpVersion, StatusCode, StatusDescription));
                foreach (var header in Headers)
                {
                    sb.AppendLine(header.ToString());
                }

                sb.AppendLine();
                return sb.ToString();
            }
        }

        internal override void EnsureBodyAvailable(bool throwWhenNotReadYet = true)
        {
            if (BodyInternal != null)
            {
                return;
            }

            if (!IsBodyRead && throwWhenNotReadYet)
            {
                throw new Exception("Response body is not read yet. " +
                                    "Use SessionEventArgs.GetResponseBody() or SessionEventArgs.GetResponseBodyAsString() " +
                                    "method to read the response body.");
            }
        }

        internal static string CreateResponseLine(Version version, int statusCode, string statusDescription)
        {
            return $"HTTP/{version.Major}.{version.Minor} {statusCode} {statusDescription}";
        }

        internal static void ParseResponseLine(string httpStatus, out Version version, out int statusCode,
            out string statusDescription)
        {
            var httpResult = httpStatus.Split(ProxyConstants.SpaceSplit, 3);
            if (httpResult.Length != 3)
            {
                throw new Exception("Invalid HTTP status line: " + httpStatus);
            }

            string httpVersion = httpResult[0];

            version = HttpHeader.Version11;
            if (httpVersion.EqualsIgnoreCase("HTTP/1.0"))
            {
                version = HttpHeader.Version10;
            }

            statusCode = int.Parse(httpResult[1]);
            statusDescription = httpResult[2];
        }
    }
}
