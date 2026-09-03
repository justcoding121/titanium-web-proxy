using Titanium.Web.Proxy;

namespace Titanium.Inspector.Services;

/// <summary>Identity / pinning hosts that should bypass system proxy or SSL decrypt.</summary>
public static class MitmBypass
{
    public static string[] SystemProxyBypassRules => MitmExclusionDefaults.SystemProxyBypassRules;

    public static SystemProxySettings CreateSystemProxySettings(bool includeLoopback = true) =>
        CreateSystemProxySettings(new InspectorSettings { ProxyLoopback = includeLoopback });

    public static SystemProxySettings CreateSystemProxySettings(InspectorSettings settings)
    {
        return MitmExclusionDefaults.CreateSystemProxySettings(
            settings.ProxyLoopback,
            settings.SystemProxyBypassHosts);
    }

    public static bool ShouldDisableSslDecrypt(string? hostname) =>
        MitmExclusionDefaults.ShouldDisableSslDecrypt(hostname);

    public static bool ShouldDisableSslDecrypt(
        string? hostname,
        IEnumerable<string>? userSkipHosts,
        IEnumerable<string>? userOnlyHosts) =>
        MitmExclusionDefaults.ShouldDisableSslDecrypt(hostname, userSkipHosts, userOnlyHosts);

    public static bool HostnameMatches(string hostname, string pattern) =>
        MitmExclusionDefaults.HostnameMatches(hostname, pattern);

    public static OpaqueTunnelReason ResolveOpaqueReason(
        string? hostname,
        bool decryptHttps,
        IEnumerable<string>? userSkipHosts,
        IEnumerable<string>? userOnlyHosts)
    {
        if (string.IsNullOrEmpty(hostname))
        {
            return OpaqueTunnelReason.None;
        }

        if (!decryptHttps)
        {
            return OpaqueTunnelReason.DecryptOff;
        }

        if (SystemProxyBypassRules.Any(rule => HostnameMatches(hostname, rule)))
        {
            return OpaqueTunnelReason.BuiltInIdentity;
        }

        if (MitmExclusionDefaults.TunnelOnlyPinningDomains.Any(domain =>
                hostname.Equals(domain, StringComparison.OrdinalIgnoreCase)
                || hostname.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
        {
            return OpaqueTunnelReason.BuiltInPinning;
        }

        if (userSkipHosts is not null && userSkipHosts.Any(p => HostnameMatches(hostname, p)))
        {
            return OpaqueTunnelReason.UserSkipList;
        }

        var only = userOnlyHosts?
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .ToList();
        if (only is { Count: > 0 } && !only.Any(p => HostnameMatches(hostname, p)))
        {
            return OpaqueTunnelReason.UserOnlyList;
        }

        return OpaqueTunnelReason.None;
    }
}
