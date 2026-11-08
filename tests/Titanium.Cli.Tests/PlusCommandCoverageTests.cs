using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Cli.Parsers;
using Titanium.Cli.Service;
using Titanium.Cli.Updates;
using Titanium.Web.Proxy.Configuration.Models;

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
        _ = ServiceDefaults.ResolveRelaunchTarget();
        _ = ServiceDefaults.ResolveExePath();
    }

    [TestMethod]
    public async Task HashPassword_ReadsRedirectedStdin_AndRejectsEmpty()
    {
        var previousIn = Console.In;
        try
        {
            Console.SetIn(new StringReader("\n"));
            Assert.AreEqual(1, await PlusCommand.ExecuteAsync(["plus", "hash-password", "--user", "stdin-user"]));
            Console.SetIn(new StringReader("from-stdin\n"));
            Assert.AreEqual(0, await PlusCommand.ExecuteAsync(["plus", "hash-password", "--user", "stdin-user"]));
        }
        finally
        {
            Console.SetIn(previousIn);
        }
    }

    [TestMethod]
    public void RunCommand_ParseZoneAndWriteWarnings_AndAcmeSessionGate()
    {
        var parseZone = typeof(RunCommand).GetMethod("ParseZone", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(parseZone);
        Assert.AreEqual("z1", parseZone.Invoke(null, [new[] { "run", "--zone", "z1" }]));
        Assert.AreEqual("z2", parseZone.Invoke(null, [new[] { "run", "--zone=z2" }]));
        Assert.IsNull(parseZone.Invoke(null, [new[] { "run", "-c", "x.yaml" }]));

        var writeWarnings = typeof(RunCommand).GetMethod("WriteConfigWarnings", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(writeWarnings);
        writeWarnings.Invoke(null, [
            new LoadedConfig
            {
                Config = new TwpConfig(),
                Path = "x.yaml",
                Dialect = "twp-native",
                UnknownKeys = ["listeners[0].typo"],
            },
        ]);

        Assert.AreEqual(1, UpdateCommand.RemovePlus(Path.Combine(Path.GetTempPath(), "twp-missing-plus-" + Guid.NewGuid().ToString("N"))));
        Assert.AreEqual(1, UpdateCommand.ApplyPlusBundleZip(
            "missing.zip",
            Path.Combine(Path.GetTempPath(), "twp-missing-plus-" + Guid.NewGuid().ToString("N"))));
    }
}
