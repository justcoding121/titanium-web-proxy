using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Titanium.Inspector.Services;

/// <summary>Detects PAC / WPAD scripts before Inspector replaces system proxy settings.</summary>
public static class SystemProxyPacHelper
{
    private const string RegKeyInternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string RegAutoConfigUrl = "AutoConfigURL";

    public static bool HasActivePacScript()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        try
        {
            using var reg = Registry.CurrentUser.OpenSubKey(RegKeyInternetSettings, false);
            var url = reg?.GetValue(RegAutoConfigUrl) as string;
            return !string.IsNullOrWhiteSpace(url);
        }
        catch
        {
            return false;
        }
    }
}
