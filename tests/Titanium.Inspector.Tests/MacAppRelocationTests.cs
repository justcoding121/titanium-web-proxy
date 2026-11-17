using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
[SupportedOSPlatform("macos")]
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
    public void ShouldOfferMove_WhenDismissedAndNotOnDmg_ReturnsFalse()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("macOS-only");
            return;
        }

        var settings = new InspectorSettings { MacDmgMovePromptDismissed = true };
        // Not on a DMG in the test host → false even before version compare.
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
            // Dismissed + DMG still re-offers when DecideRelocateAction says Install/Replace
            // (no Applications copy in this host → Install).
            Assert.IsTrue(MacAppRelocation.ShouldOfferMove(dismissed, isMacOs: true, runningFromDmg: true));
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
    public void DecideRelocateAction_MissingInstall_IsInstall()
    {
        var action = MacAppRelocation.DecideRelocateAction(
            runningVersion: new Version(7, 2, 0),
            installedVersion: null,
            installedPath: Path.Combine(Path.GetTempPath(), "ti-missing-" + Guid.NewGuid().ToString("N") + ".app"));
        Assert.AreEqual(MacRelocateAction.Install, action);
    }

    [TestMethod]
    public void DecideRelocateAction_OlderInstalled_IsReplace()
    {
        var action = MacAppRelocation.DecideRelocateAction(
            runningVersion: new Version(7, 2, 0),
            installedVersion: new Version(7, 1, 0),
            installedPath: "/Applications/Titanium Inspector.app");
        Assert.AreEqual(MacRelocateAction.Replace, action);
    }

    [TestMethod]
    public void DecideRelocateAction_SameOrNewerInstalled_IsOpenExisting()
    {
        Assert.AreEqual(
            MacRelocateAction.OpenExisting,
            MacAppRelocation.DecideRelocateAction(
                runningVersion: new Version(7, 1, 0),
                installedVersion: new Version(7, 1, 0),
                installedPath: "/Applications/Titanium Inspector.app"));
        Assert.AreEqual(
            MacRelocateAction.OpenExisting,
            MacAppRelocation.DecideRelocateAction(
                runningVersion: new Version(7, 0, 9),
                installedVersion: new Version(7, 1, 0),
                installedPath: "/Applications/Titanium Inspector.app"));
    }

    [TestMethod]
    public void ReadBundleVersion_ParsesShortVersionFromPlist()
    {
        var root = Path.Combine(Path.GetTempPath(), "ti-mac-reloc-" + Guid.NewGuid().ToString("N"));
        var contents = Path.Combine(root, "Contents");
        Directory.CreateDirectory(contents);
        File.WriteAllText(
            Path.Combine(contents, "Info.plist"),
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0">
            <dict>
              <key>CFBundleShortVersionString</key>
              <string>7.3.1</string>
              <key>CFBundleVersion</key>
              <string>7.3.1.0</string>
            </dict>
            </plist>
            """);
        try
        {
            var ver = MacAppRelocation.ReadBundleVersion(root);
            Assert.IsNotNull(ver);
            Assert.AreEqual(7, ver!.Major);
            Assert.AreEqual(3, ver.Minor);
            Assert.AreEqual(1, ver.Build);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
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
