using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class StatusFeedbackAndMenuGatingTests
{
    [TestMethod]
    public void SessionMenuCommands_CanExecute_DependsOnSelectionAndStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-menu-gate-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));

            Assert.IsFalse(vm.HasSessions);
            Assert.IsFalse(vm.HasSelectedSessions);
            Assert.IsFalse(vm.ClearSessionsCommand.CanExecute(null));
            Assert.IsFalse(vm.RemoveSelectedSessionsCommand.CanExecute(null));
            Assert.IsFalse(vm.ExportSelectedHarCommand.CanExecute(null));
            Assert.IsFalse(vm.ExportSelectedArchiveCommand.CanExecute(null));

            var snap = new SessionSnapshot
            {
                Id = 7,
                Method = "GET",
                Url = "https://example.com/",
                Host = "example.com",
            };
            vm.SeedSession(snap);
            Assert.IsTrue(vm.HasSessions);
            Assert.IsTrue(vm.ClearSessionsCommand.CanExecute(null));
            Assert.IsFalse(vm.RemoveSelectedSessionsCommand.CanExecute(null));
            Assert.IsFalse(vm.ExportSelectedHarCommand.CanExecute(null));

            vm.SetSelectedSessions([snap]);
            Assert.IsTrue(vm.HasSelectedSessions);
            Assert.IsTrue(vm.RemoveSelectedSessionsCommand.CanExecute(null));
            Assert.IsTrue(vm.ExportSelectedHarCommand.CanExecute(null));
            Assert.IsTrue(vm.ExportSelectedArchiveCommand.CanExecute(null));

            vm.ClearSessionsCommand.Execute(null);
            Assert.IsFalse(vm.HasSessions);
            Assert.IsFalse(vm.ClearSessionsCommand.CanExecute(null));
            Assert.IsFalse(vm.RemoveSelectedSessionsCommand.CanExecute(null));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void SetStatus_UpdatesSeverityBusyAndToastsWhenImportant()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-status-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            var notifier = new RecordingStatusNotifier();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()),
                statusNotifier: notifier);

            vm.SetStatus("Working…", StatusSeverity.Busy);
            Assert.AreEqual("Working…", vm.StatusText);
            Assert.AreEqual(StatusSeverity.Busy, vm.StatusSeverity);
            Assert.IsTrue(vm.IsStatusBusy);
            Assert.AreEqual(0, notifier.Calls.Count);

            var tickBefore = vm.StatusAttentionTick;
            vm.SetStatus("Titanium Inspector is up to date (Stable).", StatusSeverity.Success, toastImportant: true);
            Assert.IsFalse(vm.IsStatusBusy);
            Assert.AreEqual(StatusSeverity.Success, vm.StatusSeverity);
            Assert.IsTrue(vm.StatusAttentionTick > tickBefore);
            Assert.AreEqual(1, notifier.Calls.Count);
            Assert.AreEqual(StatusSeverity.Success, notifier.Calls[0].Severity);
            StringAssert.Contains(notifier.Calls[0].Message, "up to date");

            vm.StatusText = "Capturing on";
            Assert.AreEqual(StatusSeverity.Neutral, vm.StatusSeverity);
            Assert.IsFalse(vm.IsStatusBusy);
            Assert.AreEqual(1, notifier.Calls.Count);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task ClearSessions_ToastsSuccess()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-clear-toast-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            var notifier = new RecordingStatusNotifier();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()),
                statusNotifier: notifier);

            vm.SeedSession(new SessionSnapshot { Id = 1, Method = "GET", Url = "https://a/" });
            await ExecuteAsync(vm.ClearSessionsCommand);
            StringAssert.Contains(vm.StatusText, "cleared");
            Assert.AreEqual(StatusSeverity.Success, vm.StatusSeverity);
            Assert.AreEqual(1, notifier.Calls.Count);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task StartCapture_HealthyProxy_UsesNeutralSteadyStatus()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-steady-status-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            settings.Current.AutoStartCapture = false;
            settings.Current.AutoSystemProxyOnStart = false;
            settings.Save();

            var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(new SessionRegistry()),
                new SessionRegistry(),
                new UpdateService(settings),
                settings,
                interception);

            vm.BindPort = 0;
            vm.StartCaptureCommand.Execute(null);
            await WaitUntil(() => interception.IsRunning);

            Assert.AreEqual("Ready", vm.StatusText);
            Assert.AreEqual(StatusSeverity.Neutral, vm.StatusSeverity);
            StringAssert.Contains(vm.EndpointStatusText, "Proxy running on");
            Assert.IsFalse(vm.IsStatusBusy);

            vm.EnsureShutdown();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task SetTransientStatus_RevertsToReadyAfterDelay()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-transient-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            settings.Current.AutoStartCapture = false;
            settings.Save();

            var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(new SessionRegistry()),
                new SessionRegistry(),
                new UpdateService(settings),
                settings,
                interception);

            vm.BindPort = 0;
            vm.StartCaptureCommand.Execute(null);
            await WaitUntil(() => interception.IsRunning);
            Assert.AreEqual("Ready", vm.StatusText);

            vm.SetTransientStatus("Exported 1 sessions", StatusSeverity.Success, revertMs: 100);
            Assert.AreEqual(StatusSeverity.Success, vm.StatusSeverity);

            await Task.Delay(250);
            Assert.AreEqual("Ready", vm.StatusText);
            Assert.AreEqual(StatusSeverity.Neutral, vm.StatusSeverity);

            vm.EnsureShutdown();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task SetTransientStatus_UsesToastSeveritySeparateFromBar()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-toast-severity-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            var notifier = new RecordingStatusNotifier();
            var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                statusNotifier: notifier);

            vm.BindPort = 0;
            vm.StartCaptureCommand.Execute(null);
            await WaitUntil(() => interception.IsRunning);

            vm.SetTransientStatus(
                "Titanium Inspector is up to date (Stable).",
                StatusSeverity.Neutral,
                toastImportant: true,
                revertMs: 50,
                toastSeverity: StatusSeverity.Success);

            Assert.AreEqual(StatusSeverity.Neutral, vm.StatusSeverity);
            Assert.AreEqual("Titanium Inspector is up to date (Stable).", vm.StatusText);
            Assert.AreEqual(1, notifier.Calls.Count);
            Assert.AreEqual(StatusSeverity.Success, notifier.Calls[0].Severity);

            await Task.Delay(150);
            Assert.AreEqual("Ready", vm.StatusText);
            Assert.AreEqual(StatusSeverity.Neutral, vm.StatusSeverity);

            vm.EnsureShutdown();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!predicate())
        {
            if (Environment.TickCount64 >= deadline)
            {
                Assert.Fail("Timed out waiting for condition.");
            }

            await Task.Delay(25);
        }
    }

    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        await Task.Delay(50);
    }
}
