using System;
using System.Collections.Generic;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Web.Proxy.Routing;

/// <summary>
/// Applies ReverseProxy route match, transforms, and per-session upstream connect override.
/// No-op when <see cref="ProxyServer.ReverseProxy"/> routes are unset (zero-cost default).
/// </summary>
internal static class ReverseProxySessionDispatch
{
    /// <summary>
    /// Resolves destination for this session and sets <see cref="SessionEventArgsBase.UpstreamConnectHost"/>.
    /// Returns false when ReverseProxy is unset / unmatched (caller keeps ForwardHost behaviour).
    /// </summary>
    public static bool TryApply(ProxyServer server, SessionEventArgsBase session)
    {
        var options = server.ReverseProxy;
        if (options?.Routes is null || options.Routes.Count == 0)
        {
            return false;
        }

        session.UpstreamConnectHost = null;
        session.UpstreamConnectPort = null;
        session.UpstreamDestinationId = null;

        var request = session.HttpClient.Request;
        string? fallbackHost = null;
        var fallbackPort = 80;
        if (session.ProxyEndPoint is TransparentBaseProxyEndPoint { ForwardHost.Length: > 0 } transparent)
        {
            fallbackHost = transparent.ForwardHost;
            fallbackPort = transparent.ForwardPort ?? (request.IsHttps ? 443 : 80);
        }

        if (!DestinationResolver.TryResolve(options, request, fallbackHost, fallbackPort,
                out var destination, out var route) ||
            destination is null ||
            route is null)
        {
            return false;
        }

        var port = destination.Port;
        if (port == 0)
        {
            port = destination.UseHttps ? 443 : 80;
        }

        session.UpstreamConnectHost = destination.Address;
        session.UpstreamConnectPort = port;
        session.UpstreamDestinationId = destination.Id;

        if (options.LoadBalancer is LoadBalancer lb)
        {
            session.DestinationRequestLease?.Dispose();
            session.DestinationRequestLease = lb.Health.TrackRequest(destination.Id);
        }

        if (route.Transforms is { Count: > 0 })
        {
            ApplyTransforms(options, route.Transforms, request, session);
        }

        return true;
    }

    /// <summary>Reports passive health after a completed or failed upstream attempt.</summary>
    public static void ReportUpstreamResult(ProxyServer server, SessionEventArgsBase session, bool success)
    {
        var id = session.UpstreamDestinationId;
        if (id is null || server.ReverseProxy?.LoadBalancer is not LoadBalancer lb)
        {
            session.DestinationRequestLease?.Dispose();
            session.DestinationRequestLease = null;
            return;
        }

        session.DestinationRequestLease?.Dispose();
        session.DestinationRequestLease = null;

        if (success)
        {
            lb.Health.ReportSuccess(id);
            if (session.Timing is not null)
            {
                var total = session.Timing.TotalDuration;
                if (total > TimeSpan.Zero)
                {
                    lb.RecordDestination(id, total);
                    server.ReverseProxy?.LatencyRecorder?.RecordDestination(id, total);
                }
            }
        }
        else
        {
            lb.Health.ReportFailure(id, server.ReverseProxy?.ClusterManager);
        }
    }

    /// <summary>
    /// True when sticky ForwardHost keep-alive is safe (no multi-destination LB risk).
    /// </summary>
    public static bool AllowsStickyForwardUpstream(ProxyServer server, ProxyEndPoint endPoint)
    {
        if (endPoint is not TransparentBaseProxyEndPoint { ForwardHost.Length: > 0 } transparent)
        {
            return false;
        }

        var reverse = server.ReverseProxy;
        if (reverse?.Routes is not { Count: > 0 })
        {
            return true;
        }

        return ReverseProxyFastPath.IsForwardHostEquivalent(
            reverse.Routes,
            reverse.ClusterManager?.Snapshot,
            transparent.ForwardHost,
            transparent.ForwardPort ?? 80);
    }

    private static void ApplyTransforms(
        Titanium.Web.Proxy.Abstractions.ReverseProxyOptions options,
        IReadOnlyList<TransformConfig> transforms,
        Request request,
        SessionEventArgsBase session)
    {
        var engine = options.TransformEngine ?? new TransformEngine();
        var path = request.RequestUriString8.GetString();
        if ((path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
            Uri.TryCreate(path, UriKind.Absolute, out var absolute))
        {
            path = absolute.PathAndQuery;
        }

        var ctx = new TransformRequestContext { Path = path };
        engine.ApplyRequestTransforms(transforms, ctx);

        if (!string.Equals(path, ctx.Path, StringComparison.Ordinal))
        {
            request.RequestUriString8 = (ByteString)ctx.Path;
        }

        foreach (var name in ctx.HeadersToRemove)
        {
            request.Headers.RemoveHeader(name);
        }

        foreach (var pair in ctx.Headers)
        {
            request.Headers.SetOrAddHeaderValue(pair.Key, pair.Value);
        }

        if (ctx.ResponseHeadersToSet.Count > 0 || ctx.ResponseHeadersToRemove.Count > 0)
        {
            session.ResponseHeaderTransformPlan =
                new TransformResponseHeaderPlan(ctx.ResponseHeadersToSet, ctx.ResponseHeadersToRemove);
        }
    }

    /// <summary>Applies staged response header transforms when present.</summary>
    public static void ApplyResponseTransforms(SessionEventArgsBase session)
    {
        if (session.ResponseHeaderTransformPlan is TransformResponseHeaderPlan plan)
        {
            plan.Apply(session.HttpClient.Response.Headers);
        }
    }
}

/// <summary>Stashed on the session so response transforms apply without an always-on middleware.</summary>
internal sealed class TransformResponseHeaderPlan(
    Dictionary<string, string> set,
    HashSet<string> remove)
{
    public Dictionary<string, string> Set { get; } = set;
    public HashSet<string> Remove { get; } = remove;

    public void Apply(HeaderCollection headers)
    {
        foreach (var name in Remove)
        {
            headers.RemoveHeader(name);
        }

        foreach (var pair in Set)
        {
            headers.SetOrAddHeaderValue(pair.Key, pair.Value);
        }
    }
}
