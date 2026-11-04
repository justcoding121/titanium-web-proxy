using Titanium.Gateway.Translation;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Gateway.Tests.Support;

internal static class ConfigExtensions
{
    /// <summary>The routes translated from HTTPRoutes: every route except the trailing 404 for requests nothing matched.</summary>
    public static IReadOnlyList<RouteConfig> UserRoutes(this TwpConfig config) =>
        config.Routes.Where(r => r.Id != GatewayTranslator.NoMatchRouteId).ToList();
}
