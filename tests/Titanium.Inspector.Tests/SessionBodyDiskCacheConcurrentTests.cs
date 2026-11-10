using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
/// Verifies <see cref="SessionBodyDiskCache"/> and <see cref="SessionStore"/> correctness
/// under concurrent writes: simultaneous flushes must not corrupt the index, lose files,
/// or leave the directory in an inconsistent state.
/// </summary>
[TestClass]
public class SessionBodyDiskCacheConcurrentTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "twp-disk-conc-" + Guid.NewGuid().ToString("N"));

    private static SessionSnapshot MakeSession(long id, int bodyBytes = 128) =>
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

    [TestMethod]
    public async Task ConcurrentWrites_AllSessionsLandOnDisk()
    {
        var dir = TempDir();
        try
        {
            // Use a tight memory cap to force immediate spill so every Add writes a HAR file.
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 200,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);

            const int writers = 4;
            const int sessionsPerWriter = 25;
            var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
            {
                for (var i = 0; i < sessionsPerWriter; i++)
                {
                    var id = w * 1000L + i + 1;
                    store.Add(MakeSession(id));
                    // Yield to maximise interleaving.
                    await Task.Yield();
                }
            })).ToArray();

            await Task.WhenAll(tasks);
            await store.FlushSpillAsync();

            var harFiles = Directory.EnumerateFiles(dir, "*.har", SearchOption.AllDirectories).ToList();
            Assert.IsTrue(harFiles.Count >= writers * sessionsPerWriter,
                $"Expected at least {writers * sessionsPerWriter} HAR files; found {harFiles.Count}");

            // Verify no HAR file is empty or obviously corrupted (must contain JSON).
            foreach (var har in harFiles)
            {
                var text = await File.ReadAllTextAsync(har);
                Assert.IsTrue(text.TrimStart().StartsWith("{", StringComparison.Ordinal),
                    $"HAR file {Path.GetFileName(har)} does not start with '{{' (corrupt or empty)");
            }
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [TestMethod]
    public async Task ConcurrentWriteAndApplyOptions_DoesNotThrow()
    {
        var dir = TempDir();
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

            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            // Continuously change the disk budget while sessions are being added.
            var optionsTask = Task.Run(async () =>
            {
                var toggle = true;
                while (!stop.IsCancellationRequested)
                {
                    store.ApplyOptions(new SessionStoreOptions
                    {
                        MaxSessionsInMemory = 100,
                        SpillBodiesToDisk = true,
                        DiskCacheMaxBytes = toggle ? 64L * 1024 * 1024 : 32L * 1024 * 1024,
                    });
                    toggle = !toggle;
                    await Task.Delay(10, stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            });

            Exception? caught = null;
            try
            {
                for (var i = 0; i < 50; i++)
                {
                    store.Add(MakeSession(i + 1));
                    if (i % 5 == 0)
                    {
                        await store.FlushSpillAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            stop.Cancel();
            await optionsTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            Assert.IsNull(caught, $"Concurrent Add / ApplyOptions must not throw: {caught?.Message}");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [TestMethod]
    public async Task FlushSpillAsync_CalledConcurrently_DoesNotThrow()
    {
        var dir = TempDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 50,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);

            for (var i = 1; i <= 30; i++)
            {
                store.Add(MakeSession(i));
            }

            // Multiple concurrent FlushSpillAsync calls must not throw.
            var flushTasks = Enumerable.Range(0, 5)
                .Select(_ => store.FlushSpillAsync())
                .ToArray();

            Exception? caught = null;
            try
            {
                await Task.WhenAll(flushTasks);
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            Assert.IsNull(caught, $"Concurrent FlushSpillAsync must not throw: {caught?.Message}");
            await store.FlushSpillAsync(); // drain any remaining
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [TestMethod]
    public async Task ConcurrentAddAndClear_DoesNotThrow()
    {
        var dir = TempDir();
        try
        {
            using var store = new SessionStore(
                new SessionStoreOptions
                {
                    MaxSessionsInMemory = 50,
                    SpillBodiesToDisk = true,
                    DiskCacheMaxBytes = 64L * 1024 * 1024,
                },
                dir);

            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var clearTask = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    store.Clear();
                    await Task.Delay(20, stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            });

            Exception? caught = null;
            try
            {
                for (var i = 0; i < 100; i++)
                {
                    store.Add(MakeSession(i + 1));
                }
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            stop.Cancel();
            await clearTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            Assert.IsNull(caught, $"Concurrent Add / Clear must not throw: {caught?.Message}");
        }
        finally
        {
            TryDelete(dir);
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
            // best-effort
        }
    }
}
