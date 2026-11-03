using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
            var check = box!.AsCheckBox();
            if (check.ToggleState != ToggleState.On)
                check.Toggle();
            Assert.AreEqual(ToggleState.On, check.ToggleState);
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
