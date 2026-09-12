using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;

namespace Titanium.E2E.Tests.UiHeadless;

/// <summary>Phase 5c: real Avalonia Confirm Accept/Cancel clicks (in-memory trust).</summary>
[TestClass]
public class TrustConfirmHeadlessTests
{
    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task RotateCa_ConfirmCancel_Click_DismissesWithoutRotate()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync(useAvaloniaDialogs: true);

        await fx.DispatchAsync(async () =>
        {
            fx.Robot.Click("MenuStartCapture");
            await InspectorUiRobot.WaitForAsync(() => fx.Interception.IsRunning, TimeSpan.FromSeconds(15));
        });

        var before = fx.Interception.RootCertificate?.Thumbprint;
        await ClickMenuAndDismissDialogAsync(fx, "MenuRotateCa", "ConfirmCancel");
        await fx.WaitUntilAsync(
            () => fx.ViewModel.StatusText.Contains("cancelled", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10));
        Assert.AreEqual(before, fx.Interception.RootCertificate?.Thumbprint);
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task RemoveCa_ConfirmAccept_Click_ForcesDecryptOff()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync(useAvaloniaDialogs: true);

        await fx.DispatchAsync(async () =>
        {
            fx.Robot.Click("MenuStartCapture");
            await InspectorUiRobot.WaitForAsync(() => fx.Interception.IsRunning, TimeSpan.FromSeconds(15));
            fx.Robot.Click("MenuInstallCa");
            await InspectorUiRobot.WaitForAsync(() => fx.Interception.IsRootTrusted, TimeSpan.FromSeconds(10));
            fx.ViewModel.DecryptHttps = true;
            await InspectorUiRobot.WaitForAsync(() => fx.ViewModel.DecryptHttps, TimeSpan.FromSeconds(10));
        });

        await ClickMenuAndDismissDialogAsync(fx, "MenuRemoveCa", "ConfirmAccept");
        await fx.WaitUntilAsync(() => !fx.ViewModel.DecryptHttps, TimeSpan.FromSeconds(15));
        Assert.IsFalse(fx.ViewModel.DecryptHttps);
    }

    private static async Task ClickMenuAndDismissDialogAsync(
        InspectorHeadlessFixture fx, string menuId, string dialogButtonId)
    {
        await fx.DispatchAsync(() =>
        {
            Dispatcher.UIThread.Post(() => TryClickInOtherWindows(fx, dialogButtonId), DispatcherPriority.Input);
            fx.Robot.Click(menuId);
        });

        await fx.WaitUntilAsync(
            () =>
            {
                if (!HasOtherWindows(fx))
                    return true;
                TryClickInOtherWindows(fx, dialogButtonId);
                return !HasOtherWindows(fx);
            },
            TimeSpan.FromSeconds(10));
        Assert.IsFalse(
            HasOtherWindows(fx),
            $"Dialog still open after '{menuId}' (button '{dialogButtonId}'). Status={fx.ViewModel.StatusText}");
    }

    private static bool HasOtherWindows(InspectorHeadlessFixture fx) =>
        OtherWindows(fx).Any();

    private static bool TryClickInOtherWindows(InspectorHeadlessFixture fx, string automationId)
    {
        foreach (var window in OtherWindows(fx))
        {
            var robot = new InspectorUiRobot(window);
            if (!robot.TryFind<Control>(automationId, out _))
                continue;
            robot.Click(automationId);
            return true;
        }

        return false;
    }

    private static IEnumerable<Window> OtherWindows(InspectorHeadlessFixture fx)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            yield break;
        foreach (var window in desktop.Windows)
        {
            if (!ReferenceEquals(window, fx.Window))
                yield return window;
        }
    }
}
