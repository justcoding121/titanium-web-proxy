using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     macOS Keychain / Linux NSS + optional elevated system CA trust helpers.
/// </summary>
internal static class UnixCertificateTrust
{
    private const string SecurityBinary = "security";
    private const string NssDbDirName = "nssdb";
    private const string LibraryDirName = "Library";
    private const string KeychainsDirName = "Keychains";
    private const string LoginKeychainDbFile = "login.keychain-db";
    private const string LoginKeychainFile = "login.keychain";

    private static readonly Regex MacTrustSettingsCountRegex = new(
        @"Number of trust settings\s*:\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] WindowsCertutilCandidates =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NSS", "certutil.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "nss", "certutil.exe"),
    ];

    private static readonly string[] MacCertutilCandidates =
    [
        "/opt/homebrew/opt/nss/bin/certutil",
        "/usr/local/opt/nss/bin/certutil",
    ];

    private static readonly string[] MacBrewCandidates =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".homebrew", "bin", "brew"),
        "/opt/homebrew/bin/brew",
        "/usr/local/bin/brew",
    ];

    private static readonly string[] MacDumpTrustSettingsArgs =
    [
        "dump-trust-settings -d",
        "dump-trust-settings",
    ];

    private static readonly bool[] MacTrustExportAdminDomain = [true, false];
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
        catch (Exception ex)
        {
            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.Failed,
                "OS SSL trust failed: " + ex.Message);
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
    ///     On macOS this also best-effort clears matching System keychain copies and trust
    ///     settings (admin password may be required) — Chrome trusts System roots even when
    ///     the login / .NET user store entry is gone.
    /// </summary>
    public static bool UntrustUserSsl(X509Certificate2 certificate, string friendlyName,
        IProcessRunner? runner = null, IElevationPrompt? elevation = null)
    {
        runner ??= new ProcessRunner();
        try
        {
            if (RunTime.IsMac)
                return UntrustMacThorough(runner, certificate, friendlyName, elevation);

            if (RunTime.IsLinux)
                return UntrustLinuxNss(runner, certificate, friendlyName);

            return false;
        }
        catch
        {
            return false;
        }
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
        catch
        {
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
        try
        {
            if (RunTime.IsMac)
                return UntrustMacThorough(runner, certificate, friendlyName, elevation);

            if (RunTime.IsLinux)
                return UntrustLinuxSystem(elevation, friendlyName);

            return false;
        }
        catch
        {
            return false;
        }
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
            return ProbeLinuxCertutilInstall(runner);

        if (RunTime.IsMac)
            return ProbeMacCertutilInstall(runner);

        return CertificateOsTrustResult.Fail(
            CertificateOsTrustKind.CertutilMissing,
            "certutil not found on PATH",
            packageHint: null);
    }

    private static CertificateOsTrustResult ProbeLinuxCertutilInstall(IProcessRunner runner)
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

    private static CertificateOsTrustResult ProbeMacCertutilInstall(IProcessRunner runner)
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

    private static CertificateOsTrustResult TryInstallLinuxNssCertutil(
        IProcessRunner runner, IElevationPrompt elevation)
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
            return TryInstallLinuxNssCertutil(runner, elevation);

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
        // Friendly file name so Keychain's "trust this certificate?" dialog is recognizable.
        var cerPath = WriteTempCer(certificate, forUserGuidance: true);
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
            return VerifyLinuxNssTrust(runner, certificate);

        return false;
    }

    /// <summary>
    ///     True when <paramref name="certificate"/> is present in the user NSS DB (any nickname)
    ///     with trust attributes that include SSL (Chromium reads <c>~/.pki/nssdb</c>).
    /// </summary>
    internal static bool VerifyLinuxNssTrust(IProcessRunner runner, X509Certificate2 certificate)
    {
        var certutil = FindCertutil(runner);
        if (certutil is null) return false;

        foreach (var nssDir in LinuxNssDatabaseDirectories().Where(Directory.Exists))
        {
            var list = runner.Run(certutil, $"-d sql:{nssDir} -L");
            if (list is not { Succeeded: true })
                continue;

            // Match by certificate bytes, not nickname/CN. The same DER can sit under a
            // legacy nickname ("Titanium Inspector Root Certificate") while the product CN
            // is "Titanium Root Certificate Authority", and a CN substring hit would also
            // false-positive against an unrelated nickname.
            if (LinuxNssContainsCertificate(runner, certutil, nssDir, certificate, list.StandardOutput))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Best-effort: true when the certificate appears in the login keychain (by SHA-1 hash).
    ///     Presence does not imply SSL Always Trust.
    /// </summary>
    public static bool IsCertificateInLoginKeychain(X509Certificate2 certificate, IProcessRunner? runner = null)
    {
        if (!RunTime.IsMac)
            return false;

        runner ??= new ProcessRunner();
        var sha1 = certificate.GetCertHashString();
        if (string.IsNullOrWhiteSpace(sha1))
            return false;

        // -a: all matching; -Z: print SHA-1. Match our hash in the dump.
        var byHash = runner.Run(SecurityBinary, $"find-certificate -a -Z {sha1}");
        if (byHash is { Succeeded: true } &&
            byHash.StandardOutput.Contains(sha1, StringComparison.OrdinalIgnoreCase))
            return true;

        var commonName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (string.IsNullOrWhiteSpace(commonName))
            return false;

        var loginDb = UserLoginKeychainDbPath();
        var args = File.Exists(loginDb)
            ? $"find-certificate -a -c \"{Escape(commonName)}\" -Z \"{loginDb}\""
            : $"find-certificate -a -c \"{Escape(commonName)}\" -Z";
        var byName = runner.Run(SecurityBinary, args);
        return byName is { Succeeded: true } &&
               byName.StandardOutput.Contains(sha1, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Resolves NSS <c>certutil</c> on PATH (not Windows system <c>certutil.exe</c>).</summary>
    public static string? FindCertutil(IProcessRunner runner)
    {
        if (RunTime.IsWindows)
        {
            // Windows ships Microsoft certutil.exe — it is not NSS and must not be used for profile DBs.
            return WindowsCertutilCandidates.FirstOrDefault(File.Exists);
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
            return MacCertutilCandidates.FirstOrDefault(File.Exists);
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

        return MacBrewCandidates.FirstOrDefault(File.Exists);
    }

    private static bool CommandExists(IProcessRunner runner, string name)
    {
        var which = runner.Run("sh", $"-c \"command -v {name}\"");
        return which is { Succeeded: true } && !string.IsNullOrWhiteSpace(which.StandardOutput);
    }

    private static CertificateOsTrustResult TrustMacUserDetailed(
        IProcessRunner runner, string cerPath, X509Certificate2 certificate)
    {
        var added = TrustMacUser(runner, cerPath, certificate);
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

    private static bool TrustMacUser(
        IProcessRunner runner, string cerPath, X509Certificate2? certificate = null)
    {
        // Drop same-CN Keychain copies that are not this generation so Chrome/Safari do not
        // keep an untrusted/stale Titanium root while we add the current one.
        if (certificate is not null)
            TryRemoveStaleMacSameCommonNameRoots(runner, certificate);

        // User trust domain (no -d). Using -d writes admin-domain stubs and often leaves
        // System.keychain copies that Chrome keeps trusting after "Remove CA".
        // -r trustRoot: trust as root CA in the login keychain.
        var keychain = UserLoginKeychainDbPath();
        var result = runner.Run(SecurityBinary,
            $"add-trusted-cert -r trustRoot -k \"{keychain}\" \"{cerPath}\"");
        if (result is { Succeeded: true }) return true;

        // Older macOS keychain name
        keychain = UserLoginKeychainPath();
        result = runner.Run(SecurityBinary,
            $"add-trusted-cert -r trustRoot -k \"{keychain}\" \"{cerPath}\"");
        return result is { Succeeded: true };
    }

    /// <summary>
    ///     Best-effort: delete login-keychain certificates that share the Titanium root CN
    ///     but not the current SHA-1 (stale generations after rotate / reinstall).
    /// </summary>
    private static void TryRemoveStaleMacSameCommonNameRoots(
        IProcessRunner runner, X509Certificate2 certificate)
    {
        var keepSha1 = certificate.GetCertHashString();
        if (string.IsNullOrWhiteSpace(keepSha1))
            return;

        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cn in MacRootCommonNames(
                     certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? "",
                     certificate))
        {
            CollectMacCertificateHashes(runner, cn, hashes);
        }

        foreach (var hash in hashes)
        {
            if (string.Equals(hash, keepSha1, StringComparison.OrdinalIgnoreCase))
                continue;

            // -t also clears user trust-settings stubs for that hash.
            runner.Run(SecurityBinary, $"delete-certificate -Z {hash} -t");
        }
    }

    private static bool VerifyMacSslTrust(IProcessRunner runner, X509Certificate2 certificate)
    {
        // IMPORTANT: `security verify-cert -p ssl` often succeeds when the CA is merely present
        // in login.keychain. Keychain Access Get Info can also show "Always Trust" for incomplete
        // trust-list entries that have no policy array — Chrome still rejects MITM until real
        // SecTrustSettings policies exist (dump-trust-settings / export with trustSettings).
        return HasExplicitMacSslTrustSettings(runner, certificate);
    }

    /// <summary>
    ///     True when macOS has persisted SSL/root trust policies for this certificate
    ///     (not merely a trust-list stub or Keychain UI display state).
    /// </summary>
    internal static bool HasExplicitMacSslTrustSettings(IProcessRunner runner, X509Certificate2 certificate)
    {
        var sha1 = certificate.GetCertHashString();
        if (string.IsNullOrEmpty(sha1))
            return false;

        // Match by SHA-1 only — same CN can be a stale Keychain generation.
        if (DumpTrustSettingsHasPoliciesForSha1(runner, sha1))
            return true;

        return TrustSettingsExportHasPolicies(runner, sha1);
    }

    /// <summary>
    ///     True when <c>security dump-trust-settings</c> lists this SHA-1 with a non-zero
    ///     policy count and a TrustRoot/Proceed result. Stubs with
    ///     <c>Number of trust settings : 0</c> (or CN-only rows for another generation) are
    ///     not trusted.
    /// </summary>
    private static bool DumpTrustSettingsHasPoliciesForSha1(IProcessRunner runner, string sha1)
    {
        foreach (var args in MacDumpTrustSettingsArgs)
        {
            var dump = runner.Run(SecurityBinary, args);
            if (dump is null)
                continue;

            var text = dump.StandardOutput + "\n" + dump.StandardError;
            if (text.Contains("No Trust Settings were found", StringComparison.OrdinalIgnoreCase))
                continue;

            if (DumpTextHasSslTrustPoliciesForSha1(text, sha1))
                return true;

            // dump-trust-settings names the certificate and does not print its SHA-1.
            // A TrustRoot block still counts when that name is this hash in the login
            // or System keychain (admin-domain add-trusted-cert -d -r trustRoot).
            if (DumpNamedTrustRootMatchesSha1(runner, text, sha1))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Match a TrustRoot policy block to <paramref name="sha1"/> by looking the
    ///     displayed certificate name up in the keychains. Real
    ///     <c>security dump-trust-settings</c> output has no hash line.
    /// </summary>
    private static bool DumpNamedTrustRootMatchesSha1(IProcessRunner runner, string text, string sha1)
    {
        var parts = text.Split(["Cert "], StringSplitOptions.None);
        foreach (var part in parts)
        {
            if (!BlockHasExplicitTrustRoot(part) || BlockMentionsOtherSha1(part, sha1))
                continue;

            var name = CertBlockDisplayName(part);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (KeychainHashesForName(runner, name).Any(hash =>
                    string.Equals(NormalizeSha1(hash), NormalizeSha1(sha1), StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    private static bool BlockHasExplicitTrustRoot(string part)
    {
        var countMatch = MacTrustSettingsCountRegex.Match(part);
        if (!countMatch.Success || !int.TryParse(countMatch.Groups[1].Value, out var count) || count <= 0)
            return false;

        return part.Contains("kSecTrustSettingsResultTrustRoot", StringComparison.Ordinal) ||
               part.Contains("kSecTrustSettingsResultProceed", StringComparison.Ordinal) ||
               part.Contains("Trust Root", StringComparison.OrdinalIgnoreCase);
    }

    private static bool BlockMentionsOtherSha1(string part, string sha1)
    {
        const string marker = "SHA-1 hash:";
        var idx = part.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return false;

        var hash = part[(idx + marker.Length)..].Trim();
        var end = hash.IndexOfAny(['\r', '\n', ' ']);
        if (end > 0)
            hash = hash[..end];
        hash = NormalizeSha1(hash);
        return hash.Length >= 40 &&
               !string.Equals(hash, NormalizeSha1(sha1), StringComparison.OrdinalIgnoreCase);
    }

    private static string? CertBlockDisplayName(string part)
    {
        var line = part.Split(['\r', '\n'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var colon = line.IndexOf(':');
        if (colon < 0 || colon > 6)
            return null;

        var name = line[(colon + 1)..].Trim();
        return name.Length == 0 ? null : name;
    }

    private static IEnumerable<string> KeychainHashesForName(IProcessRunner runner, string commonName)
    {
        var searches = new[]
        {
            $"find-certificate -a -c \"{Escape(commonName)}\" -Z",
            $"find-certificate -a -c \"{Escape(commonName)}\" -Z /Library/Keychains/System.keychain",
        };

        foreach (var args in searches)
        {
            var dump = runner.Run(SecurityBinary, args);
            if (dump is null)
                continue;

            foreach (var line in (dump.StandardOutput + "\n" + dump.StandardError)
                     .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                const string marker = "SHA-1 hash:";
                var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    continue;
                var hash = NormalizeSha1(line[(idx + marker.Length)..]);
                if (hash.Length >= 40)
                    yield return hash;
            }
        }
    }

    private static string NormalizeSha1(string value) =>
        value.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Trim();

    /// <summary>Parses one dump-trust-settings document for a SHA-1 cert block with real policies.</summary>
    internal static bool DumpTextHasSslTrustPoliciesForSha1(string text, string sha1)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(sha1))
            return false;

        // Split into "Cert N: ..." blocks; require the SHA-1 inside the same block as policies.
        var parts = text.Split(["Cert "], StringSplitOptions.None);
        foreach (var part in parts)
        {
            if (!part.Contains(sha1, StringComparison.OrdinalIgnoreCase))
                continue;

            // "Number of trust settings : 0" must not count as trusted.
            var countMatch = MacTrustSettingsCountRegex.Match(part);
            if (!countMatch.Success || !int.TryParse(countMatch.Groups[1].Value, out var count) || count <= 0)
                continue;

            if (part.Contains("kSecTrustSettingsResultTrustRoot", StringComparison.Ordinal) ||
                part.Contains("kSecTrustSettingsResultProceed", StringComparison.Ordinal) ||
                part.Contains("Trust Root", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Parses <c>security trust-settings-export</c>. A trustList stub without
    ///     <c>trustSettings</c> policies is NOT enough (Keychain UI may still show Always Trust).
    /// </summary>
    private static bool TrustSettingsExportHasPolicies(IProcessRunner runner, string sha1) // NOSONAR S3776 -- trust-settings-export parse is a single plist window scan.
    {
        if (string.IsNullOrEmpty(sha1))
            return false;

        foreach (var adminDomain in MacTrustExportAdminDomain)
        {
            var path = Path.Combine(Path.GetTempPath(), "twp-trust-" + Guid.NewGuid().ToString("N") + ".plist");
            try
            {
                var args = adminDomain
                    ? $"trust-settings-export -d \"{path}\""
                    : $"trust-settings-export \"{path}\"";
                var export = runner.Run(SecurityBinary, args);
                if (export is not { Succeeded: true } || !File.Exists(path))
                    continue;

                // Avoid pulling a plist library dependency: scan the XML/binary via plutil text.
                var printed = runner.Run("plutil", $"-p \"{path}\"");
                if (printed is null)
                    continue;

                var text = printed.StandardOutput;
                // Look for our SHA-1 key block; require a nested trustSettings array nearby.
                var keyIdx = text.IndexOf(sha1, StringComparison.OrdinalIgnoreCase);
                if (keyIdx < 0)
                    continue;

                // Heuristic: within the next ~2KB after the hash key, require trustSettings.
                var window = text.Substring(keyIdx, Math.Min(2048, text.Length - keyIdx));
                if (!window.Contains("trustSettings", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Empty array / missing policies: reject.
                if (window.Contains("trustSettings => [\n  ]", StringComparison.Ordinal) ||
                    window.Contains("trustSettings => []", StringComparison.Ordinal))
                    continue;

                if (window.Contains("kSecTrustSettingsResultTrustRoot", StringComparison.Ordinal) ||
                    window.Contains("kSecTrustSettingsResultProceed", StringComparison.Ordinal) ||
                    window.Contains("TrustRoot", StringComparison.OrdinalIgnoreCase) ||
                    window.Contains("result", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            finally
            {
                TryDelete(path);
            }
        }

        return false;
    }

    /// <summary>
    ///     Removes every Titanium-named / current-hash copy from login + System keychains and
    ///     clears user/admin trust settings. System deletes require an admin password prompt.
    /// </summary>
    private static bool UntrustMacThorough(
        IProcessRunner runner,
        X509Certificate2 certificate,
        string friendlyName,
        IElevationPrompt? elevation = null)
    {
        elevation ??= new OsElevationPrompt(runner);

        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sha1 = certificate.GetCertHashString();
        if (!string.IsNullOrWhiteSpace(sha1))
            hashes.Add(sha1);

        foreach (var cn in MacRootCommonNames(friendlyName, certificate))
            CollectMacCertificateHashes(runner, cn, hashes);

        var any = false;
        var cerPath = WriteTempCer(certificate);
        try
        {
            foreach (var hash in hashes)
            {
                // -t also drops user trust settings for this cert.
                var login = runner.Run(SecurityBinary, $"delete-certificate -Z {hash} -t");
                if (login is { Succeeded: true })
                    any = true;
            }

            // One admin prompt for System.keychain + admin trust domain.
            if (hashes.Count > 0 || File.Exists(cerPath))
            {
                var parts = new List<string>();
                foreach (var hash in hashes)
                {
                    parts.Add(
                        $"/usr/bin/security delete-certificate -Z {hash} -t /Library/Keychains/System.keychain || true");
                }

                parts.Add($"/usr/bin/security remove-trusted-cert -d \"{cerPath}\" || true");
                var script = string.Join("; ", parts);
                var elevated = elevation.RunElevated("/bin/sh", $"-c \"{EscapeShell(script)}\"");
                if (elevated is { Succeeded: true })
                    any = true;
            }

            var userTrust = runner.Run(SecurityBinary, $"remove-trusted-cert \"{cerPath}\"");
            if (userTrust is { Succeeded: true })
                any = true;
        }
        finally
        {
            TryDelete(cerPath);
        }

        return any || hashes.Count == 0;
    }

    private static HashSet<string> MacRootCommonNames(string friendlyName, X509Certificate2 certificate)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name))
                seen.Add(name.Trim());
        }

        Add(friendlyName);
        Add(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        Add("Titanium Root Certificate Authority");
        Add("Titanium Inspector Root Certificate");
        return seen;
    }

    private static void CollectMacCertificateHashes(
        IProcessRunner runner, string commonName, HashSet<string> hashes)
    {
        if (string.IsNullOrWhiteSpace(commonName))
            return;

        var loginDb = UserLoginKeychainDbPath();
        var searches = new List<string>
        {
            $"find-certificate -a -c \"{Escape(commonName)}\" -Z",
        };
        if (File.Exists(loginDb))
            searches.Add($"find-certificate -a -c \"{Escape(commonName)}\" -Z \"{loginDb}\"");
        searches.Add(
            $"find-certificate -a -c \"{Escape(commonName)}\" -Z /Library/Keychains/System.keychain");

        foreach (var args in searches)
        {
            var dump = runner.Run(SecurityBinary, args);
            if (dump is null)
                continue;

            foreach (var line in (dump.StandardOutput + "\n" + dump.StandardError)
                     .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                // "SHA-1 hash: AABBCC..."
                const string marker = "SHA-1 hash:";
                var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    continue;
                var hash = line[(idx + marker.Length)..].Trim();
                if (hash.Length >= 40)
                    hashes.Add(hash);
            }
        }
    }

    /// <summary>
    ///     True when any Titanium-named certificate remains in login or System keychain,
    ///     or the current root hash is still findable.
    /// </summary>
    internal static bool IsMacRootStillPresent(
        IProcessRunner runner, X509Certificate2 certificate, string friendlyName)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cn in MacRootCommonNames(friendlyName, certificate))
            CollectMacCertificateHashes(runner, cn, found);
        if (found.Count > 0)
            return true;

        var sha1 = certificate.GetCertHashString();
        if (string.IsNullOrWhiteSpace(sha1))
            return false;

        var byHash = runner.Run(SecurityBinary, $"find-certificate -a -Z {sha1}");
        return byHash is { Succeeded: true } &&
               byHash.StandardOutput.Contains(sha1, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TrustMacSystem(IElevationPrompt elevation, string cerPath)
    {
        var result = elevation.RunElevated("/usr/bin/security",
            $"add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain \"{cerPath}\"");
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

        var certutil = FindCertutil(runner);
        if (certutil is null)
            return ProbeCertutilInstall(runner);

        // Drop any prior nickname so -A is not a silent no-op when the DER already exists
        // under a different nickname (certutil exits 0 without listing the new name).
        runner.Run(certutil, $"-d sql:{nssDir} -D -n \"{Escape(friendlyName)}\"");

        var result = runner.Run(certutil,
            $"-d sql:{nssDir} -A -t \"C,,\" -n \"{Escape(friendlyName)}\" -i \"{cerPath}\"");
        if (result is not { Succeeded: true })
        {
            var error = result?.StandardError;
            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.NssFailed,
                string.IsNullOrWhiteSpace(error)
                    ? "certutil failed to add the root CA to ~/.pki/nssdb"
                    : error.Trim());
        }

        if (NssListContainsNickname(runner, certutil, nssDir, friendlyName))
        {
            TryTrustAdditionalLinuxNss(runner, certutil, cerPath, friendlyName);
            return CertificateOsTrustResult.Ok("Root CA trusted in user NSS database");
        }

        // DER collision under another nickname: remove matching entries and re-add.
        if (TryReloadCertificateFromCer(cerPath) is { } cert)
            RemoveLinuxNssEntriesMatching(runner, certutil, nssDir, cert);

        result = runner.Run(certutil,
            $"-d sql:{nssDir} -A -t \"C,,\" -n \"{Escape(friendlyName)}\" -i \"{cerPath}\"");
        if (result is { Succeeded: true } &&
            NssListContainsNickname(runner, certutil, nssDir, friendlyName))
        {
            TryTrustAdditionalLinuxNss(runner, certutil, cerPath, friendlyName);
            return CertificateOsTrustResult.Ok("Root CA trusted in user NSS database");
        }

        return CertificateOsTrustResult.Fail(
            CertificateOsTrustKind.NssFailed,
            "certutil reported success but the root CA nickname is missing from ~/.pki/nssdb");
    }

    private static bool NssListContainsNickname(
        IProcessRunner runner, string certutil, string nssDir, string friendlyName)
    {
        var list = runner.Run(certutil, $"-d sql:{nssDir} -L");
        return list is { Succeeded: true } &&
               list.StandardOutput.Contains(friendlyName, StringComparison.OrdinalIgnoreCase);
    }

    private static X509Certificate2? TryReloadCertificateFromCer(string cerPath)
    {
        try
        {
            return X509CertificateLoader.LoadCertificateFromFile(cerPath);
        }
        catch
        {
            return null;
        }
    }

    private static void RemoveLinuxNssEntriesMatching(
        IProcessRunner runner, string certutil, string nssDir, X509Certificate2 certificate)
    {
        var list = runner.Run(certutil, $"-d sql:{nssDir} -L");
        if (list is not { Succeeded: true })
            return;

        foreach (var nick in ParseNssNicknames(list.StandardOutput))
        {
            if (!LinuxNssNicknameMatches(runner, certutil, nssDir, nick, certificate))
                continue;
            runner.Run(certutil, $"-d sql:{nssDir} -D -n \"{Escape(nick)}\"");
        }
    }

    private static bool LinuxNssContainsCertificate(
        IProcessRunner runner, string certutil, string nssDir, X509Certificate2 certificate, string listOutput)
    {
        return ParseNssNicknames(listOutput).Any(nick =>
            LinuxNssNicknameMatches(runner, certutil, nssDir, nick, certificate));
    }

    private static bool LinuxNssNicknameMatches(
        IProcessRunner runner, string certutil, string nssDir, string nickname, X509Certificate2 certificate)
    {
        var dumped = runner.Run(certutil, $"-d sql:{nssDir} -L -n \"{Escape(nickname)}\" -a");
        if (dumped is not { Succeeded: true } || string.IsNullOrWhiteSpace(dumped.StandardOutput))
            return false;

        try
        {
            using var loaded = X509Certificate2.CreateFromPem(dumped.StandardOutput);
            return string.Equals(loaded.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> ParseNssNicknames(string certutilListOutput)
    {
        foreach (var raw in certutilListOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 ||
                line.StartsWith("Certificate Nickname", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("SSL,", StringComparison.OrdinalIgnoreCase) ||
                line.All(c => c == '-' || char.IsWhiteSpace(c)))
                continue;

            // "Nickname ... spaces ... Trust"
            var nick = line;
            var trustIdx = line.LastIndexOf("  ", StringComparison.Ordinal);
            if (trustIdx > 0)
                nick = line[..trustIdx].TrimEnd();
            if (nick.Length > 0)
                yield return nick;
        }
    }

    private static bool UntrustLinuxNss(
        IProcessRunner runner, X509Certificate2 certificate, string friendlyName)
    {
        var certutil = FindCertutil(runner);
        if (certutil is null)
            return false;

        var anyDb = false;
        var deleted = false;
        foreach (var nssDir in LinuxNssDatabaseDirectories().Where(Directory.Exists))
        {
            anyDb = true;
            if (UntrustLinuxNssDirectory(runner, certutil, nssDir, certificate, friendlyName))
                deleted = true;
        }

        if (!anyDb)
            return true;

        return deleted || !VerifyLinuxNssTrust(runner, certificate);
    }

    private static bool UntrustLinuxNssDirectory(
        IProcessRunner runner, string certutil, string nssDir, X509Certificate2 certificate, string friendlyName)
    {
        // certutil -A is a silent no-op when the same DER exists under another nickname
        // (legacy "Titanium Inspector Root Certificate" vs current CN). Delete every
        // matching nickname so Remove CA actually clears Chrome trust.
        var nicks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(friendlyName))
            nicks.Add(friendlyName);

        var list = runner.Run(certutil, $"-d sql:{nssDir} -L");
        if (list is { Succeeded: true })
        {
            foreach (var nick in ParseNssNicknames(list.StandardOutput)) // NOSONAR S3267 -- nickname set is mutated while matching NSS dumps.
            {
                if (nicks.Contains(nick) ||
                    LinuxNssNicknameMatches(runner, certutil, nssDir, nick, certificate))
                    nicks.Add(nick);
            }
        }

        var deleted = false;
        foreach (var nick in nicks)
        {
            var result = runner.Run(certutil, $"-d sql:{nssDir} -D -n \"{Escape(nick)}\"");
            if (result is { Succeeded: true })
                deleted = true;
        }

        return deleted;
    }

    private static void TryTrustAdditionalLinuxNss(
        IProcessRunner runner, string certutil, string cerPath, string friendlyName)
    {
        var primary = UserPkiNssDbPath();
        foreach (var nssDir in LinuxNssDatabaseDirectories()
                     .Where(d => !string.Equals(d, primary, StringComparison.Ordinal))
                     .Where(Directory.Exists))
        {
            try
            {
                runner.Run(certutil, $"-d sql:{nssDir} -D -n \"{Escape(friendlyName)}\"");
                runner.Run(certutil,
                    $"-d sql:{nssDir} -A -t \"C,,\" -n \"{Escape(friendlyName)}\" -i \"{cerPath}\"");
            }
            catch
            {
                // Snap/Flatpak DBs are best-effort.
            }
        }
    }

    internal static IEnumerable<string> LinuxNssDatabaseDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".pki", NssDbDirName);
        yield return Path.Combine(home, "snap", "chromium", "common", ".pki", NssDbDirName);
        yield return Path.Combine(home, "snap", "chromium", "current", ".pki", NssDbDirName);
        yield return Path.Combine(home, "snap", "google-chrome", "common", ".pki", NssDbDirName);
        yield return Path.Combine(home, "snap", "google-chrome", "current", ".pki", NssDbDirName);
        yield return Path.Combine(home, ".var", "app", "org.chromium.Chromium", ".pki", NssDbDirName);
        yield return Path.Combine(home, ".var", "app", "com.google.Chrome", ".pki", NssDbDirName);
        yield return Path.Combine(home, ".var", "app", "com.brave.Browser", ".pki", NssDbDirName);
        // Microsoft Edge (deb) shares ~/.pki/nssdb; Snap/Flatpak keep private DBs when present.
        yield return Path.Combine(home, "snap", "microsoft-edge", "common", ".pki", NssDbDirName);
        yield return Path.Combine(home, "snap", "microsoft-edge", "current", ".pki", NssDbDirName);
        yield return Path.Combine(home, ".var", "app", "com.microsoft.Edge", ".pki", NssDbDirName);
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

        var nssDir = UserPkiNssDbPath();
        Directory.CreateDirectory(nssDir);
        if (!File.Exists(Path.Combine(nssDir, "cert9.db")) &&
            !File.Exists(Path.Combine(nssDir, "cert8.db")))
        {
            var init = runner.Run(certutil, $"-d sql:{nssDir} -N --empty-password");
            if (init is not { Succeeded: true }) return null;
        }

        return nssDir;
    }

    internal static string WriteTempCer(X509Certificate2 certificate, bool forUserGuidance = false)
    {
        string fileName;
        if (forUserGuidance)
        {
            var cn = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            if (string.IsNullOrWhiteSpace(cn))
                cn = "Titanium-Inspector-Root-CA";
            fileName = SanitizeFileName(cn.Trim()) + ".cer";
        }
        else
        {
            // Internal ops: unique name avoids races between concurrent trust helpers.
            fileName = "twp-" + Guid.NewGuid().ToString("N") + ".cer";
        }

        var path = Path.Combine(Path.GetTempPath(), fileName);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Cert));
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* best effort */ }
    }

    private static string UserLoginKeychainDbPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            LibraryDirName, KeychainsDirName, LoginKeychainDbFile);

    private static string UserLoginKeychainPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            LibraryDirName, KeychainsDirName, LoginKeychainFile);

    private static string UserPkiNssDbPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pki", NssDbDirName);

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
