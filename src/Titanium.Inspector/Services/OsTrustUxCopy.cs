using Titanium.Inspector.Localization;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.Services;

/// <summary>Shared user-facing copy for root CA trust — always current-OS only.</summary>
public static class OsTrustUxCopy
{
    public static string MacSslTrustWaitBody => LanguageService.Get("trust.macWaitBody");

    public static string MacSslTrustWaitStatusWaiting => LanguageService.Get("trust.macWaitStatus");

    public static string MacSslTrustWaitStatusInKeychain => LanguageService.Get("trust.macWaitInKeychain");

    public static string MacSslTrustNotSavedYet => LanguageService.Get("trust.macNotSaved");

    public static string MacSslTrustWaitConfirmSaved => LanguageService.Get("trust.macConfirmSaved");

    private static string ExportCaLabel => LanguageService.Get("trust.exportCa");

    /// <summary>Install-root confirm body for the OS this process is running on.</summary>
    public static string ConfirmInstallRootCaBody()
    {
        if (OperatingSystem.IsMacOS())
        {
            return LanguageService.Get("trust.install.mac");
        }

        if (OperatingSystem.IsLinux())
        {
            return LanguageService.Get("trust.install.linux");
        }

        if (OperatingSystem.IsWindows())
        {
            return LanguageService.Get("trust.install.windows");
        }

        return LanguageService.Get("trust.install.other");
    }

    public static string ConfirmRemoveRootCaBody()
    {
        if (OperatingSystem.IsMacOS())
            return LanguageService.Get("trust.remove.mac");
        if (OperatingSystem.IsLinux())
            return LanguageService.Get("trust.remove.linux");
        if (OperatingSystem.IsWindows())
            return LanguageService.Get("trust.remove.windows");
        return LanguageService.Get("trust.remove.other");
    }

    public static string ConfirmElevateRootCaBody()
    {
        if (OperatingSystem.IsMacOS())
            return LanguageService.Get("trust.elevate.mac");
        if (OperatingSystem.IsLinux())
            return LanguageService.Get("trust.elevate.linux");
        if (OperatingSystem.IsWindows())
            return LanguageService.Get("trust.elevate.windows");
        return LanguageService.Get("trust.elevate.other");
    }

    public static string TrustRecoveryAdminBody(string message)
    {
        if (OperatingSystem.IsMacOS())
            return message + LanguageService.Get("trust.recovery.mac");
        if (OperatingSystem.IsLinux())
            return message + LanguageService.Get("trust.recovery.linux");
        if (OperatingSystem.IsWindows())
            return message + LanguageService.Get("trust.recovery.windows");
        return message + LanguageService.Get("trust.recovery.other");
    }

    public static string ExcludedHostsIntro()
    {
        if (OperatingSystem.IsMacOS())
            return LanguageService.Get("trust.hosts.mac");
        if (OperatingSystem.IsLinux())
            return LanguageService.Get("trust.hosts.linux");
        if (OperatingSystem.IsWindows())
            return LanguageService.Get("trust.hosts.windows");
        return LanguageService.Get("trust.hosts.other");
    }

    public static string ProxyLocalhostTip()
    {
        if (OperatingSystem.IsMacOS())
            return LanguageService.Get("trust.localhost.mac");
        if (OperatingSystem.IsLinux())
            return LanguageService.Get("trust.localhost.linux");
        if (OperatingSystem.IsWindows())
            return LanguageService.Get("trust.localhost.windows");
        return LanguageService.Get("trust.localhost.other");
    }

    public static string FormatStatus(CertificateOsTrustResult? result)
    {
        if (result is null)
            return LanguageService.Get("trust.status.missing");

        return result.Kind switch
        {
            CertificateOsTrustKind.Cancelled =>
                LanguageService.Get("trust.status.cancelled"),
            CertificateOsTrustKind.CertutilMissing =>
                LanguageService.Get("trust.status.certutil"),
            CertificateOsTrustKind.HomebrewMissing =>
                string.IsNullOrWhiteSpace(result.Message)
                    ? LanguageService.Get("trust.status.homebrew")
                    : result.Message,
            CertificateOsTrustKind.MacNeedsManualTrustConfirm =>
                LanguageService.Get("trust.status.macConfirm"),
            CertificateOsTrustKind.MacKeychainFailed =>
                LanguageService.Get("trust.status.keychain"),
            _ => string.IsNullOrWhiteSpace(result.Message)
                ? LanguageService.Get("trust.status.missing")
                : result.Message,
        };
    }

    public static (string Title, string Body, string Primary, string? Secondary, double Height)
        FormatDecryptTrustFailed(CertificateOsTrustResult? result)
    {
        var kind = result?.Kind ?? CertificateOsTrustKind.Failed;
        var detail = string.IsNullOrWhiteSpace(result?.Message)
            ? null
            : result.Message.Trim();

        return kind switch
        {
            CertificateOsTrustKind.MacNeedsManualTrustConfirm => (
                LanguageService.Get("trust.fail.keychainTitle"),
                detail ?? MacSslTrustWaitBody,
                LanguageService.Get("trust.fail.continueKeychain"),
                ExportCaLabel,
                360),

            CertificateOsTrustKind.CertutilMissing => (
                LanguageService.Get("trust.fail.toolsTitle"),
                detail ??
                (OperatingSystem.IsLinux()
                    ? LanguageService.Get("trust.fail.toolsLinux")
                    : LanguageService.Get("trust.fail.toolsOther")),
                LanguageService.Get("trust.fail.tryAgain"),
                ExportCaLabel,
                280),

            CertificateOsTrustKind.HomebrewMissing => (
                LanguageService.Get("trust.fail.toolsTitle"),
                detail ?? LanguageService.Get("trust.fail.homebrewBody"),
                ExportCaLabel,
                null,
                260),

            _ => (
                LanguageService.Get("trust.fail.title"),
                detail ?? LanguageService.Get("trust.fail.body"),
                LanguageService.Get("trust.fail.tryAgain"),
                ExportCaLabel,
                260),
        };
    }
}
