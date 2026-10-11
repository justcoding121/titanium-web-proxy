using Titanium.Web.Proxy.Http;

namespace Titanium.Inspector.Services;

/// <summary>
/// Session-only request header edits from the Capture menu.
/// Both flags off leaves the header block untouched. CONNECT, WebSocket upgrades,
/// and requests with <c>Range</c> or <c>If-Range</c> are also left untouched.
/// </summary>
public static class RequestCaptureTweaks
{
    public static void Apply(Request request, bool disableCaching, bool forceUncompressed)
    {
        if (!disableCaching && !forceUncompressed)
            return;

        if (IsExcluded(request))
            return;

        var headers = request.Headers;
        if (disableCaching)
            ApplyDisableCaching(headers);
        if (forceUncompressed)
            ApplyForceUncompressed(headers);
    }

    private static bool IsExcluded(Request request)
    {
        if (request.UpgradeToWebSocket)
            return true;

        if (string.Equals(request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
            return true;

        var headers = request.Headers;
        return headers.HeaderExists("Range") || headers.HeaderExists("If-Range");
    }

    private static void ApplyDisableCaching(HeaderCollection headers)
    {
        headers.RemoveHeader("If-None-Match");
        headers.RemoveHeader("If-Modified-Since");
        headers.RemoveHeader("Cache-Control");
        headers.AddHeader("Cache-Control", "no-cache");
    }

    private static void ApplyForceUncompressed(HeaderCollection headers)
    {
        headers.RemoveHeader("Accept-Encoding");
        headers.AddHeader("Accept-Encoding", "identity");
    }
}
