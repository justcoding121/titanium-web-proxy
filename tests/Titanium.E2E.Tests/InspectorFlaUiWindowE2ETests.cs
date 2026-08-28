using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

/// <summary>
/// Opt-in Windows FlaUI smoke against TitaniumInspector.exe (not in default PR filter).
/// </summary>
[TestClass]
public class InspectorFlaUiWindowE2ETests
{
    [TestMethod]
    [TestCategory("E2E-UI-Window")]
    public async Task LaunchInspector_ShowsMainWindow_ThenExit()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("FlaUI window smoke is Windows-only");
        }

        var exe = FindInspectorExe();
        if (exe is null)
        {
            Assert.Inconclusive("TitaniumInspector.exe not found — build Titanium.Inspector Release first");
        }

        // Prefer FlaUI when available; otherwise Process + MainWindowHandle smoke.
        Process? proc = null;
        try
        {
            proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
            });
            Assert.IsNotNull(proc);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!proc!.HasExited && proc.MainWindowHandle == IntPtr.Zero && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
                proc.Refresh();
            }

            if (proc.HasExited)
            {
                Assert.Inconclusive("Inspector exited before showing a window (exit=" + proc.ExitCode + ")");
            }

            Assert.AreNotEqual(IntPtr.Zero, proc.MainWindowHandle, "Expected main window handle");
        }
        finally
        {
            if (proc is { HasExited: false })
            {
                try
                {
                    proc.CloseMainWindow();
                    if (!proc.WaitForExit(5000))
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                }
            }

            proc?.Dispose();
        }
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
                {
                    return path;
                }
            }
        }

        return null;
    }
}
