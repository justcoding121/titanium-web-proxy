using System.Net;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.Tests;

[TestClass]
public class TrustCommandCoverageTests
{
    [TestMethod]
    public async Task TrustFlow_InstallFirefoxDecryptUntrustAndDeviceSetup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-trust-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cer = Path.Combine(dir, "root.cer");
        var pem = Path.Combine(dir, "root.pem");
        try
        {
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            OverrideRootPfx(interception, Path.Combine(dir, "rootCert.pfx"));
            var dialogs = new ScriptedInspectorDialogs
            {
                InstallRootCaResult = true,
                RemoveRootCaResult = true,
                DeviceCaSetupResult = true,
                InstallRootCaBeforeFirefoxResult = true,
                StartProxyForDecryptResult = true,
            };
            var picker = new ScriptedInspectorPathPicker { SavePath = cer };
            var settings = new SettingsService(Path.Combine(dir, "settings.json"));
            settings.Current.AutoStartCapture = false;
            settings.Current.AutoSystemProxyOnStart = false;
            settings.Save();
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                dialogs,
                picker)
            {
                BindPort = 0,
                BindAddress = "127.0.0.1",
            };

            await ExecuteAsync(vm.TrustFirefoxCaCommand);
            StringAssert.Contains(vm.StatusText, "Start the proxy");

            await ExecuteAsync(vm.StartCaptureCommand);
            Assert.IsTrue(interception.IsRunning, vm.StatusText);

            await ExecuteAsync(vm.InstallCaCommand);
            Assert.IsTrue(interception.IsRootTrusted, vm.StatusText);
            StringAssert.Contains(vm.StatusText, "trusted");

            await ExecuteAsync(vm.TrustFirefoxCaCommand);

            interception.SetSystemProxy(true, settings.Current);
            Assert.IsTrue(interception.SystemProxyEnabled);
            interception.ReapplySystemProxyIfEnabled();
            interception.SetSystemProxy(false);

            Assert.IsNotNull(interception.ExportRootCertificate(cer));
            Assert.IsTrue(File.Exists(cer));
            Assert.IsNotNull(interception.ExportRootCertificate(pem));
            Assert.IsTrue(File.Exists(pem));
            Assert.IsTrue(InterceptionService.IsPemExportPath(pem));
            _ = InterceptionService.EncodeCertificatePem(File.ReadAllBytes(cer));

            picker.SavePath = cer;
            await ExecuteAsync(vm.ExportCaCommand);
            await ExecuteAsync(vm.DeviceCaSetupCommand);

            vm.DecryptHttps = true;
            await WaitUntil(() => vm.DecryptHttps, 8000);
            Assert.IsTrue(vm.DecryptHttps);
            vm.DecryptHttps = false;
            Assert.IsFalse(vm.DecryptHttps);

            await ExecuteAsync(vm.UntrustCaCommand);
            Assert.IsFalse(interception.IsRootTrusted);

            interception.FailNextUserTrustInstall = true;
            dialogs.TrustRecoveryResult = TrustRecoveryChoice.Primary;
            await ExecuteAsync(vm.InstallCaCommand);
            Assert.IsTrue(interception.IsRootTrusted, "admin recovery should trust in-memory");

            interception.FailNextUserTrustInstall = true;
            dialogs.TrustRecoveryResult = TrustRecoveryChoice.Cancel;
            await ExecuteAsync(vm.InstallCaCommand);

            Assert.IsTrue(interception.RotateRootCertificate(false));
            Assert.IsTrue(interception.InstallRootCertificateAsAdmin(false));
            _ = interception.RefreshTrustState();
            _ = interception.VerifyOsUserSslTrust();
            InterceptionService.TryEnableFirefoxEnterpriseRootsBestEffort();
            interception.SetLastOsTrustCancelled();
            Assert.AreEqual(CertificateOsTrustKind.Cancelled, interception.LastOsTrustResult?.Kind);

            InvokeProcessResolve(interception, new SessionSnapshot { Id = 7, Url = "https://example.test/" });

            await ExecuteAsync(vm.StopCaptureCommand);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public async Task DecryptHttps_WhenProxyStopped_CancelStartLeavesDecryptOff()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-decrypt-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var dialogs = new ScriptedInspectorDialogs { StartProxyForDecryptResult = false };
            var settings = new SettingsService(Path.Combine(dir, "settings.json"));
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                dialogs)
            {
                BindPort = 0,
                BindAddress = "127.0.0.1",
            };

            vm.DecryptHttps = true;
            await WaitUntil(() => vm.StatusText.Contains("cancelled", StringComparison.OrdinalIgnoreCase), 4000);
            Assert.IsFalse(vm.DecryptHttps);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    private static void InvokeProcessResolve(InterceptionService service, SessionSnapshot snap)
    {
        var workType = typeof(InterceptionService).GetNestedTypes(BindingFlags.NonPublic)
            .First(t => t.Name.Contains("ProcessResolveWork", StringComparison.Ordinal));
        var ctor = workType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)[0];
        var skipped = ctor.Invoke([snap, new Lazy<int>(() => 0)]);
        var current = ctor.Invoke([snap, new Lazy<int>(Environment.ProcessId)]);
        var method = typeof(InterceptionService).GetMethod(
            "ApplyResolvedProcess", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(service, [skipped]);
        method.Invoke(service, [current]);
        method.Invoke(service, [current]);
    }

    private static void OverrideRootPfx(InterceptionService interception, string path)
    {
        var field = typeof(InterceptionService).GetField("_rootPfxPath", BindingFlags.NonPublic | BindingFlags.Instance);
        field!.SetValue(interception, path);
    }

    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        await Task.Delay(200);
    }

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!predicate())
        {
            if (Environment.TickCount64 >= deadline)
                Assert.Fail($"Timed out waiting. Last check failed.");
            await Task.Delay(25);
        }
    }
}
