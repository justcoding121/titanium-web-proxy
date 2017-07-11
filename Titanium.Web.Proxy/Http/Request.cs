using System;
using System.Text;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Shared;

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
        /// Is Https?
        /// </summary>
        public bool IsHttps => RequestUri.Scheme == ProxyServer.UriSchemeHttps;

        /// <summary>
        /// The original request Url.
        /// </summary>
        public string OriginalRequestUrl { get; set; }

        /// <summary>
        /// Request Http Version
        /// </summary>
        public Version HttpVersion { get; set; }

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
            get
            {
                return RequestHeaders.GetHeaderValueOrNull("host");
            }
            set
            {
                RequestHeaders.SetOrAddHeaderValue("host", value);
            }
        }

        /// <summary>
        /// Content encoding header value
        /// </summary>
        public string ContentEncoding => RequestHeaders.GetHeaderValueOrNull("content-encoding");

        /// <summary>
        /// Request content-length
        /// </summary>
        public long ContentLength
        {
            get
            {
                string headerValue = RequestHeaders.GetHeaderValueOrNull("content-length");

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
                    RequestHeaders.SetOrAddHeaderValue("content-length", value.ToString());
                    IsChunked = false;
                }
                else
                {
                    RequestHeaders.RemoveHeader("content-length");
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
                return RequestHeaders.GetHeaderValueOrNull("content-type");
            }
            set
            {
                RequestHeaders.SetOrAddHeaderValue("content-type", value);
            }
        }

        /// <summary>
        /// Is request body send as chunked bytes
        /// </summary>
        public bool IsChunked
        {
            get
            {
                string headerValue = RequestHeaders.GetHeaderValueOrNull("transfer-encoding");
                return headerValue != null && headerValue.ContainsIgnoreCase("chunked");
            }
            set
            {
                if (value)
                {
                    RequestHeaders.SetOrAddHeaderValue("transfer-encoding", "chunked");
                    ContentLength = -1;
                }
                else
                {
                    RequestHeaders.RemoveHeader("transfer-encoding");
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
                string headerValue = RequestHeaders.GetHeaderValueOrNull("expect");
                return headerValue != null && headerValue.Equals("100-continue");
            }
        }

        /// <summary>
        /// Request Url
        /// </summary>
        public string Url => RequestUri.OriginalString;

        /// <summary>
        /// Encoding for this request
        /// </summary>
        public Encoding Encoding => this.GetEncoding();

        /// <summary>
        /// Terminates the underlying Tcp Connection to client after current request
        /// </summary>
        internal bool CancelRequest { get; set; }

        /// <summary>
        /// Request body as byte array
        /// </summary>
        internal byte[] RequestBody { get; set; }

        /// <summary>
        /// Request body as string
        /// </summary>
        internal string RequestBodyString { get; set; }

        /// <summary>
        /// Request body was read by user?
        /// </summary>
        internal bool RequestBodyRead { get; set; }

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
                string headerValue = RequestHeaders.GetHeaderValueOrNull("upgrade");

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
        public HeaderCollection RequestHeaders { get; private set; } = new HeaderCollection();

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
                sb.AppendLine($"{Method} {OriginalRequestUrl} HTTP/{HttpVersion.Major}.{HttpVersion.Minor}");
                foreach (var header in RequestHeaders)
                {
                    sb.AppendLine(header.ToString());
                }

                sb.AppendLine();
                return sb.ToString();
            }
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
        /// Constructor.
        /// </summary>
        public Request()
        {
        }

        /// <summary>
        /// Dispose off 
        /// </summary>
        public void Dispose()
        {
            //not really needed since GC will collect it
            //but just to be on safe side

            RequestHeaders = null;

            RequestBody = null;
            RequestBodyString = null;
        }
    }
}
