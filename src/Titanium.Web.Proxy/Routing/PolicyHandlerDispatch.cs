using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Diagnostics;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Routing;

/// <summary>
/// Hands the plugin-handled policies of a matched route (<c>oidc</c>) to their <see cref="IRoutePolicyHandler"/>.
/// Only routes that list such a policy reach this code. A policy with no handler refuses the request: a
/// missing plugin must never turn a login into an open route.
/// </summary>
internal static class PolicyHandlerDispatch
{
    /// <summary>
    /// Runs every plugin-handled policy of the route in order. Returns true when a response is staged and the
    /// request must not be forwarded.
    /// </summary>
    internal static async ValueTask<bool> DeniedAsync( // NOSONAR S3776 -- Policy handlers run in route order with shared session staging.
        ReverseProxyOptions options, RouteConfig route, SessionEventArgs session)
    {
        if (route.Policies is not { Count: > 0 } names || options.Policies is not { Count: > 0 } policies)
        {
            return false;
        }

        for (var n = 0; n < names.Count; n++)
        {
            var policy = Find(policies, names[n]);
            if (policy is null || !PolicyKinds.IsHandled(policy.Kind))
            {
                continue;
            }

            var handler = FindHandler(options.PolicyHandlers, policy.Kind);
            if (handler is null)
            {
                Refuse(session, 503);
                return true;
            }

            RoutePolicyVerdict verdict;
            try
            {
                verdict = await handler.EvaluateAsync(session, route, policy, session.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                // The handler reports its own failures. Whatever escapes is refused rather than let through.
                Refuse(session, 503);
                return true;
            }

            if (verdict.Responded)
            {
                RoutePolicies.ReleaseSessionSlot(session);
                if (session.HttpClient.Response.StatusCode >= 400)
                {
                    session.ForwarderError = ForwarderError.PolicyDenied;
                }

                return true;
            }

            if (verdict.ResponseSetCookie is { Length: > 0 } cookie)
            {
                session.Extras ??= new RoutingFeatureState();
                session.Extras.PolicySetCookie = cookie;
            }
        }

        return false;
    }

    private static RoutePolicyConfig? Find(IReadOnlyList<RoutePolicyConfig> policies, string name)
    {
        for (var i = 0; i < policies.Count; i++)
        {
            if (string.Equals(policies[i].Name, name, StringComparison.Ordinal))
            {
                return policies[i];
            }
        }

        return null;
    }

    private static IRoutePolicyHandler? FindHandler(IReadOnlyList<IRoutePolicyHandler>? handlers, string kind)
    {
        if (handlers is null)
        {
            return null;
        }

        for (var i = 0; i < handlers.Count; i++)
        {
            if (string.Equals(handlers[i].Kind, kind, StringComparison.Ordinal))
            {
                return handlers[i];
            }
        }

        return null;
    }

    private static void Refuse(SessionEventArgs session, int status)
    {
        RoutePolicies.ReleaseSessionSlot(session);
        session.ForwarderError = ForwarderError.PolicyDenied;
        session.GenericResponse(string.Empty, (System.Net.HttpStatusCode)status, (IEnumerable<HttpHeader>?)null);
    }
}
