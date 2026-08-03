namespace Titanium.Web.Proxy.Http;

/// <summary>
///     Well known http headers.
/// </summary>
public static class KnownHeaders
{
    // Both
    public static readonly KnownHeader Connection = "Connection";
    public static readonly KnownHeader ConnectionClose = "close";
    public static readonly KnownHeader ConnectionKeepAlive = "keep-alive";

    public static readonly KnownHeader ContentLength = "Content-Length";
    public static readonly KnownHeader ContentLengthHttp2 = "content-length";

    public static readonly KnownHeader ContentType = "Content-Type";
    public static readonly KnownHeader ContentTypeCharset = "charset";
    public static readonly KnownHeader ContentTypeBoundary = "boundary";

    public static readonly KnownHeader Upgrade = "Upgrade";
    public static readonly KnownHeader UpgradeWebsocket = "websocket";

    /// <summary>
    ///     Declares which header field names will appear in the trailer of a chunked message (RFC 9110 §6.5).
    ///     Titanium does not manage this header automatically; set/read it explicitly alongside
    ///     <see cref="RequestResponseBase.TrailingHeaders" /> if you want it announced/observed.
    /// </summary>
    public static readonly KnownHeader Trailer = "Trailer";

    // Request headers
    public static readonly KnownHeader Accept = "Accept";
    public static readonly KnownHeader AcceptEncoding = "Accept-Encoding";

    public static readonly KnownHeader Authorization = "Authorization";

    public static readonly KnownHeader Expect = "Expect";
    public static readonly KnownHeader Expect100Continue = "100-continue";

    public static readonly KnownHeader Host = "Host";

    public static readonly KnownHeader ProxyAuthorization = "Proxy-Authorization";
    public static readonly KnownHeader ProxyAuthorizationBasic = "basic";

    public static readonly KnownHeader ProxyConnection = "Proxy-Connection";
    public static readonly KnownHeader ProxyConnectionClose = "close";

    // Response headers
    public static readonly KnownHeader ContentEncoding = "Content-Encoding";
    public static readonly KnownHeader ContentEncodingDeflate = "deflate";
    public static readonly KnownHeader ContentEncodingGzip = "gzip";
    public static readonly KnownHeader ContentEncodingBrotli = "br";

    public static readonly KnownHeader Location = "Location";

    public static readonly KnownHeader ProxyAuthenticate = "Proxy-Authenticate";

    public static readonly KnownHeader TransferEncoding = "Transfer-Encoding";
    public static readonly KnownHeader TransferEncodingChunked = "chunked";
}
