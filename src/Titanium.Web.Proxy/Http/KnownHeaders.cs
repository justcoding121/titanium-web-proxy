using System;

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

    // Common wire names not part of the historical public surface — interned on the parse path.
    internal static readonly KnownHeader UserAgent = "User-Agent";
    internal static readonly KnownHeader Date = "Date";
    internal static readonly KnownHeader Server = "Server";
    internal static readonly KnownHeader Cookie = "Cookie";
    internal static readonly KnownHeader KeepAlive = "Keep-Alive";
    internal static readonly KnownHeader AcceptLanguage = "Accept-Language";
    internal static readonly KnownHeader Via = "Via";

    /// <summary>
    ///     Maps a header name span to a shared <see cref="KnownHeader" /> so parse/serialize reuse
    ///     interned name strings and <see cref="Models.ByteString" /> bytes.
    /// </summary>
    internal static bool TryMatchName(ReadOnlySpan<char> name, out KnownHeader header)
    {
        name = name.Trim();
        header = null!;
        switch (name.Length)
        {
            case 3:
                if (Via.Equals(name)) { header = Via; return true; }
                return false;
            case 4:
                if (Host.Equals(name)) { header = Host; return true; }
                if (Date.Equals(name)) { header = Date; return true; }
                return false;
            case 6:
                if (Accept.Equals(name)) { header = Accept; return true; }
                if (Cookie.Equals(name)) { header = Cookie; return true; }
                if (Expect.Equals(name)) { header = Expect; return true; }
                if (Server.Equals(name)) { header = Server; return true; }
                return false;
            case 7:
                if (Upgrade.Equals(name)) { header = Upgrade; return true; }
                if (Trailer.Equals(name)) { header = Trailer; return true; }
                return false;
            case 8:
                if (Location.Equals(name)) { header = Location; return true; }
                return false;
            case 10:
                if (Connection.Equals(name)) { header = Connection; return true; }
                if (UserAgent.Equals(name)) { header = UserAgent; return true; }
                if (KeepAlive.Equals(name)) { header = KeepAlive; return true; }
                return false;
            case 12:
                if (ContentType.Equals(name)) { header = ContentType; return true; }
                return false;
            case 13:
                if (Authorization.Equals(name)) { header = Authorization; return true; }
                return false;
            case 14:
                if (ContentLength.Equals(name)) { header = ContentLength; return true; }
                if (ContentLengthHttp2.Equals(name)) { header = ContentLengthHttp2; return true; }
                return false;
            case 15:
                if (AcceptEncoding.Equals(name)) { header = AcceptEncoding; return true; }
                if (AcceptLanguage.Equals(name)) { header = AcceptLanguage; return true; }
                return false;
            case 16:
                if (ContentEncoding.Equals(name)) { header = ContentEncoding; return true; }
                if (ProxyConnection.Equals(name)) { header = ProxyConnection; return true; }
                return false;
            case 17:
                if (TransferEncoding.Equals(name)) { header = TransferEncoding; return true; }
                return false;
            case 18:
                if (ProxyAuthenticate.Equals(name)) { header = ProxyAuthenticate; return true; }
                return false;
            case 19:
                if (ProxyAuthorization.Equals(name)) { header = ProxyAuthorization; return true; }
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    ///     Interns frequent header values (<c>keep-alive</c>, <c>close</c>, <c>chunked</c>, …).
    /// </summary>
    internal static bool TryMatchValue(ReadOnlySpan<char> value, out KnownHeader header)
    {
        value = value.Trim();
        header = null!;
        switch (value.Length)
        {
            case 5:
                if (ConnectionClose.Equals(value)) { header = ConnectionClose; return true; }
                return false;
            case 7:
                if (TransferEncodingChunked.Equals(value)) { header = TransferEncodingChunked; return true; }
                return false;
            case 10:
                if (ConnectionKeepAlive.Equals(value)) { header = ConnectionKeepAlive; return true; }
                return false;
            default:
                return false;
        }
    }
}
