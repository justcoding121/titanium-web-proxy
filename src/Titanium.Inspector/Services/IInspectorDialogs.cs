using Avalonia.Controls;
using Titanium.Inspector.Views;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.Services;

/// <summary>UI prompts for CA trust / remove; injectable for headless tests.</summary>
public interface IInspectorDialogs
{
    /// <summary>Ask to install the Titanium root CA. Returns true if the user chose Install.</summary>
    Task<bool> ConfirmInstallRootCaAsync(Window? owner);

    /// <summary>Ask to remove the root CA from the current-user store. Returns true if confirmed.</summary>
    Task<bool> ConfirmRemoveRootCaAsync(Window? owner);

    /// <summary>Ask to retry CA install with an OS admin prompt. Returns true if confirmed.</summary>
    Task<bool> ConfirmElevateRootCaAsync(Window? owner);

    /// <summary>
    /// Adaptive recovery when user OS trust failed (certutil missing, Keychain confirm, elevate).
    /// </summary>
    Task<TrustRecoveryChoice> ShowTrustRecoveryAsync(Window? owner, CertificateOsTrustResult? result);

    /// <summary>
    /// macOS: wait while the user sets Always Trust; polls SSL verify until trusted or cancelled.
    /// </summary>
    Task<MacSslTrustWaitResult> ShowMacSslTrustWaitAsync(
        Window? owner,
        Func<bool> verifySslTrust,
        Action openKeychain,
        Func<bool>? isInLoginKeychain = null);

    /// <summary>
    /// Terminal failure after trust recovery: Try again / Export CA / Keychain confirm.
    /// </summary>
    Task<TrustRecoveryChoice> ShowDecryptTrustFailedAsync(Window? owner, CertificateOsTrustResult? result);

    /// <summary>
    /// Offer to start the proxy so Decrypt HTTPS can continue. Returns true if Start.
    /// </summary>
    Task<bool> ConfirmStartProxyForDecryptAsync(Window? owner);

    /// <summary>Ask to install root CA before Firefox trust. Returns true if Install.</summary>
    Task<bool> ConfirmInstallRootCaBeforeFirefoxAsync(Window? owner);

    /// <summary>Ask the user to quit Firefox so the profile DB can be updated.</summary>
    Task<bool> ConfirmQuitFirefoxForTrustAsync(Window? owner);

    /// <summary>
    /// Show device CA setup steps. Returns true if the user chose Export CA; false on Close / no owner.
    /// </summary>
    Task<bool> ShowDeviceCaSetupAsync(Window? owner, string message);

    /// <summary>Ask to clear and reinstall (regenerate) the Titanium root CA. Returns true if confirmed.</summary>
    Task<bool> ConfirmRotateRootCaAsync(Window? owner);

    /// <summary>
    /// Confirm resetting Inspector preferences to factory defaults (not the root CA or sessions).
    /// </summary>
    Task<bool> ConfirmResetSettingsAsync(Window? owner);

    /// <summary>
    /// Warn when enabling System proxy will replace an existing PAC script.
    /// </summary>
    Task<bool> ConfirmPacReplaceAsync(Window? owner);

    /// <summary>
    /// Confirm installing an Inspector update for the selected channel. Returns true if Install and restart.
    /// </summary>
    Task<bool> ConfirmInstallUpdateAsync(
        Window? owner,
        string version,
        string channelDisplay,
        UpdateOfferKind offerKind = UpdateOfferKind.Upgrade);
}

/// <summary>Avalonia modal dialogs.</summary>
public sealed class AvaloniaInspectorDialogs : IInspectorDialogs
{
    private const string CancelLabel = "Cancel";
    public Task<bool> ConfirmInstallRootCaAsync(Window? owner) =>
        SimpleConfirmDialog.ShowAsync(
            owner,
            "Install root CA",
            OsTrustUxCopy.ConfirmInstallRootCaBody(),
            accept: "Install",
            cancel: CancelLabel,
            height: OperatingSystem.IsWindows() ? 260 : 220);

    public Task<bool> ConfirmRemoveRootCaAsync(Window? owner) =>
        SimpleConfirmDialog.ShowAsync(
            owner,
            "Remove root CA",
            OsTrustUxCopy.ConfirmRemoveRootCaBody(),
            accept: "Remove",
            cancel: CancelLabel);

