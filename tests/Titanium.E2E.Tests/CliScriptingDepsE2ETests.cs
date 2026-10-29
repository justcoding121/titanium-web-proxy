using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

[TestClass]
public class CliScriptingDepsE2ETests
{
    [TestMethod]
    [TestCategory("E2E")]
    public async Task ScriptingDeps_Status_Exit0_PrintsLuaAndWasmtimeLines()
    {
        using var harness = new CliProcessHarness();
        var (code, stdout, stderr) = await harness.RunOnceAsync(["scripting-deps", "status"]);
        Assert.AreEqual(0, code, stdout + stderr);
        var text = stdout + stderr;
        StringAssert.Contains(text, "Suggested RID");
        StringAssert.Contains(text, "Lua:");
        StringAssert.Contains(text, "Wasmtime:");
        Assert.IsTrue(
            text.Contains("loaded", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not found", StringComparison.OrdinalIgnoreCase),
            text);
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task ScriptingDeps_DefaultSubcommand_IsStatus()
    {
        using var harness = new CliProcessHarness();
        var (code, stdout, stderr) = await harness.RunOnceAsync(["scripting-deps"]);
        Assert.AreEqual(0, code, stdout + stderr);
        StringAssert.Contains(stdout + stderr, "Wasmtime:");
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task ScriptingDeps_UnknownSubcommand_Exit1()
    {
        using var harness = new CliProcessHarness();
        var (code, stdout, stderr) = await harness.RunOnceAsync(["scripting-deps", "explode"]);
        Assert.AreEqual(1, code);
        StringAssert.Contains(stdout + stderr, "Unknown scripting-deps");
    }
}