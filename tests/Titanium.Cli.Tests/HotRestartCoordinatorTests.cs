using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;

namespace Titanium.Cli.Tests;

[TestClass]
public class HotRestartCoordinatorTests
{
    private sealed class FakePlatform : IHotRestartPlatform
    {
        public HashSet<int> Alive { get; } = [];

        public HashSet<int> OtherPrograms { get; } = [];

        public List<int> Signaled { get; } = [];

        public bool SignalSucceeds { get; set; } = true;

        public bool IsProcessAlive(int pid) => Alive.Contains(pid);

        public bool IsSameProgram(int pid) => !OtherPrograms.Contains(pid);

        public bool SignalHandover(int pid)
        {
            Signaled.Add(pid);
            return SignalSucceeds;
        }
    }

    private string _dir = string.Empty;

    private string PidFile => Path.Combine(_dir, "titanium.pid");

    [TestInitialize]
    public void Setup() => _dir = Directory.CreateTempSubdirectory("twp-hotrestart-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    [TestMethod]
    public void NoPidFile_WritesOwnPid_AndSignalsNobody()
    {
        var platform = new FakePlatform();
        var coordinator = new HotRestartCoordinator(PidFile, platform, ownPid: 200);

        Assert.AreEqual(HotRestartHandover.NoPrevious, coordinator.TakeOver(Wait));

        Assert.AreEqual("200", File.ReadAllText(PidFile));
        Assert.AreEqual(0, platform.Signaled.Count);
        Assert.IsNull(coordinator.PreviousPid);
    }

    [TestMethod]
    public void LivePreviousProcess_IsSignaled_AndPidFileNamesTheNewOne()
    {
        File.WriteAllText(PidFile, "100");
        var platform = new FakePlatform();
        platform.Alive.Add(100);
        var coordinator = new HotRestartCoordinator(PidFile, platform, ownPid: 200);

        Assert.AreEqual(HotRestartHandover.SignaledPrevious, coordinator.TakeOver(Wait));

        CollectionAssert.AreEqual(new[] { 100 }, platform.Signaled);
        Assert.AreEqual(100, coordinator.PreviousPid);
        Assert.AreEqual("200", File.ReadAllText(PidFile));
    }

    [TestMethod]
    public void StalePid_DeadOrOtherProgram_IsIgnoredAndOverwritten()
    {
        var platform = new FakePlatform();

        File.WriteAllText(PidFile, "100"); // not alive
        Assert.AreEqual(HotRestartHandover.StalePidFile, new HotRestartCoordinator(PidFile, platform, 200).TakeOver(Wait));

        File.WriteAllText(PidFile, "300"); // alive, but another program (a recycled pid)
        platform.Alive.Add(300);
        platform.OtherPrograms.Add(300);
        Assert.AreEqual(HotRestartHandover.StalePidFile, new HotRestartCoordinator(PidFile, platform, 200).TakeOver(Wait));

        File.WriteAllText(PidFile, "not-a-number");
        Assert.AreEqual(HotRestartHandover.NoPrevious, new HotRestartCoordinator(PidFile, platform, 200).TakeOver(Wait));

        Assert.AreEqual(0, platform.Signaled.Count, "a stale or garbage pid file never signals anything");
        Assert.AreEqual("200", File.ReadAllText(PidFile));
    }

    [TestMethod]
    public void OwnPid_IsNeverSignaled()
    {
        File.WriteAllText(PidFile, "200");
        var platform = new FakePlatform();
        platform.Alive.Add(200);

        Assert.AreEqual(HotRestartHandover.SamePid, new HotRestartCoordinator(PidFile, platform, 200).TakeOver(Wait));
        Assert.AreEqual(0, platform.Signaled.Count);
    }

    [TestMethod]
    public void FailedSignal_IsReported_AndPidFileStillNamesTheNewestProcess()
    {
        File.WriteAllText(PidFile, "100");
        var platform = new FakePlatform { SignalSucceeds = false };
        platform.Alive.Add(100);

        Assert.AreEqual(HotRestartHandover.SignalFailed, new HotRestartCoordinator(PidFile, platform, 200).TakeOver(Wait));
        Assert.AreEqual("200", File.ReadAllText(PidFile));
    }

    [TestMethod]
    public void ReleasePidFile_OnlyDeletesAFileThatStillNamesThisProcess()
    {
        var platform = new FakePlatform();
        var old = new HotRestartCoordinator(PidFile, platform, ownPid: 100);
        old.TakeOver(Wait);
        platform.Alive.Add(100);
        var next = new HotRestartCoordinator(PidFile, platform, ownPid: 200);
        next.TakeOver(Wait);

        Assert.IsFalse(old.ReleasePidFile(), "the old process must not delete its successor's pid file");
        Assert.IsTrue(File.Exists(PidFile));

        Assert.IsTrue(next.ReleasePidFile());
        Assert.IsFalse(File.Exists(PidFile));
    }

    [TestMethod]
    public void PeekLivePrevious_ReportsOnlyALiveSameProgramProcess_AndTouchesNothing()
    {
        var platform = new FakePlatform();
        var coordinator = new HotRestartCoordinator(PidFile, platform, ownPid: 200);
        Assert.IsNull(coordinator.PeekLivePrevious(), "no pid file");

        File.WriteAllText(PidFile, "100");
        Assert.IsNull(coordinator.PeekLivePrevious(), "dead process");

        platform.Alive.Add(100);
        Assert.AreEqual(100, coordinator.PeekLivePrevious());

        platform.OtherPrograms.Add(100);
        Assert.IsNull(coordinator.PeekLivePrevious(), "recycled pid of another program");

        Assert.AreEqual("100", File.ReadAllText(PidFile), "peeking never rewrites the pid file");
        Assert.AreEqual(0, platform.Signaled.Count);
    }

    [TestMethod]
    public async Task DeferredBind_RunsOnlyAfterThePreviousProcessExits()
    {
        var platform = new FakePlatform();
        platform.Alive.Add(100);
        var order = new List<string>();
        var errors = new List<string>();

        var run = HotRestartDeferredBind.RunAsync(
            100, platform, [() => order.Add("h3"), () => order.Add("udp")],
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10), errors.Add, CancellationToken.None);

        await Task.Delay(100);
        Assert.AreEqual(0, order.Count, "must not bind while the old process still owns the UDP ports");

        platform.Alive.Remove(100);
        Assert.IsTrue(await run);
        CollectionAssert.AreEqual(new[] { "h3", "udp" }, order);
        Assert.AreEqual(0, errors.Count);
    }

