using System.Net;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Routing;

/// <summary>Enforces route policies before a destination is selected. No work when the route lists none.</summary>
internal static class RoutePolicies
{
    public static bool TryDeny(ProxyServer server, SessionEventArgsBase session, RouteConfig route)
    {
        if (route.Policies is not { Count: > 0 } names || server.ReverseProxy?.Policies is not { Count: > 0 } policies)
        {
            return false;
        }

        if (session is not SessionEventArgs args)
        {
            return false;
        }

        foreach (var name in names)
        {
            RoutePolicyConfig? policy = null;
            for (var i = 0; i < policies.Count; i++)
            {
                if (string.Equals(policies[i].Name, name, System.StringComparison.Ordinal))
                {
                    policy = policies[i];
                    break;
                }
            }

            if (policy is null)
            {
                continue;
            }

            if (policy.Kind == "maxRequestBodySize" &&
                policy.MaxRequestBodyBytes is long max &&
                args.HttpClient.Request.ContentLength > max)
            {
                args.GenericResponse("payload too large", HttpStatusCode.RequestEntityTooLarge);
                return true;
            }

            if (policy.Kind == "requireHeader" && !string.IsNullOrEmpty(policy.HeaderName))
            {
                var headers = args.HttpClient.Request.Headers.GetHeaders(policy.HeaderName);
                var value = headers is { Count: > 0 } ? headers[0].Value : null;
                if (!string.Equals(value, policy.HeaderValue, System.StringComparison.Ordinal))
                {
                    args.GenericResponse(string.Empty, HttpStatusCode.Unauthorized);
                    return true;
                }
            }
        }

        return false;
    }
}
