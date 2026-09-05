using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.Services;

/// <summary>Shared user-facing copy for root CA trust — always current-OS only.</summary>
public static class OsTrustUxCopy
{
    public const string MacSslTrustWaitBody =
        "Keychain Get Info can already show Always Trust even when SSL policies were not saved " +
        "(Chrome and Inspector still treat the CA as untrusted).\n\n" +
        "Force a save:\n" +
        "1. Keychain Access → login → Certificates → double-click Titanium Root Certificate Authority\n" +
        "2. Expand Trust\n" +
        "3. Set When using this certificate to Use System Defaults, then change it to Always Trust again\n" +
        "4. Close Get Info — you must get a password prompt; that writes the real SSL trust policies\n\n" +
        "This window closes automatically when those policies are detected. " +
        "If you already saved, click I’ve saved Always Trust.";

    public const string MacSslTrustWaitStatusWaiting = "Waiting for the certificate in Keychain…";
    public const string MacSslTrustWaitStatusInKeychain =
        "Waiting for saved SSL policies (toggle Always Trust, close Get Info, enter password)…";

    public const string MacSslTrustNotSavedYet =
        "Always Trust display is not enough — toggle Use System Defaults → Always Trust, close Get Info, " +
        "enter your password, then try Install root CA / Decrypt HTTPS again";

    public const string MacSslTrustWaitConfirmSaved = "I’ve saved Always Trust";

    /// <summary>Install-root confirm body for the OS this process is running on.</summary>
    public static string ConfirmInstallRootCaBody()
    {
        if (OperatingSystem.IsMacOS())
        {
            return "Decrypt HTTPS requires trusting the Titanium Inspector root CA in Keychain Access (login keychain). Install now?";
        }

        if (OperatingSystem.IsLinux())
        {
            return "Decrypt HTTPS requires trusting the Titanium Inspector root CA in your user certificate store (NSS). Install now?";
        }

        if (OperatingSystem.IsWindows())
        {
            return "Decrypt HTTPS requires trusting the Titanium Inspector root CA in your current-user Trusted Root store. Install now?" +
                   "\n\nWindows may show a Trusted Root Yes/No security dialog (not UAC) — choose Yes to trust the CA.";
        }

        return "Decrypt HTTPS requires trusting the Titanium Inspector root CA on this computer. Install now?";
    }

    public static string ConfirmRemoveRootCaBody()
    {
        if (OperatingSystem.IsMacOS())
            return "Remove the Titanium Inspector root CA from Keychain? HTTPS decrypt will be turned off.";
        if (OperatingSystem.IsLinux())
            return "Remove the Titanium Inspector root CA from your user certificate store (NSS)? HTTPS decrypt will be turned off.";
        if (OperatingSystem.IsWindows())
            return "Remove the Titanium Inspector root CA from the current-user Trusted Root store? HTTPS decrypt will be turned off.";
        return "Remove the Titanium Inspector root CA? HTTPS decrypt will be turned off.";
    }

    public static string ConfirmElevateRootCaBody()
    {
        if (OperatingSystem.IsMacOS())
            return "User-level trust failed or was insufficient. Continue to show a macOS admin password prompt? Cancel leaves certificate settings unchanged.";
        if (OperatingSystem.IsLinux())
            return "User-level trust failed or was insufficient. Continue to show a polkit admin prompt? Cancel leaves certificate settings unchanged.";
        if (OperatingSystem.IsWindows())
            return "User-level trust failed or was insufficient. Continue to show UAC? Cancel leaves certificate settings unchanged.";
        return "User-level trust failed or was insufficient. Continue with an admin prompt? Cancel leaves certificate settings unchanged.";
    }

    public static string TrustRecoveryAdminBody(string message)
    {
        if (OperatingSystem.IsMacOS())
            return message + "\n\nContinue to show a macOS admin password prompt? Not now leaves certificate settings unchanged.";
        if (OperatingSystem.IsLinux())
            return message + "\n\nContinue to show a polkit admin prompt? Not now leaves certificate settings unchanged.";
        if (OperatingSystem.IsWindows())
            return message + "\n\nContinue to show UAC? Not now leaves certificate settings unchanged.";
        return message + "\n\nContinue with an admin prompt? Not now leaves certificate settings unchanged.";
    }

