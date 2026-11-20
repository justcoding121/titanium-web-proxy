using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     GNOME gsettings is not enough on XFCE, LXDE, i3/sway, WSLg, or when Chrome is started
///     from a .desktop Exec that never reads org.gnome.system.proxy. Write Chromium-family
///     managed policy (<c>ProxyMode=system</c>) plus, only when the desktop has no GNOME/KDE
///     proxy schema, user .desktop / XFCE helper Exec flags
///     (<c>--proxy-server</c>, <c>&lt;-loopback&gt;</c>, <c>--disable-quic</c>).
/// </summary>
internal static class LinuxBrowserLaunchProxy
{
    internal const string PolicyFileName = "titanium-inspector-proxy.json";

    /// <summary>
    ///     Extra local CA for Chromium's certificate verifier. Edge (and Chrome when it
    ///     no longer treats <c>~/.pki/nssdb</c> as a server trust anchor) ignores an NSS
    ///     <c>C,,</c> root and rejects the proxy leaf. <c>CACertificates</c> is the
    ///     supported way to add that root for server authentication.
    /// </summary>
    internal const string CaPolicyFileName = "titanium-inspector-ca.json";
    internal const string DesktopMarker = "X-Titanium-Inspector-Proxy=true";
    internal const string ProxyBypassList = "<-loopback>";
    private const string DesktopExecPrefix = "Exec=";
    private const string SudoBinary = "/usr/bin/sudo";

    /// <summary>
    ///     Deprecated Chromium int enum (2 = fixed servers). New policy files do not emit it:
    ///     an older build that reads this key before <c>ProxyMode</c> would pin the process.
    ///     Validation still accepts files an older Inspector wrote.
    /// </summary>
    internal const int LegacyProxyServerModeFixedServers = 2;

    internal static int ApplyCountForTests { get; private set; }

    internal static void ResetApplyCountForTests() => ApplyCountForTests = 0;

    private const string LocalDirName = ".local";
    private const string ShareDirName = "share";
    private const string ConfigDirName = ".config";
    private const string ChromiumDirName = "chromium";
    private const string ApplicationsDirName = "applications";
    private const string PoliciesManaged = "policies";
    private const string ManagedDirName = "managed";
    private const string MicrosoftEdgeDirName = "microsoft-edge";
    private const string GoogleChromeStableBin = "/usr/bin/google-chrome-stable";
    private const string ChromiumBin = "/usr/bin/chromium";
    private const string MicrosoftEdgeStableBin = "/usr/bin/microsoft-edge-stable";
    private const string OperaBin = "/usr/bin/opera";
    private const string ExecPrefix = "exec ";
    private const string FlatpakConfigDirName = "config";

    private static readonly string[] DesktopFieldCodes = [" %U", " %u", " %f", " %F", " %s", " \"%s\""];

    private static readonly string[] DesktopSources =
    [
        "/usr/share/applications/google-chrome.desktop",
        "/usr/share/applications/google-chrome-stable.desktop",
        "/usr/share/applications/chromium.desktop",
        "/usr/share/applications/chromium-browser.desktop",
        "/usr/share/applications/brave-browser.desktop",
        "/usr/share/applications/microsoft-edge.desktop",
        "/usr/share/applications/microsoft-edge-stable.desktop",
        "/usr/share/applications/opera.desktop",
    ];

    private static readonly string[] ChromeBinaries =
    [
        GoogleChromeStableBin,
        "/usr/bin/google-chrome",
        "/opt/google/chrome/chrome",
        "/usr/bin/chromium-browser",
        ChromiumBin,
        "/usr/bin/brave-browser",
        MicrosoftEdgeStableBin,
        "/usr/bin/microsoft-edge",
        OperaBin,
        "/usr/bin/opera-stable",
    ];

    /// <summary>Returns true when at least one browser-launch hook was written and re-validated.</summary>
    /// <summary>
    ///     True when the WinINET-style bypass list asks for localhost to use the proxy
    ///     (the Inspector "send localhost" option, <c>&lt;-loopback&gt;</c>).
    /// </summary>
    internal static bool WantsProxyLoopback(string? proxyOverride) =>
        !string.IsNullOrWhiteSpace(proxyOverride) &&
        proxyOverride.Split(';').Any(rule =>
            rule.Trim().Equals(ProxyBypassList, StringComparison.OrdinalIgnoreCase));

