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

    [TestMethod]
    [TestCategory("E2E")]
    public async Task ScriptingDeps_Status_Exit0_WhenNativesMayBePresent()
    {
        using var harness = new CliProcessHarness();
        var (code, stdout, stderr) = await harness.RunOnceAsync(["scripting-deps", "status"]);
        Assert.AreEqual(0, code, stdout + stderr);
        StringAssert.Contains(stdout + stderr, "Lua:");
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task ScriptingDeps_Install_IsDocumentedOrSucceeds()
    {
        using var harness = new CliProcessHarness();
        var (code, stdout, stderr) = await harness.RunOnceAsync(
            ["scripting-deps", "install"],
            timeout: TimeSpan.FromMinutes(2));
        var text = stdout + stderr;
        // Exit 0 when natives already load; otherwise install guidance / unsupported RID.
        Assert.IsTrue(code is 0 or 1, text);
        if (code != 0)
        {
            Assert.IsTrue(
                text.Contains("bundle-scripting-native", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("RID", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Lua", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Wasmtime", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("install", StringComparison.OrdinalIgnoreCase),
                text);
        }
    }
}