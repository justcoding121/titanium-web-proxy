using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Routing;

/// <summary>Finds a static error body. gRPC callers skip this and keep trailers-only.</summary>
internal static class ErrorPages
{
    public static ErrorPageConfig? Find(SessionEventArgsBase session, int status)
    {
        var routePages = session.MatchedRoute?.ErrorPages;
        var serverPages = session.Server.ReverseProxy?.ErrorPages;
        return Match(routePages, status) ?? Match(serverPages, status);
    }

    private static ErrorPageConfig? Match(System.Collections.Generic.IReadOnlyList<ErrorPageConfig>? pages, int status)
    {
        if (pages is not { Count: > 0 })
        {
            return null;
        }

        var exact = status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var family = (status / 100).ToString(System.Globalization.CultureInfo.InvariantCulture) + "xx";
        ErrorPageConfig? familyPage = null;
        for (var i = 0; i < pages.Count; i++)
        {
            if (string.Equals(pages[i].Status, exact, System.StringComparison.Ordinal))
            {
                return pages[i];
            }

            if (familyPage is null && string.Equals(pages[i].Status, family, System.StringComparison.OrdinalIgnoreCase))
            {
                familyPage = pages[i];
            }
        }

        return familyPage;
    }
}
