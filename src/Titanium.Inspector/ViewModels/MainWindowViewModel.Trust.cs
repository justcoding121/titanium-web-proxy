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
    private const string TrustActionInProgressStatus =
        "Another certificate action is already in progress";

    private bool TryBeginTrustCommand()
    {
        if (_trustCommandBusy)
        {
            InspectorUxTrace.Event("TrustCommand.Rejected", "busy=true");
            SetGuardStatus(TrustActionInProgressStatus);
            return false;
        }

        _trustCommandBusy = true;
        InspectorUxTrace.Event("TrustCommand.Begin");
        return true;
    }

    private void EndTrustCommand()
    {
        _trustCommandBusy = false;
        InspectorUxTrace.Event("TrustCommand.End");
    }

    private async Task InstallCaAsync()
    {
        if (!_interception.IsRunning)
        {
            SetGuardStatus(StartProxyFirstStatus);
            return;
        }

        if (!TryBeginTrustCommand())
            return;

        using var scope = InspectorUxTrace.Scope(
            "InstallCa",
            $"trusted={_interception.IsRootTrusted}");
        try
        {
            // Already trusted: do not re-open the Root store or rewrite Firefox prefs.
            if (_interception.IsRootTrusted)
            {
                SetOsTrustSuccessStatus();
                return;
            }

            SetStatus("Preparing install…", StatusSeverity.Busy);
            await AwaitPriorFirefoxTrustBackgroundAsync();

            SetBusyTrustingRootCa();
            var ok = await EnsureRootCaTrustedAsync(promptIfNeeded: true);
            InspectorUxTrace.Event("InstallCa.Result", $"ok={ok}");
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
        finally
        {
            EndTrustCommand();
        }
    }

    private async Task TrustFirefoxCaAsync()
    {
        if (!_interception.IsRunning)
        {
            SetGuardStatus(StartProxyFirstStatus);
            return;
        }

        if (!TryBeginTrustCommand())
            return;

        using var scope = InspectorUxTrace.Scope("TrustFirefox");
        try
        {
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

            SetStatus("Preparing Firefox trust…", StatusSeverity.Busy);
            await AwaitPriorFirefoxTrustBackgroundAsync();

            SetStatus("Updating Firefox trust…", StatusSeverity.Busy);
            var result = await TrustFirefoxWithRecoveryAsync(owner);
            InspectorUxTrace.Event("TrustFirefox.Result", $"ok={result.Succeeded} kind={result.Kind}");
            SetOutcomeStatus(
                FormatFirefoxTrustOutcome(result),
                result.Succeeded ? StatusSeverity.Success : StatusSeverity.Error,
                toastImportant: true);
        }
        finally
        {
            EndTrustCommand();
        }
    }
    private async Task<bool> TryEnsureRootBeforeFirefoxAsync(Window? owner)
    {
        if (_interception.IsRootTrusted)
            return true;

        if (!await AwaitDialogAsync(_dialogs.ConfirmInstallRootCaBeforeFirefoxAsync(owner)))
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
            // Stay on UI sync context — recovery dialogs need the dispatcher.
            var result = await RunOffUiAsync(
                () => _interception.TrustFirefox(),
                StatusCancelToken);
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
        var choice = await AwaitDialogAsync(_dialogs.ShowTrustRecoveryAsync(owner, result));
        if (choice == TrustRecoveryChoice.Primary &&
            result.Kind == CertificateOsTrustKind.CertutilMissing &&
            (OperatingSystem.IsLinux() || result.BrewAvailable))
        {
            SetStatus("Installing browser certificate tools…", StatusSeverity.Busy);
            _ = await RunOffUiAsync(
                () => _interception.InstallNssToolsAndRetryTrust(),
                StatusCancelToken);
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
        if (!await AwaitDialogAsync(_dialogs.ConfirmQuitFirefoxForTrustAsync(owner)))
            return CertificateOsTrustResult.Fail(CertificateOsTrustKind.Cancelled, "Firefox trust cancelled");

        SetStatus("Quitting Firefox…", StatusSeverity.Busy);
        var quitOk = await RunOffUiAsync(
            () => FirefoxCertificateTrust.TryRequestFirefoxQuit(),
            StatusCancelToken);
        if (!quitOk)
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
    /// <param name="promptIfNeeded">When true, show recovery dialogs on failure.</param>
    /// <param name="skipInitialRefresh">
    ///     When true (Clear+Install after mint), skip the pre-install Root-store Find — the new
    ///     thumbprint cannot be present yet and Crypt32 is often still hot from Remove.
    /// </param>
    private async Task<bool> EnsureRootCaTrustedAsync(bool promptIfNeeded, bool skipInitialRefresh = false) // NOSONAR S3776 -- Adaptive OS-trust recovery loop shares dialog/state; splitting would hide the retry contract.
    {
        var owner = TryGetMainWindow();
        // Yield so Busy can paint. CryptUI / Keychain MUST stay on this thread (message pump).
        await Task.Yield();

        bool ok;
        CertificateOsTrustResult? result;

        if (_interception.UseInMemoryTrustState)
        {
            // In-memory still honors FailNextUserTrustInstall so recovery loops are testable.
            ok = _interception.InstallRootCertificate(machineStore: false);
            result = _interception.LastOsTrustResult;
            if (ok && result?.Kind != CertificateOsTrustKind.MacNeedsManualTrustConfirm)
            {
                ScheduleFirefoxEnterpriseRootsBestEffort();
                return true;
            }

            if (!promptIfNeeded)
                return ok;
        }
        else if (!skipInitialRefresh &&
                 await RunOffUiAsync(() =>
                 {
                     using var refreshScope = InspectorUxTrace.Scope("EnsureRoot.RefreshTrustState");
                     return _interception.RefreshTrustState(false);
                 }, StatusCancelToken))
        {
            ScheduleFirefoxEnterpriseRootsBestEffort();
            return true;
        }
        else
        {
            ok = await InstallRootInteractiveAsync();
            result = _interception.LastOsTrustResult;
            if (ok && result?.Kind != CertificateOsTrustKind.MacNeedsManualTrustConfirm)
            {
                ScheduleFirefoxEnterpriseRootsBestEffort();
                return true;
            }

            // CryptUI No — do not open the trust-recovery dialog.
            if (result?.Kind == CertificateOsTrustKind.Cancelled)
                return false;

            if (!promptIfNeeded)
                return ok;
        }

        // Adaptive recovery loop (certutil / Keychain / elevate).
        for (var i = 0; i < 4; i++)
        {
            result = _interception.LastOsTrustResult;
            if (_interception.IsRootTrusted &&
                result?.Kind != CertificateOsTrustKind.MacNeedsManualTrustConfirm)
            {
                ScheduleFirefoxEnterpriseRootsBestEffort();
                return true;
            }

            if (result?.Kind == CertificateOsTrustKind.MacNeedsManualTrustConfirm)
                return await TryCompleteMacManualTrustAsync(owner);

            if (!ok)
            {
                var recovered = await TryRecoverFailedOsTrustAsync(owner, result);
                if (recovered == true)
                {
                    ScheduleFirefoxEnterpriseRootsBestEffort();
                    return true;
                }
                if (recovered == false)
                    return false;
                ok = _interception.IsRootTrusted ||
                     _interception.LastOsTrustResult?.Kind == CertificateOsTrustKind.MacNeedsManualTrustConfirm;
                continue;
            }

            break;
        }

        if (_interception.IsRootTrusted)
            ScheduleFirefoxEnterpriseRootsBestEffort();
        return _interception.IsRootTrusted;
    }

    /// <summary>
    /// CryptUI/Keychain on UI; store verify + My prune off UI after Yes (avoids Not Responding).
    /// </summary>
    private async Task<bool> InstallRootInteractiveAsync()
    {
        using var scope = InspectorUxTrace.Scope("InstallRootInteractive");
        await Task.Yield();
        bool added;
        using (InspectorUxTrace.Scope("InstallRootStoresOnly.CryptUI"))
            added = _interception.InstallRootStoresOnly(machineStore: false);
        InspectorUxTrace.Event("InstallRootStoresOnly.Result", $"added={added}");

        if (!OperatingSystem.IsWindows())
        {
            await Task.Yield();
            using (InspectorUxTrace.Scope("ApplyUnixSslTrustOnUi"))
                _interception.ApplyUnixSslTrustOnUi(machineStore: false);
        }

        using (InspectorUxTrace.Scope("FinalizeTrustAfterStoreMutation", $"added={added}"))
        {
            return await RunOffUiAsync(
                () => _interception.FinalizeTrustAfterStoreMutation(
                    machineStore: false,
                    rootStoreAdded: added),
                StatusCancelToken);
        }
    }

    private void ScheduleFirefoxEnterpriseRootsBestEffort() =>
        _interception.ScheduleFirefoxEnterpriseRootsBestEffort();

    private async Task AwaitPriorFirefoxTrustBackgroundAsync()
    {
        using var scope = InspectorUxTrace.Scope("AwaitTrustBg");
        // Drop queued Clear/Enable left by the previous Install — a wedged running Clear used to
        // burn the full 8s budget before every Remove / Clear+Install.
        _interception.DropPendingFirefoxTrustBackgroundWork();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(StatusCancelToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await _interception.WaitForFirefoxTrustBackgroundIdleAsync(cts.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            InspectorUxTrace.Event("AwaitTrustBg.TimeoutOrCancel");
            // Proceed; serial queue + job timeout still bound prefs/prune work.
        }
    }

    private async Task<bool> TryCompleteMacManualTrustAsync(Window? owner)
    {
        var wait = await WaitForMacSslTrustAsync(owner);
        var trusted = wait == MacSslTrustWaitResult.Trusted ||
                      await RunOffUiAsync(() => _interception.VerifyOsUserSslTrust(), StatusCancelToken);
        if (trusted)
        {
            ScheduleFirefoxEnterpriseRootsBestEffort();
            return true;
        }

        _interception.SetLastOsTrustCancelled();
        if (wait == MacSslTrustWaitResult.NotSavedYet || _interception.IsRootInLoginKeychain())
            SetGuardStatus(OsTrustUxCopy.MacSslTrustNotSavedYet);
        return false;
    }

    private async Task<bool?> TryRecoverFailedOsTrustAsync(Window? owner, CertificateOsTrustResult? result)
    {
        var choice = await AwaitDialogAsync(_dialogs.ShowTrustRecoveryAsync(owner, result));
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
            // UAC/CryptUI need a message pump — do not Task.Run.
            await Task.Yield();
            var ok = _interception.InstallRootCertificateAsAdmin(machineStore: false);
            // Store Find after UAC — off UI.
            if (ok)
                ok = await RunOffUiAsync(
                    () => _interception.FinalizeTrustAfterAdminInstall(machineStore: false),
                    StatusCancelToken);
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
            // Stay on UI sync context — caller may show more dialogs / update StatusText.
            var install = await RunOffUiAsync(
                () => _interception.InstallNssToolsAndRetryTrust(),
                StatusCancelToken);
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

        return AwaitDialogAsync(_dialogs.ShowMacSslTrustWaitAsync(
            owner,
            () => _interception.VerifyOsUserSslTrust(),
            () => _interception.OpenMacKeychainGuidance(),
            () => _interception.IsRootInLoginKeychain()));
    }
    private void SetOsTrustSuccessStatus()
    {
        // Firefox prefs/policies already scheduled from EnsureRootCaTrustedAsync when trust
        // succeeded; schedule again here for paths that only call SetOsTrustSuccessStatus
        // (idempotent / best-effort).
        ScheduleFirefoxEnterpriseRootsBestEffort();

        var msg = "Root CA trusted — ready to decrypt HTTPS";
        if (!_firefoxTrustHintShown && InterceptionService.IsFirefoxProfilePresent)
        {
            _firefoxTrustHintShown = true;
            msg += " · Restart Firefox to use OS-root trust (or Capture → Trust CA in Firefox…)";
        }

        SetOutcomeStatus(msg, StatusSeverity.Success, toastImportant: true);
        NotifyDecryptTrustHealth();
    }

    private static string FormatOsTrustFailureStatus(CertificateOsTrustResult? result) =>
        OsTrustUxCopy.FormatStatus(result);

    private void SetBusyTrustingRootCa() =>
        SetStatus(
            OperatingSystem.IsWindows() ? TrustingRootCaWindowsStatus : TrustingRootCaStatus,
            StatusSeverity.Busy);
    
    private static string FormatRemoveRootPromptStatus(int total, int index)
    {
        if (OperatingSystem.IsWindows())
        {
            return total == 1
                ? "Windows may ask to DELETE the root CA - choose Yes"
                : $"Windows may ask to DELETE root CA ({index}/{total}) - choose Yes";
        }

        if (OperatingSystem.IsMacOS())
        {
            return total == 1
                ? "macOS may ask for your password to remove the root CA from Keychain"
                : $"macOS may ask for your password to remove root CA ({index}/{total})";
        }

        return total == 1
            ? "Removing root CA from the user certificate store..."
            : $"Removing root CA ({index}/{total}) from the user certificate store...";
    }

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

        if (!TryBeginTrustCommand())
            return;

        using var scope = InspectorUxTrace.Scope("UntrustCa");
        try
        {
            var owner = TryGetMainWindow();
            if (!await AwaitDialogAsync(_dialogs.ConfirmRemoveRootCaAsync(owner)))
            {
                SetTransientStatus(
                    "Remove root CA cancelled",
                    StatusSeverity.Neutral,
                    toastImportant: true,
                    revertMs: GuardStatusRevertMs);
                return;
            }

            SetStatus("Preparing remove…", StatusSeverity.Busy);
            await AwaitPriorFirefoxTrustBackgroundAsync();

            SetStatus("Removing root CA…", StatusSeverity.Busy);
            await RemoveOsRootInteractiveAsync(machineStore: false);
            // Decrypt cannot continue without a trusted CA (and we turn it off even if CryptUI
            // delete was declined — user asked to remove).
            await ForceDecryptHttpsOffAsync();

            var stillPresent = _interception.IsRootTrusted;
            string message = stillPresent
                ? FormatUntrustStillPresentStatus()
                : FormatUntrustRemovedStatus();

            SetOutcomeStatus(
                message,
                stillPresent ? StatusSeverity.Warning : StatusSeverity.Success,
                toastImportant: true);
            InspectorUxTrace.Event("UntrustCa.Result", $"stillPresent={stillPresent}");
        }
        finally
        {
            EndTrustCommand();
        }
    }

    /// <summary>
    /// List Root thumbs off UI → CryptUI Remove each on UI → My/Firefox finalize off UI.
    /// </summary>
    private async Task RemoveOsRootInteractiveAsync(bool machineStore)
    {
        using var scope = InspectorUxTrace.Scope("RemoveOsRootInteractive");
        await Task.Yield();
        if (_interception.UseInMemoryTrustState)
        {
            _interception.RemoveOsRootStoreOnly(machineStore);
            return;
        }

        SetStatus("Finding Titanium root CA in the Windows store…", StatusSeverity.Busy);
        IReadOnlyList<string> thumbs;
        using (InspectorUxTrace.Scope("ListRootThumbprintsToRemove"))
        {
            thumbs = await RunOffUiAsync(
                () => _interception.ListRootThumbprintsToRemove(machineStore),
                StatusCancelToken);
        }

        // Prefer known current thumb first so we do not depend solely on subject Find.
        var current = _interception.RootCertificate?.Thumbprint;
        if (!string.IsNullOrEmpty(current) &&
            !thumbs.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            thumbs = thumbs.Prepend(current).ToList();
        }

        if (thumbs.Count == 0 && !string.IsNullOrEmpty(current))
            thumbs = new[] { current };

        InspectorUxTrace.Event("RemoveOsRoot.Thumbs", $"count={thumbs.Count}");
        for (var i = 0; i < thumbs.Count; i++)
        {
            SetStatus(
                FormatRemoveRootPromptStatus(thumbs.Count, i + 1),
                StatusSeverity.Busy);
            await Task.Yield();
            using (InspectorUxTrace.Scope("RemoveRootThumbprint.CryptUI", $"i={i + 1}/{thumbs.Count}"))
                _interception.RemoveRootThumbprintOnUi(machineStore, thumbs[i]);
        }

        if (!OperatingSystem.IsWindows())
        {
            await Task.Yield();
            using (InspectorUxTrace.Scope("ApplyUnixUntrustOnUi"))
                _interception.ApplyUnixUntrustOnUi();
        }

        SetStatus("Finishing root CA removal…", StatusSeverity.Busy);
        using (InspectorUxTrace.Scope("FinalizeAfterRootRemove"))
        {
            await RunOffUiAsync(
                () => _interception.FinalizeAfterRootRemove(machineStore),
                StatusCancelToken);
        }
        // Firefox prefs/HKCU only — serial background lane (never await certutil).
        _interception.ScheduleClearPendingFirefoxRootTrust();
    }
    private async Task RotateCaAsync()
    {
        if (!_interception.IsRunning)
        {
            SetGuardStatus(StartProxyFirstStatus);
            return;
        }

        if (!TryBeginTrustCommand())
            return;

        using var scope = InspectorUxTrace.Scope("RotateCa");
        try
        {
            var owner = TryGetMainWindow();
            if (!await AwaitDialogAsync(_dialogs.ConfirmRotateRootCaAsync(owner)))
            {
                InspectorUxTrace.Event("RotateCa.ConfirmRotate", "accepted=false");
                SetTransientStatus(
                    "Clear and reinstall root CA cancelled",
                    StatusSeverity.Neutral,
                    toastImportant: true,
                    revertMs: GuardStatusRevertMs);
                return;
            }

            InspectorUxTrace.Event("RotateCa.ConfirmRotate", "accepted=true");

            // New root is untrusted until Install — MITM must not stay on across rotate.
            // Fire-and-forget snap so nested dispatcher bounce cannot delay CryptUI.
            _ = ForceDecryptHttpsOffAsync();

            // Await TrustBg only before Remove — never between Mint and CryptUI.
            SetStatus("Preparing clear and reinstall…", StatusSeverity.Busy);
            await AwaitPriorFirefoxTrustBackgroundAsync();

            SetStatus("Clearing root CA…", StatusSeverity.Busy);
            await Task.Yield();
            var oldThumb = _interception.RootCertificate?.Thumbprint;
            await RemoveOsRootInteractiveAsync(machineStore: false);

            SetStatus("Recreating root CA…", StatusSeverity.Busy);
            bool ok;
            using (InspectorUxTrace.Scope("MintNewRootCertificateCore"))
            {
                ok = await RunOffUiAsync(
                    () => _interception.MintNewRootCertificateCore(clearFirefox: false),
                    StatusCancelToken);
            }
            if (!ok)
            {
                SetOutcomeStatus("Clear and reinstall root CA failed — see logs", StatusSeverity.Error, toastImportant: true);
                return;
            }

            var newThumb = _interception.RootCertificate?.Thumbprint;
            var changed = !string.IsNullOrEmpty(newThumb) &&
                          !string.Equals(oldThumb, newThumb, StringComparison.OrdinalIgnoreCase);

            // User already confirmed Clear+Install — skip a second ConfirmInstall and go
            // straight to OS CryptUI/Keychain (avoids “two install dialogs then stuck”).
            InspectorUxTrace.Event("RotateCa.ConfirmInstall", "accepted=true skippedDuplicate=true");
            SetBusyTrustingRootCa();
            // Skip initial Root Find — we just minted; opening Crypt32 before CryptUI stalls.
            var trusted = await EnsureRootCaTrustedAsync(promptIfNeeded: true, skipInitialRefresh: true);
            InspectorUxTrace.Event("RotateCa.InstallResult", $"trusted={trusted} changed={changed}");
            var message = trusted
                ? FormatRotateCaTrustedStatus(changed)
                : FormatOsTrustFailureStatus(_interception.LastOsTrustResult);
            if (trusted)
                SetOsTrustSuccessStatus();
            else
                SetOutcomeStatus(message, StatusSeverity.Error, toastImportant: true);
        }
        finally
        {
            EndTrustCommand();
        }
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
        if (await AwaitDialogAsync(_dialogs.ShowDeviceCaSetupAsync(owner, message)))
        {
            await ExportCaAsync();
        }
    }
    private async Task EnableDecryptHttpsAsync(int enableGeneration)
    {
        if (!TryBeginTrustCommand())
        {
            await RejectDecryptHttpsEnableAsync();
            return;
        }

        _decryptHttpsBusy = true;
        using var scope = InspectorUxTrace.Scope("EnableDecryptHttps", $"gen={enableGeneration}");
        try
        {
            // Stay on the Avalonia UI sync context after awaits. ConfigureAwait(false) here
            // resumes on a thread-pool thread, then ShowDialog / CryptUI hang forever with
            // status stuck on "Checking certificate trust…" (no message pump / wrong thread).
            if (!await TryStartProxyForDecryptAsync())
                return;
            if (enableGeneration != Volatile.Read(ref _decryptEnableGeneration))
                return;
            if (!await TryTrustRootForDecryptAsync())
                return;
            if (enableGeneration != Volatile.Read(ref _decryptEnableGeneration))
                return;
            if (!await TryCompleteMacSslTrustForDecryptAsync())
                return;
            if (enableGeneration != Volatile.Read(ref _decryptEnableGeneration))
                return;

            SetDecryptHttpsCore(true);
            SetOutcomeStatus("Decrypting HTTPS", StatusSeverity.Success, toastImportant: true);
        }
        catch (OperationCanceledException)
        {
            if (enableGeneration == Volatile.Read(ref _decryptEnableGeneration))
            {
                SetGuardStatus("Decrypt HTTPS cancelled");
                await RejectDecryptHttpsEnableAsync();
            }
        }
        catch (Exception ex)
        {
            if (enableGeneration == Volatile.Read(ref _decryptEnableGeneration))
            {
                SetOutcomeStatus(
                    "Decrypt HTTPS failed: " + Truncate(ex.Message, 160),
                    StatusSeverity.Error,
                    toastImportant: true);
                await RejectDecryptHttpsEnableAsync();
            }
        }
        finally
        {
            if (enableGeneration == Volatile.Read(ref _decryptEnableGeneration))
                _decryptHttpsBusy = false;
            EndTrustCommand();
        }
    }

    /// <summary>
    /// After optimistic decrypt-on, re-check Root-store trust off the UI thread.
    /// Reverts decrypt + toast if the CA was removed while capturing.
    /// </summary>
    private async Task ReverifyDecryptTrustInBackgroundAsync()
    {
        var generation = Interlocked.Increment(ref _decryptTrustVerifyGeneration);
        try
        {
            var trusted = await RunOffUiAsync(
                () => _interception.RefreshTrustState(),
                StatusCancelToken).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _decryptTrustVerifyGeneration))
                return;
            await MarshalToUiAsync(NotifyDecryptTrustHealth, StatusCancelToken).ConfigureAwait(false);
            if (trusted || !_decryptHttps)
                return;

            async Task DisableIfStillWantedAsync()
            {
                if (generation != Volatile.Read(ref _decryptTrustVerifyGeneration) || !_decryptHttps)
                    return;
                await ForceDecryptHttpsOffAsync();
                SetOutcomeStatus(
                    "Decrypt HTTPS off — root CA not trusted",
                    StatusSeverity.Error,
                    toastImportant: true);
            }

            if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
                await DisableIfStillWantedAsync().ConfigureAwait(true);
            else
                await Dispatcher.UIThread.InvokeAsync(DisableIfStillWantedAsync);
        }
        catch (OperationCanceledException)
        {
            // status revert / shutdown
        }
    }

    /// <summary>
    /// Turn Decrypt HTTPS off in the model and snap Avalonia OneWay CheckBox/Menu targets.
    /// Use whenever decrypt is no longer possible (remove/rotate CA, trust lost, start without trust).
    /// </summary>
    /// <param name="cancelInFlightEnable">
    /// When true, invalidate an in-flight <see cref="EnableDecryptHttpsAsync"/> (Untrust / Rotate / Start).
    /// When false (enable cancel/reject), leave the generation alone so the enable finally-block can finish.
    /// </param>
    private async Task ForceDecryptHttpsOffAsync(bool cancelInFlightEnable = true)
    {
        if (cancelInFlightEnable)
            Interlocked.Increment(ref _decryptEnableGeneration);
        Interlocked.Increment(ref _decryptTrustVerifyGeneration);
        _decryptHttpsBusy = false;
        if (_decryptHttps)
            SetDecryptHttpsCore(false);
        await SnapDecryptHttpsUiAsync();
    }

    /// <summary>
    /// Enable was rejected — model never became true; bounce clears a locally flipped CheckBox.
    /// </summary>
    private Task RejectDecryptHttpsEnableAsync() => ForceDecryptHttpsOffAsync(cancelInFlightEnable: false);

    private void NotifyDecryptHttpsUnchanged() =>
        _ = ForceDecryptHttpsOffAsync(cancelInFlightEnable: false);

    private async Task<bool> TryStartProxyForDecryptAsync()
    {
        if (_interception.IsRunning)
            return true;

        var owner = TryGetMainWindow();
        if (!await AwaitDialogAsync(_dialogs.ConfirmStartProxyForDecryptAsync(owner)))
        {
            SetGuardStatus("Decrypt HTTPS cancelled — start the proxy first");
            await RejectDecryptHttpsEnableAsync();
            return false;
        }

        await StartCaptureAsync();
        if (_interception.IsRunning)
            return true;

        SetOutcomeStatus(
            "Could not start the proxy — Decrypt HTTPS stays off",
            StatusSeverity.Error,
            toastImportant: true);
        await RejectDecryptHttpsEnableAsync();
        return false;
    }
    private async Task<bool> TryTrustRootForDecryptAsync()
    {
        // Prefer cached trust from Start / Install — avoids Root-store Find on the UI thread.
        if (_interception.IsRootTrusted)
            return true;

        SetStatus("Checking certificate trust…", StatusSeverity.Busy);
        // No ConfigureAwait(false): dialogs and CryptUI below require the UI thread.
        var trusted = await RunOffUiAsync(
            () => _interception.RefreshTrustState(),
            StatusCancelToken);
        if (trusted)
            return true;

        var owner = TryGetMainWindow();
        SetStatus("Root CA not trusted — confirm install…", StatusSeverity.Busy);
        if (!await AwaitDialogAsync(_dialogs.ConfirmInstallRootCaAsync(owner)))
        {
            SetGuardStatus("Decrypt HTTPS cancelled — root CA not installed");
            await RejectDecryptHttpsEnableAsync();
            return false;
        }

        SetBusyTrustingRootCa();
        if (await EnsureRootCaTrustedAsync(promptIfNeeded: true))
            return true;

        if (_interception.LastOsTrustResult?.Kind == CertificateOsTrustKind.Cancelled)
        {
            SetGuardStatus("Decrypt HTTPS cancelled — root CA not trusted");
            await RejectDecryptHttpsEnableAsync();
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
        await RejectDecryptHttpsEnableAsync();
        return false;
    }
    private async Task<bool> TryCompleteMacSslTrustForDecryptAsync()
    {
        // Windows Root-store presence is trust - do not call VerifyOsUserSslTrust (second Find + Firefox prefs).
        if (OperatingSystem.IsWindows())
            return true;

        // Stay on UI sync context - ResolveTerminalTrustFailureAsync shows dialogs.
        var trusted = await RunOffUiAsync(
            () => _interception.VerifyOsUserSslTrust(),
            StatusCancelToken);
        if (trusted)
        {
            _interception.ScheduleFirefoxEnterpriseRootsBestEffort();
            return true;
        }

        // Linux: never fabricate Mac Keychain Always Trust - use NSS/certutil recovery instead.
        if (OperatingSystem.IsLinux())
        {
            var linuxIncomplete = _interception.LastOsTrustResult
                ?? CertificateOsTrustResult.Fail(
                    CertificateOsTrustKind.CertutilMissing,
                    "Root CA is not trusted yet. Install NSS certutil tools, or Export CA and trust it for your browser.");
            if (linuxIncomplete.Kind == CertificateOsTrustKind.MacNeedsManualTrustConfirm)
            {
                linuxIncomplete = CertificateOsTrustResult.Fail(
                    CertificateOsTrustKind.CertutilMissing,
                    string.IsNullOrWhiteSpace(linuxIncomplete.Message)
                        ? "Root CA is not trusted yet. Install NSS certutil tools, or Export CA and trust it for your browser."
                        : linuxIncomplete.Message);
            }

            InspectorUxTrace.Event("Decrypt.LinuxTrustIncomplete", $"kind={linuxIncomplete.Kind}");
            if (await ResolveTerminalTrustFailureAsync(linuxIncomplete))
            {
                trusted = await RunOffUiAsync(
                    () => _interception.VerifyOsUserSslTrust(),
                    StatusCancelToken);
                if (trusted)
                {
                    _interception.ScheduleFirefoxEnterpriseRootsBestEffort();
                    return true;
                }
            }

            SetOutcomeStatus(
                OsTrustUxCopy.FormatStatus(linuxIncomplete),
                StatusSeverity.Error,
                toastImportant: true);
            await RejectDecryptHttpsEnableAsync();
            return false;
        }

        var incomplete = CertificateOsTrustResult.Fail(
            CertificateOsTrustKind.MacNeedsManualTrustConfirm,
            "Root CA needs Always Trust in Keychain Access before Decrypt HTTPS");
        if (await ResolveTerminalTrustFailureAsync(incomplete))
        {
            trusted = await RunOffUiAsync(
                () => _interception.VerifyOsUserSslTrust(),
                StatusCancelToken);
            if (trusted)
            {
                _interception.ScheduleFirefoxEnterpriseRootsBestEffort();
                return true;
            }
        }

        SetOutcomeStatus(
            OsTrustUxCopy.FormatStatus(incomplete),
            StatusSeverity.Error,
            toastImportant: true);
        await RejectDecryptHttpsEnableAsync();
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
            var choice = await AwaitDialogAsync(_dialogs.ShowDecryptTrustFailedAsync(owner, result));
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

        return _interception.IsRootTrusted ||
               await RunOffUiAsync(() => _interception.VerifyOsUserSslTrust(), StatusCancelToken);
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
        NotifyDecryptTrustHealth();
        SyncToggleVisual?.Invoke(nameof(DecryptHttps), enabled);
    }

    private void NotifyDecryptTrustHealth()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DecryptTrustHealthText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowDecryptTrustHealth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDecryptTrustHealthy)));
    }
}
