using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Titanium.Inspector.InstallSmoke;

/// <summary>Windows toolbar click, scoped to the Inspector process. No settings-file fallback.</summary>
internal static class UiClicks
{
    public static async Task<ClickResult> ClickDecryptHttpsAsync(int pid, TimeSpan timeout)
    {
        using var automation = new UIA3Automation();
        var application = FlaUI.Core.Application.Attach(pid);
        var deadline = DateTime.UtcNow + timeout;
        AutomationElement? box = null;
        while (DateTime.UtcNow < deadline && box is null)
        {
            if (OsDialogVisible(application, automation))
                return new ClickResult(false, "os-dialog", "A Windows certificate dialog is visible. Not clicking it.");
            box = Find(application, automation, cf => cf.ByAutomationId("DecryptHttpsCheck"));
            if (box is null)
                await Task.Delay(300);
        }

        if (box is null)
            return new ClickResult(false, "missing", "DecryptHttpsCheck was not in the Inspector window.");

        var check = box.AsCheckBox();
        if (check.ToggleState != ToggleState.On)
            check.Toggle();

        if (await WaitToggleAsync(check, ToggleState.On, TimeSpan.FromSeconds(8)))
            return new ClickResult(true, "flaui", null);

        Invoke(Find(application, automation, cf => cf.ByAutomationId("ConfirmAccept")));
        Invoke(Find(application, automation, cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Install"))));
        Invoke(Find(application, automation, cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Start proxy"))));
        if (OsDialogVisible(application, automation))
            return new ClickResult(false, "os-dialog", "A Windows certificate dialog appeared after the in-app Install click.");

        var on = await WaitToggleAsync(check, ToggleState.On, TimeSpan.FromSeconds(15));
        return on
            ? new ClickResult(true, "flaui", null)
            : new ClickResult(false, "flaui", "Decrypt HTTPS stayed off after the click.");
    }

    public static Task<ClickResult> ClickMoveToApplicationsAsync(int pid, TimeSpan timeout) =>
        Task.FromResult(new ClickResult(false, "n/a", "Move to Applications is macOS-only."));

    private static AutomationElement? Find(FlaUI.Core.Application application, UIA3Automation automation, System.Func<FlaUI.Core.Conditions.ConditionFactory, FlaUI.Core.Conditions.ConditionBase> condition)
    {
        try
        {
            foreach (var window in application.GetAllTopLevelWindows(automation))
            {
                var found = window.FindFirstDescendant(condition);
                if (found is not null)
                    return found;
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }

        return null;
    }

    private static void Invoke(AutomationElement? element)
    {
        try { element?.AsButton().Invoke(); }
        catch { /* dialog may already be gone */ }
    }

    private static async Task<bool> WaitToggleAsync(CheckBox check, ToggleState expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (check.ToggleState == expected)
                    return true;
            }
            catch
            {
                return false;
            }

            await Task.Delay(250);
        }

        try { return check.ToggleState == expected; }
        catch { return false; }
    }

    private static bool OsDialogVisible(FlaUI.Core.Application application, UIA3Automation automation)
    {
        try
        {
            foreach (var window in application.GetAllTopLevelWindows(automation))
            {
                var name = window.Name ?? "";
                if (name.Contains("Security Warning", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Root Certificate Store", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }

        return false;
    }
}

internal readonly record struct ClickResult(bool Ok, string How, string? Error);
