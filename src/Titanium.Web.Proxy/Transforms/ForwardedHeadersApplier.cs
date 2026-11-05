using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using Titanium.Web.Proxy.Abstractions.Network;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Transforms;

/// <summary>Writes X-Forwarded-* headers. Untrusted peers cannot append to an inbound value.</summary>
internal static class ForwardedHeadersApplier
{
    /// <summary>Rules parsed once per trusted-proxy list. A rule that does not parse trusts nobody.</summary>
    private static readonly ConditionalWeakTable<IReadOnlyList<string>, CidrPrefix[]> TrustedRules = new();

    public static void Apply(SessionEventArgsBase session, ForwardedHeadersConfig config)
    {
        var headers = session.HttpClient.Request.Headers;
        var trusted = IsTrusted(session.ClientRemoteEndPoint.Address, config.TrustedProxies);
        ApplyOne(headers, "X-Forwarded-For", config.XForwardedFor, ClientText(session.ClientRemoteEndPoint.Address), trusted);
        ApplyOne(headers, "X-Forwarded-Proto", config.XForwardedProto, session.HttpClient.Request.IsHttps ? "https" : "http", trusted);
        var host = session.HttpClient.Request.Host;
        ApplyOne(headers, "X-Forwarded-Host", config.XForwardedHost, host, trusted);
    }

    /// <summary>An IPv4-mapped IPv6 client is written as plain IPv4, the form origin logs and ACLs expect.</summary>
    private static string ClientText(IPAddress address) =>
        (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

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

    internal static bool IsTrusted(IPAddress address, IReadOnlyList<string>? proxies)
    {
        if (proxies is not { Count: > 0 })
        {
            return false;
        }

        // CidrPrefix compares an IPv4-mapped IPv6 peer (what a dual-stack listener reports) as IPv4 and
        // rejects a prefix length outside the address size, which a hand-rolled shift mask did not.
        var rules = TrustedRules.GetValue(proxies, static list => ParseRules(list));
        for (var i = 0; i < rules.Length; i++)
        {
            if (rules[i].Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    private static CidrPrefix[] ParseRules(IReadOnlyList<string> proxies)
    {
        var parsed = new List<CidrPrefix>(proxies.Count);
        for (var i = 0; i < proxies.Count; i++)
        {
            if (CidrPrefix.TryParse(proxies[i], out var prefix) && prefix is not null)
            {
                parsed.Add(prefix);
            }
        }

        return [.. parsed];
    }
}
