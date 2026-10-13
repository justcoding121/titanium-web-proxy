using Titanium.Web.Proxy.Http;

namespace Titanium.Inspector.Services;

/// <summary>
/// Session-only request header edits from the Capture menu.
/// Both flags off leaves the header block untouched. CONNECT, WebSocket upgrades,
/// and requests with <c>Range</c> or <c>If-Range</c> are also left untouched.
/// </summary>
[Flags]
public enum RequestTweakChanges
{
    None = 0,
    Caching = 1,
    Encoding = 2,
}

public static class RequestCaptureTweaks
{
    public static RequestTweakChanges Apply(Request request, bool disableCaching, bool forceUncompressed)
    {
        if (!disableCaching && !forceUncompressed)
            return RequestTweakChanges.None;

        if (IsExcluded(request))
            return RequestTweakChanges.None;

        var headers = request.Headers;
        var changed = RequestTweakChanges.None;
        if (disableCaching && ApplyDisableCaching(headers))
            changed |= RequestTweakChanges.Caching;
        if (forceUncompressed && ApplyForceUncompressed(headers))
            changed |= RequestTweakChanges.Encoding;
        return changed;
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

    private static bool ApplyDisableCaching(HeaderCollection headers)
    {
        var hadValidator = headers.HeaderExists("If-None-Match") || headers.HeaderExists("If-Modified-Since");
        var cache = headers.GetFirstHeader("Cache-Control")?.Value;
        headers.RemoveHeader("If-None-Match");
        headers.RemoveHeader("If-Modified-Since");
        headers.RemoveHeader("Cache-Control");
        headers.AddHeader("Cache-Control", "no-cache");
        return hadValidator || !string.Equals(cache, "no-cache", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ApplyForceUncompressed(HeaderCollection headers)
    {
        var encoding = headers.GetFirstHeader("Accept-Encoding")?.Value;
        headers.RemoveHeader("Accept-Encoding");
        headers.AddHeader("Accept-Encoding", "identity");
        return !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase);
    }
}
