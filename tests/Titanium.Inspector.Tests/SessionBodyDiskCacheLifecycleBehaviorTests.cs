using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Spill-cache lifecycle: clear / abandon / prune / startup rebuild must keep the size index and the disk in
///     agreement and must never delete another run's captures by accident.
/// </summary>
[TestClass]
public class SessionBodyDiskCacheLifecycleBehaviorTests
{
    private string _root = null!;

    [TestInitialize]
    public void Init() =>
        _root = Path.Combine(Path.GetTempPath(), "twp-disk-life-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    private SessionBodyDiskCache NewCache(long maxBytes = 0) =>
        new(_root, maxBytes, TimeSpan.FromDays(1));

    private static SessionSnapshot Session(long id, int bodyBytes = 256) =>
        new()
        {
            Id = id,
            Method = "GET",
            Url = $"https://example.com/{id}",
            StatusCode = 200,
            RequestBodyBytes = new byte[bodyBytes],
            ResponseBodyBytes = new byte[bodyBytes],
            RequestBodyText = new string('x', Math.Min(bodyBytes, 64)),
            ResponseBodyText = new string('y', Math.Min(bodyBytes, 64)),
            RequestBodyCapture = BodyCaptureState.Complete,
            ResponseBodyCapture = BodyCaptureState.Complete,
        };

    private string OtherRun(string name, params (string File, int Bytes, DateTime LastWriteUtc)[] files)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        foreach (var (file, bytes, lastWrite) in files)
        {
            var path = Path.Combine(dir, file);
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTimeUtc(path, lastWrite);
        }

        return dir;
    }

