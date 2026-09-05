using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Network;

/// <summary>
///     Firefox-specific CA trust: Windows ImportEnterpriseRoots policy / policies.json,
///     profile <c>user.js</c> fallback, plus optional NSS import into the default profile
///     (<c>cert9.db</c>).
/// </summary>
public static class FirefoxCertificateTrust
{
    private const string WindowsPolicySubKey = @"Software\Policies\Mozilla\Firefox\Certificates";
    private const string ImportEnterpriseRootsValue = "ImportEnterpriseRoots";
    private const string EnterpriseRootsPrefName = "security.enterprise_roots.enabled";
    private static readonly Regex EnterpriseRootsUserPrefLine = new(
        @"^\s*user_pref\s*\(\s*""" + Regex.Escape(EnterpriseRootsPrefName) + @"""\s*,\s*(true|false)\s*\)\s*;\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    ///     Mozilla enterprise policies.json root shape. Extra keys are preserved on merge so we
    ///     do not wipe IT-managed policies when only ImportEnterpriseRoots is needed.
    /// </summary>
    internal const int FirefoxPoliciesSchemaCompat = 1;

    /// <summary>True when a Firefox profiles.ini (or common profile root) is present.</summary>
    public static bool IsFirefoxProfilePresent() => TryGetProfilesIniPath(out _);

    /// <summary>
    ///     Windows: enable OS-root trust for Firefox via HKCU policy when allowed,
    ///     otherwise set <c>security.enterprise_roots.enabled</c> in the default profile <c>user.js</c>.
    ///     Also best-effort writes/merges Mozilla <c>policies.json</c> on all OSes.
    /// </summary>
    public static CertificateOsTrustResult TryEnableWindowsEnterpriseRoots()
    {
        // Cross-platform policies.json is best-effort; HKCU / user.js remain authoritative on Windows.
        var policiesWritten = TryWriteOrMergeFirefoxPoliciesJson(importEnterpriseRoots: true);

        if (!OperatingSystem.IsWindows())
        {
            if (policiesWritten)
            {
                return CertificateOsTrustResult.Ok(
                    "Firefox policies.json updated (ImportEnterpriseRoots); restart Firefox to apply");
            }

            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.Unsupported,
                "ImportEnterpriseRoots registry policy is Windows-only; " +
                "could not write a user/system Firefox policies.json either");
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(WindowsPolicySubKey, true);
            if (key is not null)
            {
                key.SetValue(ImportEnterpriseRootsValue, 1, Microsoft.Win32.RegistryValueKind.DWord);
                // Re-read so a locked/redirected hive cannot look like success.
                var verify = key.GetValue(ImportEnterpriseRootsValue);
                var ok = verify switch
                {
                    int i => i == 1,
                    long l => l == 1,
                    null => false,
                    _ => Convert.ToInt32(verify) == 1,
                };
                if (ok)
                {
                    return CertificateOsTrustResult.Ok(
                        "Firefox will trust the Windows root CA after you restart Firefox");
                }
            }
        }
        catch
        {
            // Fall through to profile user.js (Policies key may be locked by enterprise).
        }

        if (!TryResolveDefaultProfileDirectory(out var profileDir, out var resolveError))
        {
            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.Failed,
                "Could not set Firefox ImportEnterpriseRoots policy and " +
                (resolveError ?? "no Firefox profile was found"));
        }

        try
        {
            EnsureEnterpriseRootsUserPref(profileDir);
            if (!VerifyEnterpriseRootsUserPref(profileDir))
            {
                return CertificateOsTrustResult.Fail(
                    CertificateOsTrustKind.Failed,
                    "Wrote Firefox user.js but security.enterprise_roots.enabled did not validate");
            }

            return CertificateOsTrustResult.Ok(
                "Firefox will trust the Windows root CA after you restart Firefox (profile preference)");
        }
        catch (Exception ex)
        {
            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.Failed,
                "Failed to enable Firefox OS-root trust: " + ex.Message);
        }
    }

    /// <summary>Clears the HKCU ImportEnterpriseRoots value and profile user.js pref we may have set.</summary>
    public static bool TryClearWindowsEnterpriseRoots()
    {
        var cleared = TryClearFirefoxPoliciesJsonImportEnterpriseRoots();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(WindowsPolicySubKey, writable: true);
                if (key is not null)
                {
                    key.DeleteValue(ImportEnterpriseRootsValue, throwOnMissingValue: false);
                    cleared = true;
                }
            }
            catch
            {
                // ignore
            }
        }

        if (TryResolveDefaultProfileDirectory(out var profileDir, out _))
        {
            try
            {
                cleared = ClearEnterpriseRootsUserPref(profileDir) || cleared;
            }
            catch
            {
                // ignore
            }
        }

        return cleared;
    }

    /// <summary>
    ///     Builds Mozilla enterprise <c>policies.json</c> content enabling ImportEnterpriseRoots,
    ///     merging into <paramref name="existingJson"/> when present so IT policies are preserved.
    /// </summary>
    internal static string BuildOrMergeFirefoxPoliciesJson(string? existingJson, bool importEnterpriseRoots)
    {
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(existingJson)
                ? new JsonObject()
                : JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            // Corrupt / non-object existing file: start fresh rather than abort CA trust.
            root = new JsonObject();
        }

        if (root["policies"] is not JsonObject policies)
        {
            policies = new JsonObject();
            root["policies"] = policies;
        }

        if (policies["Certificates"] is not JsonObject certificates)
        {
            certificates = new JsonObject();
            policies["Certificates"] = certificates;
        }

        if (importEnterpriseRoots)
            certificates["ImportEnterpriseRoots"] = true;
        else
            certificates.Remove("ImportEnterpriseRoots");

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>
    ///     True when JSON is a Mozilla policies document with Certificates.ImportEnterpriseRoots=true.
    ///     Extra top-level/policy keys are allowed (forward compatible).
    /// </summary>
    internal static bool TryValidateFirefoxPoliciesJson(string json, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "policies.json root must be an object";
                return false;
            }

            if (!doc.RootElement.TryGetProperty("policies", out var policies) ||
                policies.ValueKind != JsonValueKind.Object)
            {
                error = "missing policies object";
                return false;
            }

            if (!policies.TryGetProperty("Certificates", out var certs) ||
                certs.ValueKind != JsonValueKind.Object)
            {
                error = "missing policies.Certificates";
                return false;
            }

            if (!certs.TryGetProperty("ImportEnterpriseRoots", out var flag) ||
                flag.ValueKind != JsonValueKind.True)
            {
                error = "ImportEnterpriseRoots is not true";
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

    /// <summary>Best-effort write/merge of Firefox policies.json into known OS locations.</summary>
    internal static bool TryWriteOrMergeFirefoxPoliciesJson(bool importEnterpriseRoots)
    {
        var any = false;
        foreach (var path in GetFirefoxPoliciesJsonPaths())
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var existing = File.Exists(path) ? File.ReadAllText(path) : null;
                var json = BuildOrMergeFirefoxPoliciesJson(existing, importEnterpriseRoots);
                if (importEnterpriseRoots && !TryValidateFirefoxPoliciesJson(json, out _))
                    continue;

                var temp = path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);

                if (!importEnterpriseRoots)
                {
                    any = true;
                    continue;
                }

                var onDisk = File.ReadAllText(path);
                if (TryValidateFirefoxPoliciesJson(onDisk, out _))
                    any = true;
            }
            catch
            {
                // /etc and Program Files often need elevation — ignore.
            }
        }

        return any;
    }

    private static bool TryClearFirefoxPoliciesJsonImportEnterpriseRoots()
    {
        var cleared = false;
        foreach (var path in GetFirefoxPoliciesJsonPaths())
        {
            try
            {
                if (!File.Exists(path))
                    continue;
                var existing = File.ReadAllText(path);
                if (!existing.Contains("ImportEnterpriseRoots", StringComparison.Ordinal))
                    continue;
                var json = BuildOrMergeFirefoxPoliciesJson(existing, importEnterpriseRoots: false);
                File.WriteAllText(path, json);
                cleared = true;
            }
            catch
            {
                // ignore
            }
        }

        return cleared;
    }

    /// <summary>Known Mozilla policies.json locations (system + user-writable fallbacks).</summary>
    internal static IEnumerable<string> GetFirefoxPoliciesJsonPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            yield return Path.Combine(programFiles, "Mozilla Firefox", "distribution", "policies.json");
            if (!string.IsNullOrEmpty(programFilesX86))
                yield return Path.Combine(programFilesX86, "Mozilla Firefox", "distribution", "policies.json");
            // User-level distribution next to the profile root (portable / some enterprise layouts).
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            yield return Path.Combine(appData, "Mozilla", "Firefox", "distribution", "policies.json");
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Firefox.app/Contents/Resources/distribution/policies.json";
            yield return Path.Combine(home, "Library", "Application Support", "Firefox", "distribution",
                "policies.json");
            yield break;
        }

        if (OperatingSystem.IsLinux())
        {
            yield return "/etc/firefox/policies/policies.json";
            yield return "/usr/lib/firefox/distribution/policies.json";
            yield return "/usr/lib64/firefox/distribution/policies.json";
            yield return Path.Combine(home, ".mozilla", "firefox", "distribution", "policies.json");
            yield return Path.Combine(home, "snap", "firefox", "common", ".mozilla", "firefox", "distribution",
                "policies.json");
            yield return Path.Combine(home, ".var", "app", "org.mozilla.firefox", ".mozilla", "firefox", "distribution",
                "policies.json");
        }
    }

    internal static void EnsureEnterpriseRootsUserPref(string profileDirectory)
    {
        const string prefLine = "user_pref(\"" + EnterpriseRootsPrefName + "\", true);";
        var userJs = Path.Combine(profileDirectory, "user.js");
        if (File.Exists(userJs))
        {
            var text = File.ReadAllText(userJs);
            var lines = text.Split(['\r', '\n'], StringSplitOptions.None);
            var found = false;
            for (var i = 0; i < lines.Length; i++)
            {
                // Only rewrite real user_pref lines — never comments or lockPref.
                if (!EnterpriseRootsUserPrefLine.IsMatch(lines[i]))
                    continue;
                lines[i] = prefLine;
                found = true;
            }

            if (found)
            {
                File.WriteAllText(userJs, string.Join(Environment.NewLine, lines));
                return;
            }

            File.AppendAllText(userJs, Environment.NewLine + prefLine + Environment.NewLine);
            return;
        }

        File.WriteAllText(userJs, prefLine + Environment.NewLine);
    }

    internal static bool VerifyEnterpriseRootsUserPref(string profileDirectory)
    {
        var userJs = Path.Combine(profileDirectory, "user.js");
        if (!File.Exists(userJs)) return false;
        foreach (var line in File.ReadLines(userJs))
        {
            var m = EnterpriseRootsUserPrefLine.Match(line);
            if (m.Success && m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool ClearEnterpriseRootsUserPref(string profileDirectory)
    {
        var userJs = Path.Combine(profileDirectory, "user.js");
        if (!File.Exists(userJs)) return false;
        var lines = File.ReadAllLines(userJs)
            .Where(l => !EnterpriseRootsUserPrefLine.IsMatch(l))
            .ToArray();
        File.WriteAllLines(userJs, lines);
        return true;
    }

    /// <summary>
    ///     Imports the CA into the default Firefox profile NSS DB via <c>certutil</c>.
    ///     Caller should ensure Firefox is not locking the DB.
    /// </summary>
    public static CertificateOsTrustResult TrustDefaultProfile(
        X509Certificate2 certificate,
        string friendlyName) =>
        TrustDefaultProfile(certificate, friendlyName, new ProcessRunner());

    /// <summary>Best-effort removal of the CA nickname from the default Firefox profile.</summary>
    public static bool UntrustDefaultProfile(string friendlyName) =>
        UntrustDefaultProfile(friendlyName, new ProcessRunner());

    internal static CertificateOsTrustResult TrustDefaultProfile(
        X509Certificate2 certificate,
        string friendlyName,
        IProcessRunner runner)
    {
        if (!TryResolveDefaultProfileDirectory(out var profileDir, out var resolveError))
        {
            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.Failed,
                resolveError ?? "Firefox profile not found");
        }

        var certutil = UnixCertificateTrust.FindCertutil(runner);
        if (certutil is null)
            return UnixCertificateTrust.ProbeCertutilInstall(runner);

        if (IsFirefoxProcessRunning())
        {
            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.Failed,
                "Firefox appears to be running. Quit Firefox, then retry Trust CA in Firefox.");
        }

        var cerPath = UnixCertificateTrust.WriteTempCer(certificate);
        try
        {
            // Delete existing nickname first (ignore failure), then add as trusted CA.
            runner.Run(certutil, $"-d sql:\"{profileDir}\" -D -n \"{Escape(friendlyName)}\"");
            var add = runner.Run(certutil,
                $"-d sql:\"{profileDir}\" -A -t \"C,,\" -n \"{Escape(friendlyName)}\" -i \"{cerPath}\"");
            if (add is not { Succeeded: true })
            {
                return CertificateOsTrustResult.Fail(
                    CertificateOsTrustKind.NssFailed,
                    string.IsNullOrWhiteSpace(add?.StandardError)
                        ? "certutil failed to import the CA into the Firefox profile"
                        : add!.StandardError.Trim());
            }

            // certutil -A can exit 0 as a silent no-op when the DER exists under another nickname.
            var list = runner.Run(certutil, $"-d sql:\"{profileDir}\" -L");
            if (list is { Succeeded: true } &&
                list.StandardOutput.Contains(friendlyName, StringComparison.OrdinalIgnoreCase))
            {
                return CertificateOsTrustResult.Ok(
                    "Firefox will use the Titanium root CA after you restart Firefox");
            }

            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.NssFailed,
                "certutil reported success but the CA nickname is missing from the Firefox NSS database");
        }
        catch (Exception ex)
        {
            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.NssFailed,
                "Firefox NSS import failed: " + ex.Message);
        }
        finally
        {
            try { File.Delete(cerPath); } catch { /* best effort */ }
        }
    }

    internal static bool UntrustDefaultProfile(string friendlyName, IProcessRunner runner)
    {
        if (!TryResolveDefaultProfileDirectory(out var profileDir, out _))
            return false;

        var certutil = UnixCertificateTrust.FindCertutil(runner);
        if (certutil is null) return false;

        try
        {
            var result = runner.Run(certutil, $"-d sql:\"{profileDir}\" -D -n \"{Escape(friendlyName)}\"");
            return result is { Succeeded: true };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when a firefox process is running (best-effort).</summary>
    public static bool IsFirefoxProcessRunning()
    {
        foreach (var process in EnumerateFirefoxProcesses())
        {
            process.Dispose();
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Asks Firefox to quit gracefully (user already consented). Waits briefly for exit.
    ///     Does not force-kill; returns false if Firefox is still running after the wait.
    /// </summary>
    public static bool TryRequestFirefoxQuit(TimeSpan? waitForExit = null) =>
        TryRequestFirefoxQuit(waitForExit, new ProcessRunner());

    /// <summary>
    ///     Asks Firefox to quit gracefully (user already consented). Waits briefly for exit.
    ///     Does not force-kill; returns false if Firefox is still running after the wait.
    /// </summary>
    internal static bool TryRequestFirefoxQuit(TimeSpan? waitForExit, IProcessRunner runner)
    {
        var wait = waitForExit ?? TimeSpan.FromSeconds(8);

        if (!IsFirefoxProcessRunning())
            return true;

        if (OperatingSystem.IsWindows())
        {
            foreach (var process in EnumerateFirefoxProcesses())
            {
                try
                {
                    process.CloseMainWindow();
                }
                catch
                {
                    // ignore per-process failures; wait loop decides success
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            // Consent already given in UI — quit the app, not Keychain UI automation.
            runner.Run("osascript", "-e 'tell application \"Firefox\" to quit'");
        }
        else if (OperatingSystem.IsLinux())
        {
            foreach (var process in EnumerateFirefoxProcesses())
            {
                try
                {
                    runner.Run("kill", $"-TERM {process.Id}");
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsFirefoxProcessRunning())
                return true;
            Thread.Sleep(200);
        }

        return !IsFirefoxProcessRunning();
    }

    private static IEnumerable<Process> EnumerateFirefoxProcesses()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            yield break;
        }

        foreach (var process in processes)
        {
            var match = false;
            try
            {
                var name = process.ProcessName;
                match = name.Equals("firefox", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("firefox-bin", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                match = false;
            }

            if (match)
                yield return process;
            else
            {
                try { process.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>Resolves the default Firefox profile directory from profiles.ini.</summary>
    public static bool TryResolveDefaultProfileDirectory(out string profileDirectory, out string? error)
    {
        profileDirectory = string.Empty;
        error = null;

        if (!TryGetProfilesIniPath(out var iniPath))
        {
            error = "Firefox profile not found (no profiles.ini)";
            return false;
        }

        try
        {
            var root = Path.GetDirectoryName(iniPath)!;
            var text = File.ReadAllText(iniPath);
            var entry = ParseDefaultProfileEntry(text);
            if (entry is null || string.IsNullOrWhiteSpace(entry.Value.Path))
            {
                error = "Firefox profiles.ini has no default profile";
                return false;
            }

            var defaultPath = entry.Value.Path;
            var full = !entry.Value.IsRelative || Path.IsPathRooted(defaultPath)
                ? defaultPath
                : Path.GetFullPath(Path.Combine(root, defaultPath));

            if (!Directory.Exists(full))
            {
                error = "Firefox default profile directory does not exist: " + full;
                return false;
            }

            profileDirectory = full;
            return true;
        }
        catch (Exception ex)
        {
            error = "Failed to read Firefox profiles.ini: " + ex.Message;
            return false;
        }
    }

    /// <summary>Prefer Default=1 profile; else first Path= under a Profile section.</summary>
    internal static string? ParseDefaultProfilePath(string profilesIni) =>
        ParseDefaultProfileEntry(profilesIni)?.Path;

    internal static (string Path, bool IsRelative)? ParseDefaultProfileEntry(string profilesIni)
    {
        string? fallbackPath = null;
        var fallbackRelative = true;
        string? currentPath = null;
        var isRelative = true;
        var isDefault = false;

        using var reader = new StringReader(profilesIni);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (isDefault && !string.IsNullOrWhiteSpace(currentPath))
                    return (currentPath, isRelative);
                if (fallbackPath is null && !string.IsNullOrWhiteSpace(currentPath))
                {
                    fallbackPath = currentPath;
                    fallbackRelative = isRelative;
                }

                currentPath = null;
                isRelative = true;
                isDefault = false;
                continue;
            }

            if (line.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                currentPath = line["Path=".Length..].Trim();
            else if (line.StartsWith("IsRelative=", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["IsRelative=".Length..].Trim();
                isRelative = value is not ("0" or "false");
            }
            else if (line.StartsWith("Default=1", StringComparison.OrdinalIgnoreCase))
                isDefault = true;
        }

        if (isDefault && !string.IsNullOrWhiteSpace(currentPath))
            return (currentPath, isRelative);
        if (fallbackPath is not null)
            return (fallbackPath, fallbackRelative);
        if (!string.IsNullOrWhiteSpace(currentPath))
            return (currentPath, isRelative);
        return null;
    }

    private static bool TryGetProfilesIniPath(out string iniPath)
    {
        foreach (var root in GetFirefoxRoots())
        {
            var candidate = Path.Combine(root, "profiles.ini");
            if (File.Exists(candidate))
            {
                iniPath = candidate;
                return true;
            }
        }

        iniPath = string.Empty;
        return false;
    }

    /// <summary>Known Firefox profile roots (classic, Snap, Flatpak). First hit with profiles.ini wins.</summary>
    internal static string[] GetFirefoxRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return [Path.Combine(appData, "Mozilla", "Firefox")];
        }

        if (OperatingSystem.IsMacOS())
        {
            return
            [
                Path.Combine(home, "Library", "Application Support", "Firefox"),
            ];
        }

        if (OperatingSystem.IsLinux())
        {
            return
            [
                Path.Combine(home, ".mozilla", "firefox"),
                // Ubuntu Snap default Firefox
                Path.Combine(home, "snap", "firefox", "common", ".mozilla", "firefox"),
                // Flatpak (org.mozilla.Firefox / org.mozilla.firefox)
                Path.Combine(home, ".var", "app", "org.mozilla.firefox", ".mozilla", "firefox"),
                Path.Combine(home, ".var", "app", "org.mozilla.Firefox", ".mozilla", "firefox"),
            ];
        }

        return [];
    }

    private static string Escape(string value) => value.Replace("\"", "\\\"");
}
