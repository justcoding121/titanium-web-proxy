using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     GNOME gsettings is not enough on XFCE, LXDE, i3/sway, WSLg, or when Chrome is started
///     from a .desktop Exec that never reads org.gnome.system.proxy. Write Chromium-family
///     managed policy plus user .desktop / XFCE helper Exec flags
///     (<c>--proxy-server</c>, <c>&lt;-loopback&gt;</c>, <c>--disable-quic</c>).
/// </summary>
internal static class LinuxBrowserLaunchProxy
{
    internal const string PolicyFileName = "titanium-inspector-proxy.json";
    internal const string DesktopMarker = "X-Titanium-Inspector-Proxy=true";
    internal const string ProxyBypassList = "<-loopback>";

    private static readonly string[] DesktopSources =
    [
        "/usr/share/applications/google-chrome.desktop",
        "/usr/share/applications/google-chrome-stable.desktop",
        "/usr/share/applications/chromium.desktop",
        "/usr/share/applications/chromium-browser.desktop",
        "/usr/share/applications/brave-browser.desktop",
        "/usr/share/applications/microsoft-edge.desktop",
        "/usr/share/applications/microsoft-edge-stable.desktop",
    ];

    private static readonly string[] ChromeBinaries =
    [
        "/usr/bin/google-chrome-stable",
        "/usr/bin/google-chrome",
        "/opt/google/chrome/chrome",
        "/usr/bin/chromium-browser",
        "/usr/bin/chromium",
        "/usr/bin/brave-browser",
        "/usr/bin/microsoft-edge-stable",
        "/usr/bin/microsoft-edge",
    ];

    internal static void Apply(string hostname, int port)
    {
        WritePolicies(hostname, port);
        WriteBrowserDesktopOverrides(hostname, port);
        WriteXfceWebBrowserHelper(hostname, port);
    }

    internal static void Clear()
    {
        foreach (var dir in PolicyDirectories())
        {
            try
            {
                File.Delete(Path.Combine(dir, PolicyFileName));
            }
            catch
            {
                // best-effort
            }
        }

        foreach (var dest in UserDesktopOverridePaths())
            DeleteMarkedDesktopOverride(dest);

        DeleteMarkedDesktopOverride(UserXfceChromeHelperPath());
        RestoreXfceHelpersRc();
    }

