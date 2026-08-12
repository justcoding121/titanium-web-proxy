using System;
using Titanium.Web.Proxy;

namespace Titanium.Web.Proxy.Examples.Shared;

/// <summary>
///     Shared demo exclusions for hosts that commonly break under MITM when the examples are
///     installed as the Windows system proxy.
/// </summary>
/// <remarks>
///     <para>
///         <b>System proxy bypass</b> is used for Microsoft identity endpoints (Entra / WAM / RDP
///         auth error 0xcaa30194): those clients often fail even with an opaque CONNECT tunnel.
///     </para>
///     <para>
///         <b>DecryptSsl = false</b> (passthrough) is used for classic certificate-pinning demos
///         (Dropbox, Webex) and also for identity hosts if a client still CONNECTs through the proxy.
///         Passthrough keeps the tunnel visible in example traffic logs.
///     </para>
/// </remarks>
public static class KnownMitmExclusions
{
    /// <summary>
    ///     WinINET bypass patterns for Microsoft identity endpoints.
    /// </summary>
    public static readonly string[] SystemProxyBypassRules =
    {
        "*.microsoftonline.com",
        "*.microsoftonline-p.com",
        "login.windows.net",
        "*.login.microsoft.com",
        "login.live.com",
        "account.live.com",
        "*.msauth.net",
        "*.msftauth.net",
        "enterpriseregistration.windows.net"
    };

    /// <summary>
    ///     Builds <see cref="SystemProxySettings"/> with identity bypass rules, then runs
    ///     <paramref name="configure"/> for example-specific options (e.g. ProxyLoopback).
    /// </summary>
    public static SystemProxySettings CreateSystemProxySettings(Action<SystemProxySettings>? configure = null)
    {
        var settings = new SystemProxySettings();
        foreach (var rule in SystemProxyBypassRules)
            settings.BypassRules.Add(rule);

        configure?.Invoke(settings);
        return settings;
    }

    /// <summary>
    ///     Returns true when CONNECT tunnels for <paramref name="hostname"/> should use SSL
    ///     passthrough (<c>DecryptSsl = false</c>) instead of MITM.
    /// </summary>
    public static bool ShouldDisableSslDecrypt(string? hostname)
    {
        if (string.IsNullOrEmpty(hostname))
            return false;

        return IsMicrosoftIdentityHost(hostname) ||
               HostMatchesDomain(hostname, "dropbox.com") ||
               HostMatchesDomain(hostname, "webex.com");
    }

    public static bool IsMicrosoftIdentityHost(string? hostname)
    {
        if (string.IsNullOrEmpty(hostname))
            return false;

        return HostMatchesDomain(hostname, "microsoftonline.com") ||
               HostMatchesDomain(hostname, "microsoftonline-p.com") ||
               hostname.Equals("login.windows.net", StringComparison.OrdinalIgnoreCase) ||
               HostMatchesDomain(hostname, "login.microsoft.com") ||
               hostname.Equals("login.live.com", StringComparison.OrdinalIgnoreCase) ||
               hostname.Equals("account.live.com", StringComparison.OrdinalIgnoreCase) ||
               HostMatchesDomain(hostname, "msauth.net") ||
               HostMatchesDomain(hostname, "msftauth.net") ||
               hostname.Equals("enterpriseregistration.windows.net", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HostMatchesDomain(string hostname, string domain)
    {
        return hostname.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
               hostname.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }
}