    // Bug class: "clear everything" leaves other runs' HARs or legacy flat files, so the cache never shrinks.
    [TestMethod]
    public void ClearAllRuns_RemovesEveryRunAndLegacyFile_AndZeroesTheIndex()
    {
        var older = OtherRun("20200101-000000-000", ("1.har", 500, DateTime.UtcNow.AddDays(-2)));
        using var cache = NewCache();
        cache.Write(Session(7));
        File.WriteAllText(Path.Combine(_root, "legacy.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "stray.tmp"), "x");
        File.WriteAllText(Path.Combine(_root, "old.bin"), "x");
        Assert.IsTrue(cache.GetCacheStats().TotalBytes > 0);

        cache.ClearAllRuns();

        Assert.AreEqual(0, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length);
        Assert.IsFalse(Directory.Exists(older), "an emptied run folder must be removed");
        Assert.AreEqual(new SessionCacheStats(0, 0), cache.GetCacheStats());
        cache.Write(Session(8));
        Assert.IsTrue(cache.FileExists(8), "the cache must stay usable after a full clear");
    }

    // Bug class: ClearAllRuns on a cache whose root vanished throws instead of resetting the index.
    [TestMethod]
    public void ClearAllRuns_WhenTheRootWasRemoved_ResetsTheIndexWithoutThrowing()
    {
        using var cache = NewCache();
        cache.Write(Session(1));
        Directory.Delete(_root, recursive: true);

        cache.ClearAllRuns();

        Assert.AreEqual(0, cache.GetCacheStats().TotalBytes);
    }

    // Bug class: Clear (current run) also deletes captures from earlier runs that Import HAR still needs.
    [TestMethod]
    public void ClearAll_RemovesOnlyTheCurrentRun()
    {
        var older = OtherRun("20200101-000000-000", ("1.har", 300, DateTime.UtcNow.AddDays(-1)));
        using var cache = NewCache();
        cache.Write(Session(5));
        File.WriteAllText(cache.PathFor(6) + ".tmp", "partial");

        cache.ClearAll();

        Assert.IsFalse(cache.FileExists(5));
        Assert.IsFalse(File.Exists(cache.PathFor(6) + ".tmp"), "interrupted temp files belong to the current run");
        Assert.IsTrue(File.Exists(Path.Combine(older, "1.har")), "an earlier run must survive");
        Assert.AreEqual(300, cache.GetCacheStats().TotalBytes);
    }

    // Bug class: stats ignore HARs that were not indexed (legacy root files, foreign run folders).
    [TestMethod]
    public void GetCacheStats_CountsUnindexedRunFoldersAndLegacyRootFiles()
    {
        using var cache = NewCache();
        File.WriteAllBytes(Path.Combine(_root, "5.har"), new byte[10]);
        OtherRun("foreign-run", ("9.har", 10, DateTime.UtcNow));
        OtherRun("empty-run");

        var stats = cache.GetCacheStats();

        Assert.AreEqual(2, stats.RunCount, "root + one foreign folder; the empty folder is not a run");
        Assert.AreEqual(0, stats.TotalBytes, "bytes come from the size index, not from a directory scan");
    }

    // Bug class: abandoning a run never started (no writes) fails or does not rotate the generation.
    [TestMethod]
    public async Task AbandonCurrentRun_WithNoWrites_RotatesWithoutFreeingAnything()
    {
        using var cache = NewCache();
        var generation = cache.CurrentGeneration;
        var run = cache.RunDirectoryPath;

        var freed = cache.AbandonCurrentRun();
        await cache.FlushCleanupAsync();

        Assert.AreEqual(0, freed);
        Assert.AreNotEqual(generation, cache.CurrentGeneration);
        Assert.AreNotEqual(run, cache.RunDirectoryPath);
    }

    // Bug class: an in-flight spill that finishes after Clear recreates the abandoned session id on disk.
    [TestMethod]
    public async Task AbandonCurrentRun_DropsTheOldFolder_AndRejectsStaleGenerationWrites()
    {
        using var cache = NewCache();
        cache.Write(Session(1));
        var staleGeneration = cache.CurrentGeneration;
        var oldRun = cache.RunDirectoryPath;

        var freed = cache.AbandonCurrentRun();
        await cache.FlushCleanupAsync();

        Assert.IsTrue(freed > 0);
        Assert.IsFalse(Directory.Exists(oldRun), "the abandoned run folder must be deleted");
        Assert.AreEqual(0, cache.GetCacheStats().TotalBytes);

        var rejected = cache.Write(Session(2), staleGeneration);
        Assert.AreEqual(0, rejected.Count);
        Assert.IsFalse(cache.FileExists(2), "a stale-generation write must not create a file");

        cache.Write(Session(2), cache.CurrentGeneration);
        Assert.IsTrue(cache.FileExists(2));
    }

    // Bug class: the background full wipe deletes the run the cache just rotated into, losing new captures.
    [TestMethod]
    public async Task ScheduleClearAllRuns_WipesOldRuns_ButKeepsTheNewCurrentRunUsable()
    {
        var older = OtherRun("20200101-000000-000", ("1.har", 400, DateTime.UtcNow.AddDays(-1)));
        using var cache = NewCache();
        cache.Write(Session(3));
        var trackedBefore = cache.GetCacheStats().TotalBytes;

        var freed = cache.ScheduleClearAllRuns();
        await cache.FlushCleanupAsync();

        Assert.AreEqual(trackedBefore, freed);
        Assert.IsFalse(Directory.Exists(older));
        Assert.AreEqual(0, cache.GetCacheStats().TotalBytes);
        cache.Write(Session(4));
        Assert.IsTrue(cache.FileExists(4));
        Assert.IsFalse(cache.FileExists(3));
    }

    // Bug class: ScheduleDelete leaves the size index stale (budget never recovers) or deletes the wrong file.
    [TestMethod]
    public async Task ScheduleDelete_UpdatesTheIndexImmediately_AndDeletesInTheBackground()
    {
        using var cache = NewCache();
        cache.Write(Session(1));
        cache.Write(Session(2));
        var both = cache.GetCacheStats().TotalBytes;

        cache.ScheduleDelete([1]);
        Assert.IsTrue(cache.GetCacheStats().TotalBytes < both, "index must drop before the file is gone");
        await cache.FlushCleanupAsync();

        Assert.IsFalse(cache.FileExists(1));
        Assert.IsTrue(cache.FileExists(2));
    }

    // Bug class: shrinking the budget drops the newest captures first, or reports ids from other runs as lost rows.
    [TestMethod]
    public async Task UpdateLimits_PrunesOldestFirst_AndReportsOnlyCurrentRunIds()
    {
        using var cache = NewCache();
        cache.Write(Session(1));
        File.SetLastWriteTimeUtc(cache.PathFor(1), DateTime.UtcNow.AddMinutes(-2));
        cache.Write(Session(2));
        File.SetLastWriteTimeUtc(cache.PathFor(2), DateTime.UtcNow.AddMinutes(-1));
        cache.Write(Session(3));
        var one = new FileInfo(cache.PathFor(3)).Length;

        var dropped = cache.UpdateLimits(one + one / 2, TimeSpan.FromDays(1));
        await cache.FlushCleanupAsync();

        CollectionAssert.AreEqual(new long[] { 1, 2 }, dropped.ToArray());
        Assert.IsFalse(cache.FileExists(1));
        Assert.IsFalse(cache.FileExists(2));
        Assert.IsTrue(cache.FileExists(3));
    }

    // Bug class: a non-positive budget request disables pruning permanently or wipes the cache.
    [TestMethod]
    public void ScheduleBudgetPrune_WithNonPositiveLimit_KeepsTheCurrentBudgetAndDropsNothing()
    {
        using var cache = NewCache(maxBytes: 10_000_000);
        cache.Write(Session(1));

        var dropped = cache.ScheduleBudgetPrune(0);

        Assert.AreEqual(0, dropped.Count);
        Assert.IsTrue(cache.FileExists(1));
    }

    // Bug class: writing past the budget evicts the new file or forgets to report which live rows lost their body.
    [TestMethod]
    public void Write_OverBudget_EvictsTheOldestAndReportsItsId()
    {
        using var cache = NewCache();
        cache.Write(Session(1));
        var one = new FileInfo(cache.PathFor(1)).Length;
        File.SetLastWriteTimeUtc(cache.PathFor(1), DateTime.UtcNow.AddMinutes(-1));
        cache.UpdateLimits(one + one / 2, TimeSpan.FromDays(1));

        var evicted = cache.Write(Session(2));

        CollectionAssert.AreEqual(new long[] { 1 }, evicted.ToArray());
        Assert.IsFalse(cache.FileExists(1));
        Assert.IsTrue(cache.FileExists(2));
    }

    // Bug class: a restart with an oversize cache from earlier runs starts over budget (disk fills up).
    [TestMethod]
    public void Startup_WithAnOversizeCache_DeletesOldestRunsFirstAndRemovesEmptiedFolders()
    {
        var oldest = OtherRun("20200101-000000-000", ("1.har", 1000, DateTime.UtcNow.AddDays(-3)));
        var middle = OtherRun("20200102-000000-000", ("2.har", 1000, DateTime.UtcNow.AddDays(-2)));
        var newest = OtherRun("20200103-000000-000", ("3.har", 1000, DateTime.UtcNow.AddDays(-1)));
        OtherRun("20200104-000000-000", ("notes.har", 50, DateTime.UtcNow));

        using var cache = NewCache(maxBytes: 1500);

        Assert.IsFalse(Directory.Exists(oldest), "oldest capture and its emptied folder must go");
        Assert.IsFalse(Directory.Exists(middle));
        Assert.IsTrue(File.Exists(Path.Combine(newest, "3.har")));
        Assert.IsTrue(File.Exists(Path.Combine(_root, "20200104-000000-000", "notes.har")),
            "a HAR whose name is not a session id is never indexed, so never evicted");
    }

    // Bug class: stale temp / legacy files from a crashed run are indexed and count against the budget forever.
    [TestMethod]
    public void Startup_RemovesTempAndLegacyArtifactsFromEarlierRuns()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "1.json"), "{}");
        var run = OtherRun("20200101-000000-000", ("4.har", 20, DateTime.UtcNow.AddDays(-1)));
        File.WriteAllText(Path.Combine(run, "4.har.tmp"), "partial");
        File.WriteAllText(Path.Combine(run, "4.bin"), "legacy");

