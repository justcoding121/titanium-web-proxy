using System;
using System.Collections.Generic;
using Titanium.Web.Proxy.Abstractions.Routing;
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

        var port = destination.Port == 0
            ? (destination.UseHttps ? 443 : 80)
            : destination.Port;
        session.UpstreamConnectHost = destination.Address;
        session.UpstreamConnectPort = port;
        session.UpstreamDestinationId = destination.Id;

        if (route.Transforms is { Count: > 0 })
        {
            ApplyTransforms(options, route.Transforms, request);
        }

        return true;
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
        Abstractions.ReverseProxyOptions options,
        IReadOnlyList<TransformConfig> transforms,
        Request request)
    {
        var engine = options.TransformEngine ?? new TransformEngine();
        var path = request.RequestUriString8.GetString();
        // Origin-form may be path+query; absolute-form includes scheme — transforms expect path-like input.
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var absolute))
            {
                path = absolute.PathAndQuery;
            }
        }

        var ctx = new TransformRequestContext { Path = path };
        engine.ApplyRequestTransforms(transforms, ctx);

        if (!string.Equals(path, ctx.Path, StringComparison.Ordinal))
        {
            request.RequestUriString8 = (ByteString)ctx.Path;
        }

        foreach (var pair in ctx.Headers)
        {
            request.Headers.SetOrAddHeaderValue(pair.Key, pair.Value);
        }
    }
}
