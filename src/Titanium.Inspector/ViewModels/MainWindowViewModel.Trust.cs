using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Titanium.Inspector.Services;
using Titanium.Inspector.Views;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.ViewModels;

public sealed partial class MainWindowViewModel
{
    private async Task InstallCaAsync()
    {
        if (!_interception.IsRunning)
        {
            SetGuardStatus(StartProxyFirstStatus);
            return;
        }

        SetBusyTrustingRootCa();
        var ok = await EnsureRootCaTrustedAsync(promptIfNeeded: true);
        if (ok)
        {
            SetOsTrustSuccessStatus();
            return;
        }

        if (_interception.LastOsTrustResult?.Kind == CertificateOsTrustKind.Cancelled ||
            string.IsNullOrEmpty(_interception.LastOsTrustResult?.Message))
        {
            SetGuardStatus("Root CA install cancelled");
            return;
        }

        if (await ResolveTerminalTrustFailureAsync(_interception.LastOsTrustResult))
            SetOsTrustSuccessStatus();
        else if (_interception.LastOsTrustResult?.Kind == CertificateOsTrustKind.Cancelled)
            SetGuardStatus("Root CA install cancelled");
        else
            SetOutcomeStatus(
                OsTrustUxCopy.FormatStatus(_interception.LastOsTrustResult),
                StatusSeverity.Error,
                toastImportant: true);
    }
    private async Task TrustFirefoxCaAsync()
    {
        if (!_interception.IsRunning)
        {
            SetGuardStatus(StartProxyFirstStatus);
            return;
        }

        var owner = TryGetMainWindow();
        if (!await TryEnsureRootBeforeFirefoxAsync(owner))
            return;

        if (!FirefoxCertificateTrust.IsFirefoxProfilePresent())
        {
            SetOutcomeStatus(
                "Firefox profile not found — open Firefox once to create a profile " +
                "(classic, Snap, or Flatpak), or use Export CA → Firefox Authorities",
                StatusSeverity.Warning,
                toastImportant: true);
            return;
        }

        SetStatus("Updating Firefox trust…", StatusSeverity.Busy);
        var result = await TrustFirefoxWithRecoveryAsync(owner);
        SetOutcomeStatus(
            FormatFirefoxTrustOutcome(result),
            result.Succeeded ? StatusSeverity.Success : StatusSeverity.Error,
            toastImportant: true);
    }
    private async Task<bool> TryEnsureRootBeforeFirefoxAsync(Window? owner)
    {
        if (_interception.IsRootTrusted)
            return true;

        if (!await AwaitCancellableAsync(_dialogs.ConfirmInstallRootCaBeforeFirefoxAsync(owner)))
        {
            SetGuardStatus("Trust CA in Firefox cancelled — install root CA first");
            return false;
        }

        SetBusyTrustingRootCa();
        if (await EnsureRootCaTrustedAsync(promptIfNeeded: true))
            return true;

        SetOutcomeStatus(
            FormatOsTrustFailureStatus(_interception.LastOsTrustResult),
            StatusSeverity.Error,
            toastImportant: true);
        return false;
    }
    private static string FormatFirefoxTrustOutcome(CertificateOsTrustResult result)
    {
        if (result.Succeeded)
            return result.Message;
        if (result.Kind is CertificateOsTrustKind.CertutilMissing or CertificateOsTrustKind.HomebrewMissing)
            return result.Message + " — try Export CA";
        return result.Message;
    }
    private async Task<CertificateOsTrustResult> TrustFirefoxWithRecoveryAsync(Window? owner)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = _interception.TrustFirefox();
            if (result.Succeeded)
                return result;

            if (result.Kind is CertificateOsTrustKind.CertutilMissing or CertificateOsTrustKind.HomebrewMissing)
            {
                var recovered = await TryRecoverFirefoxCertutilAsync(owner, result);
                if (recovered)
                    continue;
                return result;
            }

