using System;
using System.Diagnostics;
#if NET6_0_OR_GREATER
using System.Runtime.Versioning;
#endif
using Microsoft.Win32;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Clears proxy settings left behind when Inspector is removed or killed.
///     A normal quit restores the in-memory snapshot. This path does not need that snapshot:
///     it turns loopback proxies off so browsers do not wait on a listener that is gone.
/// </summary>
public static class InstalledProxyCleanup
{
    private const string RegKeyInternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string RegProxyEnable = "ProxyEnable";
    private const string RegProxyServer = "ProxyServer";

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
            return;
        }

        if (OperatingSystem.IsWindows())
            ClearWindowsLoopbackProxies();
    }

#if NET6_0_OR_GREATER
    [SupportedOSPlatform("windows")]
#endif
    private static void ClearWindowsLoopbackProxies()
    {
        try
        {
            using var reg = Registry.CurrentUser.OpenSubKey(RegKeyInternetSettings, writable: true);
            if (reg is null)
                return;

            var enabled = reg.GetValue(RegProxyEnable) is int pe && pe == 1;
            var server = reg.GetValue(RegProxyServer) as string ?? string.Empty;
            if (!enabled || !IsWindowsLoopbackProxyServer(server))
                return;

            reg.SetValue(RegProxyEnable, 0);
            reg.SetValue(RegProxyServer, string.Empty);
            NativeMethods.InternetSetOption(IntPtr.Zero, SystemProxyManager.InternetOptionSettingsChanged, IntPtr.Zero, 0);
            NativeMethods.InternetSetOption(IntPtr.Zero, SystemProxyManager.InternetOptionRefresh, IntPtr.Zero, 0);
        }
        catch
        {
            // best-effort
        }
    }

    private static bool IsWindowsLoopbackProxyServer(string proxyServer)
    {
        if (string.IsNullOrWhiteSpace(proxyServer))
            return false;

        // WinINET forms: "127.0.0.1:8866", "http=127.0.0.1:8866;https=127.0.0.1:8866", "localhost:8866"
        foreach (var part in proxyServer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var hostPort = part;
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
                hostPort = part[(eq + 1)..];
            var host = hostPort;
            var colon = hostPort.LastIndexOf(':');
            if (colon > 0)
                host = hostPort[..colon];
            host = host.Trim().TrimStart('[').TrimEnd(']');
            if (host is "127.0.0.1" or "localhost" or "::1")
                return true;
        }

        return false;
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
