using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionDiffIntegrationTests
{
    [TestMethod]
    public async Task ViewModel_DiffSessions_RequiresTwoAndSetsText()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-session-diff-" + Guid.NewGuid().ToString("N") + ".json");
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

            Assert.IsFalse(vm.CanDiffSessions);
            Assert.IsFalse(vm.TryBuildSessionDiff(out _));

            var a = new SessionSnapshot { Id = 1, Method = "GET", Url = "https://x/", StatusCode = 200, ResponseBodyText = "a" };
            var b = new SessionSnapshot { Id = 2, Method = "GET", Url = "https://x/", StatusCode = 200, ResponseBodyText = "b" };
            vm.SeedSession(a);
            vm.SeedSession(b);
            vm.SetSelectedSessions([a, b]);
            Assert.IsTrue(vm.CanDiffSessions);
            Assert.IsTrue(vm.TryBuildSessionDiff(out var diff));
            Assert.IsTrue(diff.HasDifferences);

            if (vm.DiffSessionsCommand.CanExecute(null))
            {
                vm.DiffSessionsCommand.Execute(null);
            }

            await Task.Delay(50);
            StringAssert.Contains(vm.SessionDiffText, "- a");
            StringAssert.Contains(vm.StatusText, "Session Diff");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
