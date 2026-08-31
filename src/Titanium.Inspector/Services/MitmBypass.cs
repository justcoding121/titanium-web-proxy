using System.Linq;
using Titanium.Web.Proxy;

namespace Titanium.Inspector.Services;

/// <summary>Identity / pinning hosts that should bypass system proxy or SSL decrypt.</summary>
public static class MitmBypass
{
    public static readonly string[] SystemProxyBypassRules =
    [
        "*.microsoftonline.com",
        "*.microsoftonline-p.com",
        "login.windows.net",
        "*.login.microsoft.com",
        "login.live.com",
        "account.live.com",
        "*.msauth.net",
        "*.msftauth.net",
        "enterpriseregistration.windows.net",
    ];

    public static SystemProxySettings CreateSystemProxySettings(bool includeLoopback = true)
    {
        var settings = new SystemProxySettings();
        foreach (var rule in SystemProxyBypassRules)
        {
            settings.BypassRules.Add(rule);
        }

        if (includeLoopback)
        {
            settings.ProxyLoopback = true;
        }

        return settings;
    }

    public static bool ShouldDisableSslDecrypt(string? hostname) =>
        ShouldDisableSslDecrypt(hostname, userSkipHosts: null, userOnlyHosts: null);

    /// <summary>
    /// Returns true when TLS should stay opaque (no MITM decrypt).
    /// Built-in SSO/pinning hosts always skip. User skip patterns add more.
    /// When <paramref name="userOnlyHosts"/> is non-empty, only matching hosts decrypt
    /// (built-in bypass hosts still never decrypt).
    /// </summary>
    public static bool ShouldDisableSslDecrypt(
        string? hostname,
        IEnumerable<string>? userSkipHosts,
        IEnumerable<string>? userOnlyHosts)
    {
        if (string.IsNullOrEmpty(hostname))
        {
            return false;
        }

        if (IsBuiltInSslBypass(hostname))
        {
            return true;
        }

        if (MatchesAny(hostname, userSkipHosts))
        {
            return true;
        }

        var only = userOnlyHosts?
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .ToList();
        if (only is { Count: > 0 } && !MatchesAny(hostname, only))
        {
            return true;
        }

        return false;
    }

    public static bool HostnameMatches(string hostname, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        pattern = pattern.Trim();
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = pattern[1..];
            return hostname.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                   || hostname.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase);
        }

        return hostname.Equals(pattern, StringComparison.OrdinalIgnoreCase)
               || hostname.EndsWith("." + pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBuiltInSslBypass(string hostname)
    {
        if (SystemProxyBypassRules.Any(rule => HostnameMatches(hostname, rule)))
        {
            return true;
        }

        return hostname.Contains("dropbox.com", StringComparison.OrdinalIgnoreCase)
               || hostname.Contains("webex.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesAny(string hostname, IEnumerable<string>? patterns)
    {
        if (patterns is null)
        {
            return false;
        }

        return patterns.Any(pattern => HostnameMatches(hostname, pattern));
    }
}
