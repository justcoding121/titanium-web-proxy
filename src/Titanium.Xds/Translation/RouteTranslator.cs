using Envoy.Config.Route.V3;
using Titanium.Web.Proxy.Abstractions.Routing;
using EnvoyRoute = Envoy.Config.Route.V3.Route;
using EnvoyRouteMatch = Envoy.Config.Route.V3.RouteMatch;

namespace Titanium.Xds.Translation;

public static class RouteTranslator
{
    public static TranslationResult<IReadOnlyList<RouteConfig>> Translate(RouteConfiguration routeConfiguration)
    {
        if (string.IsNullOrWhiteSpace(routeConfiguration.Name))
        {
            return TranslationResult<IReadOnlyList<RouteConfig>>.Fail(new TranslationError
            {
                ResourceName = "(unnamed)",
                FieldPath = "name",
                Message = "route configuration name is required",
            });
        }

        var routes = new List<RouteConfig>();
        var order = 0;
        foreach (var vhost in routeConfiguration.VirtualHosts)
        {
            foreach (var route in vhost.Routes)
            {
                order++;
                var id = $"{routeConfiguration.Name}-{order}";
                if (route.Match is null)
                {
                    return TranslationResult<IReadOnlyList<RouteConfig>>.Fail(new TranslationError
                    {
                        ResourceName = routeConfiguration.Name,
                        FieldPath = $"virtual_hosts[].routes[{order - 1}].match",
                        Message = "route match is required",
                    });
                }

                var match = BuildMatch(route.Match, vhost);
                RouteConfig config;
                switch (route.ActionCase)
                {
                    case EnvoyRoute.ActionOneofCase.RouteAction:
                        config = new RouteConfig
                        {
                            Id = id,
                            Order = order,
                            Match = match,
                            ClusterId = route.RouteAction.Cluster,
                        };
                        break;
                    case EnvoyRoute.ActionOneofCase.DirectResponse:
                        config = new RouteConfig
                        {
                            Id = id,
                            Order = order,
                            Match = match,
                            ClusterId = string.Empty,
                            DirectResponse = new DirectResponseConfig { Status = (int)route.DirectResponse.Status },
                        };
                        break;
                    case EnvoyRoute.ActionOneofCase.Redirect:
                        return TranslationResult<IReadOnlyList<RouteConfig>>.Fail(new TranslationError
                        {
                            ResourceName = routeConfiguration.Name,
                            FieldPath = $"virtual_hosts[].routes[{order - 1}].redirect",
                            Message = "redirect actions are not supported yet",
                        });
                    default:
                        return TranslationResult<IReadOnlyList<RouteConfig>>.Fail(new TranslationError
                        {
                            ResourceName = routeConfiguration.Name,
                            FieldPath = $"virtual_hosts[].routes[{order - 1}].action",
                            Message = "unsupported route action",
                        });
                }

                routes.Add(config);
            }
        }

        return TranslationResult<IReadOnlyList<RouteConfig>>.Ok(routes);
    }

    private static Titanium.Web.Proxy.Abstractions.Routing.RouteMatch BuildMatch(EnvoyRouteMatch routeMatch, VirtualHost vhost)
    {
        string? path;
        PathMatchKind pathKind;
        switch (routeMatch.PathSpecifierCase)
        {
            case EnvoyRouteMatch.PathSpecifierOneofCase.Prefix:
                path = routeMatch.Prefix;
                pathKind = PathMatchKind.Prefix;
                break;
            case EnvoyRouteMatch.PathSpecifierOneofCase.Path:
                path = routeMatch.Path;
                pathKind = PathMatchKind.Exact;
                break;
            default:
                throw new InvalidOperationException("unsupported path match");
        }

        return new Titanium.Web.Proxy.Abstractions.Routing.RouteMatch
        {
            Path = path,
            PathKind = pathKind,
            Host = vhost.Domains is { Count: > 0 } ? vhost.Domains[0] : null,
        };
    }
}
