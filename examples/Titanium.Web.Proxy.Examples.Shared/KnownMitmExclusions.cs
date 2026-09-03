using Titanium.Web.Proxy;

namespace Titanium.Web.Proxy.Examples.Shared;

/// <summary>
///     Shared demo exclusions — delegates to <see cref="MitmExclusionDefaults"/>.
/// </summary>
public static class KnownMitmExclusions
{
    public static string[] SystemProxyBypassRules => MitmExclusionDefaults.SystemProxyBypassRules;

    public static SystemProxySettings CreateSystemProxySettings(Action<SystemProxySettings>? configure = null)
    {
        var settings = MitmExclusionDefaults.CreateSystemProxySettings();
        configure?.Invoke(settings);
        return settings;
    }

    public static bool ShouldDisableSslDecrypt(string? hostname) =>
        MitmExclusionDefaults.ShouldDisableSslDecrypt(hostname);

    public static bool IsMicrosoftIdentityHost(string? hostname)
    {
        if (string.IsNullOrEmpty(hostname))
        {
            return false;
        }

        return MitmExclusionDefaults.SystemProxyBypassRules.Any(rule =>
            MitmExclusionDefaults.HostnameMatches(hostname, rule));
    }
}
