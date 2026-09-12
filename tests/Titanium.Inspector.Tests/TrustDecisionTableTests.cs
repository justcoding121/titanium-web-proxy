using System.Net;
using System.Reflection;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class TrustDecisionTableTests
{
    [TestMethod]
    [TestCategory("Inspector-Trust-Decision")]
    public async Task RotateCa_ConfirmNo_ToastsAndLeavesThumbprint()
    {
        await using var harness = await TrustHarness.CreateAsync();
        var before = harness.Interception.RootCertificate!.Thumbprint;
        harness.Dialogs.RotateRootCaResult = false;

        await ExecuteUntilAsync(
            harness.Vm.RotateCaCommand,
            () => harness.Dialogs.RotateRootCaCalls >= 1
                  || harness.Vm.StatusText.Contains("cancelled", StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(1, harness.Dialogs.RotateRootCaCalls);
        Assert.AreEqual(before, harness.Interception.RootCertificate!.Thumbprint);
        StringAssert.Contains(harness.Vm.StatusText, "cancelled");
    }

    [TestMethod]
    [TestCategory("Inspector-Trust-Decision")]
    public async Task RotateCa_ConfirmYes_SkipsSecondInstallConfirm_AndTrustsInMemory()
    {
        await using var harness = await TrustHarness.CreateAsync();
        var before = harness.Interception.RootCertificate!.Thumbprint;
        harness.Dialogs.RotateRootCaResult = true;
        harness.Dialogs.InstallRootCaResult = false;

        await ExecuteUntilAsync(
            harness.Vm.RotateCaCommand,
            () => harness.Interception.RootCertificate is not null
                  && !string.Equals(before, harness.Interception.RootCertificate.Thumbprint,
                      StringComparison.OrdinalIgnoreCase)
                  && harness.Interception.IsRootTrusted);

        Assert.AreEqual(1, harness.Dialogs.RotateRootCaCalls);
        Assert.AreEqual(0, harness.Dialogs.InstallRootCaCalls);
        Assert.IsTrue(harness.Interception.IsRootTrusted, harness.Vm.StatusText);
        Assert.IsFalse(harness.Vm.DecryptHttps);
    }

    [TestMethod]
    [TestCategory("Inspector-Trust-Decision")]
    public async Task RotateCa_SecondImmediateYes_SucceedsWhileNotBusy()
    {
        await using var harness = await TrustHarness.CreateAsync();
        harness.Dialogs.RotateRootCaResult = true;

        await ExecuteUntilAsync(
            harness.Vm.RotateCaCommand,
            () => harness.Dialogs.RotateRootCaCalls >= 1 && harness.Interception.IsRootTrusted);
        var mid = harness.Interception.RootCertificate?.Thumbprint;
        Assert.IsFalse(string.IsNullOrEmpty(mid));

        await ExecuteUntilAsync(
            harness.Vm.RotateCaCommand,
            () =>
            {
                if (harness.Dialogs.RotateRootCaCalls < 2)
                    return false;
                var thumb = harness.Interception.RootCertificate?.Thumbprint;
                return !string.IsNullOrEmpty(thumb)
                       && !string.Equals(mid, thumb, StringComparison.OrdinalIgnoreCase);
            });

        Assert.IsTrue(harness.Dialogs.RotateRootCaCalls >= 2, $"calls={harness.Dialogs.RotateRootCaCalls} status={harness.Vm.StatusText}");
        await WaitUntil(() => !harness.Vm.IsStatusBusy && harness.Interception.RootCertificate is not null, 15000);
        Assert.IsNotNull(harness.Interception.RootCertificate);
    }

    [TestMethod]
    [TestCategory("Inspector-Trust-Decision")]
    public async Task RemoveCa_ConfirmNo_ToastsWithoutStoreWork()
    {
        await using var harness = await TrustHarness.CreateAsync();
        harness.Dialogs.InstallRootCaResult = true;
        await ExecuteUntilAsync(harness.Vm.InstallCaCommand, () => harness.Interception.IsRootTrusted);
        await WaitUntil(() => !harness.Vm.IsStatusBusy, 10000);
        harness.Dialogs.RemoveRootCaResult = false;

        await ExecuteUntilAsync(
            harness.Vm.UntrustCaCommand,
            () => harness.Dialogs.RemoveRootCaCalls >= 1
                  || harness.Vm.StatusText.Contains("cancelled", StringComparison.OrdinalIgnoreCase));

        Assert.IsTrue(harness.Dialogs.RemoveRootCaCalls >= 1, harness.Vm.StatusText);
        Assert.IsTrue(harness.Interception.IsRootTrusted);
        StringAssert.Contains(harness.Vm.StatusText, "cancelled");
    }

    [TestMethod]
    [TestCategory("Inspector-Trust-Decision")]
    public async Task RemoveCa_ConfirmYes_ForcesDecryptOff()
    {
        await using var harness = await TrustHarness.CreateAsync();
        harness.Dialogs.InstallRootCaResult = true;
        await ExecuteUntilAsync(harness.Vm.InstallCaCommand, () => harness.Interception.IsRootTrusted);
        await WaitUntil(() => !harness.Vm.IsStatusBusy, 10000);
        Assert.IsTrue(harness.Interception.IsRootTrusted);
        harness.Vm.DecryptHttps = true;
        Assert.IsTrue(harness.Vm.DecryptHttps, harness.Vm.StatusText);
        harness.Dialogs.RemoveRootCaResult = true;

        await ExecuteUntilAsync(
            harness.Vm.UntrustCaCommand,
            () => harness.Dialogs.RemoveRootCaCalls >= 1 && !harness.Vm.DecryptHttps,
            25000);

        Assert.IsFalse(harness.Vm.DecryptHttps);
    }

    [TestMethod]
    [TestCategory("Inspector-Trust-Decision")]
    public async Task Decrypt_StartProxyCancelled_SnapsOff()
    {
        await using var harness = await TrustHarness.CreateAsync(startProxy: false);
        harness.Dialogs.StartProxyForDecryptResult = false;

        harness.Vm.DecryptHttps = true;
        await WaitUntil(() => harness.Dialogs.StartProxyForDecryptCalls >= 1, 8000);
        await WaitUntil(() => !harness.Vm.DecryptHttps, 8000);

        Assert.IsFalse(harness.Vm.DecryptHttps);
        StringAssert.Contains(harness.Vm.StatusText, "cancelled");
    }

    [TestMethod]
    [TestCategory("Inspector-Trust-Decision")]
    public async Task Decrypt_InstallCancelled_SnapsOff()
    {
        await using var harness = await TrustHarness.CreateAsync();
        if (harness.Interception.IsRootTrusted)
            harness.Interception.UntrustRootCertificate(false);
        Assert.IsFalse(harness.Interception.IsRootTrusted);

        harness.Dialogs.InstallRootCaResult = false;
        harness.Vm.DecryptHttps = true;
        await WaitUntil(() => harness.Dialogs.InstallRootCaCalls >= 1, 8000);
        await WaitUntil(() => !harness.Vm.DecryptHttps, 8000);

        Assert.IsFalse(harness.Vm.DecryptHttps);
    }

    [TestMethod]
    [TestCategory("Inspector-Trust-Decision")]
    public async Task Decrypt_WhileTrustBusy_RejectedWithSnap()
    {
        await using var harness = await TrustHarness.CreateAsync();
        harness.Dialogs.RotateRootCaResult = true;

        harness.Vm.RotateCaCommand.Execute(null);
        await Task.Delay(20);
        harness.Vm.DecryptHttps = true;
        await Task.Delay(200);

        Assert.IsFalse(harness.Vm.DecryptHttps);
        await WaitUntil(() => harness.Dialogs.RotateRootCaCalls >= 1, 15000);
    }

    [TestMethod]
    [TestCategory("Inspector-Trust-Decision")]
    public async Task TrustFirefox_InMemory_DoesNotSurfaceCertutilMissing()
    {
        await using var harness = await TrustHarness.CreateAsync();
        harness.Dialogs.InstallRootCaBeforeFirefoxResult = true;
        if (!harness.Interception.IsRootTrusted)
        {
            harness.Dialogs.InstallRootCaResult = true;
            await ExecuteUntilAsync(harness.Vm.InstallCaCommand, () => harness.Interception.IsRootTrusted);
        }

        await ExecuteUntilAsync(
            harness.Vm.TrustFirefoxCaCommand,
            () => !harness.Vm.IsStatusBusy || harness.Vm.StatusText.Length > 0);

        StringAssert.DoesNotMatch(
            harness.Vm.StatusText,
            new System.Text.RegularExpressions.Regex(
                "certutil not found on PATH",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    private static async Task ExecuteUntilAsync(ICommand command, Func<bool> done, int timeoutMs = 20000)
    {
        command.Execute(null);
        await WaitUntil(done, timeoutMs);
    }

    private static async Task WaitUntil(Func<bool> done, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!done())
        {
            if (Environment.TickCount64 >= deadline)
                Assert.Fail("Timed out waiting for trust decision condition.");
            await Task.Delay(25);
        }
    }

    private sealed class TrustHarness : IAsyncDisposable
    {
        public required InterceptionService Interception { get; init; }
        public required ScriptedInspectorDialogs Dialogs { get; init; }
        public required MainWindowViewModel Vm { get; init; }
        public required string Dir { get; init; }

        public static async Task<TrustHarness> CreateAsync(bool startProxy = true)
        {
            var dir = Path.Combine(Path.GetTempPath(), "ti-td-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var interception = new InterceptionService { UseInMemoryTrustState = true };
            OverrideRootPfx(interception, Path.Combine(dir, "rootCert.pfx"));
            var dialogs = new ScriptedInspectorDialogs
            {
                InstallRootCaResult = true,
                RemoveRootCaResult = true,
                RotateRootCaResult = true,
                StartProxyForDecryptResult = true,
                InstallRootCaBeforeFirefoxResult = true,
                PacReplaceResult = true,
            };
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
                dialogs)
            {
                BindPort = 0,
                BindAddress = "127.0.0.1",
            };

            if (startProxy)
            {
                await interception.StartAsync(IPAddress.Loopback, 0);
                Assert.IsTrue(interception.IsRunning);
            }

            return new TrustHarness
            {
                Interception = interception,
                Dialogs = dialogs,
                Vm = vm,
                Dir = dir,
            };
        }

        public ValueTask DisposeAsync()
        {
            try { Interception.EnsureShutdown(); } catch { /* best-effort */ }
            Interception.Dispose();
            try
            {
                if (Directory.Exists(Dir))
                    Directory.Delete(Dir, true);
            }
            catch
            {
                // best-effort
            }

            return ValueTask.CompletedTask;
        }

        private static void OverrideRootPfx(InterceptionService interception, string path)
        {
            var field = typeof(InterceptionService).GetField("_rootPfxPath",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            field!.SetValue(interception, path);
        }
    }
}
