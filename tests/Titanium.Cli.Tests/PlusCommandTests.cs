using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;

namespace Titanium.Cli.Tests;

[TestClass]
public class PlusCommandTests
{
    [TestMethod]
    public async Task HashPassword_PrintsUsersFileLine()
    {
        var code = await PlusCommand.ExecuteAsync(["plus", "hash-password", "--user", "alice", "--role", "viewer", "pw"]);
        Assert.AreEqual(0, code);
        // Output goes through AsyncConsole; shape is validated by FormatLine round-trip in Plus.Tests.
        // Ensure the command path accepts the args without error.
    }

    [TestMethod]
    public async Task HashPassword_RejectsBadRole()
    {
        Assert.AreEqual(1, await PlusCommand.ExecuteAsync(["plus", "hash-password", "--role", "admin", "pw"]));
    }
}
