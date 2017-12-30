using System;
using System.ComponentModel;
using System.Text;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Http
{
    /// <summary>
    /// A HTTP(S) request object
    /// </summary>
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public class Request
    {
        /// <summary>
        /// Cached request body as byte array
        /// </summary>
        private byte[] body;

        /// <summary>
        /// Cached request body as string
        /// </summary>
        private string bodyString;

        /// <summary>
        /// Request Method
        /// </summary>
        public string Method { get; set; }

        /// <summary>
        /// Request HTTP Uri
        /// </summary>
        public Uri RequestUri { get; set; }

        /// <summary>
        /// Is Https?
        /// </summary>
        public bool IsHttps => RequestUri.Scheme == ProxyServer.UriSchemeHttps;

        /// <summary>
        /// The original request Url.
        /// </summary>
        public string OriginalUrl { get; set; }

        /// <summary>
        /// Request Http Version
        /// </summary>
        public Version HttpVersion { get; set; }

        /// <summary>
        /// Keeps the request body data after the session is finished
        /// </summary>
        public bool KeepBody { get; set; }

        /// <summary>
        /// Has request body?
        /// </summary>
        public bool HasBody => Method == "POST" || Method == "PUT" || Method == "PATCH";

        /// <summary>
        /// Http hostname header value if exists
        /// Note: Changing this does NOT change host in RequestUri
        /// Users can set new RequestUri separately
        /// </summary>
        public string Host
        {
            get => Headers.GetHeaderValueOrNull("host");
            set => Headers.SetOrAddHeaderValue("host", value);
        }

        /// <summary>
        /// Content encoding header value
        /// </summary>
        public string ContentEncoding => Headers.GetHeaderValueOrNull("content-encoding");

        /// <summary>
        /// Request content-length
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
        /// Request content-type
        /// </summary>
        public string ContentType
        {
            get => Headers.GetHeaderValueOrNull("content-type");
            set => Headers.SetOrAddHeaderValue("content-type", value);
        }

        /// <summary>
        /// Is request body send as chunked bytes
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
        /// Does this request has a 100-continue header?
        /// </summary>
        public bool ExpectContinue
        {
            get
            {
                string headerValue = Headers.GetHeaderValueOrNull("expect");
                return headerValue != null && headerValue.Equals("100-continue");
            }
        }

        public bool IsMultipartFormData => ContentType?.StartsWith("multipart/form-data") == true;

        /// <summary>
        /// Request Url
        /// </summary>
        public string Url => RequestUri.OriginalString;

        /// <summary>
        /// Encoding for this request
        /// </summary>
        public Encoding Encoding => HttpHelper.GetEncodingFromContentType(ContentType);

        /// <summary>
        /// Terminates the underlying Tcp Connection to client after current request
        /// </summary>
        internal bool CancelRequest { get; set; }

        internal void EnsureBodyAvailable(bool throwWhenNotReadYet = true)
        {
            //GET request don't have a request body to read
            if (!HasBody)
            {
                throw new BodyNotFoundException("Request don't have a body. " + "Please verify that this request is a Http POST/PUT/PATCH and request " +
                                                "content length is greater than zero before accessing the body.");
            }

            if (!IsBodyRead)
            {
                if (RequestLocked)
                {
                    throw new Exception("You cannot get the request body after request is made to server.");
                }

                if (throwWhenNotReadYet)
                {
                    throw new Exception("Request body is not read yet. " +
                                        "Use SessionEventArgs.GetRequestBody() or SessionEventArgs.GetRequestBodyAsString() " +
                                        "method to read the request body.");
                }
            }
        }

        /// <summary>
        /// Request body as byte array
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
        /// Request body as string
        /// Use the encoding specified in request to decode the byte[] data to string
        /// </summary>
        [Browsable(false)]
        public string BodyString => bodyString ?? (bodyString = Encoding.GetString(Body));

        /// <summary>
        /// Request body was read by user?
        /// </summary>
        public bool IsBodyRead { get; internal set; }

        /// <summary>
        /// Request is ready to be sent (user callbacks are complete?)
        /// </summary>
        internal bool RequestLocked { get; set; }

        /// <summary>
        /// Does this request has an upgrade to websocket header?
        /// </summary>
        public bool UpgradeToWebSocket
        {
            get
            {
                string headerValue = Headers.GetHeaderValueOrNull("upgrade");

                if (headerValue == null)
                {
                    return false;
                }

                return headerValue.Equals("websocket", StringComparison.CurrentCultureIgnoreCase);
            }
        }

        /// <summary>
        /// Request header collection
        /// </summary>
        public HeaderCollection Headers { get; } = new HeaderCollection();

        /// <summary>
        /// Does server responsed positively for 100 continue request
        /// </summary>
        public bool Is100Continue { get; internal set; }

        /// <summary>
        /// Server responsed negatively for the request for 100 continue
        /// </summary>
        public bool ExpectationFailed { get; internal set; }

        /// <summary>
        /// Gets the header text.
        /// </summary>
        public string HeaderText
        {
            get
            {
                var sb = new StringBuilder();
                sb.AppendLine(CreateRequestLine(Method, OriginalUrl, HttpVersion));
                foreach (var header in Headers)
                {
                    sb.AppendLine(header.ToString());
                }

                sb.AppendLine();
                return sb.ToString();
            }
        }

        internal static string CreateRequestLine(string httpMethod, string httpUrl, Version version)
        {
            return $"{httpMethod} {httpUrl} HTTP/{version.Major}.{version.Minor}";
        }

        internal static void ParseRequestLine(string httpCmd, out string httpMethod, out string httpUrl, out Version version)
        {
            //break up the line into three components (method, remote URL & Http Version)
            var httpCmdSplit = httpCmd.Split(ProxyConstants.SpaceSplit, 3);

            if (httpCmdSplit.Length < 2)
            {
                throw new Exception("Invalid HTTP request line: " + httpCmd);
            }

            //Find the request Verb
            httpMethod = httpCmdSplit[0];
            if (!IsAllUpper(httpMethod))
            {
                //method should be upper cased: https://tools.ietf.org/html/rfc7231#section-4

                //todo: create protocol violation message

                //fix it
                httpMethod = httpMethod.ToUpper();
            }

            httpUrl = httpCmdSplit[1];

            //parse the HTTP version
            version = HttpHeader.Version11;
            if (httpCmdSplit.Length == 3)
            {
                string httpVersion = httpCmdSplit[2].Trim();

                if (string.Equals(httpVersion, "HTTP/1.0", StringComparison.OrdinalIgnoreCase))
                {
                    version = HttpHeader.Version10;
                }
            }
        }

        private static bool IsAllUpper(string input)
        {
            for (int i = 0; i < input.Length; i++)
            {
                char ch = input[i];
                if (ch < 'A' || ch > 'Z')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Finish the session
        /// </summary>
        public void FinishSession()
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
