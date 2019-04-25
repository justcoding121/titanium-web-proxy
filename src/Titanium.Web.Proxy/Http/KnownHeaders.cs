namespace Titanium.Web.Proxy.Http
{
    /// <summary>
    /// Well known http headers.
    /// </summary>
    public static class KnownHeaders
    {
        // Both
        public const string Connection = "Connection";
        public const string ConnectionClose = "close";
        public const string ConnectionKeepAlive = "keep-alive";

        public const string ContentLength = "Content-Length";

        public const string ContentType = "Content-Type";
        public const string ContentTypeCharset = "charset";
        public const string ContentTypeBoundary = "boundary";

        public const string Upgrade = "Upgrade";
        public const string UpgradeWebsocket = "websocket";

        // Request headers
        public const string AcceptEncoding = "Accept-Encoding";

        public const string Authorization = "Authorization";

        public const string Expect = "Expect";
        public const string Expect100Continue = "100-continue";

        public const string Host = "Host";

        public const string ProxyAuthorization = "Proxy-Authorization";
        public const string ProxyAuthorizationBasic = "basic";

        public const string ProxyConnection = "Proxy-Connection";
        public const string ProxyConnectionClose = "close";

        // Response headers
        public const string ContentEncoding = "Content-Encoding";
        public const string ContentEncodingDeflate = "deflate";
        public const string ContentEncodingGzip = "gzip";
        public const string ContentEncodingBrotli = "br";

        public const string Location = "Location";

        public const string ProxyAuthenticate = "Proxy-Authenticate";

        public const string TransferEncoding = "Transfer-Encoding";
        public const string TransferEncodingChunked = "chunked";
    }
}
