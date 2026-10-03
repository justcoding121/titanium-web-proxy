using System.Collections.Specialized;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionListResponsivenessTests
{
    [TestMethod]
    public void ClearSessions_RaisesOneReset_NotPerRowRemoves()
    {
        var vm = CreateVm(out var settingsPath);
        try
        {
            for (var i = 1; i <= 40; i++)
            {
                vm.SeedSession(new SessionSnapshot
                {
                    Id = i,
                    Method = "GET",
                    Url = $"https://example.com/{i}",
                    Host = "example.com",
                });
            }

            var adds = 0;
            var removes = 0;
            var resets = 0;
            vm.Sessions.CollectionChanged += (_, e) => Count(e, ref adds, ref removes, ref resets);

            vm.ClearSessionsCommand.Execute(null);

            Assert.AreEqual(0, vm.Sessions.Count);
            Assert.AreEqual(1, resets);
            Assert.AreEqual(0, removes);
            Assert.AreEqual(0, adds);
        }
        finally
        {
            TryDelete(settingsPath);
        }
    }

    [TestMethod]
    public void HeaderFilter_ReplacesVisibleRowsWithOneReset()
    {
        var vm = CreateVm(out var settingsPath);
        try
        {
            for (var i = 1; i <= 30; i++)
            {
                vm.SeedSession(new SessionSnapshot
                {
                    Id = i,
                    Method = "GET",
                    Url = $"https://host{i}.test/",
                    Host = $"host{i}.test",
                });
            }

            var adds = 0;
            var removes = 0;
            var resets = 0;
            vm.Sessions.CollectionChanged += (_, e) => Count(e, ref adds, ref removes, ref resets);

            vm.SearchQuery = "host:host1.test";

            Assert.AreEqual(1, vm.Sessions.Count);
            Assert.AreEqual(1, resets);
            Assert.AreEqual(0, adds);
            Assert.AreEqual(0, removes);
        }
        finally
        {
            TryDelete(settingsPath);
        }
    }

    [TestMethod]
    public void BodyFilter_MatchesOffTheCallerThread()
    {
        var vm = CreateVm(out var settingsPath);
        try
        {
            vm.SeedSession(new SessionSnapshot
            {
                Id = 1,
                Method = "POST",
                Url = "https://example.com/a",
                StatusCode = 200,
                RequestBodyText = "alpha-needle",
            });
            vm.SeedSession(new SessionSnapshot
            {
                Id = 2,
                Method = "POST",
                Url = "https://example.com/b",
                StatusCode = 200,
                RequestBodyText = "other",
            });

            var caller = Environment.CurrentManagedThreadId;
            vm.SearchQuery = "body:alpha-needle";

            Assert.AreNotEqual(0, vm.LastBodyFilterThreadId);
            Assert.AreNotEqual(caller, vm.LastBodyFilterThreadId);
            Assert.AreEqual(1, vm.Sessions.Count);
            Assert.AreEqual(1, vm.Sessions[0].Id);
        }
        finally
        {
            TryDelete(settingsPath);
        }
    }

    [TestMethod]
    public async Task ExportHar_WritesOffTheCallerThread()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-export-thread-" + Guid.NewGuid().ToString("N") + ".json");
        var harPath = Path.Combine(Path.GetTempPath(), "twp-export-thread-" + Guid.NewGuid().ToString("N") + ".har");
        try
        {
            var settings = new SettingsService(settingsPath);
            var picker = new ScriptedInspectorPathPicker { SavePath = harPath };
            var vm = CreateVm(settings, picker);
            vm.SeedSession(new SessionSnapshot
            {
                Id = 7,
                Method = "GET",
                Url = "https://example.com/export",
                StatusCode = 200,
            });

            var caller = Environment.CurrentManagedThreadId;
            vm.ExportHarCommand.Execute(null);
            await WaitUntil(() => vm.StatusText.Contains("Exported", StringComparison.Ordinal));

            Assert.IsTrue(File.Exists(harPath));
            Assert.AreNotEqual(0, vm.LastExportThreadId);
            Assert.AreNotEqual(caller, vm.LastExportThreadId);
        }
        finally
        {
            TryDelete(settingsPath);
            TryDelete(harPath);
        }
    }

    [TestMethod]
    public async Task ImportHar_AppendsWithOneGridReset()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-import-reset-" + Guid.NewGuid().ToString("N") + ".json");
        var harPath = Path.Combine(Path.GetTempPath(), "twp-import-reset-" + Guid.NewGuid().ToString("N") + ".har");
        try
        {
            var sessions = new List<SessionSnapshot>();
            for (var i = 1; i <= 5; i++)
            {
                sessions.Add(new SessionSnapshot
                {
                    Id = i,
                    Method = "GET",
                    Url = $"https://import.test/{i}",
                    StatusCode = 200,
                });
            }

            await SessionArchive.ExportHarAsync(sessions, harPath);

            var settings = new SettingsService(settingsPath);
            var picker = new ScriptedInspectorPathPicker { OpenPath = harPath };
            var vm = CreateVm(settings, picker);
            var adds = 0;
            var removes = 0;
            var resets = 0;
            vm.Sessions.CollectionChanged += (_, e) => Count(e, ref adds, ref removes, ref resets);

            vm.ImportHarCommand.Execute(null);
            await WaitUntil(() => vm.StatusText.Contains("Appended 5", StringComparison.Ordinal));

            Assert.AreEqual(5, vm.Sessions.Count);
            Assert.AreEqual(1, resets);
            Assert.AreEqual(0, adds);
            Assert.AreEqual(0, removes);
        }
        finally
        {
            TryDelete(settingsPath);
            TryDelete(harPath);
        }
    }

    [TestMethod]
    public void RetentionEviction_UpdatesGridWithOneReset()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-retain-grid-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(settingsPath);
            var registry = new SessionRegistry(new SessionStoreOptions
            {
                MaxSessionsInMemory = 50,
                SpillBodiesToDisk = false,
            });
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));

            for (var i = 1; i <= 40; i++)
            {
                vm.SeedSession(new SessionSnapshot
                {
                    Id = i,
                    Method = "GET",
                    Url = $"https://example.com/{i}",
                });
            }

            var adds = 0;
            var removes = 0;
            var resets = 0;
            vm.Sessions.CollectionChanged += (_, e) => Count(e, ref adds, ref removes, ref resets);

            registry.Store.ApplyOptions(new SessionStoreOptions
            {
                MaxSessionsInMemory = 2,
                SpillBodiesToDisk = false,
            });

            Assert.AreEqual(2, vm.Sessions.Count);
            Assert.AreEqual(1, resets);
            Assert.AreEqual(0, removes);
            Assert.AreEqual(0, adds);
        }
        finally
        {
            TryDelete(settingsPath);
        }
    }

    [TestMethod]
    public void RemoveSelected_DoesNotResetSessionIds_ClearDoes()
    {
        var vm = CreateVm(out var settingsPath);
        try
        {
            vm.SeedSession(new SessionSnapshot { Id = 1, Method = "GET", Url = "https://a.test/1" });
            vm.SeedSession(new SessionSnapshot { Id = 2, Method = "GET", Url = "https://a.test/2" });
            vm.SetSelectedSessions([vm.Sessions[0]]);

            var nextId = typeof(InterceptionService).GetField(
                "_nextId",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(nextId);
            nextId!.SetValue(vm.Interception, 40L);

            vm.RemoveSelectedSessionsCommand.Execute(null);
            Assert.AreEqual(1, vm.Sessions.Count);
            Assert.AreEqual(40L, (long)nextId.GetValue(vm.Interception)!);

            vm.ClearSessionsCommand.Execute(null);
            Assert.AreEqual(0, vm.Sessions.Count);
            Assert.AreEqual(0L, (long)nextId.GetValue(vm.Interception)!);
        }
        finally
        {
            TryDelete(settingsPath);
        }
    }

    [TestMethod]
    public async Task Clear_DropsCurrentRunOnly_AndDoesNotReloadOldBody()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-clear-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 100,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);

            var otherRun = Path.Combine(dir, "other-run");
            Directory.CreateDirectory(otherRun);
            var otherHar = Path.Combine(otherRun, "99.har");
            await File.WriteAllTextAsync(otherHar, "{}");

            var first = new SessionSnapshot
            {
                Id = 10,
                Method = "GET",
                Url = "https://example.com/old",
                StatusCode = 200,
                ResponseBodyText = "secret-old-body",
                ResponseBodyCapture = BodyCaptureState.Complete,
            };
            store.Add(first);
            await store.FlushSpillAsync();
            Assert.IsTrue(File.Exists(store.DiskCacheRunDirectoryPath is null
                ? ""
                : Path.Combine(store.DiskCacheRunDirectoryPath, "10.har")));

            store.Clear();
            Assert.AreEqual(0, store.Count);
            await store.FlushDiskCleanupAsync();

            Assert.IsTrue(File.Exists(otherHar));
            Assert.IsFalse(Directory.EnumerateFiles(dir, "10.har", SearchOption.AllDirectories).Any());

            var again = new SessionSnapshot
            {
                Id = 10,
                Method = "GET",
                Url = "https://example.com/new",
                StatusCode = 200,
            };
            Assert.IsFalse(store.TryMatchBodySearch(again, "secret-old-body"));
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public async Task Remove_DeletesOnlyThoseHarFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-remove-har-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 100,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);

            store.Add(SessionWithBody(1));
            store.Add(SessionWithBody(2));
            await store.FlushSpillAsync();

            store.Remove([1]);
            Assert.IsNull(store.TryGet(1));
            Assert.IsNotNull(store.TryGet(2));
            await store.FlushDiskCleanupAsync();

            Assert.IsFalse(Directory.EnumerateFiles(dir, "1.har", SearchOption.AllDirectories).Any());
            Assert.IsTrue(Directory.EnumerateFiles(dir, "2.har", SearchOption.AllDirectories).Any());
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [TestMethod]
    public async Task LowerDiskBudget_DeletesFilesOffTheCallerThread()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-budget-bg-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 100,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);

            store.Add(SessionWithBody(1, 4000));
            await store.FlushSpillAsync();
            var har = Directory.EnumerateFiles(dir, "1.har", SearchOption.AllDirectories).Single();
            Assert.IsTrue(new FileInfo(har).Length > 100);

            var caller = Environment.CurrentManagedThreadId;
            store.ApplyOptions(new SessionStoreOptions
            {
                MaxSessionsInMemory = 100,
                SpillBodiesToDisk = true,
                DiskCacheMaxBytes = 1,
            });
            Assert.IsNotNull(store.TryGet(1), "A lower disk budget keeps the list row");
            await store.FlushDiskCleanupAsync();

            Assert.AreNotEqual(0, store.LastDiskCleanupThreadId);
            Assert.AreNotEqual(caller, store.LastDiskCleanupThreadId);
            Assert.IsFalse(File.Exists(har));
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    private static SessionSnapshot SessionWithBody(long id, int bytes = 128) =>
        new()
        {
            Id = id,
            Method = "GET",
            Url = $"https://example.com/{id}",
            StatusCode = 200,
            ResponseBodyBytes = new byte[bytes],
            ResponseBodyText = new string('x', Math.Min(bytes, 64)),
            ResponseBodyCapture = BodyCaptureState.Complete,
        };

    private static MainWindowViewModel CreateVm(out string settingsPath)
    {
        settingsPath = Path.Combine(Path.GetTempPath(), "twp-responsive-" + Guid.NewGuid().ToString("N") + ".json");
        return CreateVm(new SettingsService(settingsPath), pathPicker: null);
    }

    private static MainWindowViewModel CreateVm(SettingsService settings, IInspectorPathPicker? pathPicker)
    {
        var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
        return new MainWindowViewModel(
            new SessionStreamBuffer(registry),
            registry,
            new UpdateService(settings),
            settings,
            new InterceptionService(new RecordingSystemProxyController()),
            dialogs: null,
            pathPicker: pathPicker);
    }

    private static void Count(NotifyCollectionChangedEventArgs e, ref int adds, ref int removes, ref int resets)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                adds++;
                break;
            case NotifyCollectionChangedAction.Remove:
                removes++;
                break;
            case NotifyCollectionChangedAction.Reset:
                resets++;
                break;
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("Condition was not met in time");
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
            // Best-effort temp cleanup.
        }
    }
}
