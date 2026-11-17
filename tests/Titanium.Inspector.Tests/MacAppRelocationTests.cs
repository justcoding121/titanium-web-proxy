using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class MacAppRelocationTests
{
    [TestMethod]
    public void TryFindAppBundlePath_WhenNotInBundle_ReturnsNullOrExistingApp()
    {
        // Unit-test host is not inside a .app; method must not throw.
        var path = MacAppRelocation.TryFindAppBundlePath();
        if (path is not null)
            Assert.IsTrue(path.EndsWith(".app", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ShouldOfferMove_WhenDismissed_ReturnsFalse()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("macOS-only");
            return;
        }

        var settings = new InspectorSettings { MacDmgMovePromptDismissed = true };
        Assert.IsFalse(MacAppRelocation.ShouldOfferMove(settings));
    }

    [TestMethod]
    public void BundleWalk_DmgDecision_AndCopyFallback_CoverRelocationPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-mac-reloc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tool = FakeOsExecutable.Ensure("twp-sonar-fake-tool-inspector.exe");
        try
        {
#pragma warning disable CA1416 // Exercise the macOS entry points on every CI host.
            Assert.IsFalse(MacAppRelocation.IsRunningFromDmgVolume());
            Assert.IsFalse(MacAppRelocation.IsRunningFromDmgVolume(isMacOs: false, "/Volumes/Img/Titanium Inspector.app"));
            Assert.IsFalse(MacAppRelocation.IsRunningFromDmgVolume(isMacOs: true, bundle: null));
            Assert.IsFalse(MacAppRelocation.IsRunningFromDmgVolume(isMacOs: true, "/Applications/Titanium Inspector.app"));
            Assert.IsTrue(MacAppRelocation.IsRunningFromDmgVolume(isMacOs: true, "/Volumes/Img/Titanium Inspector.app"));

            var dismissed = new InspectorSettings { MacDmgMovePromptDismissed = true };
            Assert.IsFalse(MacAppRelocation.ShouldOfferMove(dismissed, isMacOs: true, runningFromDmg: true));
            Assert.IsFalse(MacAppRelocation.ShouldOfferMove(new InspectorSettings(), isMacOs: false, runningFromDmg: true));
            Assert.IsFalse(MacAppRelocation.ShouldOfferMove(new InspectorSettings(), isMacOs: true, runningFromDmg: false));
            Assert.IsTrue(MacAppRelocation.ShouldOfferMove(new InspectorSettings(), isMacOs: true, runningFromDmg: true));
            Assert.IsFalse(MacAppRelocation.ShouldOfferMove(new InspectorSettings()));

            var app = Path.Combine(root, "Titanium Inspector.app");
            Assert.AreEqual(
                app,
                MacAppRelocation.TryFindAppBundlePath(app + Path.DirectorySeparatorChar));
            Assert.AreEqual(
                app,
                MacAppRelocation.TryFindAppBundlePath(Path.Combine(app, "Contents", "MacOS")));
            Assert.IsNull(MacAppRelocation.TryFindAppBundlePath(Path.Combine(root, "NotApp", "Contents", "MacOS")));

            var source = Path.Combine(root, "source.app");
            Directory.CreateDirectory(source);
            var existing = Path.Combine(root, "existing.app");
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "old.txt"), "x");
            MacAppRelocation.CopyBundleForTests(source, existing, tool);

            var denied = Path.Combine(root, "FAIL-dest.app");
            var deniedEx = Assert.ThrowsExactly<IOException>(() =>
                MacAppRelocation.CopyBundleForTests(source, denied, tool));
            StringAssert.Contains(deniedEx.Message, "denied");

            var silent = Path.Combine(root, "FAILSILENT-dest.app");
            var silentEx = Assert.ThrowsExactly<IOException>(() =>
                MacAppRelocation.CopyBundleForTests(source, silent, tool));
            Assert.AreEqual("ditto failed", silentEx.Message);

            var home = Path.Combine(root, "home");
            var systemApps = Path.Combine(root, "FAIL-Applications");
            var installed = MacAppRelocation.CopyBundleToApplications(source, systemApps, home, tool, tool);
            StringAssert.EndsWith(installed, "Titanium Inspector.app");

            var withoutQuarantineTool = MacAppRelocation.CopyBundleToApplications(
                source,
                Path.Combine(root, "Applications"),
                home,
                tool,
                Path.Combine(root, "missing-xattr.exe"));
            StringAssert.EndsWith(withoutQuarantineTool, "Titanium Inspector.app");

            MacAppRelocation.RelaunchInstalledApp(installed, tool);
            Assert.ThrowsExactly<Win32Exception>(() => MacAppRelocation.RelaunchInstalledApp(installed));
            Assert.ThrowsExactly<InvalidOperationException>(MacAppRelocation.CopyBundleToApplications);
#pragma warning restore CA1416
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
    }

    [TestMethod]
    public async Task TryOfferMacDmgMove_OnThisHost_ReturnsFalseBeforeAnyDialog()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-mac-move-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new SettingsService(Path.Combine(dir, "settings.json"));
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));

            if (!OperatingSystem.IsMacOS())
                Assert.IsFalse(await vm.TryOfferMacDmgMoveAsync());

            var status = (string)typeof(MainWindowViewModel)
                .GetMethod("UntrustedRootStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, null)!;
            StringAssert.Contains(status, "Decrypt HTTPS off");
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
    }
}

internal static class FakeOsExecutable
{
    private static readonly object Gate = new();

    public static string Ensure(string fileName)
    {
        var exe = Path.Combine(Path.GetTempPath(), fileName);
        lock (Gate)
        {
            if (File.Exists(exe))
                return exe;

            var sourcePath = Path.Combine(Path.GetTempPath(), fileName + ".cs");
            File.WriteAllText(sourcePath, """
                using System;
                public class Program {
                  public static int Main(string[] args) {
                    var joined = string.Join(" ", args);
                    if (joined.IndexOf("SLEEP", StringComparison.Ordinal) >= 0) {
                      System.Threading.Thread.Sleep(60000);
                      return 0;
                    }
                    if (joined.IndexOf("FAILSILENT", StringComparison.Ordinal) >= 0)
                      return 1;
                    if (joined.IndexOf("FAIL", StringComparison.Ordinal) >= 0) {
                      Console.Error.WriteLine("denied");
                      return 1;
                    }
                    return 0;
                  }
                }
                """);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(
                $"Add-Type -OutputAssembly '{exe}' -OutputType ConsoleApplication -TypeDefinition (Get-Content -Raw -LiteralPath '{sourcePath}')");
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start powershell to build the fake tool.");
            if (!process.WaitForExit(30_000) || !File.Exists(exe))
            {
                throw new InvalidOperationException(
                    "Fake tool was not compiled: " + process.StandardError.ReadToEnd());
            }

            return exe;
        }
    }
}
