using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

[TestClass]
public class CliPlusHashPasswordE2ETests
{
    [TestMethod]
    [TestCategory("E2E")]
    public async Task Plus_HashPassword_PrintsUsersFileLine()
    {
        using var harness = new CliProcessHarness();
        var (code, stdout, stderr) = await harness.RunOnceAsync(
            ["plus", "hash-password", "--user", "alice", "--role", "viewer", "e2e-secret-pw"]);
        Assert.AreEqual(0, code, stdout + stderr);
        var text = stdout + stderr;
        StringAssert.Contains(text, "alice");
        Assert.IsTrue(
            text.Contains("$2", StringComparison.Ordinal) || text.Contains("bcrypt", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("hash", StringComparison.OrdinalIgnoreCase) || text.Contains(":", StringComparison.Ordinal),
            text);
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Plus_HashPassword_RejectsBadRole()
    {
        using var harness = new CliProcessHarness();
        var (code, stdout, stderr) = await harness.RunOnceAsync(
            ["plus", "hash-password", "--user", "bob", "--role", "admin", "pw"]);
        Assert.AreNotEqual(0, code, stdout + stderr);
    }
}
