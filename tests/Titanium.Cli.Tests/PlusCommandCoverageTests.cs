using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Cli.Service;

namespace Titanium.Cli.Tests;

/// <summary>Plus CLI utility branches a caller hits before a proxy ever starts.</summary>
[TestClass]
public class PlusCommandCoverageTests
{
    [TestMethod]
    public async Task Help_AndUnknownSubcommand_ExitCodes()
    {
        Assert.AreEqual(0, await PlusCommand.ExecuteAsync(["plus", "--help"]));
        Assert.AreEqual(0, await PlusCommand.ExecuteAsync(["plus", "help"]));
        Assert.AreEqual(1, await PlusCommand.ExecuteAsync(["plus"]));
        Assert.AreEqual(1, await PlusCommand.ExecuteAsync(["plus", "explode"]));
    }

    [TestMethod]
    public async Task HashPassword_Help_AndValidationBranches()
    {
        Assert.AreEqual(0, await PlusCommand.ExecuteAsync(["plus", "hash-password", "--help"]));
        Assert.AreEqual(1, await PlusCommand.ExecuteAsync(["plus", "hash-password", "--user", "a:b", "pw"]));
        Assert.AreEqual(1, await PlusCommand.ExecuteAsync(["plus", "hash-password", "--user", "alice", "--bogus", "pw"]));
        Assert.AreEqual(1, await PlusCommand.ExecuteAsync(["plus", "hash-password", "--iterations", "100", "pw"]));
        Assert.AreEqual(1, await PlusCommand.ExecuteAsync(["plus", "hash-password", "--iterations", "nope", "pw"]));
        Assert.AreEqual(0, await PlusCommand.ExecuteAsync([
            "plus", "hash-password", "--user", "bob", "--role", "operator", "--iterations", "12000", "secret",
        ]));
    }

    [TestMethod]
    public void ServiceDefaults_ResolveEnvironmentAndMuxerAliases()
    {
        var env = ServiceDefaults.ResolveServiceEnvironment();
        Assert.AreEqual("1", env["DOTNET_NOLOGO"]);
        Assert.AreEqual("1", env["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        Assert.AreEqual("1", env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"]);
        Assert.IsTrue(ServiceDefaults.IsDotnetMuxer("dotnet"));
        Assert.IsTrue(ServiceDefaults.IsDotnetHostPath("dotnet.exe"));
        Assert.IsFalse(ServiceDefaults.IsDotnetMuxer("titanium"));
        Assert.AreEqual("com.justcoding121.edge", ServiceDefaults.ResolveMacOsLabel("edge"));
    }
}