    public static string ExcludedHostsIntro()
    {
        if (OperatingSystem.IsMacOS())
            return "OS bypass needs Capture → System proxy (uses macOS network proxy settings). Tunnel-only rules apply to every client that hits Inspector. Factory defaults are seeded into the lists below — edit freely or reset.";
        if (OperatingSystem.IsLinux())
            return "OS bypass needs Capture → System proxy (uses desktop / environment proxy settings). Tunnel-only rules apply to every client that hits Inspector. Factory defaults are seeded into the lists below — edit freely or reset.";
        if (OperatingSystem.IsWindows())
            return "OS bypass needs Capture → System proxy (uses WinINET). Tunnel-only rules apply to every client that hits Inspector. Factory defaults are seeded into the lists below — edit freely or reset.";
        return "OS bypass needs Capture → System proxy. Tunnel-only rules apply to every client that hits Inspector. Factory defaults are seeded into the lists below — edit freely or reset.";
    }

    public static string ExcludedHostsLoopbackHint()
    {
        if (OperatingSystem.IsMacOS())
            return "When off, localhost is omitted from the macOS proxy bypass list so loopback can use the system proxy.";
        if (OperatingSystem.IsLinux())
            return "When off, localhost is omitted from NO_PROXY so loopback can use the system proxy.";
        if (OperatingSystem.IsWindows())
            return "When off, adds the Windows <-loopback> bypass rule so loopback skips the system proxy.";
        return "Controls whether localhost traffic uses the system proxy.";
    }

    public static string FormatStatus(CertificateOsTrustResult? result)
    {
        if (result is null)
            return "Root CA is not trusted yet — try again, or Export CA";

        return result.Kind switch
        {
            CertificateOsTrustKind.Cancelled =>
                "Root CA install cancelled",
            CertificateOsTrustKind.CertutilMissing =>
                "Browser certificate tools are missing — install them, try again, or Export CA",
            CertificateOsTrustKind.HomebrewMissing =>
                string.IsNullOrWhiteSpace(result.Message)
                    ? "Homebrew is required to install certificate tools — Export CA to trust manually"
                    : result.Message,
            CertificateOsTrustKind.MacNeedsManualTrustConfirm =>
                "Set Always Trust for the Titanium Inspector root CA in Keychain Access",
            CertificateOsTrustKind.MacKeychainFailed =>
                "Keychain trust failed — try again, or Export CA and trust it manually",
            _ => string.IsNullOrWhiteSpace(result.Message)
                ? "Root CA is not trusted yet — try again, or Export CA"
                : result.Message,
        };
    }

    public static (string Title, string Body, string Primary, string? Secondary, double Height)
        FormatDecryptTrustFailed(CertificateOsTrustResult? result)
    {
        var kind = result?.Kind ?? CertificateOsTrustKind.Failed;
        var detail = string.IsNullOrWhiteSpace(result?.Message)
            ? null
            : result!.Message.Trim();

        return kind switch
        {
            CertificateOsTrustKind.MacNeedsManualTrustConfirm => (
                "Confirm trust in Keychain",
                detail ?? MacSslTrustWaitBody,
                "Continue in Keychain Access",
                "Export CA",
                360),

            CertificateOsTrustKind.CertutilMissing => (
                "Certificate tools needed",
                detail ??
                (OperatingSystem.IsLinux()
                    ? "Inspector needs certutil (NSS tools) to finish trusting the root CA."
                    : "Inspector needs browser certificate tools to finish trusting the root CA."),
                "Try again",
                "Export CA",
                280),

            CertificateOsTrustKind.HomebrewMissing => (
                "Certificate tools needed",
                detail ??
                "Homebrew is required to install certificate tools. Export the CA to trust it manually.",
                "Export CA",
                null,
                260),

            _ => (
                "Can't decrypt HTTPS yet",
                detail ??
                "The Titanium Inspector root CA is not trusted on this computer yet.",
                "Try again",
                "Export CA",
                260),
        };
    }
}
