using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
/// Search-token toggles and clear-sessions behavior on <see cref="MainWindowViewModel"/>
/// without Avalonia views.
/// </summary>
[TestClass]
public class MainWindowViewModelSearchAndClearTests
{
    [TestMethod]
    public void HideTunnelsFilter_AddsAndRemovesToken_AndFiltersExistingRows()
    {
        // Bug class: hide:tunnel toggle leaves tunnel sessions visible / permanently hides them after untoggle.
        var path = Path.Combine(Path.GetTempPath(), "twp-hide-tunnel-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vm = CreateVm(path);
            vm.SeedSession(new SessionSnapshot
            {
                Id = 1,
                Method = "CONNECT",
                Url = "https://tunnel.example:443",
                Host = "tunnel.example",
                IsTunnel = true,
            });
            vm.SeedSession(new SessionSnapshot
            {
                Id = 2,
                Method = "GET",
                Url = "https://app.example/",
                Host = "app.example",
                StatusCode = 200,
            });
            Assert.AreEqual(2, vm.Sessions.Count);

            vm.HideTunnelsFilter = true;
            StringAssert.Contains(vm.SearchQuery, "hide:tunnel");
            Assert.AreEqual(1, vm.Sessions.Count);
            Assert.AreEqual(2, vm.Sessions[0].Id);

            vm.HideTunnelsFilter = false;
            Assert.IsFalse(vm.SearchQuery.Contains("hide:tunnel", StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(2, vm.Sessions.Count);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void ErrorsOnlyFilter_KeepsOnlyErrorRows()
    {
        // Bug class: is:error filter matches every session or never matches 5xx.
        var path = Path.Combine(Path.GetTempPath(), "twp-errors-only-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vm = CreateVm(path);
            vm.SeedSession(new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://ok.example/",
                Host = "ok.example",
                StatusCode = 200,
            });
            vm.SeedSession(new SessionSnapshot
            {
                Id = 2,
                Method = "GET",
                Url = "https://bad.example/",
                Host = "bad.example",
                StatusCode = 503,
            });

            vm.ErrorsOnlyFilter = true;
            StringAssert.Contains(vm.SearchQuery, "is:error");
            Assert.AreEqual(1, vm.Sessions.Count);
            Assert.AreEqual(2, vm.Sessions[0].Id);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void ClearSessions_EmptiesTheGrid_AndDisablesClearCommand()
    {
        // Bug class: ClearSessions leaves SessionStore entries that reappear after a filter change.
        var path = Path.Combine(Path.GetTempPath(), "twp-clear-sessions-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var vm = CreateVm(path);
            vm.SeedSession(new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://clear.example/",
                Host = "clear.example",
                StatusCode = 200,
            });
            Assert.IsTrue(vm.HasSessions);
            Assert.IsTrue(vm.ClearSessionsCommand.CanExecute(null));

            vm.ClearSessionsCommand.Execute(null);

            Assert.AreEqual(0, vm.Sessions.Count);
            Assert.IsFalse(vm.HasSessions);
            Assert.IsFalse(vm.ClearSessionsCommand.CanExecute(null));
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static MainWindowViewModel CreateVm(string settingsPath)
    {
        var settings = new SettingsService(settingsPath);
        var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
        return new MainWindowViewModel(
            new SessionStreamBuffer(registry),
            registry,
            new UpdateService(settings),
            settings,
            new InterceptionService(new RecordingSystemProxyController()));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }
}
