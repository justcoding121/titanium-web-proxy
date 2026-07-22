namespace Titanium.Web.Proxy.Http;

/// <summary>
///     Well known http headers.
/// </summary>
public static class KnownHeaders
{
    // Both
    public static KnownHeader Connection = "Connection";
    public static KnownHeader ConnectionClose = "close";
    public static KnownHeader ConnectionKeepAlive = "keep-alive";

    public static KnownHeader ContentLength = "Content-Length";
    public static KnownHeader ContentLengthHttp2 = "content-length";

    public static KnownHeader ContentType = "Content-Type";
    public static KnownHeader ContentTypeCharset = "charset";
    public static KnownHeader ContentTypeBoundary = "boundary";

    public static KnownHeader Upgrade = "Upgrade";
    public static KnownHeader UpgradeWebsocket = "websocket";

    /// <summary>
    ///     Declares which header field names will appear in the trailer of a chunked message (RFC 9110 §6.5).
    ///     Titanium does not manage this header automatically; set/read it explicitly alongside
    ///     <see cref="RequestResponseBase.TrailingHeaders" /> if you want it announced/observed.
    /// </summary>
    public static KnownHeader Trailer = "Trailer";

    // Request headers
    public static KnownHeader AcceptEncoding = "Accept-Encoding";

    public static KnownHeader Authorization = "Authorization";

    public static KnownHeader Expect = "Expect";
    public static KnownHeader Expect100Continue = "100-continue";

    public static KnownHeader Host = "Host";

    public static KnownHeader ProxyAuthorization = "Proxy-Authorization";
    public static KnownHeader ProxyAuthorizationBasic = "basic";

    public static KnownHeader ProxyConnection = "Proxy-Connection";
    public static KnownHeader ProxyConnectionClose = "close";

    // Response headers
    public static KnownHeader ContentEncoding = "Content-Encoding";
    public static KnownHeader ContentEncodingDeflate = "deflate";
    public static KnownHeader ContentEncodingGzip = "gzip";
    public static KnownHeader ContentEncodingBrotli = "br";

    public static KnownHeader Location = "Location";

    public static KnownHeader ProxyAuthenticate = "Proxy-Authenticate";

    public static KnownHeader TransferEncoding = "Transfer-Encoding";
    public static KnownHeader TransferEncodingChunked = "chunked";
}