    public Task<bool> ConfirmElevateRootCaAsync(Window? owner) =>
        SimpleConfirmDialog.ShowAsync(
            owner,
            "Install with administrator privileges",
            OsTrustUxCopy.ConfirmElevateRootCaBody(),
            accept: "Continue",
            cancel: CancelLabel);

    public Task<TrustRecoveryChoice> ShowTrustRecoveryAsync(Window? owner, CertificateOsTrustResult? result)
    {
        var kind = result?.Kind ?? CertificateOsTrustKind.Failed;
        var message = result?.Message ?? "Root CA trust failed.";

        return kind switch
        {
            CertificateOsTrustKind.CertutilMissing when result is { BrewAvailable: true } =>
                TrustRecoveryDialog.ShowAsync(
                    owner,
                    "Install browser certificate tools",
                    message + "\n\nThis runs: brew install nss",
                    primary: "Install via Homebrew",
                    secondary: "Export CA",
                    height: 280),

            CertificateOsTrustKind.CertutilMissing =>
                TrustRecoveryDialog.ShowAsync(
                    owner,
                    "Install browser certificate tools",
                    message + (string.IsNullOrEmpty(result?.PackageHint)
                        ? ""
                        : $"\n\nPackage: {result!.PackageHint}"),
                    primary: "Install browser certificate tools",
                    secondary: "Export CA",
                    height: 280),

            CertificateOsTrustKind.HomebrewMissing =>
                TrustRecoveryDialog.ShowAsync(
                    owner,
                    "certutil not available",
                    message,
                    primary: "Export CA",
                    secondary: null,
                    height: 260),

            CertificateOsTrustKind.MacNeedsManualTrustConfirm =>
                TrustRecoveryDialog.ShowAsync(
                    owner,
                    "Confirm trust in Keychain Access",
                    OsTrustUxCopy.MacSslTrustWaitBody,
                    primary: "Open Keychain Access",
                    secondary: null,
                    height: 340),

            _ => TrustRecoveryDialog.ShowAsync(
                owner,
                "Install with administrator privileges",
                OsTrustUxCopy.TrustRecoveryAdminBody(message),
                primary: "Install with administrator",
                secondary: "Export CA",
                height: 280),
        };
    }

    public Task<MacSslTrustWaitResult> ShowMacSslTrustWaitAsync(
        Window? owner,
        Func<bool> verifySslTrust,
        Action openKeychain,
        Func<bool>? isInLoginKeychain = null) =>
        MacSslTrustWaitDialog.ShowAsync(owner, verifySslTrust, openKeychain, isInLoginKeychain);

    public Task<TrustRecoveryChoice> ShowDecryptTrustFailedAsync(
        Window? owner,
        CertificateOsTrustResult? result)
    {
        var (title, body, primary, secondary, height) = OsTrustUxCopy.FormatDecryptTrustFailed(result);
        return TrustRecoveryDialog.ShowAsync(owner, title, body, primary, secondary, height);
    }

    public Task<bool> ConfirmStartProxyForDecryptAsync(Window? owner) =>
        SimpleConfirmDialog.ShowAsync(
            owner,
            "Start the proxy?",
            "Decrypt HTTPS needs the proxy running so Inspector can install and verify the root CA. Start now?",
            accept: "Start proxy",
            cancel: CancelLabel,
            height: 220);

    public Task<bool> ConfirmInstallRootCaBeforeFirefoxAsync(Window? owner) =>
        SimpleConfirmDialog.ShowAsync(
            owner,
            "Install root CA first",
            "Firefox trust needs the Titanium Inspector root CA installed on this PC first. Install the root CA now?",
            accept: "Install",
            cancel: CancelLabel);

    public Task<bool> ConfirmQuitFirefoxForTrustAsync(Window? owner) =>
        SimpleConfirmDialog.ShowAsync(
            owner,
            "Quit Firefox",
            "Firefox appears to be running and may lock its certificate database.\n\n" +
            "Inspector can ask Firefox to quit gracefully (unsaved tabs may prompt inside Firefox). " +
            "It will not force-kill the process.",
            accept: "Quit Firefox and retry",
            cancel: CancelLabel,
            height: 260);

    public Task<bool> ShowDeviceCaSetupAsync(Window? owner, string message) =>
        SimpleConfirmDialog.ShowAsync(
            owner,
            "Device CA setup",
            message,
            accept: "Export CA",
            cancel: "Close",
            height: 320);

