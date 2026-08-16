using System;
using System.ComponentModel;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     Http(s) request object
/// </summary>
[TypeConverter(typeof(ExpandableObjectConverter))]
public class Request : RequestResponseBase
{
    private ByteString requestUriString8;

    /// <summary>
    ///     Request Method.
    /// </summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>
    ///     Is Https?
    /// </summary>
    public bool IsHttps { get; internal set; }

    internal ByteString RequestUriString8
    {
        get => requestUriString8;
        set
        {
            requestUriString8 = value;
            var scheme = UriExtensions.GetScheme(value);
            if (scheme.Length > 0) IsHttps = scheme.Equals(ProxyServer.UriSchemeHttps8);
        }
    }

    internal ByteString Authority { get; set; }

    /// <summary>
    ///     Request HTTP Uri.
    /// </summary>
    public Uri RequestUri
    {
        get => new(Url);
        set => Url = value.OriginalString;
    }

    /// <summary>
    ///     The request url as it is in the HTTP header
    /// </summary>
    public string Url
    {
        get
        {
            var url = RequestUriString8.GetString();
            if (UriExtensions.GetScheme(RequestUriString8).Length == 0)
            {
                var hostAndPath = Host ?? Authority.GetString();

                if (url.StartsWith('/'))
                {
                    hostAndPath += url;
                }

                url = string.Concat(IsHttps ? "https://" : "http://", hostAndPath);
            }

            return url;
        }
        set => RequestUriString = value;
    }

    /// <summary>
    ///     The request uri as it is in the HTTP header
    /// </summary>
    public string RequestUriString
    {
        get => RequestUriString8.GetString();
        set
        {
            RequestUriString8 = (ByteString)value;

            var scheme = UriExtensions.GetScheme(RequestUriString8);
            if (scheme.Length > 0 && Host != null)
            {
                var uri = new Uri(value);
                Host = uri.Authority;
                Authority = ByteString.Empty;
            }
        }
    }

    /// <summary>
    ///     Has request body?
    /// </summary>
    public override bool HasBody
    {
        get
        {
            var contentLength = ContentLength;

            // If content length is set to 0 the request has no body
            if (contentLength == 0) return false;

            // Has body only if request is chunked or content length >0
            if (IsChunked || contentLength > 0) return true;

            // has body if POST and when version is http/1.0
            if (Method == "POST" && HttpVersion == HttpHeader.Version10) return true;

            return false;
        }
    }

