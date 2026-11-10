using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;

namespace Titanium.Cli.Tests;

/// <summary>
///     Pid-file edge cases around <see cref="ConfigReloadGate" /> and the hot-restart helpers: a pid file that cannot
///     be written, read, or deleted must be reported or tolerated, never turn into a crash of the running proxy.
/// </summary>
[TestClass]
[DoNotParallelize]
public class HotRestartAndReloadGateEdgeBehaviorTests
{
    private string _dir = null!;
    private string _configPath = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Directory.CreateTempSubdirectory("twp-gate-edge-").FullName;
        _configPath = Path.Combine(_dir, "edge-" + Guid.NewGuid().ToString("N") + ".yaml");
    }

    [TestCleanup]
    public void Cleanup()
    {
        TryDeletePidArtifacts();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    // Bug class: a pid file that cannot be written (path taken by a directory, read-only volume) throws out of the
    // start-up path and kills a proxy that is already serving, instead of only disabling `titanium reload`.
    [TestMethod]
    public async Task WritePidFile_PathOccupiedByADirectory_IsReportedAndDoesNotThrow()
    {
        var pidPath = ConfigReloadGate.PidFilePath(_configPath);
        Directory.CreateDirectory(pidPath);

        var stderr = await CaptureStderrAsync(() => ConfigReloadGate.WritePidFile(_configPath, 4242));

        StringAssert.Contains(stderr, "Could not write the pid file");
        Assert.IsTrue(Directory.Exists(pidPath), "the occupying directory must be left alone");
    }

    // Bug class: cleanup at exit throws (a scanner holds the pid file open) and turns a clean shutdown into a crash,
    // or deletes a file it could not prove was its own.
    [TestMethod]
    public void TryDeletePidFile_FileHeldOpenByAnotherProcess_DoesNotThrowAndLeavesTheFile()
    {
        ConfigReloadGate.WritePidFile(_configPath, 4242);
        var pidPath = ConfigReloadGate.PidFilePath(_configPath);

        using (new FileStream(pidPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ConfigReloadGate.TryDeletePidFile(_configPath);
            Assert.IsTrue(File.Exists(pidPath));
        }

        ConfigReloadGate.TryDeletePidFile(_configPath);
        Assert.IsFalse(File.Exists(pidPath), "once nothing holds it the file is removed");
    }

    // Bug class: `titanium reload` crashes with an IO exception instead of saying "no running process" when the
    // pid file is locked mid-write by the process that owns it.
    [TestMethod]
    public void TryReadPid_FileLockedExclusively_ReturnsNullInsteadOfThrowing()
    {
        ConfigReloadGate.WritePidFile(_configPath, 4242);
        var pidPath = ConfigReloadGate.PidFilePath(_configPath);

        using (new FileStream(pidPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.IsNull(ConfigReloadGate.TryReadPid(_configPath));
        }

        Assert.AreEqual(4242, ConfigReloadGate.TryReadPid(_configPath));
    }

    // Bug class: a config path that cannot be turned into a gate key (embedded NUL from a bad argument) throws from
    // the reload helpers instead of reporting that nothing was signalled.
    [TestMethod]
    public void ReloadHelpers_UnusableConfigPath_ReportNothingToSignal()
    {
        const string bad = "bad\0path.yaml";

        Assert.IsFalse(ConfigReloadGate.TrySignalWindowsReload(bad));
        Assert.IsNull(ConfigReloadGate.TryReadPid(bad));
        ConfigReloadGate.TryDeletePidFile(bad);
        if (OperatingSystem.IsWindows())
        {
            Assert.IsFalse(ConfigReloadGate.TrySendSighup(Environment.ProcessId), "SIGHUP does not exist on Windows");
        }
    }

    // Bug class: a read-only or locked pid file makes the release path throw after the process already drained.
    [TestMethod]
    public void ReleasePidFile_FileCannotBeDeleted_ReturnsFalseAndKeepsTheFile()
    {
        var pidFile = Path.Combine(_dir, "titanium.pid");
        File.WriteAllText(pidFile, "100");
        var coordinator = new HotRestartCoordinator(pidFile, new FakePlatform(), ownPid: 100);

        using (new FileStream(pidFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.IsFalse(coordinator.ReleasePidFile());
        }

        Assert.IsTrue(File.Exists(pidFile));

        File.SetAttributes(pidFile, FileAttributes.ReadOnly);
        try
        {
            Assert.IsFalse(
                coordinator.ReleasePidFile() && !File.Exists(pidFile) && OperatingSystem.IsWindows(),
                "a read-only pid file is not deleted on Windows");
        }
        finally
        {
            File.SetAttributes(pidFile, FileAttributes.Normal);
        }

        Assert.IsTrue(coordinator.ReleasePidFile(), "a normal file naming this process is released");
        Assert.IsFalse(File.Exists(pidFile));
    }

    // Bug class: the successor's startup peek treats an unreadable pid file as a live predecessor and defers binds that
    // nothing will ever release.
    [TestMethod]
    public void PeekLivePrevious_UnreadablePidFile_IsTreatedAsNoPredecessor()
    {
        var pidFile = Path.Combine(_dir, "titanium.pid");
        File.WriteAllText(pidFile, "100");
        var platform = new FakePlatform();
        platform.Alive.Add(100);
        var coordinator = new HotRestartCoordinator(pidFile, platform, ownPid: 200);
        Assert.AreEqual(100, coordinator.PeekLivePrevious());

        using (new FileStream(pidFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.IsNull(coordinator.PeekLivePrevious());
        }
    }

    // Bug class: an empty bind list makes the deferred-bind task wait on the old process for nothing (and report a
    // timeout for work that did not exist).
    [TestMethod]
    [Timeout(15_000)]
    public async Task DeferredBind_NothingToBind_SucceedsWithoutWaiting()
    {
        var platform = new FakePlatform();
        platform.Alive.Add(100);
        var errors = new List<string>();

        var ok = await HotRestartDeferredBind.RunAsync(
            100, platform, [], TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), errors.Add, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, errors.Count);
    }

    // Bug class: cancelling the run (process stop) while waiting for the predecessor throws out of the background task
    // or goes on to bind UDP anyway.
    [TestMethod]
    [Timeout(15_000)]
    public async Task DeferredBind_CancelledWhileWaiting_DoesNotBind()
    {
        var platform = new FakePlatform();
        platform.Alive.Add(100);
        var bound = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var ok = await HotRestartDeferredBind.RunAsync(
            100, platform, [() => bound++], TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10), _ => { }, cts.Token);

        Assert.IsFalse(ok);
        Assert.AreEqual(0, bound);
    }

    // Bug class: the platform probe reports a live same-program process on a host without /proc, so a Windows or
    // macOS machine believes a predecessor exists and tries to signal it.
    [TestMethod]
    public void LinuxPlatform_ReportsOnlyWhatProcAndTheOsCanProve()
    {
        var platform = new LinuxHotRestartPlatform();

        Assert.AreEqual(OperatingSystem.IsLinux(), platform.IsProcessAlive(Environment.ProcessId));
        Assert.AreEqual(OperatingSystem.IsLinux(), platform.IsSameProgram(Environment.ProcessId));
        Assert.IsFalse(platform.IsProcessAlive(int.MaxValue));
        Assert.IsFalse(platform.IsSameProgram(int.MaxValue));
    }

    // Bug class: the predecessor never notices that a successor owns the pid file (so it keeps accepting), or keeps
    // polling after it was disposed.
    [TestMethod]
    [Timeout(30_000)]
    public async Task HandoverWatch_SuccessorNamedInThePidFile_TriggersTheHandoverOnce()
    {
        var pidFile = Path.Combine(_dir, "titanium.pid");
        File.WriteAllText(pidFile, "200");
        var platform = new FakePlatform();
        platform.Alive.Add(200);
        var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;

        using (HotRestartHandoverWatch.Start(pidFile, ownPid: 100, platform, () =>
               {
                   Interlocked.Increment(ref count);
                   fired.TrySetResult(true);
               }))
        {
            Assert.IsTrue(await fired.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        await Task.Delay(400);
        Assert.AreEqual(1, count);
    }

    // Bug class: a pid file that names this process (or nobody alive) triggers a handover, so the only running process
    // stops accepting connections.
    [TestMethod]
    [Timeout(30_000)]
    public async Task HandoverWatch_PidFileNamingThisProcessOrADeadOne_NeverTriggers()
    {
        var pidFile = Path.Combine(_dir, "titanium.pid");
        var platform = new FakePlatform();
        var fired = 0;

        using var watch = HotRestartHandoverWatch.Start(pidFile, ownPid: 100, platform, () => Interlocked.Increment(ref fired));
        await Task.Delay(300); // no pid file yet

        await File.WriteAllTextAsync(pidFile, "not-a-pid");
        await Task.Delay(300);
        await File.WriteAllTextAsync(pidFile, "100"); // this process
        await Task.Delay(300);
        await File.WriteAllTextAsync(pidFile, "300"); // not alive
        await Task.Delay(400);

        Assert.AreEqual(0, Volatile.Read(ref fired));
    }

    // Bug class: a pid file that is briefly locked by the successor's atomic write kills the watch loop, so the
    // handover that follows is never noticed.
    [TestMethod]
    [Timeout(30_000)]
    public async Task HandoverWatch_PidFileTemporarilyLocked_KeepsPollingAndStillHandsOver()
    {
        var pidFile = Path.Combine(_dir, "titanium.pid");
        File.WriteAllText(pidFile, "200");
        var platform = new FakePlatform();
        platform.Alive.Add(200);
        var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var locked = new FileStream(pidFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            using var watch = HotRestartHandoverWatch.Start(pidFile, ownPid: 100, platform, () => fired.TrySetResult(true));
            await Task.Delay(700);
            Assert.IsFalse(fired.Task.IsCompleted, "a locked pid file cannot be read, so no handover yet");
            await locked.DisposeAsync();
            locked = null!;

            Assert.IsTrue(await fired.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            if (locked is not null)
            {
                await locked.DisposeAsync();
            }
        }
    }

    // Bug class: `titanium restart -c` with the value missing indexes past the argument list (crash) or silently
    // restarts some other config instead of asking for the config path.
    [TestMethod]
    [Timeout(15_000)]
    public async Task Restart_ConfigFlagWithoutValue_ExitsOneAndAsksForTheConfig()
    {
        var stderr = new StringWriter();
        var previous = Console.Error;
        try
        {
            Console.SetError(stderr);
            Assert.AreEqual(1, await RestartCommand.ExecuteAsync(["restart", "-c"]));
            await AsyncConsole.FlushAsync();
        }
        finally
        {
            Console.SetError(previous);
        }

        StringAssert.Contains(stderr.ToString(), "Provide -c");
    }

    // Unix file locks are advisory, so reading a "locked" file still succeeds there; the locked-file edge is Windows-only.
    private static void RequireWindowsMandatoryLocking()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Mandatory file locking is a Windows behaviour.");
        }
    }

    private void TryDeletePidArtifacts()
    {
        try
        {
            var pidPath = ConfigReloadGate.PidFilePath(_configPath);
            if (Directory.Exists(pidPath))
            {
                Directory.Delete(pidPath, recursive: true);
            }

            if (File.Exists(pidPath))
            {
                File.Delete(pidPath);
            }
        }
        catch
        {
            // Best-effort cleanup of the per-user pid directory entry.
        }
    }

    private static async Task<string> CaptureStderrAsync(Action action)
    {
        var previous = Console.Error;
        var writer = new StringWriter();
        try
        {
            Console.SetError(writer);
            action();
            await AsyncConsole.FlushAsync();
            await Task.Delay(50);
            await AsyncConsole.FlushAsync();
            return writer.ToString();
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    private sealed class FakePlatform : IHotRestartPlatform
    {
        public HashSet<int> Alive { get; } = [];

        public bool IsProcessAlive(int pid) => Alive.Contains(pid);

        public bool IsSameProgram(int pid) => true;

        public bool SignalHandover(int pid) => true;
    }
}
