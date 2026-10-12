using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionCleanupTests
{
    private static readonly long[] SessionSeven = [7L];

    [TestMethod]
    public void KeepOnly_RemovesHiddenRows_AndCancelRemovesNothing()
    {
        var (vm, dialogs, registry) = NewVm();
        var keep = new SessionSnapshot { Id = 1, Url = "https://a/", StatusCode = 200, DurationMs = 1 };
        var hidden = new SessionSnapshot { Id = 2, Url = "https://a/", StatusCode = 404, DurationMs = 1 };
        var inFlight = new SessionSnapshot { Id = 3, Url = "https://a/", Method = "GET" };
        vm.SeedSession(keep);
        vm.SeedSession(hidden);
        vm.SeedSession(inFlight);
        vm.SearchQuery = "status:200";
        vm.SelectedSession = keep;

        dialogs.RemoveSessionsResult = false;
        vm.KeepOnlySelectedCommand.Execute(null);
        Assert.AreEqual(1, dialogs.RemoveSessionsCalls);
        Assert.AreEqual(2, dialogs.LastRemoveCount);
        Assert.AreEqual(2, dialogs.LastHiddenCount);
        Assert.IsNotNull(registry.TryGet(2));
        Assert.IsNotNull(registry.TryGet(3));

        dialogs.RemoveSessionsResult = true;
        vm.KeepOnlySelectedCommand.Execute(null);
        Assert.IsNotNull(registry.TryGet(1));
        Assert.IsNull(registry.TryGet(2));
        Assert.IsNull(registry.TryGet(3));
    }

    [TestMethod]
    public void RemoveMatching_UsesTheVisibleFilter_AndEmptyFilterRemovesNothing()
    {
        var (vm, dialogs, registry) = NewVm();
        vm.SeedSession(new SessionSnapshot { Id = 1, Url = "https://a/", StatusCode = 200 });
        vm.SeedSession(new SessionSnapshot { Id = 2, Url = "https://a/", StatusCode = 404 });
        vm.SearchQuery = "status:404";
        dialogs.RemoveSessionsResult = true;
        Assert.IsTrue(vm.RemoveMatchingFilterCommand.CanExecute(null));
        vm.RemoveMatchingFilterCommand.Execute(null);
        Assert.AreEqual(1, dialogs.LastRemoveCount);
        Assert.AreEqual(0, dialogs.LastHiddenCount);
        Assert.IsNotNull(registry.TryGet(1));
        Assert.IsNull(registry.TryGet(2));

        var calls = dialogs.RemoveSessionsCalls;
        vm.SearchQuery = "";
        Assert.IsFalse(vm.CanRemoveMatchingFilter);
        Assert.IsFalse(vm.RemoveMatchingFilterCommand.CanExecute(null));
        vm.RemoveMatchingFilterCommand.Execute(null);
        Assert.AreEqual(calls, dialogs.RemoveSessionsCalls);
        Assert.IsNotNull(registry.TryGet(1));
    }

    [TestMethod]
    public void NothingSelected_DoesNotKeepOnly()
    {
        var (vm, dialogs, registry) = NewVm();
        vm.SeedSession(new SessionSnapshot { Id = 1, Url = "https://a/", StatusCode = 200 });
        Assert.IsFalse(vm.KeepOnlySelectedCommand.CanExecute(null));
        vm.KeepOnlySelectedCommand.Execute(null);
        Assert.AreEqual(0, dialogs.RemoveSessionsCalls);
        Assert.IsNotNull(registry.TryGet(1));
    }

    [TestMethod]
    public async Task Remove_DeletesSpilledHar()
    {
        var cache = Path.Combine(Path.GetTempPath(), "ti-clean-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        using var registry = new SessionRegistry(
            new SessionStoreOptions { SpillBodiesToDisk = true, MaxSessionsInMemory = 20 },
            cache);
        registry.Add(new SessionSnapshot
        {
            Id = 7,
            Url = "https://a/",
            StatusCode = 200,
            DurationMs = 5,
            ResponseBodyText = "body",
        });
        string? path = null;
        for (var i = 0; i < 40 && path is null; i++)
        {
            path = Directory.EnumerateFiles(cache, "7.har", SearchOption.AllDirectories).FirstOrDefault();
            if (path is null)
            {
                await Task.Delay(50);
            }
        }

        Assert.IsNotNull(path, "spill did not write 7.har");
        registry.Store.Remove(SessionSeven);
        var gone = false;
        for (var i = 0; i < 40 && !gone; i++)
        {
            gone = !File.Exists(path);
            if (!gone)
            {
                await Task.Delay(50);
            }
        }

        Assert.IsTrue(gone, path);
        Assert.IsNull(registry.TryGet(7));
    }

    [TestMethod]
    public async Task Remove_WhileSpillPending_DoesNotRecreateHar()
    {
        var cache = Path.Combine(Path.GetTempPath(), "ti-clean-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions { SpillBodiesToDisk = true, MaxSessionsInMemory = 50 },
                cache);

            // Queue a spill then Remove immediately — before the writer drains — so the
            // pending write must not recreate the deleted HAR.
            store.Add(new SessionSnapshot
            {
                Id = 42,
                Url = "https://race.test/",
                StatusCode = 200,
                DurationMs = 3,
                ResponseBodyText = new string('x', 4096),
            });
            store.Remove([42L]);

            await store.FlushDiskCleanupAsync(TimeSpan.FromSeconds(5));

            Assert.IsNull(store.TryGet(42));
            Assert.IsFalse(
                Directory.EnumerateFiles(cache, "42.har", SearchOption.AllDirectories).Any(),
                "pending spill must not recreate HAR after Remove");
        }
        finally
        {
            try
            {
                Directory.Delete(cache, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static (MainWindowViewModel Vm, ScriptedInspectorDialogs Dialogs, SessionRegistry Registry) NewVm()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-clean-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new SettingsService(Path.Combine(dir, "settings.json"));
        settings.Current.AutoStartCapture = false;
        settings.Save();
        var dialogs = new ScriptedInspectorDialogs();
        var registry = new SessionRegistry();
        var vm = new MainWindowViewModel(new InspectorViewModelServices(
            new SessionStreamBuffer(registry),
            registry,
            new UpdateService(settings),
            settings,
            new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true },
            dialogs,
            new ScriptedInspectorPathPicker()));
        return (vm, dialogs, registry);
    }
}
