using System;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>
    ///     After every response, inspect the <c>Alt-Svc</c> header and cache any HTTP/3 capability
    ///     advertised by the origin. This allows subsequent requests to the same host to use HTTP/3
    ///     proactively (when <see cref="EnableHttp3" /> is <see langword="true" /> and
    ///     <see cref="Models.UpstreamHttpProtocol.Auto" /> is in effect).
    /// </summary>
    private void TryUpdateHttp3CapabilityFromResponse(SessionEventArgs args)
    {
        if (!EnableHttp3) return;

        var response = args.HttpClient.Response;
        if (response == null) return;

        var altSvc = response.Headers.GetHeaderValueOrNull("Alt-Svc");
        if (string.IsNullOrEmpty(altSvc) || altSvc == "clear")
        {
            if (altSvc == "clear")
                Http3OriginCapabilityCache.Evict($"{args.HttpClient.Request.RequestUri?.Host}:{args.HttpClient.Request.RequestUri?.Port}");
            return;
        }

        var entries = AltSvcParser.Parse(altSvc);
        if (entries.Count == 0) return;

        var host = args.HttpClient.Request.RequestUri?.Host;
        var port = args.HttpClient.Request.RequestUri?.Port ?? 443;
        if (string.IsNullOrEmpty(host)) return;

        var hostAndPort = $"{host}:{port}";

        foreach (var entry in entries)
        {
            if (entry.MaxAgeSeconds <= 0) continue;

            var altPort = entry.Port == port ? int.MinValue : entry.Port;
            var ttl = TimeSpan.FromSeconds(Math.Min(entry.MaxAgeSeconds, Http3OriginCapabilityCache.DefaultTtl.TotalSeconds * 2));
            Http3OriginCapabilityCache.Set(hostAndPort, altPort, ttl);
            break; // Take the first valid h3 entry.
        }
    }

    /// <summary>
    ///     Returns <see langword="true" /> when this request should be forwarded to the origin over
    ///     HTTP/3 rather than the normal TCP pipeline. Evaluated in <c>HandleHttpSessionRequest</c>
    ///     before the TCP connection generator runs.
    /// </summary>
    internal bool ShouldUseHttp3Origin(SessionEventArgs args)
    {
        if (!EnableHttp3) return false;

        var effectiveProtocol = args.UpstreamHttpProtocol ?? UpstreamHttpProtocol.Auto;

        if (effectiveProtocol == UpstreamHttpProtocol.Http3) return true;

        if (effectiveProtocol == UpstreamHttpProtocol.Auto)
        {
            var host = args.HttpClient.Request.RequestUri?.Host;
            var port = args.HttpClient.Request.RequestUri?.Port ?? 443;
            if (host != null && Http3OriginCapabilityCache.TryGet($"{host}:{port}", out _))
                return true;
        }

        return false;
    }
}
