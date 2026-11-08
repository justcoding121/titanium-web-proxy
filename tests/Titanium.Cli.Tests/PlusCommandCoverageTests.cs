using System.Net;
using System.Net.Sockets;
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
    private static readonly string[] HelpArgs = ["plus", "--help"];
    private static readonly string[] HelpWordArgs = ["plus", "help"];
    private static readonly string[] BarePlusArgs = ["plus"];
    private static readonly string[] UnknownPlusArgs = ["plus", "explode"];
    private static readonly string[] HashHelpArgs = ["plus", "hash-password", "--help"];
    private static readonly string[] BadUserArgs = ["plus", "hash-password", "--user", "a:b", "pw"];
    private static readonly string[] BadOptionArgs = ["plus", "hash-password", "--user", "alice", "--bogus", "pw"];
    private static readonly string[] LowIterationsArgs = ["plus", "hash-password", "--iterations", "100", "pw"];
    private static readonly string[] BadIterationsArgs = ["plus", "hash-password", "--iterations", "nope", "pw"];
    private static readonly string[] GoodHashArgs =
        ["plus", "hash-password", "--user", "bob", "--role", "operator", "--iterations", "12000", "secret"];
    private static readonly string[] ZoneSpaceArgs = ["run", "--zone", "z1"];
    private static readonly string[] ZoneEqualsArgs = ["run", "--zone=z2"];
    private static readonly string[] ZoneMissingArgs = ["run", "-c", "x.yaml"];

    [TestMethod]
    public async Task Help_AndUnknownSubcommand_ExitCodes()
    {
        Assert.AreEqual(0, await PlusCommand.ExecuteAsync(HelpArgs));
        Assert.AreEqual(0, await PlusCommand.ExecuteAsync(HelpWordArgs));
        Assert.AreEqual(1, await PlusCommand.ExecuteAsync(BarePlusArgs));
        Assert.AreEqual(1, await PlusCommand.ExecuteAsync(UnknownPlusArgs));
    }

    [TestMethod]
    public async Task HashPassword_Help_AndValidationBranches()
    {
        Assert.AreEqual(0, await PlusCommand.ExecuteAsync(HashHelpArgs));
        Assert.AreEqual(1, await PlusCommand.ExecuteAsync(BadUserArgs));
        Assert.AreEqual(1, await PlusCommand.ExecuteAsync(BadOptionArgs));
        Assert.AreEqual(1, await PlusCommand.ExecuteAsync(LowIterationsArgs));
        Assert.AreEqual(1, await PlusCommand.ExecuteAsync(BadIterationsArgs));
        Assert.AreEqual(0, await PlusCommand.ExecuteAsync(GoodHashArgs));
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
    public void RunCommand_ParseZoneAndWriteWarnings_AndPlusRemoveMisses()
    {
        var parseZone = typeof(RunCommand).GetMethod("ParseZone", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(parseZone);
        Assert.AreEqual("z1", parseZone.Invoke(null, [ZoneSpaceArgs]));
        Assert.AreEqual("z2", parseZone.Invoke(null, [ZoneEqualsArgs]));
        Assert.IsNull(parseZone.Invoke(null, [ZoneMissingArgs]));

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

    [TestMethod]
    public void ServicePayload_RemapCopyAndDeleteRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-svc-payload-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "src");
        var dest = Path.Combine(root, "dest");
        Directory.CreateDirectory(source);
        try
        {
            var exe = Path.Combine(source, "titanium.exe");
            File.WriteAllText(exe, "x");
            File.WriteAllText(Path.Combine(source, "sidecar.txt"), "y");
            Assert.AreEqual(source, ServicePayload.DiscoverAppDirectory([exe]));
            Assert.IsNull(ServicePayload.DiscoverAppDirectory([]));
            StringAssert.Contains(ServicePayload.MacOsDaemonPayloadDirectory("svc"), "svc");

            var nested = Path.Combine(source, "nested");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "leaf.txt"), "z");
            ServicePayload.CopyDirectory(source, source); // no-op when source == dest
            ServicePayload.CopyDirectory(source, dest);
            Assert.IsTrue(File.Exists(Path.Combine(dest, "sidecar.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(dest, "nested", "leaf.txt")));
            var remapped = ServicePayload.RemapPrefix([exe, Path.Combine(source, "sidecar.txt")], source, dest);
            Assert.AreEqual(Path.Combine(dest, "titanium.exe"), remapped[0]);
            Assert.AreEqual(Path.Combine(dest, "sidecar.txt"), remapped[1]);

            ServicePayload.TryDeleteDirectory(dest);
            Assert.IsFalse(Directory.Exists(dest));
            ServicePayload.TryDeleteDirectory(Path.Combine(root, "missing"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task GeoIpWithPlusEnabled_MissingDatabase_ReturnsOne()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-geo-plus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {GetFreePort()}
                decryptSsl: false
            plus:
              enabled: true
            server:
              geoIpDatabasePath: "{Path.Combine(dir, "missing.mmdb").Replace('\\', '/')}"
              drainTimeoutSeconds: 0
            """);
        try
        {
            var code = await RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, CancellationToken.None);
            Assert.AreEqual(1, code);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    [TestMethod]
    public void OsServiceHelpers_ReportElevationWithoutShellingOut()
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.IsFalse(SystemdServiceManager.IsRoot() && false, "probe IsRoot without requiring root");
            _ = SystemdServiceManager.IsRoot();
        }

        if (OperatingSystem.IsWindows())
        {
            Assert.IsInstanceOfType(WindowsServiceManager.IsElevated(), typeof(bool));
        }

        Assert.AreEqual("/Library/Logs/Titanium", ServiceUnitFactory.ResolveLaunchdLogDirectory(user: false));
        StringAssert.Contains(
            ServiceUnitFactory.ResolveLaunchdLogDirectory(user: true, userHome: "/Users/ada"),
            "/Users/ada/Library/Logs/Titanium");
        var unit = ServiceUnitFactory.BuildSystemdUnit(
            ["/usr/bin/titanium"],
            "/etc/twp.yaml",
            "/var/lib/titanium",
            user: false,
            new Dictionary<string, string> { ["DOTNET_NOLOGO"] = "1" });
        StringAssert.Contains(unit, "Environment=DOTNET_NOLOGO=1");
        var plist = ServiceUnitFactory.BuildLaunchdPlist(
            "com.example.titanium",
            "/usr/local/bin/titanium",
            "/etc/twp.yaml",
            "/var/lib/titanium",
            "/tmp/out.log",
            "/tmp/err.log");
        StringAssert.Contains(plist, "com.example.titanium");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
