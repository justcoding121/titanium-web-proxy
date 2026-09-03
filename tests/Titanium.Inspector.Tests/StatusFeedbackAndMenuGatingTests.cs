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

    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        await Task.Delay(50);
    }
}
