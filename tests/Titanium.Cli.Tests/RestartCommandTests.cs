using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;

namespace Titanium.Cli.Tests;

[TestClass]
public class RestartCommandTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(5);
    private static readonly string[] RunArgsWatch = ["run", "-c", "a.json", "--watch"];

    [TestMethod]
    public void RunArguments_AreRunPlusEverythingAfterRestart()
    {
        CollectionAssert.AreEqual(
            RunArgsWatch,
            RestartCommand.BuildRunArguments(["restart", "-c", "a.json", "--watch"]).ToArray());
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