    public Task<bool> ConfirmRotateRootCaAsync(Window? owner) =>
        SimpleConfirmDialog.ShowAsync(
            owner,
            "Clear and reinstall root CA",
            "Clear the current Titanium Inspector root CA and create a new one?\n\n" +
            "• All same-name Titanium roots are removed from the current-user Trusted Root store\n" +
            "• Cached site certificates for this install are cleared\n" +
            "• You will be asked to trust the new root CA again (or enable Decrypt HTTPS)\n\n" +
            "Stop capture is recommended first.",
            accept: "Clear and reinstall",
            cancel: CancelLabel,
            height: 320);

    public Task<bool> ConfirmResetSettingsAsync(Window? owner) =>
        SimpleConfirmDialog.ShowAsync(
            owner,
            "Reset Inspector settings",
            "Restore bind address, menus, Tools, retention, logging, exclusion host lists (bypass and tunnel-only), and layout to factory defaults?\n\n" +
            "This does not remove the root CA, change OS proxy or Store loopback exemptions, clear captured sessions, or delete the on-disk body cache. Restart Inspector afterward so retention limits fully apply.",
            accept: "Reset settings",
            cancel: CancelLabel,
            height: 320);

    public Task<bool> ConfirmPacReplaceAsync(Window? owner) =>
        SimpleConfirmDialog.ShowAsync(
            owner,
            "Replace PAC script?",
            "Inspector will set itself as the system proxy and replace any PAC script. Your existing bypass list will be preserved and merged. Disabling System proxy restores previous settings.",
            accept: "Enable system proxy",
            cancel: CancelLabel,
            height: 280);

    public Task<bool> ConfirmInstallUpdateAsync(
        Window? owner,
        string version,
        string channelDisplay,
        UpdateOfferKind offerKind = UpdateOfferKind.Upgrade)
    {
        var (title, body, accept) = offerKind switch
        {
            UpdateOfferKind.Downgrade => (
                "Install older release",
                $"Install older {channelDisplay} {version}? Your current build is newer and will be replaced.\n\n" +
                "Inspector will close, replace the current installation, and relaunch.",
                "Install and restart"),
            UpdateOfferKind.ChannelSwitch => (
                "Switch update channel",
                $"Switch to {channelDisplay} {version}? This replaces your current build.\n\n" +
                "Inspector will close, replace the current installation, and relaunch.",
                "Switch and restart"),
            _ => (
                "Update available",
                $"Version {version} ({channelDisplay}) is available.\n\n" +
                "Inspector will close, replace the current installation, and relaunch.",
                "Update and restart"),
        };

        return SimpleConfirmDialog.ShowAsync(
            owner,
            title,
            body,
            accept: accept,
            cancel: "Later",
            height: 240);
    }
}

/// <summary>Scripted answers for unit / E2E-UI tests (no real windows).</summary>
public sealed class ScriptedInspectorDialogs : IInspectorDialogs
{
    public bool InstallRootCaResult { get; set; } = true;
    public bool RemoveRootCaResult { get; set; } = true;
    public bool ElevateRootCaResult { get; set; } = true;
    public TrustRecoveryChoice TrustRecoveryResult { get; set; } = TrustRecoveryChoice.Primary;
    public MacSslTrustWaitResult MacSslTrustWaitResult { get; set; } = MacSslTrustWaitResult.Trusted;
    public bool InstallRootCaBeforeFirefoxResult { get; set; } = true;
    public bool QuitFirefoxForTrustResult { get; set; } = true;
    public bool DeviceCaSetupResult { get; set; }
    public bool ResetSettingsResult { get; set; } = true;
    public bool PacReplaceResult { get; set; } = true;
    public bool RotateRootCaResult { get; set; } = true;
    public bool InstallUpdateResult { get; set; } = true;
    public TrustRecoveryChoice DecryptTrustFailedResult { get; set; } = TrustRecoveryChoice.Cancel;
    public bool StartProxyForDecryptResult { get; set; }
    public int InstallRootCaCalls { get; private set; }
    public int RemoveRootCaCalls { get; private set; }
    public int ElevateRootCaCalls { get; private set; }
    public int TrustRecoveryCalls { get; private set; }
    public int MacSslTrustWaitCalls { get; private set; }
    public int DecryptTrustFailedCalls { get; private set; }
    public int StartProxyForDecryptCalls { get; private set; }
    public int InstallRootCaBeforeFirefoxCalls { get; private set; }
    public int QuitFirefoxForTrustCalls { get; private set; }
    public int DeviceCaSetupCalls { get; private set; }
    public int ResetSettingsCalls { get; private set; }
    public int RotateRootCaCalls { get; private set; }
    public int InstallUpdateCalls { get; private set; }
    public string? LastDeviceCaSetupMessage { get; private set; }
    public string? LastInstallUpdateVersion { get; private set; }
    public string? LastInstallUpdateChannel { get; private set; }
    public CertificateOsTrustResult? LastTrustRecoveryResult { get; private set; }
    public CertificateOsTrustResult? LastDecryptTrustFailedResult { get; private set; }

