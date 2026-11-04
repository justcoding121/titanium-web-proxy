using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.DesktopProbe.Shared;

namespace Titanium.E2E.Tests;

/// <summary>
///     Clicks Decrypt traffic on the real Inspector window. Runs only on an ephemeral VM after
///     machine trust is installed, so this PC never sees a certificate prompt.
/// </summary>
[TestClass]
public class InspectorDecryptWindowE2ETests
{
    [TestMethod]
    [TestCategory("Inspector-Unattended")]
    public async Task DecryptTraffic_OnAnEphemeralVm_TogglesTheCheck()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TITANIUM_EPHEMERAL_VM"), "1", StringComparison.Ordinal)
            || !string.Equals(Environment.GetEnvironmentVariable("TITANIUM_CI_MACHINE_TRUST"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Runs on an ephemeral VM after machine-trust install.");
            return;
        }

        var exe = FindInspectorExe();
        if (exe is null)
        {
            Assert.Inconclusive("TitaniumInspector.exe was not built.");
            return;
        }

        Process? proc = null;
        try
        {
            proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
            });
            Assert.IsNotNull(proc);
            using var automation = new UIA3Automation();
            var box = await FindCheckBoxAsync(automation, TimeSpan.FromSeconds(30));
            Assert.IsNotNull(box, "DecryptHttpsCheck was not in the window.");
            await TrustInspectorRootAfterAutoStartAsync();
            var check = box!.AsCheckBox();
            if (check.ToggleState != ToggleState.On)
                check.Toggle();
            var on = await WaitForToggleAsync(check, ToggleState.On, TimeSpan.FromSeconds(15));
            if (!on)
            {
                ClickNamedButton(automation, "Start proxy");
                ClickNamedButton(automation, "Install");
                on = await WaitForToggleAsync(check, ToggleState.On, TimeSpan.FromSeconds(15));
            }

            Assert.IsTrue(on, "Decrypt HTTPS stayed off.");
        }
        finally
        {
            if (proc is { HasExited: false })
            {
                try
                {
                    proc.CloseMainWindow();
                    if (!proc.WaitForExit(5000))
                        proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                }
            }

            proc?.Dispose();
        }
    }

    /// <summary>
    /// Auto-start writes the Inspector root before the checkbox is clicked. Import it so the
    /// toggle sees an already-trusted CA and does not open the install dialog.
    /// </summary>
    private static async Task TrustInspectorRootAfterAutoStartAsync()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var pfx = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TitaniumInspector",
            "rootCert.pfx");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(pfx) && DateTime.UtcNow < deadline)
            await Task.Delay(250);

        if (File.Exists(pfx))
            RootTrustHelpers.TrustPfxInWindowsRootStore(pfx);
    }

    private static async Task<bool> WaitForToggleAsync(CheckBox check, ToggleState expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (check.ToggleState == expected)
                return true;
            await Task.Delay(250);
        }

        return check.ToggleState == expected;
    }

    private static void ClickNamedButton(UIA3Automation automation, string name)
    {
        var button = automation.GetDesktop().FindFirstDescendant(
            cf => cf.ByControlType(ControlType.Button).And(cf.ByName(name)));
        button?.AsButton().Invoke();
    }

    private static async Task<AutomationElement?> FindCheckBoxAsync(UIA3Automation automation, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var found = automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("DecryptHttpsCheck"));
            if (found is not null)
                return found;
            await Task.Delay(250);
        }

        return null;
    }

    private static string? FindInspectorExe()
    {
        var roots = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Titanium.Inspector")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "src", "Titanium.Inspector")),
        };
        foreach (var root in roots)
        {
            foreach (var cfg in new[] { "Release", "Debug" })
            {
                var path = Path.Combine(root, "bin", cfg, "net10.0", "TitaniumInspector.exe");
                if (File.Exists(path))
                    return path;
            }
        }

        return null;
    }
}
