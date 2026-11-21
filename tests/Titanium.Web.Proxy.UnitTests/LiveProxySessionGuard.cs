using System;
using System.IO;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Read-only check. Unit tests that rewrite browser prefs, relaunch browsers,
///     or stop the fail-open helper must bail out when this desktop still needs
///     the Inspector listener. A connect-and-close does not stop that listener.
/// </summary>
internal static class LiveProxySessionGuard
{
    internal static void InconclusiveIfDesktopDependsOnInspector()
    {
        if (!DesktopDependsOnInspectorProxy())
            return;

        Assert.Inconclusive(
            "This desktop is using the Inspector proxy. Refusing to change browsers, fail-open, or port 8866.");
    }

    internal static bool DesktopDependsOnInspectorProxy()
    {
        if (LoopbackPortAccepts(8866))
            return true;
        if (FailOpenPidFilePresent())
            return true;
        if (InspectorExecutableRunning())
            return true;

        var proxy = Environment.GetEnvironmentVariable("http_proxy")
                    ?? Environment.GetEnvironmentVariable("HTTP_PROXY")
                    ?? Environment.GetEnvironmentVariable("https_proxy")
                    ?? Environment.GetEnvironmentVariable("HTTPS_PROXY");
        return proxy != null &&
               (proxy.Contains("127.0.0.1:8866", StringComparison.Ordinal) ||
                proxy.Contains("localhost:8866", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LoopbackPortAccepts(int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync("127.0.0.1", port);
            return connect.Wait(TimeSpan.FromMilliseconds(200)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static bool FailOpenPidFilePresent()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return false;
        var pidPath = Path.Combine(home, ".config", "TitaniumInspector", "fail-open-proxy.pid");
        return File.Exists(pidPath);
    }

    private static bool InspectorExecutableRunning()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc"))
            return false;

        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(dir);
            if (name.Length == 0 || !char.IsDigit(name[0]))
                continue;
            try
            {
                var raw = File.ReadAllText(Path.Combine(dir, "cmdline"));
                var exe = raw.Split('\0', 2)[0];
                if (exe.EndsWith("/TitaniumInspector", StringComparison.Ordinal))
                    return true;
            }
            catch
            {
                // process exited or is not readable
            }
        }

        return false;
    }
}