    public Task<bool> ConfirmInstallRootCaAsync(Window? owner)
    {
        InstallRootCaCalls++;
        return Task.FromResult(InstallRootCaResult);
    }

    public Task<bool> ConfirmRemoveRootCaAsync(Window? owner)
    {
        RemoveRootCaCalls++;
        return Task.FromResult(RemoveRootCaResult);
    }

    public Task<bool> ConfirmElevateRootCaAsync(Window? owner)
    {
        ElevateRootCaCalls++;
        return Task.FromResult(ElevateRootCaResult);
    }

    public Task<TrustRecoveryChoice> ShowTrustRecoveryAsync(Window? owner, CertificateOsTrustResult? result)
    {
        TrustRecoveryCalls++;
        LastTrustRecoveryResult = result;
        return Task.FromResult(TrustRecoveryResult);
    }

    public Task<MacSslTrustWaitResult> ShowMacSslTrustWaitAsync(
        Window? owner,
        Func<bool> verifySslTrust,
        Action openKeychain,
        Func<bool>? isInLoginKeychain = null)
    {
        MacSslTrustWaitCalls++;
        try
        {
            openKeychain();
        }
        catch
        {
            // ignore in tests
        }

        if (MacSslTrustWaitResult == MacSslTrustWaitResult.Trusted)
        {
            try
            {
                // Allow scripted verify to update interception state when tests wire a real callback.
                _ = verifySslTrust();
            }
            catch
            {
                // ignore
            }
        }

        return Task.FromResult(MacSslTrustWaitResult);
    }

    public Task<TrustRecoveryChoice> ShowDecryptTrustFailedAsync(
        Window? owner,
        CertificateOsTrustResult? result)
    {
        DecryptTrustFailedCalls++;
        LastDecryptTrustFailedResult = result;
        return Task.FromResult(DecryptTrustFailedResult);
    }

    public Task<bool> ConfirmStartProxyForDecryptAsync(Window? owner)
    {
        StartProxyForDecryptCalls++;
        return Task.FromResult(StartProxyForDecryptResult);
    }

    public Task<bool> ConfirmInstallRootCaBeforeFirefoxAsync(Window? owner)
    {
        InstallRootCaBeforeFirefoxCalls++;
        return Task.FromResult(InstallRootCaBeforeFirefoxResult);
    }

    public Task<bool> ConfirmQuitFirefoxForTrustAsync(Window? owner)
    {
        QuitFirefoxForTrustCalls++;
        return Task.FromResult(QuitFirefoxForTrustResult);
    }

    public Task<bool> ConfirmRotateRootCaAsync(Window? owner)
    {
        RotateRootCaCalls++;
        return Task.FromResult(RotateRootCaResult);
    }

    public Task<bool> ShowDeviceCaSetupAsync(Window? owner, string message)
    {
        DeviceCaSetupCalls++;
        LastDeviceCaSetupMessage = message;
        return Task.FromResult(DeviceCaSetupResult);
    }

    public Task<bool> ConfirmResetSettingsAsync(Window? owner)
    {
        ResetSettingsCalls++;
        return Task.FromResult(ResetSettingsResult);
    }

    public Task<bool> ConfirmPacReplaceAsync(Window? owner) =>
        Task.FromResult(PacReplaceResult);

    public Task<bool> ConfirmInstallUpdateAsync(
        Window? owner,
        string version,
        string channelDisplay,
        UpdateOfferKind offerKind = UpdateOfferKind.Upgrade)
    {
        InstallUpdateCalls++;
        LastInstallUpdateVersion = version;
        LastInstallUpdateChannel = channelDisplay;
        LastInstallUpdateOfferKind = offerKind;
        return Task.FromResult(InstallUpdateResult);
    }

    public UpdateOfferKind LastInstallUpdateOfferKind { get; private set; }
}
