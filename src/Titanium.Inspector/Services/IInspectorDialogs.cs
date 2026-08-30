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
}

/// <summary>Avalonia modal dialogs.</summary>
public sealed class AvaloniaInspectorDialogs : IInspectorDialogs
{
    public Task<bool> ConfirmInstallRootCaAsync(Window? owner) =>
        SimpleConfirmDialog.ShowAsync(
            owner,
            "Install root CA",
            "Decrypt HTTPS requires trusting the Titanium Inspector root CA in your current-user certificate store (and Keychain/NSS on macOS/Linux). Install now?",
            accept: "Install",
            cancel: "Cancel");

    public Task<bool> ConfirmRemoveRootCaAsync(Window? owner) =>
        SimpleConfirmDialog.ShowAsync(
            owner,
            "Remove root CA",
            "Remove the Titanium Inspector root CA from the current-user Trusted Root store? HTTPS decrypt will be turned off.",
            accept: "Remove",
            cancel: "Cancel");

    public Task<bool> ConfirmElevateRootCaAsync(Window? owner) =>
        SimpleConfirmDialog.ShowAsync(
            owner,
            "Install with administrator privileges",
            "User-level trust failed or was insufficient. Continue to show the OS admin prompt (UAC / macOS authentication / polkit)? Cancel leaves certificate settings unchanged.",
            accept: "Continue",
            cancel: "Cancel");

    public Task<bool> ShowDeviceCaSetupAsync(Window? owner, string message) =>
        SimpleConfirmDialog.ShowAsync(
            owner,
            "Device CA setup",
            message,
            accept: "Export CA",
            cancel: "Close",
            height: 320);
}

/// <summary>Scripted answers for unit / E2E-UI tests (no real windows).</summary>
public sealed class ScriptedInspectorDialogs : IInspectorDialogs
{
    public bool InstallRootCaResult { get; set; } = true;
    public bool RemoveRootCaResult { get; set; } = true;
    public bool ElevateRootCaResult { get; set; } = true;
    public bool DeviceCaSetupResult { get; set; }
    public int InstallRootCaCalls { get; private set; }
    public int RemoveRootCaCalls { get; private set; }
    public int ElevateRootCaCalls { get; private set; }
    public int DeviceCaSetupCalls { get; private set; }
    public string? LastDeviceCaSetupMessage { get; private set; }

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

    public Task<bool> ShowDeviceCaSetupAsync(Window? owner, string message)
    {
        DeviceCaSetupCalls++;
        LastDeviceCaSetupMessage = message;
        return Task.FromResult(DeviceCaSetupResult);
    }
}