    /// <param name="relaunch">
    ///     Restart running Chromium-family browsers so Preference edits apply. A caller that
    ///     writes hooks twice passes false for the early write, so the user's windows restart once.
    /// </param>
    /// <param name="pinProcess">
    ///     When false (GNOME or KDE proxy schema is present), write <c>ProxyMode=system</c> only.
    ///     Do not pass <c>--proxy-server</c> on a launcher; that flag outlives File → Exit.
    /// </param>
    internal static bool Apply(
        string hostname, int port, string? proxyOverride = null, bool relaunch = true, bool pinProcess = true)
    {
        ApplyCountForTests++;
        var loopback = WantsProxyLoopback(proxyOverride);
        if (!pinProcess)
            RemoveProcessPins();
        var policyCount = WritePolicies(hostname, port, proxyLoopback: loopback);
        var policyOk = policyCount > 0;
        var desktopCount = pinProcess ? WriteBrowserDesktopOverrides(hostname, port, loopback) : 0;
        var desktopOk = desktopCount > 0;
        var xfceOk = pinProcess && WriteXfceWebBrowserHelper(hostname, port, loopback);
        var profileCount = LinuxChromeProfileProxy.Apply(hostname, port, loopback);
        var profileOk = profileCount > 0;
        // Live switch uses OS proxy (gsettings / KDE) plus Chromium mode=system and Firefox type=5.
        // Do not quit or relaunch browsers. relaunch is ignored.
        _ = relaunch;
        var firefoxOk = LinuxFirefoxProxy.Apply(hostname, port, proxyOverride, relaunch: false);
        if (desktopOk)
            TryUpdateDesktopDatabase();
        return policyOk || desktopOk || xfceOk || profileOk || firefoxOk;
    }

    internal static void Clear() => Clear(relaunchBrowsers: true, startFailOpen: true);

    /// <summary>
    ///     Dead-app / package-removal clear: strip hooks without relaunching browsers or
    ///     starting the fail-open helper (Inspector is already gone).
    /// </summary>
    internal static void ClearInstalledHooks() => Clear(relaunchBrowsers: false, startFailOpen: false);

    private static void Clear(bool relaunchBrowsers, bool startFailOpen)
    {
        LinuxChromeProfileProxy.Clear(startFailOpen);
        if (relaunchBrowsers)
            LinuxFirefoxProxy.Clear();
        else
            LinuxFirefoxProxy.ClearInstalledHooks();

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

        TrySyncMachinePolicies(null);

        // Neutralize launcher scripts before removing .desktop overrides. GNOME Shell
        // caches Exec paths; if the script is gone while the dock still points at it,
        // Chrome fails with "Program '.../launchers/google-chrome.sh' not found".
        RemoveProcessPins();
        _ = relaunchBrowsers;
    }

