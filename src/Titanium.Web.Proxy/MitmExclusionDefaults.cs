using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy;

/// <summary>
///     Default hostname exclusions for MITM proxies (Microsoft identity / certificate pinning).
///     Use with <see cref="SystemProxySettings.BypassRules"/> and
///     <see cref="EventArguments.TunnelConnectSessionEventArgs.DecryptSsl"/>.
/// </summary>
public static class MitmExclusionDefaults
{
    /// <summary>WinINET bypass patterns for Microsoft identity endpoints (Entra / WAM / RDP).</summary>
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

    /// <summary>Pinning demo hosts that should tunnel (DecryptSsl=false) but stay visible.</summary>
    public static readonly string[] TunnelOnlyPinningDomains = ["dropbox.com", "webex.com"];

    /// <summary>
    ///     Builds <see cref="SystemProxySettings"/> with identity bypass rules and optional user additions.
    /// </summary>
    public static SystemProxySettings CreateSystemProxySettings(
        bool proxyLoopback = true,
        IEnumerable<string>? additionalBypassRules = null)
    {
        var settings = new SystemProxySettings();
        foreach (var rule in SystemProxyBypassRules)
        {
            settings.BypassRules.Add(rule);
        }

        if (additionalBypassRules is not null)
        {
            foreach (var rule in additionalBypassRules.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                settings.BypassRules.Add(rule.Trim());
            }
        }

        settings.ProxyLoopback = proxyLoopback;
        return settings;
    }

    /// <summary>Returns true when CONNECT should use SSL passthrough instead of MITM.</summary>
    public static bool ShouldDisableSslDecrypt(string? hostname) =>
        ShouldDisableSslDecrypt(hostname, userSkipHosts: null, userOnlyHosts: null);

    /// <summary>
    ///     Returns true when TLS should stay opaque (no MITM decrypt).
    ///     Built-in SSO/pinning hosts always skip. User skip patterns add more.
    ///     When <paramref name="userOnlyHosts"/> is non-empty, only matching hosts decrypt
    ///     (built-in bypass hosts still never decrypt).
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

    /// <summary>Wildcard-aware hostname match (<c>*.example.com</c>).</summary>
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

    /// <summary>Registers per-CONNECT <c>DecryptSsl</c> gating on an explicit endpoint.</summary>
    public static void ApplyDecryptExclusions(
        ExplicitProxyEndPoint endPoint,
        Func<bool> decryptHttpsEnabled,
        IEnumerable<string>? decryptSkipHosts = null,
        IEnumerable<string>? decryptOnlyHosts = null)
    {
        endPoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            var host = e.HttpClient.Request.RequestUri?.Host
                       ?? e.HttpClient.Request.Host;
            e.DecryptSsl = decryptHttpsEnabled()
                           && !ShouldDisableSslDecrypt(host, decryptSkipHosts, decryptOnlyHosts);
            return Task.CompletedTask;
        };
    }

    private static bool IsBuiltInSslBypass(string hostname)
    {
        if (SystemProxyBypassRules.Any(rule => HostnameMatches(hostname, rule)))
        {
            return true;
        }

        return TunnelOnlyPinningDomains.Any(domain =>
            hostname.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || hostname.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
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
