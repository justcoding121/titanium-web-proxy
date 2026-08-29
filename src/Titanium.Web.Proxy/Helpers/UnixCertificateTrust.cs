using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     macOS Keychain / Linux NSS + optional elevated system CA trust helpers.
/// </summary>
internal static class UnixCertificateTrust
{
    /// <summary>
    ///     Trusts <paramref name="certificate"/> for SSL in the current-user store backends
    ///     (login keychain on macOS, NSS db on Linux). Returns false when tools are missing or fail.
    /// </summary>
    public static bool TrustUserSsl(X509Certificate2 certificate, string friendlyName,
        IProcessRunner? runner = null)
    {
        runner ??= new ProcessRunner();
        var cerPath = WriteTempCer(certificate);
        try
        {
            if (RunTime.IsMac)
                return TrustMacUser(runner, cerPath);

            if (RunTime.IsLinux)
                return TrustLinuxNss(runner, cerPath, friendlyName);

            return false;
        }
        finally
        {
            TryDelete(cerPath);
        }
    }

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

    private static bool TrustLinuxNss(IProcessRunner runner, string cerPath, string friendlyName)
    {
        var nssDir = EnsureLinuxNssDb(runner);
        if (nssDir is null) return false;

        var result = runner.Run("certutil",
            $"-d sql:{nssDir} -A -t \"C,,\" -n \"{Escape(friendlyName)}\" -i \"{cerPath}\"");
        return result is { Succeeded: true };
    }

    private static bool UntrustLinuxNss(IProcessRunner runner, string friendlyName)
    {
        var nssDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pki", "nssdb");
        if (!Directory.Exists(nssDir)) return false;
        var result = runner.Run("certutil", $"-d sql:{nssDir} -D -n \"{Escape(friendlyName)}\"");
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
        var which = runner.Run("sh", "-c \"command -v certutil\"");
        if (which is not { Succeeded: true } || string.IsNullOrWhiteSpace(which.StandardOutput))
            return null;

        var nssDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pki", "nssdb");
        Directory.CreateDirectory(nssDir);
        if (!File.Exists(Path.Combine(nssDir, "cert9.db")) &&
            !File.Exists(Path.Combine(nssDir, "cert8.db")))
        {
            var init = runner.Run("certutil", $"-d sql:{nssDir} -N --empty-password");
            if (init is not { Succeeded: true }) return null;
        }

        return nssDir;
    }

    private static string WriteTempCer(X509Certificate2 certificate)
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
}