    /// <summary>
    ///     Drop <c>--proxy-server</c> launchers this writer owns. A dock that still Execs
    ///     the helper gets a passthrough script instead of a missing file.
    /// </summary>
    private static void RemoveProcessPins()
    {
        NeutralizeLauncherScripts();

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
            var home = CoverageOs.Home;
            var apps = Path.Combine(home, LocalDirName, ShareDirName, ApplicationsDirName);
            if (!Directory.Exists(apps))
                return;
            var updateDesktopDb = UnixProcessPath.Resolve(
                "/usr/bin/update-desktop-database", "/usr/local/bin/update-desktop-database");
            if (updateDesktopDb is null)
                return;

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = updateDesktopDb,
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
    internal static int WritePolicies(
        string hostname, int port, IEnumerable<string>? directories = null, bool proxyLoopback = false)
    {
        string json;
        try
        {
            json = BuildPolicyJson(hostname, port, proxyLoopback);
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

        // Official Chrome reads only the machine policy directory. A non-interactive
        // sudo installs it when the user is already an admin; a password prompt is never shown.
        if (directories is null)
            TrySyncMachinePolicies(json);

        return written;
    }

    /// <summary>
    ///     Install or remove managed policy under <c>/etc</c> without prompting.
    ///     Pass null to remove the files this writer owns.
    /// </summary>
    private static void TrySyncMachinePolicies(string? json)
    {
        if (!CoverageOs.IsLinux)
            return;

        SyncMachinePolicies(json, MachinePolicyVendors);
    }

    /// <summary>
    ///     Same non-interactive sudo install as the proxy policy, for a different file name.
    ///     Pass null to remove the file from vendor directories where it exists.
    /// </summary>
    private static void SyncMachinePolicyFile(string fileName, string? json) =>
        SyncMachinePolicyFile(fileName, json, MachinePolicyVendors);

    /// <summary>Runs the machine-policy file sync against caller-supplied vendor roots (tests).</summary>
    internal static void SyncMachinePolicyFileForTests(
        string fileName, string? json, (string Root, string[] Binaries)[] vendors) =>
        SyncMachinePolicyFile(fileName, json, vendors);

    private static void SyncMachinePolicyFile(
        string fileName, string? json, (string Root, string[] Binaries)[] vendors)
    {
        if (!CoverageOs.IsLinux)
            return;

        try
        {
            var targets = new List<string>();
            foreach (var (root, binaries) in vendors)
            {
                var path = Path.Combine(root, PoliciesManaged, ManagedDirName, fileName);
                if (json is null)
                {
                    if (File.Exists(path))
                        targets.Add(path);
                    continue;
                }

                if (!Directory.Exists(root) && !binaries.Any(File.Exists))
                    continue;
                if (!MachinePolicyMatches(path, json))
                    targets.Add(path);
            }

            if (targets.Count == 0)
                return;

            if (json is null)
            {
                TrySudoNonInteractive(["rm", "-f", "--", .. targets], stdin: null);
                return;
            }

            const string script =
                "for f in \"$@\"; do mkdir -p -- \"$(dirname -- \"$f\")\" || exit 1; done; " +
                "tee -- \"$@\" >/dev/null && chmod 644 -- \"$@\"";
            TrySudoNonInteractive(["sh", "-c", script, "sh", .. targets], json);
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>Runs the machine-policy sync against caller-supplied vendor roots (tests).</summary>
    internal static void SyncMachinePoliciesForTests(
        string? json, (string Root, string[] Binaries)[] vendors) =>
        SyncMachinePolicies(json, vendors);

    private static void SyncMachinePolicies(string? json, (string Root, string[] Binaries)[] vendors)
    {
        try
        {
            if (json is null)
            {
                // Only ask for sudo when a file we own is actually there to remove.
                var owned = MachinePolicyFiles(installedOnly: false, vendors).Where(File.Exists).ToList();
                if (owned.Count > 0)
                    TrySudoNonInteractive(["rm", "-f", "--", .. owned], stdin: null);
                return;
            }

            // One sudo call, only for installed browsers, and only when the file would change.
            // The JSON travels on stdin: a predictable file under /tmp could be swapped for a
            // link, and root would then install attacker-chosen policy for every user.
            var targets = MachinePolicyFiles(installedOnly: true, vendors)
                .Where(path => !MachinePolicyMatches(path, json))
                .ToList();
            if (targets.Count == 0)
                return;

            const string script =
                "for f in \"$@\"; do mkdir -p -- \"$(dirname -- \"$f\")\" || exit 1; done; " +
                "tee -- \"$@\" >/dev/null && chmod 644 -- \"$@\"";
            TrySudoNonInteractive(["sh", "-c", script, "sh", .. targets], json);
        }
        catch
        {
            // best-effort
        }
    }

    private static readonly (string Root, string[] Binaries)[] MachinePolicyVendors =
    [
        ("/etc/opt/chrome", [GoogleChromeStableBin, "/usr/bin/google-chrome", "/opt/google/chrome/chrome"]),
        ("/etc/chromium", [ChromiumBin, "/usr/bin/chromium-browser"]),
        ("/etc/opt/edge", [MicrosoftEdgeStableBin, "/usr/bin/microsoft-edge"]),
        ("/etc/brave", ["/usr/bin/brave-browser"]),
        ("/etc/opera", [OperaBin, "/usr/bin/opera-stable"]),
    ];

    private static IEnumerable<string> MachinePolicyFiles(
        bool installedOnly, (string Root, string[] Binaries)[] vendors)
    {
        foreach (var (root, binaries) in vendors)
        {
            if (installedOnly && !Directory.Exists(root) && !binaries.Any(File.Exists))
                continue;
            yield return Path.Combine(root, PoliciesManaged, ManagedDirName, PolicyFileName);
        }
    }

    internal static List<string> MachinePolicyFilesForTests(
        bool installedOnly, (string Root, string[] Binaries)[] vendors) =>
        MachinePolicyFiles(installedOnly, vendors).ToList();

    private static bool MachinePolicyMatches(string path, string json)
    {
        try
        {
            return File.Exists(path) && string.Equals(File.ReadAllText(path), json, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs <c>sudo -n</c>: never prompts, fails fast when sudo needs a password.</summary>
    private static void TrySudoNonInteractive(IReadOnlyList<string> command, string? stdin) =>
        TrySudoNonInteractive(command, stdin, SudoBinary, waitMilliseconds: 8000);

    private static void TrySudoNonInteractive(
        IReadOnlyList<string> command, string? stdin, string sudoExecutable, int waitMilliseconds)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo
            {
                FileName = sudoExecutable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = stdin is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("-n");
            foreach (var part in command)
                info.ArgumentList.Add(part);

            using var process = System.Diagnostics.Process.Start(info);
            if (process is null)
                return;
            if (stdin is not null)
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Close();
            }

            if (!process.WaitForExit(waitMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
        }
        catch
        {
            // sudo missing or not permitted
        }
    }

    /// <summary>Same sudo launch with the executable and wait supplied by the caller (tests).</summary>
    internal static void TrySudoNonInteractiveForTests(
        IReadOnlyList<string> command, string? stdin, string sudoExecutable, int waitMilliseconds) =>
        TrySudoNonInteractive(command, stdin, sudoExecutable, waitMilliseconds);

    /// <summary>
    ///     Public-only base64 DER of <paramref name="certificate"/> under <c>CACertificates</c>.
    /// </summary>
    internal static string BuildCaPolicyJson(X509Certificate2 certificate)
    {
        var der = certificate.Export(X509ContentType.Cert);
        return JsonSerializer.Serialize(new Dictionary<string, string[]>
        {
            ["CACertificates"] = [Convert.ToBase64String(der)],
        });
    }

    /// <summary>
    ///     Writes <see cref="CaPolicyFileName"/> next to the proxy policy so Chromium-family
    ///     browsers trust the proxy root. Kept separate from the proxy file so clearing the
    ///     system proxy does not drop CA trust.
    /// </summary>
    /// <param name="machinePolicy">
    ///     When true, also install the file under <c>/etc</c> for every account.
    ///     User trust stays in the per-user policy directories.
    /// </param>
    internal static void ApplyCaCertificatesPolicy(X509Certificate2 certificate, bool machinePolicy = false)
    {
        if (!CoverageOs.IsLinux)
            return;

        string json;
        try
        {
            json = BuildCaPolicyJson(certificate);
        }
        catch
        {
            return;
        }

        foreach (var dir in PolicyDirectories())
        {
            if (!machinePolicy && dir.StartsWith("/etc/", StringComparison.Ordinal))
                continue;

            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, CaPolicyFileName);
                var temp = path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                // snap roots the user cannot create are skipped
            }
        }

        if (machinePolicy)
            SyncMachinePolicyFile(CaPolicyFileName, json);
    }

    /// <summary>Removes <see cref="CaPolicyFileName"/> from user and, when requested, machine policy directories.</summary>
    /// <param name="machinePolicy">When true, also remove the <c>/etc</c> copy.</param>
    internal static void RemoveCaCertificatesPolicy(bool machinePolicy = false)
    {
        if (!CoverageOs.IsLinux)
            return;

        foreach (var dir in PolicyDirectories())
        {
            if (!machinePolicy && dir.StartsWith("/etc/", StringComparison.Ordinal))
                continue;

            try
            {
                File.Delete(Path.Combine(dir, CaPolicyFileName));
            }
            catch
            {
                // best-effort
            }
        }

        if (machinePolicy)
            SyncMachinePolicyFile(CaPolicyFileName, null);
    }

    /// <summary>
    ///     Builds Chromium managed-policy JSON. <c>ProxyMode=system</c> follows the desktop
    ///     proxy. Do not emit <c>ProxyServerMode</c> or <c>ProxyServer</c>: the legacy int
    ///     2 means fixed servers and pins an older Chromium for the process lifetime.
    /// </summary>
    internal static string BuildPolicyJson(string hostname, int port, bool proxyLoopback = false)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            throw new ArgumentException("hostname is required", nameof(hostname));
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        var payload = new JsonObject
        {
            ["ProxyMode"] = "system",
            ["QuicAllowed"] = false,
        };
        if (proxyLoopback)
            payload["ProxyBypassList"] = ProxyBypassList;

        return payload.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            // Keep <-loopback> readable; \u003C form is also valid JSON but harder to audit.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n";
    }

    /// <summary>
    ///     True when <paramref name="json"/> is valid Chromium managed-policy JSON.
    ///     <c>ProxyMode=system</c> needs no <c>ProxyServer</c>. Older files that pin with
    ///     <c>ProxyMode=fixed_servers</c> or <c>ProxyServerMode=2</c> must still name the endpoint.
    ///     Extra/unknown properties are allowed (forward compatible).
    /// </summary>
    internal static bool TryValidatePolicyJson(string json, string hostname, int port, out string? error) // NOSONAR S3776 -- Chromium policy schema branches share one error string
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
            var systemMode = false;
            var requiresServer = false;
            if (root.TryGetProperty("ProxyMode", out var mode) &&
                mode.ValueKind == JsonValueKind.String)
            {
                var modeText = mode.GetString();
                if (modeText?.Equals("system", StringComparison.OrdinalIgnoreCase) == true)
                    systemMode = true;
                else if (modeText?.Equals("fixed_servers", StringComparison.OrdinalIgnoreCase) == true)
                    requiresServer = true;
            }

            if (root.TryGetProperty("ProxyServerMode", out var legacyMode) &&
                legacyMode.ValueKind == JsonValueKind.Number &&
                legacyMode.TryGetInt32(out var legacyInt) &&
                legacyInt == LegacyProxyServerModeFixedServers)
            {
                requiresServer = true;
            }

            if (!systemMode && !requiresServer)
            {
                error = "missing ProxyMode=system (or fixed_servers / ProxyServerMode=2)";
                return false;
            }

            // ProxyMode=system is enough. A fixed-server enum next to it still has to match.
            if (systemMode && !requiresServer)
                return true;

            if (!root.TryGetProperty("ProxyServer", out var server) ||
                server.ValueKind != JsonValueKind.String)
            {
                error = "missing ProxyServer string";
                return false;
            }

            var expected = $"http://{hostname}:{port}"; // NOSONAR S5332 -- Chromium ProxyServer is an http CONNECT proxy URL.
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

    internal static string InjectChromeProxyArgs(string execLine, string hostname, int port, bool proxyLoopback = false)
    {
        var flags = ChromeProxyShellFlags(hostname, port, proxyLoopback);
        if (execLine.Contains("--proxy-server=", StringComparison.Ordinal))
            return execLine;

        var prefix = execLine.StartsWith(DesktopExecPrefix, StringComparison.Ordinal) ? DesktopExecPrefix : "";
        var rest = prefix.Length > 0 ? execLine[prefix.Length..] : execLine;

        // Insert before desktop field codes so every Chromium-family Exec line picks up flags.
        var insertAt = rest.Length;
            foreach (var code in DesktopFieldCodes)
        {
            var idx = rest.IndexOf(code, StringComparison.Ordinal);
            if (idx >= 0 && idx < insertAt)
                insertAt = idx;
        }

        return prefix + rest[..insertAt] + " " + flags + rest[insertAt..];
    }

    /// <summary>
    ///     Plain argv text (a consumer that word-splits and never runs a shell parse).
    ///     <paramref name="proxyLoopback"/> keeps localhost on the proxy, as the Inspector option asks.
    /// </summary>
    internal static string ChromeProxyFlags(string hostname, int port, bool proxyLoopback = false) =>
        $"--proxy-server=http://{hostname}:{port} --disable-quic" + // NOSONAR S5332 -- Chromium --proxy-server flag requires an http CONNECT proxy URL.
        (proxyLoopback ? $" --proxy-bypass-list={ProxyBypassList}" : string.Empty);

    /// <summary>
    ///     Same flags for a desktop Exec line or shell script, where an unquoted
    ///     <c>&lt;</c> is a reserved character or a redirection.
    /// </summary>
    internal static string ChromeProxyShellFlags(string hostname, int port, bool proxyLoopback = false) =>
        $"--proxy-server=http://{hostname}:{port} --disable-quic" + // NOSONAR S5332 -- Chromium --proxy-server flag requires an http CONNECT proxy URL.
        (proxyLoopback ? $" \"--proxy-bypass-list={ProxyBypassList}\"" : string.Empty);

    /// <summary>
    ///     Rewrites a vendor desktop file so launches use <paramref name="scriptPath"/>
    ///     and <c>DBusActivatable=false</c>. D-Bus activation ignores Exec, which is how
    ///     Chromium was starting with no proxy flags.
    /// </summary>
    internal static (string Desktop, string Script)? BuildDesktopOverride(
        string sourceText, string scriptPath, string hostname, int port, bool proxyLoopback = false)
    {
        string? binary = null;
        var body = new StringBuilder();
        var sawDbus = false;
        foreach (var rawLine in sourceText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = RewriteDesktopLine(
                rawLine, scriptPath, hostname, port, proxyLoopback, ref binary, ref sawDbus);
            body.Append(line);
            body.Append('\n');
        }

        if (string.IsNullOrEmpty(binary))
            return null;

        if (!sawDbus)
            InsertAfterDesktopHeader(body, "DBusActivatable=false\n");

        if (!body.ToString().Contains(DesktopMarker, StringComparison.Ordinal))
            InsertAfterDesktopHeader(body, DesktopMarker + "\n");

        return (body.ToString(), BuildDesktopLaunchScript(binary, hostname, port, proxyLoopback));
    }

    private static string RewriteDesktopLine(
        string rawLine,
        string scriptPath,
        string hostname,
        int port,
        bool proxyLoopback,
        ref string? binary,
        ref bool sawDbus)
    {
        var line = rawLine;
        if (line.StartsWith("DBusActivatable=", StringComparison.OrdinalIgnoreCase))
        {
            sawDbus = true;
            return "DBusActivatable=false";
        }

        if (!line.StartsWith(DesktopExecPrefix, StringComparison.Ordinal))
            return line;

        var rest = line[DesktopExecPrefix.Length..];
        var fieldAt = FieldCodeIndex(rest);
        var command = (fieldAt >= 0 ? rest[..fieldAt] : rest).Trim();
        var field = fieldAt >= 0 ? rest[fieldAt..] : string.Empty;
        var token = FirstToken(command);
        if (string.IsNullOrEmpty(token))
            return line;

        binary ??= token;
        if (!string.Equals(token, binary, StringComparison.Ordinal))
            return InjectChromeProxyArgs(line, hostname, port, proxyLoopback);

        var extra = command.Length > token.Length ? command[token.Length..] : string.Empty;
        return DesktopExecPrefix + QuoteDesktopPath(scriptPath) + extra + field;
    }

    private static void InsertAfterDesktopHeader(StringBuilder body, string line)
    {
        var text = body.ToString();
        var header = text.IndexOf('\n');
        if (header >= 0)
            body.Insert(header + 1, line);
    }

    private static string BuildDesktopLaunchScript(
        string binary, string hostname, int port, bool proxyLoopback)
    {
        // First launch on a new profile sits on the welcome flow and never requests the
        // page, so the proxy sees no session. Skip that flow, and pass the proxy both as
        // an environment variable and as a switch.
        var proxyUrl = $"http://{hostname}:{port}"; // NOSONAR S5332 -- Chromium proxy URL is http.
        // TITANIUM_SMOKE_BROWSER_FLAGS is empty for a normal launch. The install smoke sets it
        // so the browser opens the page instead of spending the wait on component downloads.
        return "#!/bin/sh\n" +
               "# " + DesktopMarker + "\n" +
               "export http_proxy=" + QuoteShellArg(proxyUrl) + "\n" +
               "export https_proxy=\"$http_proxy\"\n" +
               "export HTTP_PROXY=\"$http_proxy\"\n" +
               "export HTTPS_PROXY=\"$http_proxy\"\n" +
               "unset no_proxy NO_PROXY\n" +
               "extra=\n" +
               "if [ -n \"${TITANIUM_SMOKE_BROWSER_FLAGS:-}\" ]; then\n" +
               "  extra=\" $TITANIUM_SMOKE_BROWSER_FLAGS\"\n" +
               "fi\n" +
               ExecPrefix + QuoteShellArg(binary) + " " + ChromeProxyShellFlags(hostname, port, proxyLoopback) +
               " --no-first-run --no-default-browser-check$extra \"$@\"\n";
    }

    private static int FieldCodeIndex(string rest)
    {
        var insertAt = -1;
        foreach (var code in DesktopFieldCodes)
        {
            var idx = rest.IndexOf(code, StringComparison.Ordinal);
            if (idx >= 0 && (insertAt < 0 || idx < insertAt))
                insertAt = idx;
        }

        return insertAt;
    }

    private static string FirstToken(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0)
            return string.Empty;
        if (trimmed[0] == '"')
        {
            var end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed[1..end] : trimmed.Trim('"');
        }

        var space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed[..space];
    }

    private static string QuoteDesktopPath(string path) =>
        path.IndexOfAny([' ', '\t', '"', '\'', '<', '>']) >= 0 ? QuoteShellArg(path) : path;

    internal static IEnumerable<string> PolicyDirectories()
    {
        var home = CoverageOs.Home;
        yield return Path.Combine(home, ConfigDirName, "google-chrome", PoliciesManaged, ManagedDirName);
        yield return Path.Combine(home, ConfigDirName, "google-chrome-beta", PoliciesManaged, ManagedDirName);
        yield return Path.Combine(home, ConfigDirName, "google-chrome-unstable", PoliciesManaged, ManagedDirName);
        yield return Path.Combine(home, ConfigDirName, ChromiumDirName, PoliciesManaged, ManagedDirName);
        yield return Path.Combine(home, ConfigDirName, "BraveSoftware", "Brave-Browser", PoliciesManaged, ManagedDirName);
        yield return Path.Combine(home, ConfigDirName, MicrosoftEdgeDirName, PoliciesManaged, ManagedDirName);
        yield return Path.Combine(home, ConfigDirName, "opera", PoliciesManaged, ManagedDirName);
        yield return Path.Combine(home, "snap", ChromiumDirName, "common", ChromiumDirName, PoliciesManaged, ManagedDirName);
        yield return Path.Combine(home, "snap", ChromiumDirName, "current", ConfigDirName, ChromiumDirName, PoliciesManaged, ManagedDirName);
        yield return Path.Combine(home, "snap", MicrosoftEdgeDirName, "common", MicrosoftEdgeDirName, PoliciesManaged, ManagedDirName);
        yield return Path.Combine(home, ".var", "app", "com.google.Chrome", FlatpakConfigDirName, "google-chrome", PoliciesManaged,
            ManagedDirName);
        yield return Path.Combine(home, ".var", "app", "org.chromium.Chromium", FlatpakConfigDirName, ChromiumDirName, PoliciesManaged,
            ManagedDirName);
        yield return Path.Combine(home, ".var", "app", "com.brave.Browser", FlatpakConfigDirName, "BraveSoftware", "Brave-Browser",
            PoliciesManaged, ManagedDirName);
        yield return Path.Combine(home, ".var", "app", "com.microsoft.Edge", FlatpakConfigDirName, MicrosoftEdgeDirName, PoliciesManaged,
            ManagedDirName);
        yield return "/etc/opt/chrome/policies/managed";
        yield return "/etc/chromium/policies/managed";
        yield return "/etc/opt/edge/policies/managed";
        yield return "/etc/opera/policies/managed";
    }

    private static int WriteBrowserDesktopOverrides(string hostname, int port, bool proxyLoopback) =>
        WriteBrowserDesktopOverrides(hostname, port, proxyLoopback, DesktopSources);

    private static int WriteBrowserDesktopOverrides(
        string hostname, int port, bool proxyLoopback, IEnumerable<string> sources)
    {
        var home = CoverageOs.Home;
        var destDir = Path.Combine(home, LocalDirName, ShareDirName, ApplicationsDirName);
        var written = 0;
        foreach (var source in sources)
        {
            if (!File.Exists(source))
                continue;
            var dest = Path.Combine(destDir, Path.GetFileName(source));
            if (WriteMarkedDesktopExec(source, dest, hostname, port, proxyLoopback))
                written++;
        }

        return written;
    }

    internal static int WriteBrowserDesktopOverridesForTests(
        string hostname, int port, bool proxyLoopback, IEnumerable<string> sources) =>
        WriteBrowserDesktopOverrides(hostname, port, proxyLoopback, sources);

    private static IEnumerable<string> UserDesktopOverridePaths()
    {
        var home = CoverageOs.Home;
        var destDir = Path.Combine(home, LocalDirName, ShareDirName, ApplicationsDirName);
        foreach (var source in DesktopSources)
            yield return Path.Combine(destDir, Path.GetFileName(source));
    }

    private static bool WriteMarkedDesktopExec(string source, string dest, string hostname, int port, bool proxyLoopback)
    {
        try
        {
            var text = File.ReadAllText(source);
            var scriptPath = LauncherScriptPath(dest);
            var built = BuildDesktopOverride(text, scriptPath, hostname, port, proxyLoopback);
            if (built is null)
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
            File.WriteAllText(scriptPath, built.Value.Script.Replace("\r\n", "\n", StringComparison.Ordinal));
            TryMakeExecutable(scriptPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllText(dest, built.Value.Desktop);

            var verify = File.ReadAllText(dest);
            var script = File.ReadAllText(scriptPath);
            return verify.Contains(DesktopMarker, StringComparison.Ordinal) &&
                   verify.Contains("DBusActivatable=false", StringComparison.Ordinal) &&
                   script.Contains("--proxy-server=", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string LauncherScriptPath(string desktopDest)
    {
        var name = Path.GetFileNameWithoutExtension(desktopDest);
        return Path.Combine(LauncherDirectory(), name + ".sh");
    }

    private static string LauncherDirectory()
    {
        var home = CoverageOs.Home;
        return Path.Combine(home, LocalDirName, ShareDirName, "titanium-inspector", "launchers");
    }

    /// <summary>
    ///     Rewrites marked launcher scripts to a proxy-free passthrough so a stale GNOME
    ///     Exec cache still launches the browser after Clear removes the .desktop override.
    /// </summary>
    private static void NeutralizeLauncherScripts()
    {
        try
        {
            var dir = LauncherDirectory();
            if (!Directory.Exists(dir))
                return;
            foreach (var file in Directory.EnumerateFiles(dir, "*.sh"))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    if (!text.Contains(DesktopMarker, StringComparison.Ordinal))
                        continue;
                    if (!TryExtractLaunchBinary(text, out var binary) || string.IsNullOrEmpty(binary))
                        binary = GuessBinaryFromLauncherName(file);
                    if (string.IsNullOrEmpty(binary))
                        continue;
                    File.WriteAllText(file, BuildPassthroughLaunchScript(binary).Replace("\r\n", "\n", StringComparison.Ordinal));
                    TryMakeExecutable(file);
                }
                catch
                {
                    // best-effort per file
                }
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static string BuildPassthroughLaunchScript(string binary) =>
        "#!/bin/sh\n" +
        "# " + DesktopMarker + "\n" +
        "# neutralized: dock may still Exec this path after Clear removes the .desktop override\n" +
        ExecPrefix + QuoteShellArg(binary) + " \"$@\"\n";

    private static bool TryExtractLaunchBinary(string scriptText, out string binary)
    {
        binary = string.Empty;
        foreach (var rawLine in scriptText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(ExecPrefix, StringComparison.Ordinal))
                continue;
            var rest = line[ExecPrefix.Length..].TrimStart();
            if (rest.Length == 0)
                continue;
            if (rest[0] == '"')
            {
                var end = rest.IndexOf('"', 1);
                if (end > 1)
                {
                    binary = rest[1..end];
                    return binary.Length > 0;
                }
            }

            var space = rest.IndexOfAny([' ', '\t']);
            binary = space < 0 ? rest : rest[..space];
            return binary.Length > 0;
        }

        return false;
    }

    private static string? GuessBinaryFromLauncherName(string scriptPath)
    {
        var name = Path.GetFileNameWithoutExtension(scriptPath);
        return name switch
        {
            "google-chrome" or "google-chrome-stable" => File.Exists("/usr/bin/google-chrome-stable")
                ? "/usr/bin/google-chrome-stable"
                : "/usr/bin/google-chrome",
            "chromium" or "chromium-browser" => File.Exists("/usr/bin/chromium")
                ? "/usr/bin/chromium"
                : "/usr/bin/chromium-browser",
            "brave-browser" => "/usr/bin/brave-browser",
            "microsoft-edge" or "microsoft-edge-stable" => File.Exists("/usr/bin/microsoft-edge-stable")
                ? "/usr/bin/microsoft-edge-stable"
                : "/usr/bin/microsoft-edge",
            "opera" => File.Exists("/usr/bin/opera") ? "/usr/bin/opera" : "/usr/bin/opera-stable",
            _ => null,
        };
    }

    private static void TryMakeExecutable(string scriptPath) =>
        TryMakeExecutable(scriptPath, CoverageOs.IsLinux || CoverageOs.IsMac);

    private static void TryMakeExecutable(string scriptPath, bool applyUnixMode)
    {
        try
        {
            if (applyUnixMode)
            {
                // chmod u+x: add the owner execute bit and leave the mode WriteAllText created.
                // Tests pass applyUnixMode on Windows; the call throws and the catch stays best-effort.
#pragma warning disable CA1416
                var mode = File.GetUnixFileMode(scriptPath);
                File.SetUnixFileMode(scriptPath, mode | UnixFileMode.UserExecute);
#pragma warning restore CA1416
            }
        }
        catch
        {
            // best-effort; File.SetUnixFileMode is the same chmod u+x without spawning a process
        }
    }

    internal static void TryMakeExecutableForTests(string scriptPath) =>
        TryMakeExecutable(scriptPath, applyUnixMode: true);

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

    private static bool WriteXfceWebBrowserHelper(string hostname, int port, bool proxyLoopback) =>
        WriteXfceWebBrowserHelper(hostname, port, proxyLoopback, ChromeBinaries);

    private static bool WriteXfceWebBrowserHelper(
        string hostname, int port, bool proxyLoopback, IEnumerable<string> chromeBinaries)
    {
        var chrome = chromeBinaries.FirstOrDefault(File.Exists);
        if (chrome is null)
            return false;

        var flags = ChromeProxyShellFlags(hostname, port, proxyLoopback);
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

    internal static bool WriteXfceWebBrowserHelperForTests(
        string hostname, int port, bool proxyLoopback, IEnumerable<string> chromeBinaries) =>
        WriteXfceWebBrowserHelper(hostname, port, proxyLoopback, chromeBinaries);

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
        var home = CoverageOs.Home;
        return Path.Combine(home, LocalDirName, ShareDirName, "xfce4", "helpers", "google-chrome.desktop");
    }

    private static string UserXfceHelpersRcPath()
    {
        var home = CoverageOs.Home;
        return Path.Combine(home, ConfigDirName, "xfce4", "helpers.rc");
    }
}