    internal static void WritePolicies(string hostname, int port, IEnumerable<string>? directories = null)
    {
        var json = BuildPolicyJson(hostname, port);
        foreach (var dir in directories ?? PolicyDirectories())
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, PolicyFileName), json);
            }
            catch
            {
                // /etc/opt/chrome and snap roots may not be writable
            }
        }
    }

    internal static string BuildPolicyJson(string hostname, int port)
    {
        var server = $"http://{hostname}:{port}";
        return
            "{\n" +
            "  \"ProxyMode\": \"fixed_servers\",\n" +
            "  \"ProxyServer\": \"" + server + "\",\n" +
            "  \"ProxyBypassList\": \"" + ProxyBypassList + "\",\n" +
            "  \"QuicAllowed\": false\n" +
            "}\n";
    }

    internal static string InjectChromeProxyArgs(string execLine, string hostname, int port)
    {
        var flags = ChromeProxyFlags(hostname, port);
        if (execLine.Contains("--proxy-server=", StringComparison.Ordinal))
            return execLine;

        var prefix = execLine.StartsWith("Exec=", StringComparison.Ordinal) ? "Exec=" : "";
        var rest = prefix.Length > 0 ? execLine[prefix.Length..] : execLine;

        // Insert before desktop field codes so every Chromium-family Exec line picks up flags.
        var insertAt = rest.Length;
        foreach (var code in new[] { " %U", " %u", " %f", " %F", " %s", " \"%s\"" })
        {
            var idx = rest.IndexOf(code, StringComparison.Ordinal);
            if (idx >= 0 && idx < insertAt)
                insertAt = idx;
        }

        return prefix + rest[..insertAt] + " " + flags + rest[insertAt..];
    }

    internal static string ChromeProxyFlags(string hostname, int port) =>
        $"--proxy-server=http://{hostname}:{port} --proxy-bypass-list={ProxyBypassList} --disable-quic";

    internal static IEnumerable<string> PolicyDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".config", "google-chrome", "policies", "managed");
        yield return Path.Combine(home, ".config", "google-chrome-beta", "policies", "managed");
        yield return Path.Combine(home, ".config", "google-chrome-unstable", "policies", "managed");
        yield return Path.Combine(home, ".config", "chromium", "policies", "managed");
        yield return Path.Combine(home, ".config", "BraveSoftware", "Brave-Browser", "policies", "managed");
        yield return Path.Combine(home, ".config", "microsoft-edge", "policies", "managed");
        yield return Path.Combine(home, "snap", "chromium", "common", "chromium", "policies", "managed");
        yield return Path.Combine(home, "snap", "chromium", "current", ".config", "chromium", "policies", "managed");
        yield return Path.Combine(home, ".var", "app", "com.google.Chrome", "config", "google-chrome", "policies",
            "managed");
        yield return Path.Combine(home, ".var", "app", "org.chromium.Chromium", "config", "chromium", "policies",
            "managed");
        yield return Path.Combine(home, ".var", "app", "com.brave.Browser", "config", "BraveSoftware", "Brave-Browser",
            "policies", "managed");
        yield return "/etc/opt/chrome/policies/managed";
        yield return "/etc/chromium/policies/managed";
        yield return "/etc/opt/edge/policies/managed";
    }

    private static void WriteBrowserDesktopOverrides(string hostname, int port)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var destDir = Path.Combine(home, ".local", "share", "applications");
        foreach (var source in DesktopSources)
        {
            if (!File.Exists(source))
                continue;
            var dest = Path.Combine(destDir, Path.GetFileName(source));
            WriteMarkedDesktopExec(source, dest, hostname, port);
        }
    }

    private static IEnumerable<string> UserDesktopOverridePaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var destDir = Path.Combine(home, ".local", "share", "applications");
        foreach (var source in DesktopSources)
            yield return Path.Combine(destDir, Path.GetFileName(source));
    }

    private static void WriteMarkedDesktopExec(string source, string dest, string hostname, int port)
    {
        try
        {
            var text = File.ReadAllText(source);
            var rewritten = new StringBuilder();
            foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine;
                if (line.StartsWith("Exec=", StringComparison.Ordinal))
                    line = InjectChromeProxyArgs(line, hostname, port);
                rewritten.Append(line);
                rewritten.Append('\n');
            }

            if (!rewritten.ToString().Contains(DesktopMarker, StringComparison.Ordinal))
            {
                var firstNl = rewritten.ToString().IndexOf('\n');
                if (firstNl >= 0)
                    rewritten.Insert(firstNl + 1, DesktopMarker + "\n");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllText(dest, rewritten.ToString());
        }
        catch
        {
            // ignore
        }
    }

    private static void DeleteMarkedDesktopOverride(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            var text = File.ReadAllText(path);
            if (text.Contains(DesktopMarker, StringComparison.Ordinal))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static void WriteXfceWebBrowserHelper(string hostname, int port)
    {
        var chrome = ChromeBinaries.FirstOrDefault(File.Exists);
        if (chrome is null)
            return;

        var flags = ChromeProxyFlags(hostname, port);
        var helperPath = UserXfceChromeHelperPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(helperPath)!);
            File.WriteAllText(helperPath,
                "[Desktop Entry]\n" +
                "Version=1.0\n" +
                "Type=X-XFCE-Helper\n" +
                "Name=Web Browser (Titanium Inspector proxy)\n" +
                "Icon=google-chrome\n" +
                "StartupNotify=true\n" +
                DesktopMarker + "\n" +
                "X-XFCE-Category=WebBrowser\n" +
                "X-XFCE-Binaries=google-chrome;google-chrome-stable;chromium;chromium-browser;brave-browser;\n" +
                $"X-XFCE-Commands={chrome} {flags};\n" +
                $"X-XFCE-CommandsWithParameter={chrome} {flags} \"%s\";\n");
        }
        catch
        {
            // ignore
        }

        WriteXfceHelpersRc();
    }

    private static void WriteXfceHelpersRc()
    {
        var path = UserXfceHelpersRcPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            if (!existing.Contains(DesktopMarker, StringComparison.Ordinal) &&
                File.Exists("/etc/xdg/xfce4/helpers.rc") &&
                string.IsNullOrWhiteSpace(existing))
            {
                existing = File.ReadAllText("/etc/xdg/xfce4/helpers.rc");
            }

            var lines = existing.Replace("\r\n", "\n").Split('\n').ToList();
            var found = false;
            for (var i = 0; i < lines.Count; i++)
            {
                if (!lines[i].StartsWith("WebBrowser=", StringComparison.Ordinal))
                    continue;
                lines[i] = "WebBrowser=google-chrome";
                found = true;
                break;
            }

            if (!found)
                lines.Add("WebBrowser=google-chrome");

            if (!lines.Exists(l => l.Contains(DesktopMarker, StringComparison.Ordinal)))
                lines.Insert(0, "# " + DesktopMarker);

            File.WriteAllText(path, string.Join('\n', lines).TrimEnd() + "\n");
        }
        catch
        {
            // ignore
        }
    }

    private static void RestoreXfceHelpersRc()
    {
        var path = UserXfceHelpersRcPath();
        try
        {
            if (!File.Exists(path))
                return;
            var text = File.ReadAllText(path);
            if (!text.Contains(DesktopMarker, StringComparison.Ordinal))
                return;
            File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static string UserXfceChromeHelperPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "xfce4", "helpers", "google-chrome.desktop");
    }

    private static string UserXfceHelpersRcPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "xfce4", "helpers.rc");
    }
}
