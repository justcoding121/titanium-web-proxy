using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: SessionStore spill reload/export paths, MainWindowViewModel
///     selection body hydration, quick-filter notifications, and command guards without dialogs.
/// </summary>
[TestClass]
public class InspectorVmSessionsAndStoreBodiesSoftCoverageBehaviorTests
{
    [TestMethod]
    public async Task SessionStore_WithBodiesForExportAsync_ReloadsSpilledBodies()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-export-bodies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    SpillBodiesToDisk = true,
                    MaxSessionsInMemory = 50,
                    DiskCacheMaxBytes = 4 * 1024 * 1024,
                },
                root);
            var snap = new SessionSnapshot
            {
                Id = 12,
                Method = "POST",
                Url = "https://export.example/submit",
                StatusCode = 200,
                RequestBodyText = new string('r', 120),
                ResponseBodyText = new string('s', 80),
            };
            store.Add(snap);
            await store.FlushSpillAsync();
            snap.RequestBodyText = null;
            snap.ResponseBodyBytes = null;
            snap.ResponseBodyText = null;
            snap.ResponseBodyBytes = null;

            string? seenReq = null;
            await store.WithBodiesForExportAsync([snap], async list =>
            {
                seenReq = list[0].RequestBodyText;
                await Task.CompletedTask;
            });
            Assert.IsNotNull(seenReq);
            Assert.AreEqual(120, seenReq!.Length);
            Assert.IsNull(snap.RequestBodyBytes, "export path unloads RAM bodies afterward");
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    [TestMethod]
    public async Task MainWindowViewModel_SelectSpilledSession_LoadsBodies()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-vm-spill-" + Guid.NewGuid().ToString("N") + ".json");
        var cacheDir = Path.Combine(Path.GetTempPath(), "twp-vm-spill-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDir);
        try
        {
            var settings = new SettingsService(settingsPath);
            var registry = new SessionRegistry(
                new SessionStoreOptions
                {
                    SpillBodiesToDisk = true,
                    MaxSessionsInMemory = 50,
                    DiskCacheMaxBytes = 4 * 1024 * 1024,
                },
                cacheDir);
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true });

            var snap = new SessionSnapshot
            {
                Id = 5,
                Method = "GET",
                Url = "https://spill.example/page",
                Host = "spill.example",
                StatusCode = 200,
                RequestBodyText = "req",
                ResponseBodyText = "resp",
            };
            vm.SeedSession(snap);
            await registry.Store.FlushSpillAsync();
            snap.RequestBodyText = null;
            snap.ResponseBodyText = null;
            snap.RequestBodyBytes = null;
            snap.ResponseBodyBytes = null;

            vm.SelectedSession = snap;
            await WaitUntil(() => !string.IsNullOrEmpty(snap.RequestBodyText) || snap.RequestBodyBytes is { Length: > 0 });
            Assert.IsFalse(snap.BodiesMissingFromDisk);
        }
        finally
        {
            TryDelete(settingsPath);
            TryDeleteDir(cacheDir);
        }
    }

    [TestMethod]
    public void MainWindowViewModel_ToggleAlwaysOnTopCommand_FlipsProperty()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-vm-aot-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false })),
                new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false }),
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true });

            Assert.IsFalse(vm.AlwaysOnTop);
            Assert.IsTrue(vm.ToggleAlwaysOnTopCommand.CanExecute(null));
            vm.ToggleAlwaysOnTopCommand.Execute(null);
            Assert.IsTrue(vm.AlwaysOnTop);
            vm.ToggleAlwaysOnTopCommand.Execute(null);
            Assert.IsFalse(vm.AlwaysOnTop);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void MainWindowViewModel_RemoveSelected_NoSelection_SetsGuardStatus()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-vm-rm-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false })),
                new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false }),
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true });

            vm.RemoveSelectedSessionsCommand.Execute(null);
            StringAssert.Contains(vm.StatusText, "Select one or more sessions");
            Assert.IsFalse(vm.RemoveSelectedSessionsCommand.CanExecute(null));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [TestMethod]
    public void MainWindowViewModel_QuickFilters_NotifySessionCountAndMethodRows()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-vm-filters-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true });

            vm.SeedSession(new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://a.example/x.png",
                Host = "a.example",
                StatusCode = 200,
            });
            vm.SeedSession(new SessionSnapshot
            {
                Id = 2,
                Method = "GET",
                Url = "https://b.example/api",
                Host = "b.example",
                StatusCode = 500,
            });

            var before = vm.SessionCountText;
            vm.HideImagesFilter = true;
            Assert.IsTrue(vm.HideImagesFilter);
            Assert.AreNotEqual(before, vm.SessionCountText);
            vm.ErrorsOnlyFilter = true;
            Assert.IsTrue(vm.ErrorsOnlyFilter);
            vm.ErrorsOnlyFilter = false;
            vm.HideImagesFilter = false;
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 12_000)
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
            // Best-effort.
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort.
        }
    }
}
