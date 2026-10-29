using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>
    ///     True when at least one endpoint could use H1 terminate-lite (a transparent endpoint with a fixed
    ///     <c>ForwardHost</c>). Only then does a lite blocker matter.
    /// </summary>
    internal bool LiteApplicable
    {
        get
        {
            try
            {
                foreach (var endPoint in ProxyEndPoints)
                {
                    if (endPoint is TransparentBaseProxyEndPoint { ForwardHost.Length: > 0 })
                    {
                        return true;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Endpoints were edited while a metrics reader looked. Treat it as not applicable for this reading.
            }

            return false;
        }
    }

    /// <summary>
    ///     Names every configured feature that takes requests off H1 terminate-lite. Empty means eligible.
    ///     Each name points at what to change, never at hosts, URLs or secrets.
    /// </summary>
    internal IReadOnlyList<string> LiteBlockers()
    {
        var blockers = new List<string>(4);
        if (CompressionPolicy is not null)
        {
            blockers.Add("response compression is enabled");
        }

        if (RequestDecompressionPolicy is not null)
        {
            blockers.Add("request decompression is enabled");
        }

        var reverse = ReverseProxy;
        if (reverse is null)
        {
            return blockers;
        }

        if (reverse.HasSessionOnlyMiddleware)
        {
            blockers.Add("a session-only middleware (such as the WAF) is registered");
        }

        if (reverse.HasRoutePlugins)
        {
            blockers.Add("a route names a per-route plugin (scripted route)");
        }

        if (reverse.HasServicePolicies)
        {
            blockers.Add("a route lists an extAuth or oidc policy");
        }

        if (reverse.HasHandledPolicies)
        {
            blockers.Add("a route lists a plugin-handled policy (oidc)");
        }

        if (reverse.HasGrpcWebRoutes)
        {
            blockers.Add("a route sets grpcWeb");
        }

        if (reverse.Policies is { Count: > 0 } && Routing.RoutePolicies.HoldsConnectionSlot(reverse))
        {
            blockers.Add("a route lists a connectionLimit policy (it holds a slot for the whole request)");
        }

        return blockers;
    }

    /// <summary>1 while every blocker is absent, 0 otherwise. Null when terminate-lite does not apply to this server.</summary>
    internal int? LiteEligibleValue()
    {
        if (!ProxyRunning || !LiteApplicable)
        {
            return null;
        }

        return LiteBlockers().Count == 0 ? 1 : 0;
    }

    /// <summary>Logs one warning naming what disabled terminate-lite, and publishes the server to <c>twp.lite.eligible</c>.</summary>
    private void ReportLiteEligibilityAtStartup()
    {
        ProxyMetrics.RegisterProxyServer(this);
        if (!LiteApplicable)
        {
            return;
        }

        var blockers = LiteBlockers();
        if (blockers.Count > 0)
        {
            ProxyLog.TerminateLiteDisabled(logger, string.Join("; ", blockers));
        }
    }
}