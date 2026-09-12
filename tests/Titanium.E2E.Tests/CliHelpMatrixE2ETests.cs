using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

[TestClass]
public class CliHelpMatrixE2ETests
{
    private static bool ContainsAll(string text, params string[] needles) =>
        needles.All(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    [TestMethod]
    [TestCategory("E2E")]
    public async Task HelpMatrix_AllNestedHelps_Exit0()
    {
        using var harness = new CliProcessHarness();

        await AssertHelp(harness, ["help"], text => ContainsAll(text, "run", "test", "service", "http3-deps"), exit: 0);
        await AssertHelp(harness, ["-h"], text => ContainsAll(text, "run", "test"), exit: 0);
        await AssertHelp(harness, ["--help"], text => ContainsAll(text, "run", "test"), exit: 0);
        await AssertHelp(harness, ["run", "--help"], text =>
            text.Contains("-c", StringComparison.Ordinal) &&
            text.Contains("--service", StringComparison.Ordinal) &&
            !text.Contains("Missing required -c", StringComparison.OrdinalIgnoreCase), exit: 0);
        await AssertHelp(harness, ["test", "--help"], _ => true, exit: 0);
        await AssertHelp(harness, ["test", "-h"], _ => true, exit: 0);
        await AssertHelp(harness, ["version", "--help"], _ => true, exit: 0);
        await AssertHelp(harness, ["update", "--help"], text =>
            text.Contains("--plus", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("--remove-plus", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("Checking for updates", StringComparison.OrdinalIgnoreCase), exit: 0);
        await AssertHelp(harness, ["http3-deps", "--help"], _ => true, exit: 0);
        await AssertHelp(harness, ["http3-deps", "status", "--help"], _ => true, exit: 0);
        await AssertHelp(harness, ["http3-deps", "install", "--help"], _ => true, exit: 0);
        await AssertHelp(harness, ["service", "--help"], text =>
            ContainsAll(text, "install", "uninstall", "start", "stop", "restart", "status"), exit: 0);
        await AssertHelp(harness, ["service", "install", "--help"], text =>
            ContainsAll(text, "-c", "--name", "--user", "--no-start"), exit: 0);
        await AssertHelp(harness, ["service", "start", "--help"], _ => true, exit: 0);
        await AssertHelp(harness, ["service", "stop", "--help"], _ => true, exit: 0);
        await AssertHelp(harness, ["service", "restart", "--help"], _ => true, exit: 0);
        await AssertHelp(harness, ["service", "uninstall", "--help"], _ => true, exit: 0);
        await AssertHelp(harness, ["service", "status", "--help"], _ => true, exit: 0);
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task UnknownCommand_PrintsHelp_Exit1()
    {
        using var harness = new CliProcessHarness();
        var (code, stdout, stderr) = await harness.RunOnceAsync(["not-a-real-command"]);
        Assert.AreEqual(1, code);
        var text = stdout + stderr;
        StringAssert.Contains(text, "Unknown command");
        StringAssert.Contains(text, "run");
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task NoArgs_PrintsHelp_Exit1()
    {
        using var harness = new CliProcessHarness();
        var (code, stdout, stderr) = await harness.RunOnceAsync([]);
        Assert.AreEqual(1, code);
        StringAssert.Contains(stdout + stderr, "run");
    }

    private static async Task AssertHelp(
        CliProcessHarness harness,
        string[] args,
        Func<string, bool> predicate,
        int exit)
    {
        var (code, stdout, stderr) = await harness.RunOnceAsync(args, timeout: TimeSpan.FromSeconds(20));
        var text = stdout + stderr;
        Assert.AreEqual(exit, code, $"args=[{string.Join(' ', args)}] output={text}");
        Assert.IsTrue(predicate(text), $"Help predicate failed for [{string.Join(' ', args)}]: {text}");
    }
}
