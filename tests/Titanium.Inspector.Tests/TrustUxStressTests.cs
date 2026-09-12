using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class TrustUxStressTests
{
    [TestMethod]
    [TestCategory("Inspector-Stress")]
    public async Task TrustActions_RepeatLoop_NoHangAndNoLeftoverBusy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-stress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var uxBefore = SnapshotUxTraceLength();
        try
        {
            using var interception = new InterceptionService { UseInMemoryTrustState = true };
            OverrideRootPfx(interception, Path.Combine(dir, "rootCert.pfx"));
            await interception.StartAsync(IPAddress.Loopback, 0);

            var dialogs = new ScriptedInspectorDialogs
            {
                InstallRootCaResult = true,
                RemoveRootCaResult = true,
                RotateRootCaResult = true,
                StartProxyForDecryptResult = true,
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

            const int loops = 6;
            for (var i = 0; i < loops; i++)
            {
                await WaitUntil(() => !vm.IsStatusBusy, 15000);
                if (!interception.IsRootTrusted)
                {
                    await ExecuteUntilAsync(vm.InstallCaCommand, () => interception.IsRootTrusted, 20000);
                    await WaitUntil(() => !vm.IsStatusBusy, 10000);
                }

                if (interception.IsRootTrusted)
                {
                    vm.DecryptHttps = true;
                    await WaitUntil(() => vm.DecryptHttps || vm.StatusText.Contains("Decrypt", StringComparison.OrdinalIgnoreCase), 10000);
                    vm.DecryptHttps = false;
                    await WaitUntil(() => !vm.DecryptHttps, 5000);
                }

                var rotateCalls = dialogs.RotateRootCaCalls;
                await ExecuteUntilAsync(
                    vm.RotateCaCommand,
                    () => dialogs.RotateRootCaCalls > rotateCalls && !vm.IsStatusBusy,
                    25000);

                var removeCalls = dialogs.RemoveRootCaCalls;
                await ExecuteUntilAsync(
                    vm.UntrustCaCommand,
                    () => dialogs.RemoveRootCaCalls > removeCalls && !vm.DecryptHttps && !vm.IsStatusBusy,
                    20000);
            }

            dialogs.RotateRootCaResult = true;
            var before = dialogs.RotateRootCaCalls;
            vm.RotateCaCommand.Execute(null);
            vm.RotateCaCommand.Execute(null);
            await WaitUntil(() => !vm.IsStatusBusy && dialogs.RotateRootCaCalls > before, 30000);

            Assert.IsFalse(vm.IsStatusBusy, vm.StatusText);
            AssertNoUnexpectedSlowUx(uxBefore);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    private static long SnapshotUxTraceLength()
    {
        try
        {
            var path = InspectorUxTrace.LogFilePath;
            return File.Exists(path) ? new FileInfo(path).Length : 0L;
        }
        catch
        {
            return 0L;
        }
    }

    private static void AssertNoUnexpectedSlowUx(long uxBefore)
    {
        try
        {
            var path = InspectorUxTrace.LogFilePath;
            if (!File.Exists(path))
                return;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (uxBefore > 0 && fs.Length > uxBefore)
                fs.Seek(uxBefore, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            var tail = reader.ReadToEnd();
            foreach (Match m in Regex.Matches(tail, @"SLOW\s+#\d+\s+(\S+)\s+ms=(\d+)"))
            {
                var name = m.Groups[1].Value;
                var ms = int.Parse(m.Groups[2].Value);
                if (ms >= 3000 &&
                    (name.Contains("AwaitTrustBg", StringComparison.OrdinalIgnoreCase)
                     || name.Contains("TrustBg.Job", StringComparison.OrdinalIgnoreCase)))
                {
                    Assert.Fail($"Unexpected UxTrace SLOW {name} ms={ms}");
                }
            }
        }
        catch (AssertFailedException)
        {
            throw;
        }
        catch
        {
            // tracing optional
        }
    }

    private static void OverrideRootPfx(InterceptionService interception, string path)
    {
        var field = typeof(InterceptionService).GetField("_rootPfxPath",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        field!.SetValue(interception, path);
    }

    private static async Task ExecuteUntilAsync(ICommand command, Func<bool> done, int timeoutMs)
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
                Assert.Fail("Timed out in trust stress loop.");
            await Task.Delay(25);
        }
    }
}
