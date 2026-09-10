using Titanium.Web.Proxy;

namespace Titanium.Inspector.Services;

/// <summary>Identity / pinning hosts that should bypass system proxy or SSL decrypt.</summary>
public static class MitmBypass
{
    public static string[] SystemProxyBypassRules => MitmExclusionDefaults.SystemProxyBypassRules;

    public static string[] TunnelOnlyPinningDomains => MitmExclusionDefaults.TunnelOnlyPinningDomains;

    /// <summary>
    ///     Factory OS-bypass defaults with optional loopback (Merge mode — for callers without saved settings).
    /// </summary>
    public static SystemProxySettings CreateSystemProxySettings(bool includeLoopback = true) =>
        MitmExclusionDefaults.CreateSystemProxySettings(includeLoopback);

    /// <summary>
    ///     Builds system-proxy settings from the Inspector exclusion lists (Replace mode —
    ///     factory defaults are not re-merged; seed them into settings instead).
    /// </summary>
    public static SystemProxySettings CreateSystemProxySettings(InspectorSettings settings)
    {
        return MitmExclusionDefaults.CreateSystemProxySettings(
            settings.ProxyLoopback,
            settings.SystemProxyBypassHosts,
            MitmExclusionMode.Replace);
    }

    public static bool ShouldDisableSslDecrypt(string? hostname) =>
        MitmExclusionDefaults.ShouldDisableSslDecrypt(hostname);

    public static bool ShouldDisableSslDecrypt(
        string? hostname,
        IEnumerable<string>? userSkipHosts,
        IEnumerable<string>? userOnlyHosts) =>
        MitmExclusionDefaults.ShouldDisableSslDecrypt(
            hostname, userSkipHosts, userOnlyHosts, MitmExclusionMode.Replace);

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

        // Replace mode: classify using the lists the user (or seed) provided.
        if (userSkipHosts is not null && userSkipHosts.Any(p => HostnameMatches(hostname, p)))
        {
            // Prefer friendlier labels when the pattern matches factory seeds.
            if (SystemProxyBypassRules.Any(rule => HostnameMatches(hostname, rule)))
            {
                return OpaqueTunnelReason.BuiltInIdentity;
            }

            if (TunnelOnlyPinningDomains.Any(domain =>
                    hostname.Equals(domain, StringComparison.OrdinalIgnoreCase)
                    || hostname.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
            {
                return OpaqueTunnelReason.BuiltInPinning;
            }

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
