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
    /// <summary>WinINET / system-proxy bypass patterns for Microsoft identity endpoints (Entra / WAM / RDP).</summary>
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

    /// <summary>Pinning hosts that should tunnel (DecryptSsl=false) but stay visible.</summary>
    public static readonly string[] TunnelOnlyPinningDomains = ["dropbox.com", "webex.com"];

    /// <summary>
    ///     Builds <see cref="SystemProxySettings"/> with factory identity bypass rules and loopback
    ///     (<see cref="MitmExclusionMode.Merge"/>).
    /// </summary>
    public static SystemProxySettings CreateSystemProxySettings() =>
        CreateSystemProxySettings(proxyLoopback: true, null, MitmExclusionMode.Merge);

    /// <summary>
    ///     Builds <see cref="SystemProxySettings"/> with factory identity bypass rules
    ///     (<see cref="MitmExclusionMode.Merge"/>).
    /// </summary>
    public static SystemProxySettings CreateSystemProxySettings(bool proxyLoopback) =>
        CreateSystemProxySettings(proxyLoopback, null, MitmExclusionMode.Merge);

    /// <summary>
    ///     Builds <see cref="SystemProxySettings"/> with identity bypass rules and optional user additions
    ///     (<see cref="MitmExclusionMode.Merge"/>).
    /// </summary>
    public static SystemProxySettings CreateSystemProxySettings(
        bool proxyLoopback,
        IEnumerable<string>? additionalBypassRules) =>
        CreateSystemProxySettings(proxyLoopback, additionalBypassRules, MitmExclusionMode.Merge);

    /// <summary>
    ///     Builds <see cref="SystemProxySettings"/> from factory and/or caller bypass rules.
    /// </summary>
    /// <param name="proxyLoopback">When true, localhost uses the proxy (platform-specific loopback rule).</param>
    /// <param name="bypassRules">
    ///     Extra rules in <see cref="MitmExclusionMode.Merge"/>, or the full authoritative list in
    ///     <see cref="MitmExclusionMode.Replace"/>.
    /// </param>
    /// <param name="mode">Merge factory OS-bypass defaults, or replace them with <paramref name="bypassRules"/>.</param>
    public static SystemProxySettings CreateSystemProxySettings(
        bool proxyLoopback,
        IEnumerable<string>? bypassRules,
        MitmExclusionMode mode)
    {
        var settings = new SystemProxySettings();
        if (mode == MitmExclusionMode.Merge)
        {
            foreach (var rule in SystemProxyBypassRules)
            {
                settings.BypassRules.Add(rule);
            }
        }

        if (bypassRules is not null)
        {
            foreach (var rule in bypassRules.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                settings.BypassRules.Add(rule.Trim());
            }
        }

        settings.ProxyLoopback = proxyLoopback;
        return settings;
    }

    /// <summary>Returns true when CONNECT should use SSL passthrough instead of MITM (Merge mode).</summary>
    public static bool ShouldDisableSslDecrypt(string? hostname) =>
        ShouldDisableSslDecrypt(hostname, userSkipHosts: null, userOnlyHosts: null, MitmExclusionMode.Merge);

    /// <summary>
    ///     Returns true when TLS should stay opaque (no MITM decrypt) using <see cref="MitmExclusionMode.Merge"/>.
    /// </summary>
    public static bool ShouldDisableSslDecrypt(
        string? hostname,
        IEnumerable<string>? userSkipHosts,
        IEnumerable<string>? userOnlyHosts) =>
        ShouldDisableSslDecrypt(hostname, userSkipHosts, userOnlyHosts, MitmExclusionMode.Merge);

    /// <summary>
    ///     Returns true when TLS should stay opaque (no MITM decrypt).
    ///     <see cref="MitmExclusionMode.Merge"/>: factory SSO/pinning hosts always skip, then user skip /
    ///     optional decrypt-only allowlist.
    ///     <see cref="MitmExclusionMode.Replace"/>: only <paramref name="userSkipHosts"/> and optional
    ///     <paramref name="userOnlyHosts"/> apply (factory hosts are not forced).
    /// </summary>
    public static bool ShouldDisableSslDecrypt(
        string? hostname,
        IEnumerable<string>? userSkipHosts,
        IEnumerable<string>? userOnlyHosts,
        MitmExclusionMode mode)
    {
        if (string.IsNullOrEmpty(hostname))
        {
            return false;
        }

        if (mode == MitmExclusionMode.Merge && IsBuiltInSslBypass(hostname))
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

    /// <summary>Registers per-CONNECT <c>DecryptSsl</c> gating on an explicit endpoint (Merge mode).</summary>
    public static void ApplyDecryptExclusions(
        ExplicitProxyEndPoint endPoint,
        Func<bool> decryptHttpsEnabled) =>
        ApplyDecryptExclusions(endPoint, decryptHttpsEnabled, null, null, MitmExclusionMode.Merge);

    /// <summary>Registers per-CONNECT <c>DecryptSsl</c> gating on an explicit endpoint (Merge mode).</summary>
    public static void ApplyDecryptExclusions(
        ExplicitProxyEndPoint endPoint,
        Func<bool> decryptHttpsEnabled,
        IEnumerable<string>? decryptSkipHosts,
        IEnumerable<string>? decryptOnlyHosts) =>
        ApplyDecryptExclusions(
            endPoint, decryptHttpsEnabled, decryptSkipHosts, decryptOnlyHosts, MitmExclusionMode.Merge);

    /// <summary>Registers per-CONNECT <c>DecryptSsl</c> gating on an explicit endpoint.</summary>
    public static void ApplyDecryptExclusions(
        ExplicitProxyEndPoint endPoint,
        Func<bool> decryptHttpsEnabled,
        IEnumerable<string>? decryptSkipHosts,
        IEnumerable<string>? decryptOnlyHosts,
        MitmExclusionMode mode)
    {
        endPoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            var host = e.HttpClient.Request.RequestUri?.Host
                       ?? e.HttpClient.Request.Host;
            e.DecryptSsl = decryptHttpsEnabled()
                           && !ShouldDisableSslDecrypt(host, decryptSkipHosts, decryptOnlyHosts, mode);
            return Task.CompletedTask;
        };
    }

    /// <summary>True when <paramref name="hostname"/> matches factory OS-bypass or tunnel-only defaults.</summary>
    public static bool IsBuiltInSslBypass(string hostname)
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