    /// <summary>
    ///     Http hostname header value if exists.
    ///     Note: Changing this does NOT change host in RequestUri.
    ///     Users can set new RequestUri separately.
    /// </summary>
    public string? Host
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
            var headerValue = Headers.GetHeaderValueOrNull(KnownHeaders.Expect);
            return KnownHeaders.Expect100Continue.Equals(headerValue);
        }
    }

    /// <summary>
    ///     Does this request contain multipart/form-data?
    /// </summary>
    public bool IsMultipartFormData =>
        ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    ///     Cancels the client HTTP request without sending to server.
    ///     This should be set when API user responds with custom response.
    /// </summary>
    internal bool CancelRequest { get; set; }

    /// <summary>
    ///     RFC 8441: the value of the <c>:protocol</c> pseudo-header for HTTP/2 extended CONNECT
    ///     requests (e.g. <c>"websocket"</c>). <see langword="null"/> for all other requests.
    ///     This property allows <c>BeforeRequest</c> handlers to identify a WebSocket-over-HTTP/2
    ///     upgrade and inspect or modify it before the tunnel is established.
    /// </summary>
    public string? ExtendedConnectProtocol { get; internal set; }

    /// <summary>
    ///     Does this request has an upgrade to websocket header?
    ///     Returns <see langword="true"/> for both HTTP/1.1 WebSocket upgrades (<c>Upgrade: websocket</c>)
    ///     and HTTP/2 extended CONNECT tunnels (<c>CONNECT :protocol=websocket</c>, RFC 8441).
    /// </summary>
    public bool UpgradeToWebSocket
    {
        get
        {
            // HTTP/2 extended CONNECT (RFC 8441)
            if (ExtendedConnectProtocol != null)
                return string.Equals(ExtendedConnectProtocol, "websocket", StringComparison.OrdinalIgnoreCase);

            // HTTP/1.1 Upgrade header
            var headerValue = Headers.GetHeaderValueOrNull(KnownHeaders.Upgrade);

            if (headerValue == null) return false;

            return headerValue.EqualsIgnoreCase(KnownHeaders.UpgradeWebsocket.String);
        }
    }

    /// <summary>
    ///     Did server respond positively for 100 continue request?
    /// </summary>
    public bool ExpectationSucceeded { get; internal set; }

    /// <summary>
    ///     Did server respond negatively for 100 continue request?
    /// </summary>
    public bool ExpectationFailed { get; internal set; }

    /// <summary>
    ///     Gets the header text.
    /// </summary>
    public override string HeaderText
    {
        get
        {
            var headerBuilder = new HeaderBuilder();
            headerBuilder.WriteRequestLine(Method, RequestUriString, HttpVersion);
            headerBuilder.WriteHeaders(Headers);
            return headerBuilder.GetString(HttpHeader.Encoding);
        }
    }

    internal override void EnsureBodyAvailable(bool throwWhenNotReadYet = true)
    {
        if (BodyInternal != null) return;

        // GET request don't have a request body to read
        if (!HasBody)
            throw new BodyNotFoundException("Request don't have a body. " +
                                            "Please verify that this request is a Http POST/PUT/PATCH and request " +
                                            "content length is greater than zero before accessing the body.");

        if (!IsBodyRead)
        {
            if (Locked) throw new InvalidOperationException("You cannot get the request body after request is made to server.");

            if (throwWhenNotReadYet)
                throw new InvalidOperationException("Request body is not read yet. " +
                                    "Use SessionEventArgs.GetRequestBody() or SessionEventArgs.GetRequestBodyAsString() " +
                                    "method to read the request body.");
        }
    }

    internal static readonly ByteString OriginFormRoot = (ByteString)"/";
    internal static readonly ByteString AsteriskForm = (ByteString)"*";

    internal static void ParseRequestLine(string httpCmd, out string method, out ByteString requestUri,
        out Version version)
    {
        var firstSpace = httpCmd.IndexOf(' ');
        if (firstSpace == -1)
            // does not contain at least 2 parts
            throw new FormatException("Invalid HTTP request line: " + httpCmd);

        var lastSpace = httpCmd.LastIndexOf(' ');

        // break up the line into three components (method, remote URL & Http Version)

        method = InternMethod(httpCmd.AsSpan(0, firstSpace));

        version = HttpHeader.Version11;

        if (firstSpace == lastSpace)
        {
            requestUri = InternTarget(httpCmd.AsSpan(firstSpace + 1));
        }
        else
        {
            requestUri = InternTarget(httpCmd.AsSpan(firstSpace + 1, lastSpace - firstSpace - 1));

            // parse the HTTP version
            var httpVersion = httpCmd.AsSpan(lastSpace + 1);

            if (httpVersion.EqualsIgnoreCase("HTTP/1.0".AsSpan(0))) version = HttpHeader.Version10;
        }
    }

    private static string InternMethod(ReadOnlySpan<char> method)
    {
        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase)) return "GET";
        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase)) return "POST";
        if (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) return "HEAD";
        if (method.Equals("PUT", StringComparison.OrdinalIgnoreCase)) return "PUT";
        if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase)) return "DELETE";
        if (method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase)) return "OPTIONS";
        if (method.Equals("PATCH", StringComparison.OrdinalIgnoreCase)) return "PATCH";
        if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase)) return "CONNECT";

        var allocated = method.ToString();
        return IsAllUpper(allocated) ? allocated : allocated.ToUpperInvariant();
    }

    private static ByteString InternTarget(ReadOnlySpan<char> target)
    {
        if (target.Length == 1)
        {
            if (target[0] == '/') return OriginFormRoot;
            if (target[0] == '*') return AsteriskForm;
        }

        return (ByteString)target.ToString();
    }

    private static bool IsAllUpper(string input)
    {
        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (ch < 'A' || ch > 'Z') return false;
        }

        return true;
    }
}