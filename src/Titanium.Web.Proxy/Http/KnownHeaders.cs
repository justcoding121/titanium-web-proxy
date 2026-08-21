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
    public static readonly KnownHeader ContentEncodingIdentity = "identity";

    public static readonly KnownHeader Location = "Location";

    public static readonly KnownHeader AltSvc = "Alt-Svc";

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
                return TryAssign(name, Via, out header);
            case 4:
                return TryAssign(name, Host, Date, out header);
            case 6:
                return TryAssign(name, Accept, Cookie, Expect, Server, out header);
            case 7:
                return TryAssign(name, Upgrade, Trailer, out header);
            case 8:
                return TryAssign(name, Location, out header);
            case 10:
                return TryAssign(name, Connection, UserAgent, KeepAlive, out header);
            case 12:
                return TryAssign(name, ContentType, out header);
            case 13:
                return TryAssign(name, Authorization, out header);
            case 14:
                return TryAssign(name, ContentLength, ContentLengthHttp2, out header);
            case 15:
                return TryAssign(name, AcceptEncoding, AcceptLanguage, out header);
            case 16:
                return TryAssign(name, ContentEncoding, ProxyConnection, out header);
            case 17:
                return TryAssign(name, TransferEncoding, out header);
            case 18:
                return TryAssign(name, ProxyAuthenticate, out header);
            case 19:
                return TryAssign(name, ProxyAuthorization, out header);
            default:
                return false;
        }
    }

    /// <summary>Byte-span overload for header lines parsed without <c>Encoding.GetString</c>.</summary>
    internal static bool TryMatchName(ReadOnlySpan<byte> name, out KnownHeader header)
    {
        name = TrimAscii(name);
        header = null!;
        switch (name.Length)
        {
            case 3:
                return TryAssign(name, Via, out header);
            case 4:
                return TryAssign(name, Host, Date, out header);
            case 6:
                return TryAssign(name, Accept, Cookie, Expect, Server, out header);
            case 7:
                return TryAssign(name, Upgrade, Trailer, out header);
            case 8:
                return TryAssign(name, Location, out header);
            case 10:
                return TryAssign(name, Connection, UserAgent, KeepAlive, out header);
            case 12:
                return TryAssign(name, ContentType, out header);
            case 13:
                return TryAssign(name, Authorization, out header);
            case 14:
                return TryAssign(name, ContentLength, ContentLengthHttp2, out header);
            case 15:
                return TryAssign(name, AcceptEncoding, AcceptLanguage, out header);
            case 16:
                return TryAssign(name, ContentEncoding, ProxyConnection, out header);
            case 17:
                return TryAssign(name, TransferEncoding, out header);
            case 18:
                return TryAssign(name, ProxyAuthenticate, out header);
            case 19:
                return TryAssign(name, ProxyAuthorization, out header);
            default:
                return false;
        }
    }

    private static bool TryAssign(ReadOnlySpan<char> name, KnownHeader candidate, out KnownHeader header)
    {
        if (candidate.Equals(name))
        {
            header = candidate;
            return true;
        }

        header = null!;
        return false;
    }

    private static bool TryAssign(ReadOnlySpan<byte> name, KnownHeader candidate, out KnownHeader header)
    {
        if (candidate.Equals(name))
        {
            header = candidate;
            return true;
        }

        header = null!;
        return false;
    }

    private static bool TryAssign(ReadOnlySpan<char> name, KnownHeader first, KnownHeader second, out KnownHeader header)
        => TryAssign(name, first, out header) || TryAssign(name, second, out header);

    private static bool TryAssign(ReadOnlySpan<byte> name, KnownHeader first, KnownHeader second, out KnownHeader header)
        => TryAssign(name, first, out header) || TryAssign(name, second, out header);

    private static bool TryAssign(ReadOnlySpan<char> name, KnownHeader first, KnownHeader second, KnownHeader third,
        out KnownHeader header)
        => TryAssign(name, first, out header) || TryAssign(name, second, third, out header);

    private static bool TryAssign(ReadOnlySpan<byte> name, KnownHeader first, KnownHeader second, KnownHeader third,
        out KnownHeader header)
        => TryAssign(name, first, out header) || TryAssign(name, second, third, out header);

    private static bool TryAssign(ReadOnlySpan<char> name, KnownHeader first, KnownHeader second, KnownHeader third,
        KnownHeader fourth, out KnownHeader header)
        => TryAssign(name, first, out header)
           || TryAssign(name, second, out header)
           || TryAssign(name, third, out header)
           || TryAssign(name, fourth, out header);

    private static bool TryAssign(ReadOnlySpan<byte> name, KnownHeader first, KnownHeader second, KnownHeader third,
        KnownHeader fourth, out KnownHeader header)
        => TryAssign(name, first, out header)
           || TryAssign(name, second, out header)
           || TryAssign(name, third, out header)
           || TryAssign(name, fourth, out header);

    /// <summary>
    ///     Interns frequent header values (<c>keep-alive</c>, <c>close</c>, <c>chunked</c>, encodings, …).
    /// </summary>
    internal static bool TryMatchValue(ReadOnlySpan<char> value, out KnownHeader header)
    {
        value = value.Trim();
        header = null!;
        switch (value.Length)
        {
            case 2:
                if (ContentEncodingBrotli.Equals(value)) { header = ContentEncodingBrotli; return true; }
                return false;
            case 4:
                if (ContentEncodingGzip.Equals(value)) { header = ContentEncodingGzip; return true; }
                return false;
            case 5:
                if (ConnectionClose.Equals(value)) { header = ConnectionClose; return true; }
                return false;
            case 7:
                if (TransferEncodingChunked.Equals(value)) { header = TransferEncodingChunked; return true; }
                if (ContentEncodingDeflate.Equals(value)) { header = ContentEncodingDeflate; return true; }
                return false;
            case 8:
                if (ContentEncodingIdentity.Equals(value)) { header = ContentEncodingIdentity; return true; }
                return false;
            case 10:
                if (ConnectionKeepAlive.Equals(value)) { header = ConnectionKeepAlive; return true; }
                return false;
            default:
                return false;
        }
    }

    internal static bool TryMatchValue(ReadOnlySpan<byte> value, out KnownHeader header)
    {
        value = TrimAscii(value);
        header = null!;
        switch (value.Length)
        {
            case 2:
                if (ContentEncodingBrotli.Equals(value)) { header = ContentEncodingBrotli; return true; }
                return false;
            case 4:
                if (ContentEncodingGzip.Equals(value)) { header = ContentEncodingGzip; return true; }
                return false;
            case 5:
                if (ConnectionClose.Equals(value)) { header = ConnectionClose; return true; }
                return false;
            case 7:
                if (TransferEncodingChunked.Equals(value)) { header = TransferEncodingChunked; return true; }
                if (ContentEncodingDeflate.Equals(value)) { header = ContentEncodingDeflate; return true; }
                return false;
            case 8:
                if (ContentEncodingIdentity.Equals(value)) { header = ContentEncodingIdentity; return true; }
                return false;
            case 10:
                if (ConnectionKeepAlive.Equals(value)) { header = ConnectionKeepAlive; return true; }
                return false;
            default:
                return false;
        }
    }

    private static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && value[start] is (byte)' ' or (byte)'\t')
            start++;
        var end = value.Length;
        while (end > start && value[end - 1] is (byte)' ' or (byte)'\t')
            end--;
        return value.Slice(start, end - start);
    }
}
