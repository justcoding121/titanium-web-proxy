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

    public static bool ShouldDisableSslDecrypt(string? hostname)
    {
        if (string.IsNullOrEmpty(hostname))
        {
            return false;
        }

        foreach (var rule in SystemProxyBypassRules)
        {
            if (HostnameMatches(hostname, rule))
            {
                return true;
            }
        }

        return hostname.Contains("dropbox.com", StringComparison.OrdinalIgnoreCase)
               || hostname.Contains("webex.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HostnameMatches(string hostname, string pattern)
    {
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = pattern[1..];
            return hostname.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                   || hostname.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase);
        }

        return hostname.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
