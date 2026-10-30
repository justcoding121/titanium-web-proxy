using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Cli.Tests.Support;

namespace Titanium.Cli.Tests;

/// <summary>
///     A ConfigMap or Secret mount keeps the file name and swaps the <c>..data</c> symlink behind it. The watcher
///     follows content, so one swap is one callback, a failed callback does not stop it, and a missed event is
///     caught by the poll.
/// </summary>
[TestClass]
public class ContentWatcherTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(100);

    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "twp-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A watcher thread may still hold the directory for a moment.
        }
    }

    [TestMethod]
    public async Task ContentChange_RunsTheCallbackOnce()
    {
        var path = Path.Combine(_dir, "twp.yaml");
        await File.WriteAllTextAsync(path, "one");
        var calls = new Counter();
        using var watcher = Watch([path], calls);

        await File.WriteAllTextAsync(path, "two");

        await calls.WaitForAsync(1);
        await calls.StaysAtAsync(1);
    }

    [TestMethod]
    public async Task RewritingTheSameContent_DoesNotReload()
    {
        var path = Path.Combine(_dir, "twp.yaml");
        await File.WriteAllTextAsync(path, "same");
        var calls = new Counter();
        using var watcher = Watch([path], calls);

        await File.WriteAllTextAsync(path, "same");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        await calls.StaysAtAsync(0);
    }

    [TestMethod]
    public async Task ABurstOfWrites_IsOneCallback()
    {
        var path = Path.Combine(_dir, "twp.yaml");
        await File.WriteAllTextAsync(path, "0");
        var calls = new Counter();
        using var watcher = Watch([path], calls);

        for (var i = 1; i <= 8; i++)
        {
            await File.WriteAllTextAsync(path, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        await calls.WaitForAsync(1);
        await calls.StaysAtAsync(1);
    }

    [TestMethod]
    public async Task FileReplacedByRename_IsOneCallback()
    {
        var path = Path.Combine(_dir, "twp.yaml");
        await File.WriteAllTextAsync(path, "old");
        var calls = new Counter();
        using var watcher = Watch([path], calls);

        var staged = Path.Combine(_dir, "twp.yaml.new");
        await File.WriteAllTextAsync(staged, "new");
        File.Move(staged, path, overwrite: true);

        await calls.WaitForAsync(1);
        await calls.StaysAtAsync(1);
    }

    [TestMethod]
    public async Task ASecretFile_InTheSetReloadsToo()
    {
        var config = Path.Combine(_dir, "twp.yaml");
        var secret = Path.Combine(_dir, "token");
        await File.WriteAllTextAsync(config, "cfg");
        await File.WriteAllTextAsync(secret, "t1");
        var calls = new Counter();
        using var watcher = Watch([config, secret], calls);

        await File.WriteAllTextAsync(secret, "t2");

        await calls.WaitForAsync(1);
    }

    [TestMethod]
    public async Task UnrelatedFilesInTheDirectory_DoNotTrigger()
    {
        var path = Path.Combine(_dir, "twp.yaml");
        await File.WriteAllTextAsync(path, "cfg");
        var calls = new Counter();
        using var watcher = Watch([path], calls);

        await File.WriteAllTextAsync(Path.Combine(_dir, "other.txt"), "noise");
        await File.WriteAllTextAsync(Path.Combine(_dir, "other.txt"), "more noise");

        await calls.StaysAtAsync(0);
    }

    [TestMethod]
    public async Task ACallbackThatThrows_IsReported_AndTheNextChangeStillReloads()
    {
        var path = Path.Combine(_dir, "twp.yaml");
        await File.WriteAllTextAsync(path, "good");
        var calls = new Counter { ThrowOnFirst = true };
        var errors = new List<string>();
        using var watcher = new ContentWatcher(
            [path],
            calls.OnChangedAsync,
            ex => errors.Add(ex.Message),
            Debounce,
            pollInterval: Timeout.InfiniteTimeSpan);

        await File.WriteAllTextAsync(path, "broken");
        await calls.WaitForAsync(1);
        await calls.StaysAtAsync(1);
        Assert.AreEqual(1, errors.Count);
        StringAssert.Contains(errors[0], "reload refused");

        await File.WriteAllTextAsync(path, "fixed");
        await calls.WaitForAsync(2);
    }

    [TestMethod]
    public async Task APathThatIsMissing_DefersUntilItAppears()
    {
        var path = Path.Combine(_dir, "twp.yaml");
        await File.WriteAllTextAsync(path, "v1");
        var calls = new Counter();
        using var watcher = Watch([path], calls);

        File.Delete(path);
        await calls.StaysAtAsync(0);

        await File.WriteAllTextAsync(path, "v2");
        await calls.WaitForAsync(1);
    }

    [TestMethod]
    public async Task Poll_CatchesAChangeWhenNoEventArrives()
    {
        var path = Path.Combine(_dir, "twp.yaml");
        await File.WriteAllTextAsync(path, "v1");
        var clock = new FakeTimeProvider();
        var calls = new Counter();
        using var watcher = new ContentWatcher(
            [path],
            calls.OnChangedAsync,
            debounce: Debounce,
            pollInterval: ContentWatcher.DefaultPollInterval,
            timeProvider: clock,
            useFileSystemEvents: false);

        await File.WriteAllTextAsync(path, "v2");
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.AreEqual(0, calls.Count, "the poll is every 30 seconds");

        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForTimersAsync(clock, 2);
        clock.Advance(Debounce);

        await calls.WaitForAsync(1);

        // The next tick sees the same content and stays quiet.
        clock.Advance(ContentWatcher.DefaultPollInterval);
        await WaitForTimersAsync(clock, 2);
        clock.Advance(Debounce);
        await Task.Delay(200);
        Assert.AreEqual(1, calls.Count);
    }

    [TestMethod]
    public async Task SymlinkSwap_IsOneCallback_AndTheNewContentIsRead()
    {
        var mount = BuildKubernetesMount("v1");
        var config = Path.Combine(mount, "twp.yaml");
        var calls = new Counter();
        var seen = new List<string>();
        using var watcher = new ContentWatcher(
            [config],
            async () =>
            {
                seen.Add(await File.ReadAllTextAsync(config));
                await calls.OnChangedAsync();
            },
            debounce: Debounce,
            pollInterval: Timeout.InfiniteTimeSpan);

        SwapKubernetesData(mount, "v2");

        await calls.WaitForAsync(1);
        await calls.StaysAtAsync(1);
        CollectionAssert.AreEqual(new[] { "v2" }, seen.ToArray());

        SwapKubernetesData(mount, "v3");
        await calls.WaitForAsync(2);
        await calls.StaysAtAsync(2);
        CollectionAssert.AreEqual(new[] { "v2", "v3" }, seen.ToArray());
    }

    [TestMethod]
    public async Task SymlinkSwap_ToIdenticalContent_DoesNotReload()
    {
        var mount = BuildKubernetesMount("same");
        var calls = new Counter();
        using var watcher = Watch([Path.Combine(mount, "twp.yaml")], calls);

        SwapKubernetesData(mount, "same");

        await calls.StaysAtAsync(0);
    }

    [TestMethod]
    public async Task SymlinkSwap_WithAFailedReload_KeepsTheOldConfig_ThenAppliesTheNextGoodOne()
    {
        var mount = BuildKubernetesMount("good-1");
        var config = Path.Combine(mount, "twp.yaml");
        var applied = "good-1";
        var attempts = new Counter();
        using var watcher = new ContentWatcher(
            [config],
            async () =>
            {
                var text = await File.ReadAllTextAsync(config);
                await attempts.OnChangedAsync();
                if (text.StartsWith("bad", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("validation failed");
                }

                applied = text;
            },
            _ => { },
            Debounce,
            Timeout.InfiniteTimeSpan);

        SwapKubernetesData(mount, "bad-2");
        await attempts.WaitForAsync(1);
        await attempts.StaysAtAsync(1);
        Assert.AreEqual("good-1", applied, "a failed reload must leave the running config in force");

        SwapKubernetesData(mount, "good-3");
        await attempts.WaitForAsync(2);
        await attempts.StaysAtAsync(2);
        Assert.AreEqual("good-3", applied);
    }

    [TestMethod]
    public void KubernetesSegments_AreRecognised()
    {
        Assert.IsTrue(ContentWatcher.HasKubernetesSegment("/etc/titanium/..data/twp.yaml"));
        Assert.IsTrue(ContentWatcher.HasKubernetesSegment("/etc/titanium/..2026_10_28_12_00_00.123/twp.yaml"));
        Assert.IsFalse(ContentWatcher.HasKubernetesSegment("/etc/titanium/twp.yaml"));
        Assert.IsFalse(ContentWatcher.HasKubernetesSegment("/etc/titanium/../twp.yaml"));
    }

    [TestMethod]
    public async Task Dispose_StopsCallbacks()
    {
        var path = Path.Combine(_dir, "twp.yaml");
        await File.WriteAllTextAsync(path, "a");
        var calls = new Counter();
        var watcher = Watch([path], calls);

        watcher.Dispose();
        await File.WriteAllTextAsync(path, "b");

        await calls.StaysAtAsync(0);
    }

    private static ContentWatcher Watch(string[] paths, Counter calls) =>
        new(paths, calls.OnChangedAsync, debounce: Debounce, pollInterval: Timeout.InfiniteTimeSpan);

    private static async Task WaitForTimersAsync(FakeTimeProvider clock, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (clock.ActiveTimers < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(clock.ActiveTimers >= count, "the debounce timer was never created");
    }

    /// <summary>
    ///     <c>dir/twp.yaml -&gt; ..data/twp.yaml</c>, <c>dir/..data -&gt; ..rev1</c>: the layout of a mounted ConfigMap.
    ///     Marked inconclusive when the platform will not let this user create symbolic links.
    /// </summary>
    private string BuildKubernetesMount(string content)
    {
        var mount = Path.Combine(_dir, "mount");
        var revision = Path.Combine(mount, "..rev1");
        Directory.CreateDirectory(revision);
        File.WriteAllText(Path.Combine(revision, "twp.yaml"), content);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(mount, "..data"), "..rev1");
            File.CreateSymbolicLink(Path.Combine(mount, "twp.yaml"), Path.Combine("..data", "twp.yaml"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Creating symbolic links needs a privilege this account does not have: " + ex.Message);
        }

        return mount;
    }

    private int _revision = 1;

    /// <summary>What the kubelet does: write a new revision directory, point a temp link at it, rename over <c>..data</c>.</summary>
    private void SwapKubernetesData(string mount, string content)
    {
        var name = "..rev" + (++_revision);
        var revision = Path.Combine(mount, name);
        Directory.CreateDirectory(revision);
        File.WriteAllText(Path.Combine(revision, "twp.yaml"), content);

        var data = Path.Combine(mount, "..data");
        if (OperatingSystem.IsWindows())
        {
            // Windows cannot rename over a directory link, so the swap is delete then create.
            Directory.Delete(data);
            Directory.CreateSymbolicLink(data, name);
            return;
        }

        var temp = Path.Combine(mount, "..data_tmp");
        Directory.CreateSymbolicLink(temp, name);
        File.Move(temp, data, overwrite: true);
    }

    private sealed class Counter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public bool ThrowOnFirst { get; init; }

        public Task OnChangedAsync()
        {
            var n = Interlocked.Increment(ref _count);
            return ThrowOnFirst && n == 1
                ? Task.FromException(new InvalidOperationException("reload refused"))
                : Task.CompletedTask;
        }

        public async Task WaitForAsync(int expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Count < expected && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.IsTrue(Count >= expected, $"expected {expected} callback(s), saw {Count}");
        }

        /// <summary>Holds long enough for any straggling event or second debounce to fire.</summary>
        public async Task StaysAtAsync(int expected)
        {
            await Task.Delay(900);
            Assert.AreEqual(expected, Count);
        }
    }
}
