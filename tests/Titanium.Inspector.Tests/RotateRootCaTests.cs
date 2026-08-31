using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class RotateRootCaTests
{
    [TestMethod]
    public async Task RotateCa_Cancel_DoesNotChangePfx()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-rot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var interception = new InterceptionService { UseInMemoryTrustState = true };
            OverrideRootPfx(interception, Path.Combine(dir, "rootCert.pfx"));
            await interception.StartAsync(IPAddress.Loopback, 0);
            var before = interception.RootCertificate!.Thumbprint;

            var dialogs = new ScriptedInspectorDialogs { RotateRootCaResult = false };
            var settings = new SettingsService(Path.Combine(dir, "settings.json"));
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                dialogs);

            await ExecuteAsync(vm.RotateCaCommand);
            Assert.AreEqual(1, dialogs.RotateRootCaCalls);
            Assert.AreEqual(before, interception.RootCertificate!.Thumbprint);
            StringAssert.Contains(vm.StatusText, "cancelled");
            interception.EnsureShutdown();
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public async Task RotateCa_Accept_ChangesThumbprintAndClearsLocalCrts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-rot2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var interception = new InterceptionService { UseInMemoryTrustState = true };
            OverrideRootPfx(interception, Path.Combine(dir, "rootCert.pfx"));
            await interception.StartAsync(IPAddress.Loopback, 0);
            var before = interception.RootCertificate!.Thumbprint;
            Directory.CreateDirectory(Path.Combine(dir, "crts"));
            File.WriteAllText(Path.Combine(dir, "crts", "junk.pfx"), "x");

            var dialogs = new ScriptedInspectorDialogs
            {
                RotateRootCaResult = true,
                InstallRootCaResult = false
            };
            var settings = new SettingsService(Path.Combine(dir, "settings.json"));
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                dialogs);

            await ExecuteAsync(vm.RotateCaCommand);
            Assert.AreNotEqual(before, interception.RootCertificate!.Thumbprint);
            Assert.IsFalse(Directory.Exists(Path.Combine(dir, "crts")));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "rootCert.pfx")));
            interception.EnsureShutdown();
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public async Task LegacySharedCrts_PruneOnce_IsIdempotentOnStart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-leg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var shared = Path.Combine(dir, "shared-crts");
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "old.pfx"), "x");
        try
        {
            using var interception = new InterceptionService
            {
                UseInMemoryTrustState = true,
                LegacyCrtsTestRoot = dir
            };
            OverrideRootPfx(interception, Path.Combine(dir, "rootCert.pfx"));
            await interception.StartAsync(IPAddress.Loopback, 0);
            Assert.IsFalse(Directory.Exists(shared), "first Start should prune shared crts");
            Assert.IsTrue(File.Exists(Path.Combine(dir, "legacy-shared-crts-cleared")));

            Directory.CreateDirectory(shared);
            File.WriteAllText(Path.Combine(shared, "again.pfx"), "y");
            interception.EnsureShutdown();

            using var interception2 = new InterceptionService
            {
                UseInMemoryTrustState = true,
                LegacyCrtsTestRoot = dir
            };
            OverrideRootPfx(interception2, Path.Combine(dir, "rootCert.pfx"));
            await interception2.StartAsync(IPAddress.Loopback, 0);
            Assert.IsTrue(Directory.Exists(shared), "marker present → Start must not prune again");
            interception2.EnsureShutdown();
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public async Task Rotate_AlwaysPrunesSharedCrtsEvenWithMarker()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-leg2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "legacy-shared-crts-cleared"), "already");
        var shared = Path.Combine(dir, "shared-crts");
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "old.pfx"), "x");
        try
        {
            using var interception = new InterceptionService
            {
                UseInMemoryTrustState = true,
                LegacyCrtsTestRoot = dir
            };
            OverrideRootPfx(interception, Path.Combine(dir, "rootCert.pfx"));
            await interception.StartAsync(IPAddress.Loopback, 0);
            Assert.IsTrue(Directory.Exists(shared), "Start with marker keeps shared crts");

            Assert.IsTrue(interception.RotateRootCertificate(false));
            Assert.IsFalse(Directory.Exists(shared), "Rotate always prunes shared crts");
            interception.EnsureShutdown();
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    private static void OverrideRootPfx(InterceptionService interception, string path)
    {
        var field = typeof(InterceptionService).GetField("_rootPfxPath",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        field!.SetValue(interception, path);
    }

    private static async Task ExecuteAsync(ICommand command)
    {
        command.Execute(null);
        await Task.Delay(150);
    }
    [TestMethod]
    public async Task RotateCa_WhenProxyStopped_SetsStartFirstStatus()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-rot-stop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var interception = new InterceptionService { UseInMemoryTrustState = true };
            OverrideRootPfx(interception, Path.Combine(dir, "rootCert.pfx"));
            var dialogs = new ScriptedInspectorDialogs { RotateRootCaResult = true };
            var settings = new SettingsService(Path.Combine(dir, "settings.json"));
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                dialogs);

            await ExecuteAsync(vm.RotateCaCommand);
            StringAssert.Contains(vm.StatusText, "Start the proxy first");
            Assert.AreEqual(0, dialogs.RotateRootCaCalls);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task RotateCa_AcceptInstall_UpdatesStatusViaInstallHelper()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-rot-inst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var interception = new InterceptionService { UseInMemoryTrustState = true };
            OverrideRootPfx(interception, Path.Combine(dir, "rootCert.pfx"));
            await interception.StartAsync(IPAddress.Loopback, 0);
            // Force decrypt on so RotateCa clears it.
            var settings = new SettingsService(Path.Combine(dir, "settings.json"));
            settings.Current.DecryptHttps = true;
            var dialogs = new ScriptedInspectorDialogs
            {
                RotateRootCaResult = true,
                InstallRootCaResult = true,
                ElevateRootCaResult = false
            };
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                dialogs);
            vm.DecryptHttps = true;

            await ExecuteAsync(vm.RotateCaCommand);
            Assert.IsFalse(vm.DecryptHttps);
            Assert.IsTrue(dialogs.InstallRootCaCalls >= 1);
            StringAssert.Contains(vm.StatusText, "reinstalled");
            interception.EnsureShutdown();
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task RotateCa_UserInstallFails_ElevatesAndTrusts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-rot-elev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var interception = new InterceptionService
            {
                UseInMemoryTrustState = true,
                FailNextUserTrustInstall = true
            };
            OverrideRootPfx(interception, Path.Combine(dir, "rootCert.pfx"));
            await interception.StartAsync(IPAddress.Loopback, 0);
            var dialogs = new ScriptedInspectorDialogs
            {
                RotateRootCaResult = true,
                InstallRootCaResult = true,
                ElevateRootCaResult = true
            };
            var settings = new SettingsService(Path.Combine(dir, "settings.json"));
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                dialogs);

            await ExecuteAsync(vm.RotateCaCommand);
            Assert.AreEqual(1, dialogs.ElevateRootCaCalls);
            StringAssert.Contains(vm.StatusText, "reinstalled");
            interception.EnsureShutdown();
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task OpenLoopbackExempt_WithoutOwner_SetsProbeStatus_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows-only");

        var dir = Path.Combine(Path.GetTempPath(), "ti-loop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var interception = new InterceptionService { UseInMemoryTrustState = true };
            var settings = new SettingsService(Path.Combine(dir, "settings.json"));
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                new ScriptedInspectorDialogs());

            await ExecuteAsync(vm.OpenLoopbackExemptCommand);
            StringAssert.Contains(vm.StatusText, "Store app allow-list OK");
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }
}