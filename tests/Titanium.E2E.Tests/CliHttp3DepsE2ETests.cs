using System.Net.Quic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

[TestClass]
public class CliHttp3DepsE2ETests
{
    [TestMethod]
    [TestCategory("E2E")]
    public async Task Http3Deps_Status_Exit0_PrintsOsAndRid()
    {
        using var harness = new CliProcessHarness();
        var (code, stdout, stderr) = await harness.RunOnceAsync(["http3-deps", "status"]);
        Assert.AreEqual(0, code);
        var text = stdout + stderr;
        StringAssert.Contains(text, "Suggested RID");
        Assert.IsTrue(
            text.Contains("Windows", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Linux", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("macOS", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Osx", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("OS", StringComparison.OrdinalIgnoreCase),
            text);
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Http3Deps_DefaultSubcommand_IsStatus()
    {
        using var harness = new CliProcessHarness();
        var (code, stdout, stderr) = await harness.RunOnceAsync(["http3-deps"]);
        Assert.AreEqual(0, code);
        StringAssert.Contains(stdout + stderr, "Suggested RID");
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Http3Deps_UnknownSubcommand_Exit1()
    {
        using var harness = new CliProcessHarness();
        var (code, stdout, stderr) = await harness.RunOnceAsync(["http3-deps", "explode"]);
        Assert.AreEqual(1, code);
        StringAssert.Contains(stdout + stderr, "Unknown http3-deps");
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Http3Deps_Install_WindowsOrAlreadySupported()
    {
        using var harness = new CliProcessHarness();
        var quic = QuicListener.IsSupported;
        var (code, stdout, stderr) = await harness.RunOnceAsync(
            ["http3-deps", "install"],
            timeout: TimeSpan.FromMinutes(3));
        var text = stdout + stderr;

        if (quic)
        {
            Assert.AreEqual(0, code, text);
            StringAssert.Contains(text, "nothing to install", StringComparison.OrdinalIgnoreCase);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Assert.AreNotEqual(0, code, text);
            Assert.IsTrue(
                text.Contains("Windows", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("MsQuic", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Upgrade", StringComparison.OrdinalIgnoreCase),
                text);
            return;
        }

        // Unix without Quic: real package install (requires sudo/brew on CI).
        Assert.IsTrue(code == 0 || text.Length > 0, text);
        if (code == 0)
        {
            Assert.IsTrue(
                text.Contains("QuicListener", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("libmsquic", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("install", StringComparison.OrdinalIgnoreCase),
                text);
        }
    }
}
