using System.Diagnostics;
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
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return HasWindowsPac();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return HasMacPac();

        return false;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool HasWindowsPac()
    {
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

    private static bool HasMacPac()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "scutil",
                Arguments = "--proxy",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return ScutilIndicatesPacOrWpad(output);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when <c>scutil --proxy</c> shows PAC or WPAD enabled (Firefox would ignore manual HTTP proxy).</summary>
    internal static bool ScutilIndicatesPacOrWpad(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;

        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var sep = line.IndexOf(':');
            if (sep < 0) continue;
            var key = line[..sep].Trim();
            var value = line[(sep + 1)..].Trim();
            if (!key.Equals("ProxyAutoConfigEnable", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("ProxyAutoDiscoveryEnable", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
