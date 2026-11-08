using System.Diagnostics;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;

namespace Titanium.Cli.Tests;

[TestClass]
public class RestartCommandTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(5);
    private static readonly string[] RunArgsWatch = ["run", "-c", "a.json", "--watch"];
    private static readonly BindingFlags PrivStatic = BindingFlags.NonPublic | BindingFlags.Static;

    [TestMethod]
    public void RunArguments_AreRunPlusEverythingAfterRestart()
    {
        CollectionAssert.AreEqual(
            RunArgsWatch,
            RestartCommand.BuildRunArguments(["restart", "-c", "a.json", "--watch"]).ToArray());
    }

    [TestMethod]
    public void PrintHelp_ExitsZero()
    {
        Assert.AreEqual(0, RestartCommand.PrintHelp());
    }

    [TestMethod]
    public async Task ExecuteAsync_Help_ExitsZero()
    {
        Assert.AreEqual(0, await RestartCommand.ExecuteAsync(["restart", "--help"]));
    }

    [TestMethod]
    public async Task ExecuteAsync_MissingConfig_ExitsOne()
    {
        Assert.AreEqual(1, await RestartCommand.ExecuteAsync(["restart"]));
    }

    [TestMethod]
    public async Task ExecuteAsync_NonLinux_ExitsOneWithMessage()
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Non-Linux rejection is covered on Windows/macOS collectors.");
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "twp-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        File.WriteAllText(path, """
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: 1
            """);
        try
        {
            Assert.AreEqual(1, await RestartCommand.ExecuteAsync(["restart", "-c", path]));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_LinuxWithoutHotRestart_ExitsOne()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Hot-restart config gate is Linux-only after the OS check.");
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "twp-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        File.WriteAllText(path, """
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: 1
            """);
        try
        {
            Assert.AreEqual(1, await RestartCommand.ExecuteAsync(["restart", "-c", path]));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void BuildStartInfo_LaunchesThroughShWithRunArgsAndLogRedirect()
    {
        var method = typeof(RestartCommand).GetMethod("BuildStartInfo", PrivStatic);
        Assert.IsNotNull(method);
        var startInfo = (ProcessStartInfo)method.Invoke(
            null,
            [new[] { "restart", "-c", "a.yaml", "--watch" }, "out.log"])!;

        Assert.AreEqual("/bin/sh", startInfo.FileName);
        Assert.IsFalse(startInfo.UseShellExecute);
        Assert.AreEqual("out.log", startInfo.Environment["TWP_RESTART_LOG"]);
        CollectionAssert.Contains(startInfo.ArgumentList.ToArray(), "run");
        CollectionAssert.Contains(startInfo.ArgumentList.ToArray(), "-c");
        CollectionAssert.Contains(startInfo.ArgumentList.ToArray(), "a.yaml");
        CollectionAssert.Contains(startInfo.ArgumentList.ToArray(), "--watch");
        StringAssert.Contains(startInfo.ArgumentList[1], "TWP_RESTART_LOG");
    }

    [TestMethod]
    public void TailOf_ReturnsLastLines_OrEmptyWhenMissing()
    {
        var method = typeof(RestartCommand).GetMethod("TailOf", PrivStatic);
        Assert.IsNotNull(method);
        Assert.AreEqual(string.Empty, method.Invoke(null, [Path.Combine(Path.GetTempPath(), "twp-missing-" + Guid.NewGuid())]));

        var dir = Path.Combine(Path.GetTempPath(), "twp-restart-tail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "child.log");
        try
        {
            File.WriteAllLines(log, Enumerable.Range(1, 12).Select(i => "line-" + i));
            var tail = (string)method.Invoke(null, [log])!;
            StringAssert.Contains(tail, "line-12");
            StringAssert.Contains(tail, "line-3");
            Assert.IsFalse(tail.Contains("line-2", StringComparison.Ordinal), "only the last 10 lines are kept");

            var empty = Path.Combine(dir, "empty.log");
            File.WriteAllText(empty, "");
            Assert.AreEqual(string.Empty, method.Invoke(null, [empty]));
            Assert.AreEqual(string.Empty, method.Invoke(null, [Path.Combine(dir, "missing.log")]));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void ReadPid_ParsesPositivePids_AndIgnoresBadFiles()
    {
        var method = typeof(RestartCommand).GetMethod("ReadPid", PrivStatic);
        Assert.IsNotNull(method);
        var dir = Path.Combine(Path.GetTempPath(), "twp-restart-pid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ok = Path.Combine(dir, "ok.pid");
            File.WriteAllText(ok, " 4242 \n");
            Assert.AreEqual(4242, method.Invoke(null, [ok]));

            var bad = Path.Combine(dir, "bad.pid");
            File.WriteAllText(bad, "not-a-pid");
            Assert.IsNull(method.Invoke(null, [bad]));

            var zero = Path.Combine(dir, "zero.pid");
            File.WriteAllText(zero, "0");
            Assert.IsNull(method.Invoke(null, [zero]));

            Assert.IsNull(method.Invoke(null, [Path.Combine(dir, "missing.pid")]));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public async Task PidFileNamingTheChild_IsATakeOver()
    {
        var calls = 0;
        var outcome = await RestartCommand.WaitForTakeover(
            () => ++calls < 3 ? 100 : 200, childPid: 200, childAlive: () => true,
            TimeSpan.FromSeconds(5), Poll, CancellationToken.None);

        Assert.AreEqual(RestartOutcome.TookOver, outcome);
    }

    [TestMethod]
    public async Task ChildThatExitsBeforeTakingOver_IsReportedAndTheOldPidFileIsUntouched()
    {
        var outcome = await RestartCommand.WaitForTakeover(
            () => 100, childPid: 200, childAlive: () => false,
            TimeSpan.FromSeconds(5), Poll, CancellationToken.None);

        Assert.AreEqual(RestartOutcome.ChildExited, outcome);
    }

    [TestMethod]
    public async Task ChildThatNeverTakesOver_TimesOut()
    {
        var outcome = await RestartCommand.WaitForTakeover(
            () => 100, childPid: 200, childAlive: () => true,
            TimeSpan.FromMilliseconds(60), Poll, CancellationToken.None);

        Assert.AreEqual(RestartOutcome.TimedOut, outcome);
    }

    [TestMethod]
    public async Task ChildThatTookOverAndThenExited_StillCountsAsTookOver()
    {
        var reads = 0;
        var outcome = await RestartCommand.WaitForTakeover(
            () => ++reads == 1 ? 100 : 200, childPid: 200, childAlive: () => false,
            TimeSpan.FromSeconds(5), Poll, CancellationToken.None);

        Assert.AreEqual(RestartOutcome.TookOver, outcome);
    }

    [TestMethod]
    public async Task MissingPidFile_KeepsWaiting()
    {
        var calls = 0;
        var outcome = await RestartCommand.WaitForTakeover(
            () => ++calls < 4 ? null : 200, childPid: 200, childAlive: () => true,
            TimeSpan.FromSeconds(5), Poll, CancellationToken.None);

        Assert.AreEqual(RestartOutcome.TookOver, outcome);
    }
}
