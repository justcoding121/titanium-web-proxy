using System;
using System.Net;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Transforms;

/// <summary>Writes X-Forwarded-* headers. Untrusted peers cannot append to an inbound value.</summary>
internal static class ForwardedHeadersApplier
{
    public static void Apply(SessionEventArgsBase session, ForwardedHeadersConfig config)
    {
        var headers = session.HttpClient.Request.Headers;
        var trusted = IsTrusted(session.ClientRemoteEndPoint.Address, config.TrustedProxies);
        ApplyOne(headers, "X-Forwarded-For", config.XForwardedFor, session.ClientRemoteEndPoint.Address.ToString(), trusted);
        ApplyOne(headers, "X-Forwarded-Proto", config.XForwardedProto, session.HttpClient.Request.IsHttps ? "https" : "http", trusted);
        var host = session.HttpClient.Request.Host;
        ApplyOne(headers, "X-Forwarded-Host", config.XForwardedHost, host, trusted);
    }

    private static void ApplyOne(Titanium.Web.Proxy.Http.HeaderCollection headers, string name, ForwardedHeaderAction action, string? value, bool trusted)
    {
        if (action == ForwardedHeaderAction.Off || string.IsNullOrEmpty(value))
        {
            return;
        }

        if (action == ForwardedHeaderAction.Remove)
        {
            headers.RemoveHeader(name);
            return;
        }

        if (!trusted || action == ForwardedHeaderAction.Set)
        {
            headers.SetOrAddHeaderValue(name, value);
            return;
        }

        var existing = headers.GetHeaders(name);
        var current = existing is { Count: > 0 } ? existing[0].Value : null;
        headers.SetOrAddHeaderValue(name, string.IsNullOrEmpty(current) ? value : current + ", " + value);
    }

    private static bool IsTrusted(IPAddress address, System.Collections.Generic.IReadOnlyList<string>? proxies)
    {
        if (proxies is null)
        {
            return false;
        }

        var ip = address.ToString();
        for (var i = 0; i < proxies.Count; i++)
        {
            var rule = proxies[i];
            var slash = rule.IndexOf('/');
            if (slash < 0)
            {
                if (string.Equals(rule, ip, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                continue;
            }

            if (!IPAddress.TryParse(rule[..slash], out var network) || !int.TryParse(rule[(slash + 1)..], out var bits))
            {
                continue;
            }

            var mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
            var peer = ToUInt(address);
            var net = ToUInt(network);
            if ((peer & mask) == (net & mask))
            {
                return true;
            }
        }

        return false;
    }

    private static uint ToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}
