using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     macOS Keychain / Linux NSS + optional elevated system CA trust helpers.
/// </summary>
internal static class UnixCertificateTrust
{
    /// <summary>
    ///     Trusts <paramref name="certificate"/> for SSL in the current-user store backends
    ///     (login keychain on macOS, NSS db on Linux).
    /// </summary>
    public static CertificateOsTrustResult TrustUserSsl(X509Certificate2 certificate, string friendlyName,
        IProcessRunner? runner = null)
    {
        runner ??= new ProcessRunner();
        var cerPath = WriteTempCer(certificate);
        try
        {
            if (RunTime.IsMac)
                return TrustMacUserDetailed(runner, cerPath, certificate);

            if (RunTime.IsLinux)
                return TrustLinuxNssDetailed(runner, cerPath, friendlyName);

            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.Unsupported,
                "OS SSL trust helpers are only available on macOS and Linux");
        }
        finally
        {
            TryDelete(cerPath);
        }
    }

    /// <summary>Bool wrapper for callers that only need success/failure.</summary>
    public static bool TryTrustUserSsl(X509Certificate2 certificate, string friendlyName,
        IProcessRunner? runner = null) =>
        TrustUserSsl(certificate, friendlyName, runner).Succeeded;

    /// <summary>
    ///     Removes user SSL trust previously added by <see cref="TrustUserSsl"/>.
    /// </summary>
    public static bool UntrustUserSsl(X509Certificate2 certificate, string friendlyName,
        IProcessRunner? runner = null)
    {
        runner ??= new ProcessRunner();
        if (RunTime.IsMac)
            return UntrustMacUser(runner, certificate);

        if (RunTime.IsLinux)
            return UntrustLinuxNss(runner, friendlyName);

        return false;
    }

    /// <summary>
    ///     Trusts the certificate machine-wide with an admin prompt (System keychain / update-ca-certificates).
    /// </summary>
    public static bool TrustMachineSsl(X509Certificate2 certificate, string friendlyName,
        IProcessRunner? runner = null, IElevationPrompt? elevation = null)
    {
        runner ??= new ProcessRunner();
        elevation ??= new OsElevationPrompt(runner);
        var cerPath = WriteTempCer(certificate);
        try
        {
            if (RunTime.IsMac)
                return TrustMacSystem(elevation, cerPath);

            if (RunTime.IsLinux)
                return TrustLinuxSystem(elevation, cerPath, friendlyName);

            return false;
        }
        finally
        {
            TryDelete(cerPath);
        }
    }

    /// <summary>
    ///     Removes machine-wide trust with an admin prompt.
    /// </summary>
    public static bool UntrustMachineSsl(X509Certificate2 certificate, string friendlyName,
        IProcessRunner? runner = null, IElevationPrompt? elevation = null)
    {
        runner ??= new ProcessRunner();
        elevation ??= new OsElevationPrompt(runner);

        if (RunTime.IsMac)
            return UntrustMacSystem(elevation, certificate);

        if (RunTime.IsLinux)
            return UntrustLinuxSystem(elevation, friendlyName);

        return false;
    }

    /// <summary>
    ///     Detects how to install NSS <c>certutil</c> on this OS (package or Homebrew), if possible.
    /// </summary>
    public static CertificateOsTrustResult ProbeCertutilInstall(IProcessRunner? runner = null)
    {
        runner ??= new ProcessRunner();
        if (FindCertutil(runner) != null)
            return CertificateOsTrustResult.Ok("certutil is available");

        if (RunTime.IsLinux)
        {
            var hint = DetectLinuxNssPackage(runner);
            if (hint is null)
            {
                return CertificateOsTrustResult.Fail(
                    CertificateOsTrustKind.CertutilMissing,
                    "certutil not found and no supported package manager (apt/dnf/zypper) was detected",
                    packageHint: null);
            }

            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.CertutilMissing,
                $"certutil not found. Install {hint.Package} for Chrome/Chromium (and Firefox profile) trust.",
                packageHint: hint.Package);
        }

        if (RunTime.IsMac)
        {
            var brew = FindBrew(runner);
            if (brew != null)
            {
                return CertificateOsTrustResult.Fail(
                    CertificateOsTrustKind.CertutilMissing,
                    "certutil not found. Install via Homebrew: brew install nss",
                    packageHint: "nss",
                    brewAvailable: true);
            }

            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.HomebrewMissing,
                "certutil not found and Homebrew is not installed. Export the CA and import it in Firefox Authorities, or install Homebrew then retry.",
                packageHint: "nss",
                brewAvailable: false);
        }

        return CertificateOsTrustResult.Fail(
            CertificateOsTrustKind.CertutilMissing,
            "certutil not found on PATH",
            packageHint: null);
    }

    /// <summary>
    ///     Installs NSS tools providing <c>certutil</c> after explicit user consent
    ///     (Linux elevated package manager, or macOS <c>brew install nss</c>).
    /// </summary>
    public static CertificateOsTrustResult TryInstallNssCertutil(
        IProcessRunner? runner = null,
        IElevationPrompt? elevation = null)
    {
        runner ??= new ProcessRunner();
        elevation ??= new OsElevationPrompt(runner);

        if (FindCertutil(runner) != null)
            return CertificateOsTrustResult.Ok("certutil already available");

        if (RunTime.IsLinux)
        {
            var hint = DetectLinuxNssPackage(runner);
            if (hint is null)
            {
                return CertificateOsTrustResult.Fail(
                    CertificateOsTrustKind.CertutilMissing,
                    "No supported package manager found to install certutil");
            }

            var result = elevation.RunElevated(hint.FileName, hint.Arguments);
            if (result is null)
            {
                return CertificateOsTrustResult.Fail(
                    CertificateOsTrustKind.Cancelled,
                    "Package install cancelled or elevation unavailable");
            }

            if (!result.Succeeded)
            {
                return CertificateOsTrustResult.Fail(
                    CertificateOsTrustKind.Failed,
                    string.IsNullOrWhiteSpace(result.StandardError)
                        ? $"Failed to install {hint.Package} (exit {result.ExitCode})"
                        : result.StandardError.Trim(),
                    packageHint: hint.Package);
            }

            return FindCertutil(runner) != null
                ? CertificateOsTrustResult.Ok($"Installed {hint.Package}")
                : CertificateOsTrustResult.Fail(
                    CertificateOsTrustKind.Failed,
                    $"{hint.Package} install finished but certutil is still not on PATH",
                    packageHint: hint.Package);
        }

        if (RunTime.IsMac)
        {
            var brew = FindBrew(runner);
            if (brew is null)
            {
                return CertificateOsTrustResult.Fail(
                    CertificateOsTrustKind.HomebrewMissing,
                    "Homebrew not found; cannot install nss automatically");
            }

            var result = runner.Run(brew, "install nss");
            if (result is not { Succeeded: true })
            {
                return CertificateOsTrustResult.Fail(
                    CertificateOsTrustKind.Failed,
                    result?.StandardError.Trim() is { Length: > 0 } err
                        ? err
                        : "brew install nss failed",
                    packageHint: "nss",
                    brewAvailable: true);
            }

            return FindCertutil(runner) != null
                ? CertificateOsTrustResult.Ok("Installed nss via Homebrew")
                : CertificateOsTrustResult.Fail(
                    CertificateOsTrustKind.Failed,
                    "brew install nss finished but certutil is still not on PATH",
                    packageHint: "nss",
                    brewAvailable: true);
        }

        return CertificateOsTrustResult.Fail(
            CertificateOsTrustKind.Unsupported,
            "Automatic certutil install is only supported on Linux and macOS");
    }

    /// <summary>Opens Keychain Access and optionally the certificate file for manual Always Trust.</summary>
    public static bool OpenMacKeychainGuidance(string? cerPath = null, IProcessRunner? runner = null)
    {
        if (!RunTime.IsMac) return false;
        runner ??= new ProcessRunner();
        runner.Run("open", "-a \"Keychain Access\"");
        if (!string.IsNullOrWhiteSpace(cerPath) && File.Exists(cerPath))
            runner.Run("open", $"\"{cerPath}\"");
        return true;
    }

    /// <summary>Writes a temp .cer and opens Keychain guidance for <paramref name="certificate"/>.</summary>
    public static string? OpenMacKeychainGuidanceForCertificate(
        X509Certificate2 certificate,
        IProcessRunner? runner = null)
    {
        if (!RunTime.IsMac) return null;
        var cerPath = WriteTempCer(certificate);
        OpenMacKeychainGuidance(cerPath, runner);
        return cerPath;
    }

    /// <summary>Verifies whether the certificate is trusted for SSL on macOS (best-effort).</summary>
    public static bool VerifyUserSslTrust(X509Certificate2 certificate, IProcessRunner? runner = null)
    {
        runner ??= new ProcessRunner();
        if (RunTime.IsMac)
            return VerifyMacSslTrust(runner, certificate);

        if (RunTime.IsLinux)
        {
            // Presence in user NSS db is a practical signal for Chromium trust.
            var nssDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pki", "nssdb");
            if (!Directory.Exists(nssDir)) return false;
            var certutil = FindCertutil(runner);
            if (certutil is null) return false;
            var list = runner.Run(certutil, $"-d sql:{nssDir} -L");
            return list is { Succeeded: true } &&
                   list.StandardOutput.Contains(certificate.GetNameInfo(X509NameType.SimpleName, false) ?? "",
                       StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>Resolves NSS <c>certutil</c> on PATH (not Windows system <c>certutil.exe</c>).</summary>
    public static string? FindCertutil(IProcessRunner runner)
    {
        if (RunTime.IsWindows)
        {
            // Windows ships Microsoft certutil.exe — it is not NSS and must not be used for profile DBs.
            foreach (var candidate in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                             "NSS", "certutil.exe"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "Programs", "nss", "certutil.exe"),
                     })
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        var which = runner.Run("sh", "-c \"command -v certutil\"");
        if (which is { Succeeded: true } && !string.IsNullOrWhiteSpace(which.StandardOutput))
        {
            var line = which.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (line.Length > 0 && !string.IsNullOrWhiteSpace(line[0]))
                return line[0].Trim();
        }

        if (RunTime.IsMac)
        {
            foreach (var candidate in new[]
                     {
                         "/opt/homebrew/opt/nss/bin/certutil",
                         "/usr/local/opt/nss/bin/certutil",
                     })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    internal static LinuxPackageHint? DetectLinuxNssPackage(IProcessRunner runner)
    {
        if (CommandExists(runner, "apt-get"))
            return new LinuxPackageHint("apt-get", "install -y libnss3-tools", "libnss3-tools");
        if (CommandExists(runner, "dnf"))
            return new LinuxPackageHint("dnf", "install -y nss-tools", "nss-tools");
        if (CommandExists(runner, "yum"))
            return new LinuxPackageHint("yum", "install -y nss-tools", "nss-tools");
        if (CommandExists(runner, "zypper"))
            return new LinuxPackageHint("zypper", "--non-interactive install mozilla-nss-tools", "mozilla-nss-tools");
        return null;
    }

    internal static string? FindBrew(IProcessRunner runner)
    {
        if (!RunTime.IsMac) return null;
        var which = runner.Run("sh", "-c \"command -v brew\"");
        if (which is { Succeeded: true } && !string.IsNullOrWhiteSpace(which.StandardOutput))
            return which.StandardOutput.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0];

        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         ".homebrew", "bin", "brew"),
                     "/opt/homebrew/bin/brew",
                     "/usr/local/bin/brew",
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static bool CommandExists(IProcessRunner runner, string name)
    {
        var which = runner.Run("sh", $"-c \"command -v {name}\"");
        return which is { Succeeded: true } && !string.IsNullOrWhiteSpace(which.StandardOutput);
    }

    private static CertificateOsTrustResult TrustMacUserDetailed(
        IProcessRunner runner, string cerPath, X509Certificate2 certificate)
    {
        var added = TrustMacUser(runner, cerPath);
        if (!added)
        {
            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.MacKeychainFailed,
                "Failed to add the root CA to the login keychain");
        }

        if (VerifyMacSslTrust(runner, certificate))
            return CertificateOsTrustResult.Ok("Root CA trusted in login keychain");

        return CertificateOsTrustResult.Fail(
            CertificateOsTrustKind.MacNeedsManualTrustConfirm,
            "Root CA was added to Keychain but may need Always Trust for SSL. Open Keychain Access, find the certificate, and set Trust → Always Trust.");
    }

    private static bool TrustMacUser(IProcessRunner runner, string cerPath)
    {
        // -d: add to admin cert store domain; -r trustRoot: trust as root CA.
        var keychain = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Keychains", "login.keychain-db");
        var result = runner.Run("security",
            $"add-trusted-cert -d -r trustRoot -k \"{keychain}\" \"{cerPath}\"");
        if (result is { Succeeded: true }) return true;

        // Older macOS keychain name
        keychain = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Keychains", "login.keychain");
        result = runner.Run("security",
            $"add-trusted-cert -d -r trustRoot -k \"{keychain}\" \"{cerPath}\"");
        return result is { Succeeded: true };
    }

    private static bool VerifyMacSslTrust(IProcessRunner runner, X509Certificate2 certificate)
    {
        var cerPath = WriteTempCer(certificate);
        try
        {
            // verify-cert returns 0 when the cert chains to a trusted root for SSL.
            var verify = runner.Run("security", $"verify-cert -c \"{cerPath}\" -p ssl");
            if (verify is { Succeeded: true })
                return true;

            // Fallback: trust settings dump mentioning the SHA-1 (legacy) hash.
            var sha1 = certificate.GetCertHashString();
            var dump = runner.Run("security", "dump-trust-settings -d");
            if (dump is { Succeeded: true } &&
                dump.StandardOutput.Contains(sha1, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
        finally
        {
            TryDelete(cerPath);
        }
    }

    private static bool UntrustMacUser(IProcessRunner runner, X509Certificate2 certificate)
    {
        var sha1 = certificate.GetCertHashString();
        var result = runner.Run("security", $"delete-certificate -Z {sha1}");
        return result is { Succeeded: true };
    }

    private static bool TrustMacSystem(IElevationPrompt elevation, string cerPath)
    {
        var result = elevation.RunElevated("/usr/bin/security",
            $"add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain \"{cerPath}\"");
        return result is { Succeeded: true };
    }

    private static bool UntrustMacSystem(IElevationPrompt elevation, X509Certificate2 certificate)
    {
        var sha1 = certificate.GetCertHashString();
        var result = elevation.RunElevated("/usr/bin/security",
            $"delete-certificate -Z {sha1} /Library/Keychains/System.keychain");
        return result is { Succeeded: true };
    }

    private static CertificateOsTrustResult TrustLinuxNssDetailed(
        IProcessRunner runner, string cerPath, string friendlyName)
    {
        if (FindCertutil(runner) is null)
            return ProbeCertutilInstall(runner);

        var nssDir = EnsureLinuxNssDb(runner);
        if (nssDir is null)
        {
            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.NssFailed,
                "Could not initialize the user NSS database (~/.pki/nssdb)");
        }

        var certutil = FindCertutil(runner)!;
        var result = runner.Run(certutil,
            $"-d sql:{nssDir} -A -t \"C,,\" -n \"{Escape(friendlyName)}\" -i \"{cerPath}\"");
        if (result is { Succeeded: true })
            return CertificateOsTrustResult.Ok("Root CA trusted in user NSS database");

        return CertificateOsTrustResult.Fail(
            CertificateOsTrustKind.NssFailed,
            string.IsNullOrWhiteSpace(result?.StandardError)
                ? "certutil failed to add the root CA to ~/.pki/nssdb"
                : result!.StandardError.Trim());
    }

    private static bool UntrustLinuxNss(IProcessRunner runner, string friendlyName)
    {
        var nssDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pki", "nssdb");
        if (!Directory.Exists(nssDir)) return false;
        var certutil = FindCertutil(runner);
        if (certutil is null) return false;
        var result = runner.Run(certutil, $"-d sql:{nssDir} -D -n \"{Escape(friendlyName)}\"");
        return result is { Succeeded: true };
    }

    private static bool TrustLinuxSystem(IElevationPrompt elevation, string cerPath, string friendlyName)
    {
        var safeName = SanitizeFileName(friendlyName) + ".crt";
        var dest = $"/usr/local/share/ca-certificates/{safeName}";
        // Single elevated shell: copy + update-ca-certificates
        var script =
            $"cp \"{cerPath}\" \"{dest}\" && chmod 644 \"{dest}\" && update-ca-certificates";
        var result = elevation.RunElevated("/bin/sh", $"-c \"{EscapeShell(script)}\"");
        return result is { Succeeded: true };
    }

    private static bool UntrustLinuxSystem(IElevationPrompt elevation, string friendlyName)
    {
        var safeName = SanitizeFileName(friendlyName) + ".crt";
        var dest = $"/usr/local/share/ca-certificates/{safeName}";
        var script = $"rm -f \"{dest}\" && update-ca-certificates";
        var result = elevation.RunElevated("/bin/sh", $"-c \"{EscapeShell(script)}\"");
        return result is { Succeeded: true };
    }

    private static string? EnsureLinuxNssDb(IProcessRunner runner)
    {
        var certutil = FindCertutil(runner);
        if (certutil is null)
            return null;

        var nssDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pki", "nssdb");
        Directory.CreateDirectory(nssDir);
        if (!File.Exists(Path.Combine(nssDir, "cert9.db")) &&
            !File.Exists(Path.Combine(nssDir, "cert8.db")))
        {
            var init = runner.Run(certutil, $"-d sql:{nssDir} -N --empty-password");
            if (init is not { Succeeded: true }) return null;
        }

        return nssDir;
    }

    internal static string WriteTempCer(X509Certificate2 certificate)
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-" + Guid.NewGuid().ToString("N") + ".cer");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Cert));
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* best effort */ }
    }

    private static string Escape(string value) => value.Replace("\"", "\\\"");

    private static string EscapeShell(string value) => value.Replace("\"", "\\\"");

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == ' ')
                chars[i] = '-';
        return new string(chars);
    }

    internal sealed record LinuxPackageHint(string FileName, string Arguments, string Package);
}