    [TestMethod]
    public async Task DeferredBind_PreviousProcessAlreadyGone_BindsImmediately()
    {
        var order = new List<string>();
        var ok = await HotRestartDeferredBind.RunAsync(
            100, new FakePlatform(), [() => order.Add("h3")],
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10), _ => { }, CancellationToken.None);

        Assert.IsTrue(ok);
        CollectionAssert.AreEqual(new[] { "h3" }, order);
    }

    [TestMethod]
    public async Task DeferredBind_Timeout_SkipsBindsAndLogsAnError()
    {
        var platform = new FakePlatform();
        platform.Alive.Add(100);
        var order = new List<string>();
        var errors = new List<string>();

        var ok = await HotRestartDeferredBind.RunAsync(
            100, platform, [() => order.Add("h3")],
            TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(10), errors.Add, CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.AreEqual(0, order.Count);
        Assert.AreEqual(1, errors.Count);
        StringAssert.Contains(errors[0], "did not exit within");
        StringAssert.Contains(errors[0], "TCP keeps serving");
    }

    [TestMethod]
    public async Task DeferredBind_OneFailingBind_DoesNotStopTheOthers()
    {
        var order = new List<string>();
        var errors = new List<string>();

        var ok = await HotRestartDeferredBind.RunAsync(
            100, new FakePlatform(),
            [() => throw new InvalidOperationException("address in use"), () => order.Add("udp")],
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10), errors.Add, CancellationToken.None);

        Assert.IsFalse(ok);
        CollectionAssert.AreEqual(new[] { "udp" }, order);
        StringAssert.Contains(errors.Single(), "address in use");
    }

    [TestMethod]
    public async Task DeferredBind_Cancellation_StopsWaitingWithoutBinding()
    {
        var platform = new FakePlatform();
        platform.Alive.Add(100);
        var order = new List<string>();
        using var cts = new CancellationTokenSource(50);

        var ok = await HotRestartDeferredBind.RunAsync(
            100, platform, [() => order.Add("h3")],
            TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10), _ => { }, cts.Token);

        Assert.IsFalse(ok);
        Assert.AreEqual(0, order.Count);
    }

    [TestMethod]
    public async Task HandoverWatch_FiresWhenThePidFileNamesALiveSuccessor()
    {
        File.WriteAllText(PidFile, "100");
        var platform = new FakePlatform();
        platform.Alive.Add(100);
        platform.Alive.Add(200);
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watch = HotRestartHandoverWatch.Start(PidFile, ownPid: 100, platform, () => fired.TrySetResult());
        Assert.IsFalse(fired.Task.IsCompleted, "own pid must not be treated as a handover");

        await File.WriteAllTextAsync(PidFile, "200");
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task HandoverWatch_IgnoresAStaleOrForeignPidInTheFile()
    {
        File.WriteAllText(PidFile, "100");
        var platform = new FakePlatform();
        platform.Alive.Add(100);
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watch = HotRestartHandoverWatch.Start(PidFile, ownPid: 100, platform, () => fired.TrySetResult());
        await File.WriteAllTextAsync(PidFile, "300"); // dead
        await Task.Delay(400);
        Assert.IsFalse(fired.Task.IsCompleted);

        platform.Alive.Add(300);
        platform.OtherPrograms.Add(300); // live but another program
        await Task.Delay(400);
        Assert.IsFalse(fired.Task.IsCompleted);
    }

    [TestMethod]
    public void HeldLock_BlocksASecondRestart_ThenTimesOut()
    {
        using var held = new FileStream(PidFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var coordinator = new HotRestartCoordinator(PidFile, new FakePlatform(), ownPid: 200);

        var ex = Assert.ThrowsExactly<IOException>(() => coordinator.TakeOver(TimeSpan.FromMilliseconds(150)));
        StringAssert.Contains(ex.Message, ".lock");
        Assert.IsFalse(File.Exists(PidFile));
    }
}
