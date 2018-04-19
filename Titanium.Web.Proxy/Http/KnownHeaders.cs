namespace Titanium.Web.Proxy.Http
{
    public static class KnownHeaders
    {
        // Both
        public const string Connection = "connection";
        public const string ConnectionClose = "close";
        public const string ConnectionKeepAlive = "keep-alive";

        public const string ContentLength = "content-length";

        public const string ContentType = "content-type";
        public const string ContentTypeCharset = "charset";
        public const string ContentTypeBoundary = "boundary";

        public const string Upgrade = "upgrade";
        public const string UpgradeWebsocket = "websocket";

        // Request headers
        public const string AcceptEncoding = "accept-encoding";

        public const string Authorization = "Authorization";

        public const string Expect = "expect";
        public const string Expect100Continue = "100-continue";

        public const string Host = "host";

        public const string ProxyAuthorization = "Proxy-Authorization";
        public const string ProxyAuthorizationBasic = "basic";

        public const string ProxyConnection = "Proxy-Connection";
        public const string ProxyConnectionClose = "close";

        // Response headers
        public const string ContentEncoding = "content-encoding";
        public const string ContentEncodingDeflate = "deflate";
        public const string ContentEncodingGzip = "gzip";

        public const string Location = "Location";

        public const string ProxyAuthenticate = "Proxy-Authenticate";

        public const string TransferEncoding = "transfer-encoding";
        public const string TransferEncodingChunked = "chunked";
    }
}
