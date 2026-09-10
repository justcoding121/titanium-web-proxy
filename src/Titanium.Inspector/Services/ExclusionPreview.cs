using System.Runtime.InteropServices;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Inspector.Services;

/// <summary>Formats effective OS proxy bypass lists for the exclusions UI.</summary>
public static class ExclusionPreview
{
    public static string BuildWinInetOverride(InspectorSettings settings, string? currentOverride = null)
    {
        var proxySettings = MitmBypass.CreateSystemProxySettings(settings);
        return proxySettings.BuildProxyOverride(currentOverride);
    }

    public static (string Label, string Value) FormatForCurrentOs(
        InspectorSettings settings,
        string? currentOverride = null)
    {
        var winInet = BuildWinInetOverride(settings, currentOverride);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ("WinINET bypass list", winInet);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return ("Proxy bypass domains (networksetup)", UnixProxyBypassMapper.ToCommaSeparated(winInet));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var gsettings = UnixProxyBypassMapper.ToGsettingsArray(winInet);
            var noProxy = UnixProxyBypassMapper.ToNoProxyEnv(winInet);
            return ("Ignored hosts / NO_PROXY", $"gsettings: {gsettings}\nNO_PROXY={noProxy}");
        }

        return ("Bypass list", winInet);
    }

    public static string ExclusionSummary(InspectorSettings settings)
    {
        var bypass = settings.SystemProxyBypassHosts?.Count(h => !string.IsNullOrWhiteSpace(h)) ?? 0;
        var tunnel = settings.DecryptSkipHosts?.Count(h => !string.IsNullOrWhiteSpace(h)) ?? 0;
        if (bypass == 0 && tunnel == 0)
        {
            return "";
        }

        return $"Exclusions: {bypass} OS bypass, {tunnel} tunnel-only";
    }

    public static string DescribeOpaqueReason(OpaqueTunnelReason reason) => reason switch
    {
        OpaqueTunnelReason.DecryptOff => "Encrypted: Decrypt HTTPS is off",
        OpaqueTunnelReason.BuiltInIdentity => "Encrypted: Microsoft identity bypass (tunnel)",
        OpaqueTunnelReason.BuiltInPinning => "Encrypted: pinning host (tunnel only)",
        OpaqueTunnelReason.UserSkipList => "Encrypted: tunnel-only exclusion list",
        OpaqueTunnelReason.UserOnlyList => "Encrypted: not on decrypt-only allowlist",
        _ => "",
    };
}