        using var cache = NewCache();

        Assert.IsFalse(File.Exists(Path.Combine(_root, "1.json")));
        Assert.IsFalse(File.Exists(Path.Combine(run, "4.har.tmp")));
        Assert.IsFalse(File.Exists(Path.Combine(run, "4.bin")));
        Assert.AreEqual(20, cache.GetCacheStats().TotalBytes);
    }

    // Bug class: a corrupt HAR throws into the UI thread instead of reading as "body not available".
    [TestMethod]
    public void ReadingACorruptOrMissingSpill_ReturnsFalseInsteadOfThrowing()
    {
        using var cache = NewCache();
        Directory.CreateDirectory(cache.RunDirectoryPath);
        File.WriteAllText(cache.PathFor(9), "{ this is not har");

        Assert.IsFalse(cache.TryReadSession(9, out var session));
        Assert.IsNull(session);
        Assert.IsFalse(cache.TryReadBodyTexts(9, out var request, out var response));
        Assert.IsNull(request);
        Assert.IsNull(response);
        Assert.IsFalse(cache.TryLoad(new SessionSnapshot { Id = 9 }));
        Assert.IsFalse(cache.TryReadSession(404, out _));
        Assert.IsFalse(cache.TryOpenRead(404, out var stream));
        Assert.IsNull(stream);
    }

    // Bug class: body text and capture state do not survive the spill round-trip, so the inspector shows blanks.
    [TestMethod]
    public void WrittenSession_RoundTripsThroughReadLoadAndOpenRead()
    {
        using var cache = NewCache();
        cache.Write(Session(11));

        Assert.IsTrue(cache.TryReadBodyTexts(11, out var request, out var response));
        Assert.AreEqual(new string('x', 64), request);
        Assert.AreEqual(new string('y', 64), response);

        var target = new SessionSnapshot { Id = 11 };
        Assert.IsTrue(cache.TryLoad(target));
        Assert.AreEqual(new string('y', 64), target.ResponseBodyText);

        Assert.IsTrue(cache.TryOpenRead(11, out var stream));
        using (stream)
        {
            Assert.IsTrue(stream!.Length > 0);
        }
    }

    // Bug class: Delete of a file that is already gone corrupts the tracked size (goes negative / double-subtracts).
    [TestMethod]
    public void Delete_IsIdempotent_AndDeleteManyRemovesOnlyTheNamedSessions()
    {
        using var cache = NewCache();
        cache.Write(Session(1));
        cache.Write(Session(2));
        cache.Write(Session(3));
        var survivorBytes = new FileInfo(cache.PathFor(3)).Length;

        cache.DeleteMany([1, 2]);
        cache.Delete(1);
        cache.Delete(99);

        Assert.IsFalse(cache.FileExists(1));
        Assert.IsFalse(cache.FileExists(2));
        Assert.IsTrue(cache.FileExists(3));
        Assert.AreEqual(survivorBytes, cache.GetCacheStats().TotalBytes);
    }

    // Bug class: using the cache after shutdown silently writes to disk.
    [TestMethod]
    public void AfterDispose_WritesAndReadsAreRefused()
    {
        var cache = NewCache();
        cache.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => cache.Write(Session(1)));
        Assert.ThrowsExactly<ObjectDisposedException>(() => cache.TryReadSession(1, out _));
        Assert.ThrowsExactly<ObjectDisposedException>(() => cache.TryLoad(new SessionSnapshot { Id = 1 }));
    }

    // Bug class: the default spill location is not under the per-user data folder (writes into the install dir).
    [TestMethod]
    public void DefaultDirectory_LivesUnderLocalApplicationData()
    {
        var path = SessionBodyDiskCache.GetDefaultDirectory();

        StringAssert.StartsWith(
            path,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Assert.AreEqual("session-cache", Path.GetFileName(path));
    }
}
