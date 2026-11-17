using System;
using System.Diagnostics;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Clears proxy settings left behind when Inspector is removed or killed.
///     A normal quit restores the in-memory snapshot. This path does not need that snapshot:
///     it turns loopback proxies off so browsers do not wait on a listener that is gone.
/// </summary>
public static class InstalledProxyCleanup
{
    public static void ForceClear()
    {
        if (OperatingSystem.IsLinux())
        {
            using var backend = new LinuxSystemProxyBackend();
            backend.ForceClearInstalledProxy();
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            ClearMacLoopbackProxies();
        }
    }

    private static void ClearMacLoopbackProxies()
    {
        var listed = Run("networksetup", "-listallnetworkservices");
        if (string.IsNullOrWhiteSpace(listed))
        {
            return;
        }

        foreach (var raw in listed.Split('\n'))
        {
            var service = raw.Trim().TrimStart('*').Trim();
            if (service.Length == 0 || service.StartsWith("An asterisk", StringComparison.Ordinal))
            {
                continue;
            }

            DisableIfLoopback(service, "-getwebproxy", "-setwebproxystate");
            DisableIfLoopback(service, "-getsecurewebproxy", "-setsecurewebproxystate");
            DisableIfLoopback(service, "-getsocksfirewallproxy", "-setsocksfirewallproxystate");
        }
    }

    private static void DisableIfLoopback(string service, string getFlag, string stateFlag)
    {
        var info = Run("networksetup", getFlag + " " + Quote(service));
        if (string.IsNullOrWhiteSpace(info) || !IsEnabledLoopback(info))
        {
            return;
        }

        Run("networksetup", stateFlag + " " + Quote(service) + " off");
    }

    private static bool IsEnabledLoopback(string info)
    {
        var enabled = false;
        var loopback = false;
        foreach (var raw in info.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Enabled:", StringComparison.OrdinalIgnoreCase)
                && line.Contains("Yes", StringComparison.OrdinalIgnoreCase))
            {
                enabled = true;
            }

            if (line.StartsWith("Server:", StringComparison.OrdinalIgnoreCase))
            {
                var server = line["Server:".Length..].Trim();
                loopback = server is "127.0.0.1" or "localhost" or "::1";
            }
        }

        return enabled && loopback;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Run(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null || !process.WaitForExit(8000))
            {
                return "";
            }

            return process.StandardOutput.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }
}
