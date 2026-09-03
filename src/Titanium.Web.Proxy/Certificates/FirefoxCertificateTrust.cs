using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Network;

/// <summary>
///     Firefox-specific CA trust: Windows ImportEnterpriseRoots policy, plus optional
///     NSS import into the default Firefox profile (<c>cert9.db</c>).
/// </summary>
public static class FirefoxCertificateTrust
{
    private const string WindowsPolicySubKey = @"Software\Policies\Mozilla\Firefox\Certificates";
    private const string ImportEnterpriseRootsValue = "ImportEnterpriseRoots";

    /// <summary>True when a Firefox profiles.ini (or common profile root) is present.</summary>
    public static bool IsFirefoxProfilePresent() => TryGetProfilesIniPath(out _);

    /// <summary>
    ///     Windows: enable OS-root trust for Firefox via HKCU policy when allowed,
    ///     otherwise set <c>security.enterprise_roots.enabled</c> in the default profile <c>user.js</c>.
    /// </summary>
    public static CertificateOsTrustResult TryEnableWindowsEnterpriseRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.Unsupported,
                "ImportEnterpriseRoots is Windows-only");
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(WindowsPolicySubKey, true);
            if (key is not null)
            {
                key.SetValue(ImportEnterpriseRootsValue, 1, Microsoft.Win32.RegistryValueKind.DWord);
                return CertificateOsTrustResult.Ok(
                    "Firefox will trust the Windows root CA after you restart Firefox");
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
        if (!OperatingSystem.IsWindows()) return false;
        var cleared = false;
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

    private static void EnsureEnterpriseRootsUserPref(string profileDirectory)
    {
        const string prefLine = "user_pref(\"security.enterprise_roots.enabled\", true);";
        var userJs = Path.Combine(profileDirectory, "user.js");
        if (File.Exists(userJs))
        {
            var text = File.ReadAllText(userJs);
            if (text.Contains("security.enterprise_roots.enabled", StringComparison.Ordinal))
            {
                // Replace existing pref line(s) to true.
                var lines = text.Split(['\r', '\n'], StringSplitOptions.None)
                    .Select(l =>
                        l.Contains("security.enterprise_roots.enabled", StringComparison.Ordinal)
                            ? prefLine
                            : l)
                    .ToArray();
                File.WriteAllText(userJs, string.Join(Environment.NewLine, lines));
                return;
            }

            File.AppendAllText(userJs, Environment.NewLine + prefLine + Environment.NewLine);
            return;
        }

        File.WriteAllText(userJs, prefLine + Environment.NewLine);
    }

    private static bool ClearEnterpriseRootsUserPref(string profileDirectory)
    {
        var userJs = Path.Combine(profileDirectory, "user.js");
        if (!File.Exists(userJs)) return false;
        var lines = File.ReadAllLines(userJs)
            .Where(l => !l.Contains("security.enterprise_roots.enabled", StringComparison.Ordinal))
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
            if (add is { Succeeded: true })
            {
                return CertificateOsTrustResult.Ok(
                    "Firefox will use the Titanium root CA after you restart Firefox");
            }

            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.NssFailed,
                string.IsNullOrWhiteSpace(add?.StandardError)
                    ? "certutil failed to import the CA into the Firefox profile"
                    : add!.StandardError.Trim());
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

        var result = runner.Run(certutil, $"-d sql:\"{profileDir}\" -D -n \"{Escape(friendlyName)}\"");
        return result is { Succeeded: true };
    }

    /// <summary>True when a firefox process is running (best-effort).</summary>
    public static bool IsFirefoxProcessRunning()
    {
        try
        {
            return Process.GetProcesses()
                .Any(p =>
                {
                    try
                    {
                        var name = p.ProcessName;
                        return name.Equals("firefox", StringComparison.OrdinalIgnoreCase)
                               || name.Equals("firefox-bin", StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                });
        }
        catch
        {
            return false;
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
            var defaultPath = ParseDefaultProfilePath(text);
            if (string.IsNullOrWhiteSpace(defaultPath))
            {
                error = "Firefox profiles.ini has no default profile";
                return false;
            }

            var full = Path.IsPathRooted(defaultPath)
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

    internal static string? ParseDefaultProfilePath(string profilesIni)
    {
        // Prefer Default=1 profile; else first Path= under a Profile section.
        string? fallback = null;
        string? currentPath = null;
        var isDefault = false;

        using var reader = new StringReader(profilesIni);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (isDefault && !string.IsNullOrWhiteSpace(currentPath))
                    return currentPath;
                if (fallback is null && !string.IsNullOrWhiteSpace(currentPath))
                    fallback = currentPath;
                currentPath = null;
                isDefault = false;
                continue;
            }

            if (line.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                currentPath = line["Path=".Length..].Trim();
            else if (line.StartsWith("Default=1", StringComparison.OrdinalIgnoreCase))
                isDefault = true;
        }

        if (isDefault && !string.IsNullOrWhiteSpace(currentPath))
            return currentPath;
        return fallback ?? currentPath;
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

    private static string[] GetFirefoxRoots()
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
                Path.Combine(home, "snap", "firefox", "common", ".mozilla", "firefox"),
            ];
        }

        return [];
    }

    private static string Escape(string value) => value.Replace("\"", "\\\"");
}
