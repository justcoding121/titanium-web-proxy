using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    /// <summary>
    ///     Chromium managed-policy floor we target: modern <c>ProxyMode</c> string enum plus
    ///     legacy <c>ProxyServerMode</c> int (2 = fixed servers) for older builds that still
    ///     read the deprecated key. Unknown keys are ignored by Chromium.
    /// </summary>
    internal const int LegacyProxyServerModeFixedServers = 2;

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

    /// <summary>Returns true when at least one browser-launch hook was written and re-validated.</summary>
    internal static bool Apply(string hostname, int port)
    {
        var policyCount = WritePolicies(hostname, port);
        var policyOk = policyCount > 0;
        var desktopCount = WriteBrowserDesktopOverrides(hostname, port);
        var desktopOk = desktopCount > 0;
        var xfceOk = WriteXfceWebBrowserHelper(hostname, port);
        var profileCount = LinuxChromeProfileProxy.Apply(hostname, port);
        var profileOk = profileCount > 0;
        if (desktopOk)
            TryUpdateDesktopDatabase();
        return policyOk || desktopOk || xfceOk || profileOk;
    }

    internal static void Clear()
    {
        LinuxChromeProfileProxy.Clear();

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
        TryUpdateDesktopDatabase();
    }

    private static void TryUpdateDesktopDatabase()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var apps = Path.Combine(home, ".local", "share", "applications");
            if (!Directory.Exists(apps))
                return;
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "update-desktop-database",
                Arguments = QuoteShellArg(apps),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process?.WaitForExit(3000);
        }
        catch
        {
            // optional helper; dock may still pick up overrides without a cache refresh
        }
    }

    private static string QuoteShellArg(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>Writes managed policy JSON; returns count of directories that validated after write.</summary>
    internal static int WritePolicies(string hostname, int port, IEnumerable<string>? directories = null)
    {
        string json;
        try
        {
            json = BuildPolicyJson(hostname, port);
        }
        catch
        {
            return 0;
        }

        if (!TryValidatePolicyJson(json, hostname, port, out _))
            return 0;

        var written = 0;
        foreach (var dir in directories ?? PolicyDirectories())
        {
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, PolicyFileName);
                // Atomic-ish: write temp then replace so a crash mid-write cannot leave truncated JSON.
                var temp = path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
                var onDisk = File.ReadAllText(path);
                if (TryValidatePolicyJson(onDisk, hostname, port, out _))
                    written++;
                else
                {
                    try { File.Delete(path); } catch { /* ignore */ }
                }
            }
            catch
            {
                // /etc/opt/chrome and snap roots may not be writable
            }
        }

        return written;
    }

    /// <summary>
    ///     Builds Chromium managed-policy JSON. Prefer modern <see cref="ProxyMode"/> string keys;
    ///     also emit deprecated <c>ProxyServerMode</c>=2 for older Chromium that still reads it.
    /// </summary>
    internal static string BuildPolicyJson(string hostname, int port)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            throw new ArgumentException("hostname is required", nameof(hostname));
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        var server = $"http://{hostname}:{port}";
        var payload = new JsonObject
        {
            ["ProxyMode"] = "fixed_servers",
            // Deprecated int enum (2 = Use a fixed proxy server). Harmless on modern Chrome.
            ["ProxyServerMode"] = LegacyProxyServerModeFixedServers,
            ["ProxyServer"] = server,
            ["ProxyBypassList"] = ProxyBypassList,
            ["QuicAllowed"] = false,
        };

        return payload.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            // Keep <-loopback> readable; \u003C form is also valid JSON but harder to audit.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n";
    }

    /// <summary>
    ///     True when <paramref name="json"/> is valid Chromium managed-policy JSON containing
    ///     either modern <c>ProxyMode=fixed_servers</c> or legacy <c>ProxyServerMode=2</c>,
    ///     plus matching host/port. Extra/unknown properties are allowed (forward compatible).
    /// </summary>
    internal static bool TryValidatePolicyJson(string json, string hostname, int port, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "policy root must be a JSON object";
                return false;
            }

            var root = doc.RootElement;
            var modeOk = false;
            if (root.TryGetProperty("ProxyMode", out var mode) &&
                mode.ValueKind == JsonValueKind.String &&
                mode.GetString()?.Equals("fixed_servers", StringComparison.OrdinalIgnoreCase) == true)
            {
                modeOk = true;
            }

            if (root.TryGetProperty("ProxyServerMode", out var legacyMode) &&
                legacyMode.ValueKind == JsonValueKind.Number &&
                legacyMode.TryGetInt32(out var legacyInt) &&
                legacyInt == LegacyProxyServerModeFixedServers)
            {
                modeOk = true;
            }

            if (!modeOk)
            {
                error = "missing ProxyMode=fixed_servers (or ProxyServerMode=2)";
                return false;
            }

            if (!root.TryGetProperty("ProxyServer", out var server) ||
                server.ValueKind != JsonValueKind.String)
            {
                error = "missing ProxyServer string";
                return false;
            }

            var expected = $"http://{hostname}:{port}";
            var actual = server.GetString() ?? string.Empty;
            if (!actual.Contains($"{hostname}:{port}", StringComparison.Ordinal) &&
                !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                error = "ProxyServer does not point at the Inspector endpoint";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
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

    private static int WriteBrowserDesktopOverrides(string hostname, int port)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var destDir = Path.Combine(home, ".local", "share", "applications");
        var written = 0;
        foreach (var source in DesktopSources)
        {
            if (!File.Exists(source))
                continue;
            var dest = Path.Combine(destDir, Path.GetFileName(source));
            if (WriteMarkedDesktopExec(source, dest, hostname, port))
                written++;
        }

        return written;
    }

    private static IEnumerable<string> UserDesktopOverridePaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var destDir = Path.Combine(home, ".local", "share", "applications");
        foreach (var source in DesktopSources)
            yield return Path.Combine(destDir, Path.GetFileName(source));
    }

    private static bool WriteMarkedDesktopExec(string source, string dest, string hostname, int port)
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

            var verify = File.ReadAllText(dest);
            return verify.Contains(DesktopMarker, StringComparison.Ordinal) &&
                   verify.Contains("--proxy-server=", StringComparison.Ordinal);
        }
        catch
        {
            return false;
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

    private static bool WriteXfceWebBrowserHelper(string hostname, int port)
    {
        var chrome = ChromeBinaries.FirstOrDefault(File.Exists);
        if (chrome is null)
            return false;

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

            var helperOk = File.Exists(helperPath) &&
                           File.ReadAllText(helperPath).Contains("--proxy-server=", StringComparison.Ordinal);
            var rcOk = WriteXfceHelpersRc();
            return helperOk && rcOk;
        }
        catch
        {
            return false;
        }
    }

    private static bool WriteXfceHelpersRc()
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
            return File.ReadAllText(path).Contains(DesktopMarker, StringComparison.Ordinal);
        }
        catch
        {
            return false;
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
