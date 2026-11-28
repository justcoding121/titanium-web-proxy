using System;
using System.Collections.Generic;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Codec = Titanium.Http3.QpackEncoder;

namespace Titanium.Web.Proxy.Http3.Qpack;

/// <summary>
///     Proxy entry points for QPACK. Field encoding lives in Titanium.Http3.
///     Request and response encoding stays here because it reads proxy header objects.
/// </summary>
internal static class QpackEncoder
{
    public static byte[] Encode(IEnumerable<(string Name, string Value)> headers) =>
        Codec.Encode(headers);

    public static byte[] Encode(IEnumerable<(string Name, string Value)> headers, QpackContext? context) =>
        Codec.Encode(headers, context);

    internal static ulong EncodeRequiredInsertCount(ulong requiredInsertCount, uint maxTableCapacity) =>
        Codec.EncodeRequiredInsertCount(requiredInsertCount, maxTableCapacity);

    internal static byte[] AppendLiteralHeader(byte[] block, string lowerName, string value) =>
        Codec.AppendLiteralHeader(block, lowerName, value);

    internal static Codec.ResponseBlockBuilder RentResponseBlockBuilder(int statusCode, QpackContext? context) =>
        Codec.RentResponseBlockBuilder(statusCode, context);

    /// <summary>
    ///     Encodes an HTTP response without building an intermediate header list (hot H3→client path).
    /// </summary>
    public static byte[] EncodeResponse(Response response, QpackContext? context)
    {
        var body = Codec.RentBuffer();
        var outboundTable = Codec.SelectOutboundTable(context);
        ulong maxRequiredInsertCount = 0;
        Codec.EncodeOne(body, outboundTable, ref maxRequiredInsertCount, ":status",
            Codec.StatusCodeToString(response.StatusCode));

        foreach (var header in response.Headers)
        {
            var name = header.Name;
            if (!response.HeaderNamesAreHttp2Normalized && HasUpperAscii(name))
                name = name.ToLowerInvariant();
            if (name is "connection" or "keep-alive" or "proxy-connection"
                or "transfer-encoding" or "upgrade")
                continue;
            Codec.EncodeOne(body, outboundTable, ref maxRequiredInsertCount, name, header.Value);
        }

        return Codec.FinishBlock(body, maxRequiredInsertCount, outboundTable, context);
    }

    /// <summary>
    ///     Encodes an HTTP request without building an intermediate header list (hot H3→origin path).
    /// </summary>
    public static byte[] EncodeRequest(Request request, string authorityHost, QpackContext? context = null) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        var body = Codec.RentBuffer();
        var outboundTable = Codec.SelectOutboundTable(context);
        ulong maxRequiredInsertCount = 0;

        string authority;
        if (request.Authority.Length > 0)
            authority = request.Authority.GetString();
        else if (!string.IsNullOrEmpty(request.Host))
            authority = request.Host;
        else
            authority = authorityHost;
        var path = request.RequestUriString8.Length > 0
            ? request.RequestUriString8.GetString()
            : "/";
        if (UriExtensions.GetScheme(request.RequestUriString8).Length > 0)
        {
            try
            {
                var uri = request.RequestUri;
                authority = uri.Authority;
                path = uri.PathAndQuery;
            }
            catch
            {
                // Keep ByteString-derived authority/path.
            }
        }

        Codec.EncodeOne(body, outboundTable, ref maxRequiredInsertCount, ":method", request.Method);
        Codec.EncodeOne(body, outboundTable, ref maxRequiredInsertCount, ":scheme",
            request.IsHttps ? "https" : "http");
        Codec.EncodeOne(body, outboundTable, ref maxRequiredInsertCount, ":authority", authority);
        Codec.EncodeOne(body, outboundTable, ref maxRequiredInsertCount, ":path",
            path.Length > 0 ? path : "/");

        if (request.ExtendedConnectProtocol != null)
        {
            Codec.EncodeOne(body, outboundTable, ref maxRequiredInsertCount, ":protocol",
                request.ExtendedConnectProtocol);
        }

        foreach (var header in request.Headers.GetAllHeaders())
        {
            var name = header.Name;
            if (HasUpperAscii(name))
                name = name.ToLowerInvariant();
            if (name is "connection" or "keep-alive" or "proxy-connection"
                or "transfer-encoding" or "upgrade" or "te" or "host"
                or "http2-settings" or "proxy-authorization" or "proxy-authenticate")
                continue;
            Codec.EncodeOne(body, outboundTable, ref maxRequiredInsertCount, name, header.Value);
        }

        return Codec.FinishBlock(body, maxRequiredInsertCount, outboundTable, context);
    }

    private static bool HasUpperAscii(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is >= 'A' and <= 'Z') return true;
        }

        return false;
    }
}
