using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     SessionBodyDiskCache clear/wipe edges and SessionStore missing-body hydration that remain thin on develop.
/// </summary>
[TestClass]
public class SessionBodyDiskCacheAndStoreMissingBodyBehaviorTests
{
    [TestMethod]
    public void ClearAllRuns_WithHarFilesAndArtifacts_WipesTrackedBodies()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-disk-wipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var cache = new SessionBodyDiskCache(root, maxBytes: 10_000_000, maxAge: TimeSpan.FromDays(7));
            cache.Write(new SessionSnapshot
            {
                Id = 42,
                Method = "POST",
                Url = "https://disk.example/",
                RequestBodyText = "req-body",
                ResponseBodyText = "resp-body",
                StatusCode = 200,
            });
            Assert.IsTrue(cache.FileExists(42));

            var orphanDir = Path.Combine(root, "orphan-empty");
            Directory.CreateDirectory(orphanDir);
            File.WriteAllText(Path.Combine(root, "legacy-session.tmp"), "x");
            File.WriteAllText(Path.Combine(root, "stale.bin"), "y");

            cache.ClearAll();
            cache.ClearAllRuns();
            Assert.IsFalse(cache.FileExists(42));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void ClearAllRuns_WhenRootIsGone_DoesNotThrow()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-disk-gone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cache = new SessionBodyDiskCache(root, maxBytes: 1_000_000, maxAge: TimeSpan.FromDays(1));
        try
        {
            Directory.Delete(root, recursive: true);
            cache.ClearAllRuns();
        }
        finally
        {
            cache.Dispose();
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task SessionStore_EnsureBodiesLoaded_MarksMissingWhenNoSpillFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-store-miss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new SessionStoreOptions
            {
                SpillBodiesToDisk = true,
                MaxSessionsInMemory = 100,
            };
            using var store = new SessionStore(options, cacheDirectory: root);
            var snap = new SessionSnapshot
            {
                Id = 7,
                Method = "GET",
                Url = "https://miss.example/",
                BodiesOnDisk = true,
                RequestBodyText = null,
                ResponseBodyText = null,
            };
            store.Add(snap);

            await store.EnsureBodiesLoadedAsync(snap);
            Assert.IsTrue(snap.BodiesMissingFromDisk);

            await store.EnsureBodiesLoadedAsync([snap]);
            Assert.IsTrue(snap.BodiesMissingFromDisk);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string dir)
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
