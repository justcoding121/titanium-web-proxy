using Avalonia.Controls;
using Titanium.Inspector.Views;

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
    /// Confirm installing an Inspector update for the selected channel. Returns true if Install and restart.
    /// </summary>
    Task<bool> ConfirmInstallUpdateAsync(Window? owner, string version, string channelDisplay);
}

/// <summary>Avalonia modal dialogs.</summary>
public sealed class AvaloniaInspectorDialogs : IInspectorDialogs
{
    private const string CancelLabel = "Cancel";
    public Task<bool> ConfirmInstallRootCaAsync(Window? owner) =>
        SimpleConfirmDialog.ShowAsync(
            owner,
            "Install root CA",
            "Decrypt HTTPS requires trusting the Titanium Inspector root CA in your current-user certificate store (and Keychain/NSS on macOS/Linux). Install now?",
            accept: "Install",
            cancel: CancelLabel);

    public Task<bool> ConfirmRemoveRootCaAsync(Window? owner) =>
        SimpleConfirmDialog.ShowAsync(
            owner,
            "Remove root CA",
            "Remove the Titanium Inspector root CA from the current-user Trusted Root store? HTTPS decrypt will be turned off.",
            accept: "Remove",
            cancel: CancelLabel);

    public Task<bool> ConfirmElevateRootCaAsync(Window? owner) =>
        SimpleConfirmDialog.ShowAsync(
            owner,
            "Install with administrator privileges",
            "User-level trust failed or was insufficient. Continue to show the OS admin prompt (UAC / macOS authentication / polkit)? Cancel leaves certificate settings unchanged.",
            accept: "Continue",
            cancel: CancelLabel);

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
            "Restore bind address, menus, Tools (Composer/Breakpoints/AutoResponder/Scripts), retention, logging, HTTPS host lists, and layout to factory defaults?\n\n" +
            "This does not remove the root CA, change OS trust, clear captured sessions, or delete the on-disk body cache. Restart Inspector afterward so retention limits fully apply.",
            accept: "Reset settings",
            cancel: CancelLabel,
            height: 300);

    public Task<bool> ConfirmInstallUpdateAsync(Window? owner, string version, string channelDisplay) =>
        SimpleConfirmDialog.ShowAsync(
            owner,
            "Install release",
            $"Install {version} ({channelDisplay}) and restart now?\n\n" +
            "Inspector will close, replace the current installation, and relaunch.",
            accept: "Install and restart",
            cancel: "Later",
            height: 240);
}

/// <summary>Scripted answers for unit / E2E-UI tests (no real windows).</summary>
public sealed class ScriptedInspectorDialogs : IInspectorDialogs
{
    public bool InstallRootCaResult { get; set; } = true;
    public bool RemoveRootCaResult { get; set; } = true;
    public bool ElevateRootCaResult { get; set; } = true;
    public bool DeviceCaSetupResult { get; set; }
    public bool ResetSettingsResult { get; set; } = true;
    public bool RotateRootCaResult { get; set; } = true;
    public bool InstallUpdateResult { get; set; } = true;
    public int InstallRootCaCalls { get; private set; }
    public int RemoveRootCaCalls { get; private set; }
    public int ElevateRootCaCalls { get; private set; }
    public int DeviceCaSetupCalls { get; private set; }
    public int ResetSettingsCalls { get; private set; }
    public int RotateRootCaCalls { get; private set; }
    public int InstallUpdateCalls { get; private set; }
    public string? LastDeviceCaSetupMessage { get; private set; }
    public string? LastInstallUpdateVersion { get; private set; }
    public string? LastInstallUpdateChannel { get; private set; }

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

    public Task<bool> ConfirmInstallUpdateAsync(Window? owner, string version, string channelDisplay)
    {
        InstallUpdateCalls++;
        LastInstallUpdateVersion = version;
        LastInstallUpdateChannel = channelDisplay;
        return Task.FromResult(InstallUpdateResult);
    }
}
