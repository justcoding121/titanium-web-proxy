using System;
using System.ComponentModel;
using System.Text;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Http
{
    /// <summary>
    ///     Http(s) request object
    /// </summary>
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public class Request : RequestResponseBase
    {
        /// <summary>
        ///     Request Method.
        /// </summary>
        public string Method { get; set; }

        /// <summary>
        ///     Request HTTP Uri.
        /// </summary>
        public Uri RequestUri { get; set; }

        /// <summary>
        ///     Is Https?
        /// </summary>
        public bool IsHttps => RequestUri.Scheme == ProxyServer.UriSchemeHttps;

        /// <summary>
        ///     The original request Url.
        /// </summary>
        public string OriginalUrl { get; set; }

        /// <summary>
        ///     Has request body?
        /// </summary>
        public override bool HasBody
        {
            get
            {
                long contentLength = ContentLength;

                // If content length is set to 0 the request has no body
                if (contentLength == 0)
                {
                    return false;
                }

                // Has body only if request is chunked or content length >0
                if (IsChunked || contentLength > 0)
                {
                    return true;
                }

                // has body if POST and when version is http/1.0
                if (Method == "POST" && HttpVersion == HttpHeader.Version10)
                {
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        ///     Http hostname header value if exists.
        ///     Note: Changing this does NOT change host in RequestUri.
        ///     Users can set new RequestUri separately.
        /// </summary>
        public string Host
        {
            get => Headers.GetHeaderValueOrNull(KnownHeaders.Host);
            set => Headers.SetOrAddHeaderValue(KnownHeaders.Host, value);
        }

        /// <summary>
        ///     Does this request has a 100-continue header?
        /// </summary>
        public bool ExpectContinue
        {
            get
            {
                string headerValue = Headers.GetHeaderValueOrNull(KnownHeaders.Expect);
                return headerValue != null && headerValue.Equals(KnownHeaders.Expect100Continue);
            }
        }

        /// <summary>
        ///     Does this request contain multipart/form-data?
        /// </summary>
        public bool IsMultipartFormData => ContentType?.StartsWith("multipart/form-data") == true;

        /// <summary>
        ///     Request Url.
        /// </summary>
        public string Url => RequestUri.OriginalString;

        /// <summary>
        ///     Cancels the client HTTP request without sending to server.
        ///     This should be set when API user responds with custom response.
        /// </summary>
        internal bool CancelRequest { get; set; }

        /// <summary>
        ///     Does this request has an upgrade to websocket header?
        /// </summary>
        public bool UpgradeToWebSocket
        {
            get
            {
                string headerValue = Headers.GetHeaderValueOrNull(KnownHeaders.Upgrade);

                if (headerValue == null)
                {
                    return false;
                }

                return headerValue.EqualsIgnoreCase(KnownHeaders.UpgradeWebsocket);
            }
        }

        /// <summary>
        ///     Did server responsed positively for 100 continue request?
        /// </summary>
        public bool Is100Continue { get; internal set; }

        /// <summary>
        ///     Did server responsed negatively for the request for 100 continue?
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
                sb.AppendLine(CreateRequestLine(Method, OriginalUrl, HttpVersion));
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

            // GET request don't have a request body to read
            if (!HasBody)
            {
                throw new BodyNotFoundException("Request don't have a body. " +
                                                "Please verify that this request is a Http POST/PUT/PATCH and request " +
                                                "content length is greater than zero before accessing the body.");
            }

            if (!IsBodyRead)
            {
                if (Locked)
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

        internal static string CreateRequestLine(string httpMethod, string httpUrl, Version version)
        {
            return $"{httpMethod} {httpUrl} HTTP/{version.Major}.{version.Minor}";
        }

        internal static void ParseRequestLine(string httpCmd, out string httpMethod, out string httpUrl,
            out Version version)
        {
            // break up the line into three components (method, remote URL & Http Version)
            var httpCmdSplit = httpCmd.Split(ProxyConstants.SpaceSplit, 3);

            if (httpCmdSplit.Length < 2)
            {
                throw new Exception("Invalid HTTP request line: " + httpCmd);
            }

            // Find the request Verb
            httpMethod = httpCmdSplit[0];
            if (!isAllUpper(httpMethod))
            {
                httpMethod = httpMethod.ToUpper();
            }

            httpUrl = httpCmdSplit[1];

            // parse the HTTP version
            version = HttpHeader.Version11;
            if (httpCmdSplit.Length == 3)
            {
                string httpVersion = httpCmdSplit[2].Trim();

                if (httpVersion.EqualsIgnoreCase("HTTP/1.0"))
                {
                    version = HttpHeader.Version10;
                }
            }
        }

        private static bool isAllUpper(string input)
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

    }
}
