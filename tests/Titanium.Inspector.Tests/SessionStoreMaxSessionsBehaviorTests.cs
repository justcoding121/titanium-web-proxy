using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
/// Exercises <see cref="SessionStore"/> retention at <see cref="SessionStoreOptions.MaxSessionsInMemory"/>,
/// including the bulk eviction path used when many rows exceed the cap at once.
/// </summary>
[TestClass]
public class SessionStoreMaxSessionsBehaviorTests
{
    private static SessionSnapshot Row(long id) =>
      new()
      {
          Id = id,
          Method = "GET",
          Url = $"https://example.com/{id}",
          StatusCode = 200,
          StartedUtc = DateTimeOffset.UtcNow.AddMilliseconds(id),
      };

    [TestMethod]
    public void BulkAdd_PastMaxSessions_EvictsOldestInOneBatch()
    {
        using var store = new SessionStore(new SessionStoreOptions
        {
            MaxSessionsInMemory = 8,
            SpillBodiesToDisk = false,
        });

        var removedBatches = new List<int>();
        store.SessionsRemoved += list => removedBatches.Add(list.Count);

        for (var i = 1; i <= 50; i++)
        {
            store.Add(Row(i));
        }

        Assert.AreEqual(8, store.Count);
        Assert.IsNull(store.TryGet(1));
        Assert.IsNull(store.TryGet(42));
        Assert.IsNotNull(store.TryGet(43));
        Assert.IsNotNull(store.TryGet(50));
        Assert.IsTrue(removedBatches.Count >= 1, "SessionsRemoved should fire for bulk eviction");
        Assert.AreEqual(42, removedBatches.Sum());
    }

    [TestMethod]
    public void AddMany_EnforcesMaxSessions_WithoutDuplicates()
    {
        using var store = new SessionStore(new SessionStoreOptions
        {
            MaxSessionsInMemory = 5,
            SpillBodiesToDisk = false,
        });

        var batch = Enumerable.Range(1, 40).Select(i => Row(i)).ToList();
        store.AddMany(batch);

        Assert.AreEqual(5, store.Count);
        Assert.IsNotNull(store.TryGet(40));
        Assert.IsNull(store.TryGet(35));
        Assert.AreEqual(5, store.Sessions.Count);
        Assert.AreEqual(5, store.Sessions.Distinct().Count());
    }

    [TestMethod]
    public void RetentionEviction_UpdatesMainWindowSessionCountHint()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "twp-max-sess-vm-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(settingsPath);
            var registry = new SessionRegistry(new SessionStoreOptions
            {
                MaxSessionsInMemory = 5,
                SpillBodiesToDisk = false,
            });
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));

            for (var i = 1; i <= 12; i++)
            {
                vm.SeedSession(Row(i));
            }

            Assert.AreEqual(5, vm.Sessions.Count);
            StringAssert.Contains(vm.SessionCountText, "5");
            StringAssert.Contains(vm.SessionCountText, "most recent");

            vm.SearchQuery = "host:missing.example";
            StringAssert.Contains(vm.SessionCountText, "7");
            StringAssert.Contains(vm.SessionCountText, "older removed by retention");
        }
        finally
        {
            TryDelete(settingsPath);
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
            // Best-effort temp cleanup.
        }
    }
}