            if (IsFirefoxRunningTrustError(result))
            {
                var quitOk = await TryQuitFirefoxForTrustAsync(owner);
                if (quitOk is null)
                    continue;
                return quitOk;
            }

            return result;
        }

        return CertificateOsTrustResult.Fail(
            CertificateOsTrustKind.Failed, "Firefox trust failed after retries");
    }

    private async Task<bool> TryRecoverFirefoxCertutilAsync(Window? owner, CertificateOsTrustResult result)
    {
        var choice = await AwaitCancellableAsync(_dialogs.ShowTrustRecoveryAsync(owner, result));
        if (choice == TrustRecoveryChoice.Primary &&
            result.Kind == CertificateOsTrustKind.CertutilMissing &&
            (OperatingSystem.IsLinux() || result.BrewAvailable))
        {
            SetStatus("Installing browser certificate tools…", StatusSeverity.Busy);
            _ = _interception.InstallNssToolsAndRetryTrust();
            return true;
        }

        if (choice == TrustRecoveryChoice.Secondary ||
            (choice == TrustRecoveryChoice.Primary && result.Kind == CertificateOsTrustKind.HomebrewMissing))
        {
            await ExportCaAsync();
        }

        return false;
    }

    private static bool IsFirefoxRunningTrustError(CertificateOsTrustResult result) =>
        result.Message.Contains("Quit Firefox", StringComparison.OrdinalIgnoreCase) ||
        result.Message.Contains("running", StringComparison.OrdinalIgnoreCase);

    private async Task<CertificateOsTrustResult?> TryQuitFirefoxForTrustAsync(Window? owner)
    {
        if (!await AwaitCancellableAsync(_dialogs.ConfirmQuitFirefoxForTrustAsync(owner)))
            return CertificateOsTrustResult.Fail(CertificateOsTrustKind.Cancelled, "Firefox trust cancelled");

        SetStatus("Quitting Firefox…", StatusSeverity.Busy);
        if (!FirefoxCertificateTrust.TryRequestFirefoxQuit())
        {
            return CertificateOsTrustResult.Fail(
                CertificateOsTrustKind.Failed,
                "Firefox is still running — close it fully, then retry Trust CA in Firefox");
        }

        SetStatus("Updating Firefox trust…", StatusSeverity.Busy);
        return null;
    }
    /// <summary>
    /// Attempts user OS trust and adaptive recovery (certutil install / Keychain / elevate).
    /// </summary>
    private async Task<bool> EnsureRootCaTrustedAsync(bool promptIfNeeded)
    {
        var owner = TryGetMainWindow();
        var ok = _interception.InstallRootCertificate(machineStore: false);
        var result = _interception.LastOsTrustResult;

        if (ok && result?.Kind != CertificateOsTrustKind.MacNeedsManualTrustConfirm)
            return true;

        if (!promptIfNeeded)
            return ok;

        // Adaptive recovery loop (certutil / Keychain / elevate).
        for (var i = 0; i < 4; i++)
        {
            result = _interception.LastOsTrustResult;
            if (_interception.IsRootTrusted &&
                result?.Kind != CertificateOsTrustKind.MacNeedsManualTrustConfirm)
                return true;

            if (result?.Kind == CertificateOsTrustKind.MacNeedsManualTrustConfirm)
                return await TryCompleteMacManualTrustAsync(owner);

            if (!ok)
            {
                var recovered = await TryRecoverFailedOsTrustAsync(owner, result);
                if (recovered == true)
                    return true;
                if (recovered == false)
                    return false;
                ok = _interception.IsRootTrusted ||
                     _interception.LastOsTrustResult?.Kind == CertificateOsTrustKind.MacNeedsManualTrustConfirm;
                continue;
            }

            break;
        }

        return _interception.IsRootTrusted;
    }

    private async Task<bool> TryCompleteMacManualTrustAsync(Window? owner)
    {
        var wait = await WaitForMacSslTrustAsync(owner);
        if (wait == MacSslTrustWaitResult.Trusted || _interception.VerifyOsUserSslTrust())
            return true;

        _interception.SetLastOsTrustCancelled();
        if (wait == MacSslTrustWaitResult.NotSavedYet || _interception.IsRootInLoginKeychain())
            SetGuardStatus(OsTrustUxCopy.MacSslTrustNotSavedYet);
        return false;
    }

    private async Task<bool?> TryRecoverFailedOsTrustAsync(Window? owner, CertificateOsTrustResult? result)
    {
        var choice = await AwaitCancellableAsync(_dialogs.ShowTrustRecoveryAsync(owner, result));
        if (choice == TrustRecoveryChoice.Cancel)
        {
            _interception.SetLastOsTrustCancelled();
            return false;
        }

        if (result?.Kind == CertificateOsTrustKind.CertutilMissing &&
            (result.BrewAvailable || OperatingSystem.IsLinux()))
        {
            return await TryRecoverCertutilMissingAsync(choice);
        }

        if (result?.Kind == CertificateOsTrustKind.HomebrewMissing)
        {
            if (choice == TrustRecoveryChoice.Primary)
                await ExportCaAsync();
            return false;
        }

        if (choice == TrustRecoveryChoice.Primary)
        {
            SetStatus("Trusting root CA (administrator)…", StatusSeverity.Busy);
            var ok = _interception.InstallRootCertificateAsAdmin(machineStore: false);
            if (ok && _interception.LastOsTrustResult?.Kind !=
                CertificateOsTrustKind.MacNeedsManualTrustConfirm)
                return true;
            return null;
        }

        if (choice == TrustRecoveryChoice.Secondary)
            await ExportCaAsync();
        return false;
    }

    private async Task<bool?> TryRecoverCertutilMissingAsync(TrustRecoveryChoice choice)
    {
        if (choice == TrustRecoveryChoice.Primary)
        {
            SetStatus("Installing browser certificate tools…", StatusSeverity.Busy);
            var install = _interception.InstallNssToolsAndRetryTrust();
            if (install.Succeeded)
                return true;
            if (install.Kind == CertificateOsTrustKind.MacNeedsManualTrustConfirm)
                return null;

            SetOutcomeStatus(install.Message, StatusSeverity.Error);
            return null;
        }

        if (choice == TrustRecoveryChoice.Secondary)
            await ExportCaAsync();
        return false;
    }

    private Task<MacSslTrustWaitResult> WaitForMacSslTrustAsync(Window? owner)
    {
        // Waiting UX is entirely in the modal — clear main-window Busy so the status bar
        // spinner / "Trusting root CA…" does not compete with the dialog status line.
        if (IsStatusBusy)
        {
            SetSteadyStatus(
                _interception.IsRunning
                    ? $"Proxy running on {FormatBindDisplay()}:{BindPort}"
                    : StatusReady);
        }

        return AwaitCancellableAsync(_dialogs.ShowMacSslTrustWaitAsync(
            owner,
            () => _interception.VerifyOsUserSslTrust(),
            () => _interception.OpenMacKeychainGuidance(),
            () => _interception.IsRootInLoginKeychain()));
    }
    private void SetOsTrustSuccessStatus()
    {
        var msg = "Root CA trusted — ready to decrypt HTTPS";
        if (!_firefoxTrustHintShown && InterceptionService.IsFirefoxProfilePresent)
        {
            _firefoxTrustHintShown = true;
            msg += " · Restart Firefox to use OS-root trust (or Capture → Trust CA in Firefox…)";
        }

        SetOutcomeStatus(msg, StatusSeverity.Success, toastImportant: true);
    }
    private static string FormatOsTrustFailureStatus(CertificateOsTrustResult? result) =>
        OsTrustUxCopy.FormatStatus(result);
    private void SetBusyTrustingRootCa() =>
        SetStatus(
            OperatingSystem.IsWindows() ? TrustingRootCaWindowsStatus : TrustingRootCaStatus,
            StatusSeverity.Busy);
    private static string FormatUntrustStillPresentStatus()
    {
        if (OperatingSystem.IsMacOS())
            return "Remove incomplete — Titanium CA still in Keychain (approve the admin password prompt to clear System.keychain)";
        if (OperatingSystem.IsLinux())
            return "Remove requested but CA still present in the certificate store";
        return "Remove requested but CA still present in store";
    }
    private static string FormatUntrustRemovedStatus()
    {
        if (OperatingSystem.IsMacOS())
            return "Root CA removed from Keychain; Decrypt HTTPS is off until you install the CA again";
        if (OperatingSystem.IsLinux())
            return "Root CA removed from the user certificate store; Decrypt HTTPS is off until you install the CA again";
        return "Root CA removed from current user store; Decrypt HTTPS is off until you install the CA again";
    }
    private static string FormatRotateCaTrustedStatus(bool changed) =>
        changed
            ? "Root CA cleared and trusted — ready to enable Decrypt HTTPS"
            : "Root CA trusted — ready to enable Decrypt HTTPS";
    private async Task UntrustCaAsync()
    {
        if (!_interception.IsRunning)
        {
            SetGuardStatus(StartProxyFirstStatus);
            return;
        }

        var owner = TryGetMainWindow();
        if (!await AwaitCancellableAsync(_dialogs.ConfirmRemoveRootCaAsync(owner)))
        {
            SetTransientStatus("Remove root CA cancelled", StatusSeverity.Neutral, revertMs: GuardStatusRevertMs);
            return;
        }

        _interception.UntrustRootCertificate(machineStore: false);
        if (DecryptHttps)
        {
            SetDecryptHttpsCore(false);
        }

        var stillPresent = _interception.IsRootTrusted;
        string message = stillPresent
            ? FormatUntrustStillPresentStatus()
            : FormatUntrustRemovedStatus();

        SetOutcomeStatus(
            message,
            stillPresent ? StatusSeverity.Warning : StatusSeverity.Success,
            toastImportant: true);
    }
    private async Task RotateCaAsync()
    {
        if (!_interception.IsRunning)
        {
            SetGuardStatus(StartProxyFirstStatus);
            return;
        }

        var owner = TryGetMainWindow();
        if (!await AwaitCancellableAsync(_dialogs.ConfirmRotateRootCaAsync(owner)))
        {
            SetTransientStatus("Clear and reinstall root CA cancelled", StatusSeverity.Neutral, revertMs: GuardStatusRevertMs);
            return;
        }

        if (DecryptHttps)
            SetDecryptHttpsCore(false);

        var oldThumb = _interception.RootCertificate?.Thumbprint;
        var ok = _interception.RotateRootCertificate(machineStore: false);
        if (!ok)
        {
            SetOutcomeStatus("Clear and reinstall root CA failed — see logs", StatusSeverity.Error, toastImportant: true);
            return;
        }

        var newThumb = _interception.RootCertificate?.Thumbprint;
        var changed = !string.IsNullOrEmpty(newThumb) &&
                      !string.Equals(oldThumb, newThumb, StringComparison.OrdinalIgnoreCase);

        if (await AwaitCancellableAsync(_dialogs.ConfirmInstallRootCaAsync(owner)))
        {
            SetBusyTrustingRootCa();
            var trusted = await EnsureRootCaTrustedAsync(promptIfNeeded: true);
            var message = trusted
                ? FormatRotateCaTrustedStatus(changed)
                : FormatOsTrustFailureStatus(_interception.LastOsTrustResult);
            if (trusted)
                SetOsTrustSuccessStatus();
            else
                SetOutcomeStatus(message, StatusSeverity.Error, toastImportant: true);
            return;
        }

        SetOutcomeStatus(FormatRotateCaDeferredTrustStatus(changed), StatusSeverity.Warning, toastImportant: true);
    }
    private static string FormatRotateCaDeferredTrustStatus(bool changed) =>
        changed ? "Root CA cleared — Install root CA (or enable Decrypt HTTPS) to trust the new certificate" : "Root CA recreate completed — Install root CA to trust";
    private async Task ExportCaAsync()
    {
        if (_interception.RootCertificate is null)
        {
            SetGuardStatus("No root certificate yet — Start the proxy first");
            return;
        }

        var path = await _pathPicker.PickSavePathAsync(
            "Export root CA",
            "TitaniumInspector-RootCA.cer",
            [
                new PathPickerFileType("Certificate", "*.cer"),
                new PathPickerFileType("PEM", "*.pem"),
            ]);
        if (path is null)
        {
            SetTransientStatus("Export CA cancelled", StatusSeverity.Neutral, revertMs: GuardStatusRevertMs);
            return;
        }

        try
        {
            var exported = _interception.ExportRootCertificate(path);
            if (exported is null)
            {
                SetGuardStatus("No root certificate yet — Start the proxy first");
                return;
            }

            SetOutcomeStatus("Exported CA: " + exported, StatusSeverity.Success, toastImportant: true);
        }
        catch (Exception ex)
        {
            SetOutcomeStatus("Export CA failed: " + Truncate(ex.Message, 160), StatusSeverity.Error, toastImportant: true);
        }
    }
    private async Task DeviceCaSetupAsync()
    {
        var message =
            "To decrypt HTTPS from a phone or other device:\n\n" +
            "1. Export the root CA (use Export CA below, or Capture → Export root CA…).\n" +
            "2. Install the exported .cer (or .pem) on the device as a trusted CA.\n" +
            $"3. Set the device HTTP proxy to this PC's LAN IP on port {BindPort} " +
            $"(current bind is {BindAddress}:{BindPort}).\n\n" +
            "Use Bind address 0.0.0.0 so other devices can reach the proxy.";

        var owner = TryGetMainWindow();
        if (await AwaitCancellableAsync(_dialogs.ShowDeviceCaSetupAsync(owner, message)))
        {
            await ExportCaAsync();
        }
    }
    private async Task EnableDecryptHttpsAsync()
    {
        _decryptHttpsBusy = true;
        try
        {
            if (!await TryStartProxyForDecryptAsync())
                return;
            if (!await TryTrustRootForDecryptAsync())
                return;
            if (!await TryCompleteMacSslTrustForDecryptAsync())
                return;

            SetDecryptHttpsCore(true);
            SetOutcomeStatus("Decrypting HTTPS", StatusSeverity.Success, toastImportant: true);
        }
        finally
        {
            _decryptHttpsBusy = false;
        }
    }
    private void NotifyDecryptHttpsUnchanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DecryptHttps)));
    private async Task<bool> TryStartProxyForDecryptAsync()
    {
        if (_interception.IsRunning)
            return true;

        var owner = TryGetMainWindow();
        if (!await AwaitCancellableAsync(_dialogs.ConfirmStartProxyForDecryptAsync(owner)))
        {
            SetGuardStatus("Decrypt HTTPS cancelled — start the proxy first");
            NotifyDecryptHttpsUnchanged();
            return false;
        }

        await StartCaptureAsync();
        if (_interception.IsRunning)
            return true;

        SetOutcomeStatus(
            "Could not start the proxy — Decrypt HTTPS stays off",
            StatusSeverity.Error,
            toastImportant: true);
        NotifyDecryptHttpsUnchanged();
        return false;
    }
    private async Task<bool> TryTrustRootForDecryptAsync()
    {
        _interception.RefreshTrustState();
        if (_interception.IsRootTrusted)
            return true;

        var owner = TryGetMainWindow();
        if (!await AwaitCancellableAsync(_dialogs.ConfirmInstallRootCaAsync(owner)))
        {
            SetGuardStatus("Decrypt HTTPS cancelled — root CA not installed");
            NotifyDecryptHttpsUnchanged();
            return false;
        }

        SetBusyTrustingRootCa();
        if (await EnsureRootCaTrustedAsync(promptIfNeeded: true))
            return true;

        if (_interception.LastOsTrustResult?.Kind == CertificateOsTrustKind.Cancelled)
        {
            SetGuardStatus("Decrypt HTTPS cancelled — root CA not trusted");
            NotifyDecryptHttpsUnchanged();
            return false;
        }

        if (await ResolveTerminalTrustFailureAsync(_interception.LastOsTrustResult))
            return true;

        if (_interception.LastOsTrustResult?.Kind == CertificateOsTrustKind.Cancelled)
            SetGuardStatus("Decrypt HTTPS cancelled — root CA not trusted");
        else
            SetOutcomeStatus(
                OsTrustUxCopy.FormatStatus(_interception.LastOsTrustResult),
                StatusSeverity.Error,
                toastImportant: true);
        NotifyDecryptHttpsUnchanged();
        return false;
    }
    private async Task<bool> TryCompleteMacSslTrustForDecryptAsync()
    {
        if (_interception.VerifyOsUserSslTrust() || OperatingSystem.IsWindows())
            return true;

        var incomplete = CertificateOsTrustResult.Fail(
            CertificateOsTrustKind.MacNeedsManualTrustConfirm,
            "Root CA needs Always Trust in Keychain Access before Decrypt HTTPS");
        if (await ResolveTerminalTrustFailureAsync(incomplete) &&
            (_interception.VerifyOsUserSslTrust() || OperatingSystem.IsWindows()))
            return true;

        SetOutcomeStatus(
            OsTrustUxCopy.FormatStatus(incomplete),
            StatusSeverity.Error,
            toastImportant: true);
        NotifyDecryptHttpsUnchanged();
        return false;
    }
    /// <summary>
    /// Terminal trust-failure modal: retry / Keychain / Export. Returns true when trust is established.
    /// </summary>
    private async Task<bool> ResolveTerminalTrustFailureAsync(CertificateOsTrustResult? result)
    {
        if (result?.Kind == CertificateOsTrustKind.Cancelled)
            return false;

        var owner = TryGetMainWindow();
        for (var i = 0; i < 4; i++)
        {
            var choice = await AwaitCancellableAsync(_dialogs.ShowDecryptTrustFailedAsync(owner, result));
            if (choice == TrustRecoveryChoice.Cancel)
            {
                _interception.SetLastOsTrustCancelled();
                return false;
            }

            var kind = result?.Kind ?? CertificateOsTrustKind.Failed;
            var handled = await TryHandleTerminalTrustChoiceAsync(owner, choice, kind);
            if (handled.HasValue)
                return handled.Value;

            // Primary = Try again
            SetBusyTrustingRootCa();
            if (await EnsureRootCaTrustedAsync(promptIfNeeded: true))
                return true;

            result = _interception.LastOsTrustResult;
            if (result?.Kind == CertificateOsTrustKind.Cancelled)
                return false;
        }

        return _interception.IsRootTrusted || _interception.VerifyOsUserSslTrust();
    }

    private async Task<bool?> TryHandleTerminalTrustChoiceAsync(
        Window? owner, TrustRecoveryChoice choice, CertificateOsTrustKind kind)
    {
        if (kind == CertificateOsTrustKind.HomebrewMissing)
        {
            if (choice is TrustRecoveryChoice.Primary or TrustRecoveryChoice.Secondary)
                await ExportCaAsync();
            return false;
        }

        if (kind == CertificateOsTrustKind.MacNeedsManualTrustConfirm)
        {
            if (choice == TrustRecoveryChoice.Secondary)
            {
                await ExportCaAsync();
                return false;
            }

            return await TryCompleteMacManualTrustAsync(owner);
        }

        if (choice == TrustRecoveryChoice.Secondary)
        {
            await ExportCaAsync();
            return false;
        }

        return null;
    }

    private void SetDecryptHttpsCore(bool enabled)
    {
        _decryptHttps = enabled;
        _interception.DecryptHttps = enabled;
        PersistSettings();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DecryptHttps)));
    }
}
