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
    public void HeldLock_BlocksASecondRestart_ThenTimesOut()
    {
        using var held = new FileStream(PidFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var coordinator = new HotRestartCoordinator(PidFile, new FakePlatform(), ownPid: 200);

        var ex = Assert.ThrowsExactly<IOException>(() => coordinator.TakeOver(TimeSpan.FromMilliseconds(150)));
        StringAssert.Contains(ex.Message, ".lock");
        Assert.IsFalse(File.Exists(PidFile));
    }
}
