using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Diagnostics;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Middleware;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Routing;

/// <summary>
/// Runs the plugins a matched route names in <see cref="RouteConfig.Middleware"/>. A route that names none returns at the
/// first check, so only routes that opt in pay for plugins. Every call builds a fresh <see cref="ProxyMiddlewareContext"/>,
/// so nothing a plugin stores on one request is visible to the next request on the same connection.
/// </summary>
internal static class RoutePluginDispatch
{
    /// <summary>
    /// Returns true when a response is staged and the request must not be forwarded: a plugin handled the request,
    /// stopped the chain without calling <c>next</c>, a named plugin is missing, or a fail-closed plugin failed.
    /// </summary>
    internal static async ValueTask<bool> DeniedAsync(
        ReverseProxyOptions options, RouteConfig route, SessionEventArgs session)
    {
        if (route.Middleware is not { Count: > 0 } names)
        {
            return false;
        }

        var chain = new List<IProxyMiddleware>(names.Count);
        for (var i = 0; i < names.Count; i++)
        {
            if (options.RoutePlugins is null || !options.RoutePlugins.TryGetValue(names[i], out var plugin))
            {
                // A route that names a plugin the host does not have is never an open route.
                Stage(session, 503, string.Empty, null, null);
                return true;
            }

            chain.Add(plugin);
        }

        var context = new ProxyMiddlewareContext
        {
            Session = session,
            ClientRemoteEndPoint = session.ClientRemoteEndPoint,
        };
        var reachedEnd = false;
        await ProxyMiddlewarePipeline.Build(chain, (_, _) =>
        {
            reachedEnd = true;
            return default;
        })(context, session.CancellationToken).ConfigureAwait(false);

        if (reachedEnd && !context.IsHandled)
        {
            return false;
        }

        Stage(session, context.HandledStatusCode ?? 403, context.HandledBody, context.HandledBodyBytes, context.HandledHeaders);
        return true;
    }

    private static void Stage(
        SessionEventArgs session,
        int status,
        string? body,
        byte[]? bodyBytes,
        List<KeyValuePair<string, string>>? headers)
    {
        RoutePolicies.ReleaseSessionSlot(session);
        if (status >= 400)
        {
            session.ForwarderError = ForwarderError.PolicyDenied;
        }

        // A plugin that already answered through the session has nothing more for the host to write.
        if (session.HttpClient.Request.CancelRequest)
        {
            return;
        }

        var extra = headers?.Select(h => new HttpHeader(h.Key, h.Value));
        if (bodyBytes is not null)
        {
            session.GenericResponse(bodyBytes, (HttpStatusCode)status, extra);
        }
        else
        {
            session.GenericResponse(body ?? string.Empty, (HttpStatusCode)status, extra);
        }
    }
}
