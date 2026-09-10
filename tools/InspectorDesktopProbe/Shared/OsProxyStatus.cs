using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Titanium.Inspector.DesktopProbe.Shared;

public readonly record struct WinInetSnapshot(int? ProxyEnable, string? ProxyServer, string? ProxyOverride);

/// <summary>Read OS system-proxy state for probe / E2E-Slow asserts.</summary>
public static class OsProxyStatus
{
    public static WinInetSnapshot ReadWinInet()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return default;

        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: false);
        if (key is null)
            return default;

        var enableObj = key.GetValue("ProxyEnable");
        int? enable = enableObj is null ? null : Convert.ToInt32(enableObj);
        return new WinInetSnapshot(
            enable,
            key.GetValue("ProxyServer") as string,
            key.GetValue("ProxyOverride") as string);
    }

    public static string Dump()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var w = ReadWinInet();
            return $"WinINET ProxyEnable={w.ProxyEnable} ProxyServer={w.ProxyServer} ProxyOverride={w.ProxyOverride}";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "gsettings: " + GsettingsGet("org.gnome.system.proxy", "mode") +
                   " http.host=" + GsettingsGet("org.gnome.system.proxy.http", "host") +
                   " http.port=" + GsettingsGet("org.gnome.system.proxy.http", "port") +
                   " http.enabled=" + GsettingsGet("org.gnome.system.proxy.http", "enabled");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "scutil --proxy:\n" + RunCapture("scutil", "--proxy");

        return "unknown OS";
    }

    public static void AssertLinuxGsettingsPointsAtProxy(int port)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        var mode = GsettingsGet("org.gnome.system.proxy", "mode").Trim().Trim('\'', '"');
        var host = GsettingsGet("org.gnome.system.proxy.http", "host").Trim().Trim('\'', '"');
        var portText = GsettingsGet("org.gnome.system.proxy.http", "port").Trim();
        var enabled = GsettingsGet("org.gnome.system.proxy.http", "enabled").Trim().Trim('\'', '"');

        if (!string.Equals(mode, "manual", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Expected gsettings mode=manual, got '{mode}'");
        if (!string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Expected gsettings http host=127.0.0.1, got '{host}'");
        if (!int.TryParse(portText, out var p) || p != port)
            throw new InvalidOperationException($"Expected gsettings http port={port}, got '{portText}'");
        if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Expected gsettings http enabled=true, got '{enabled}' (GIO/Chrome ignore mode=manual otherwise)");
    }

    public static bool WinInetPointsAt(int port)
    {
        var w = ReadWinInet();
        return w.ProxyEnable == 1
               && (w.ProxyServer ?? string.Empty).Contains($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase);
    }

    private static string GsettingsGet(string schema, string key)
    {
        // Clear poisoned sandbox dbus so reads hit the real session bus.
        try
        {
            var psi = new ProcessStartInfo("gsettings", $"get {schema} {key}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment.Remove("DBUS_SESSION_BUS_ADDRESS");
            using var p = Process.Start(psi);
            if (p is null)
                return string.Empty;
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            return stdout.Trim();
        }
        catch (Exception ex)
        {
            return $"(gsettings failed: {ex.Message})";
        }
    }

    private static string RunCapture(string fileName, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null)
                return string.Empty;
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            return stdout.Trim();
        }
        catch (Exception ex)
        {
            return $"({fileName} failed: {ex.Message})";
        }
    }
}
