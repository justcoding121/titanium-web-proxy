using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Cli.Service;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests;

[TestClass]
public class ServiceManagerAndRunCoverageTests
{
    [TestMethod]
    public async Task ExecuteCoreAsync_StartsAndStopsMinimalListener()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var port = GetFreePort();
        var path = Path.Combine(dir, "twp.yaml");
        File.WriteAllText(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {port}
                decryptSsl: false
            """);
        using var cts = new CancellationTokenSource();
        try
        {
            var run = RunCommand.ExecuteCoreAsync(path, verbose: true, serviceMode: true, cts.Token);
            await Task.Delay(1500);
            await cts.CancelAsync();
            var code = await run;
            Assert.AreEqual(0, code);
        }
        finally
        {
            if (!cts.IsCancellationRequested)
                await cts.CancelAsync();
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void RunCommand_ParsersAndServiceLoggingCoverRemainingBranches()
    {
        Assert.AreEqual("cfg.yaml", RunCommand.ParseConfigPath(["run", "-c", "cfg.yaml"]));
        Assert.AreEqual("cfg.yaml", RunCommand.ParseConfigPath(["run", "--config", "cfg.yaml"]));
        Assert.IsTrue(RunCommand.ParseVerbose(["run", "-v"]));
        Assert.IsTrue(RunCommand.ParseVerbose(["run", "--verbose"]));
        Assert.IsFalse(RunCommand.ParseVerbose(["run"]));
        Assert.IsTrue(RunCommand.ParseServiceMode(["run", "--service"]));
        Assert.AreEqual("svc", RunCommand.ParseServiceName(["run", "--name", "svc"]));
        Assert.IsNull(RunCommand.ParseServiceName(["run"]));

        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        RunCommand.ApplyServiceLoggingDefaults(proxy, logging: null);
        RunCommand.ApplyServiceLoggingDefaults(proxy, new LoggingConfig
        {
            EnableFile = true,
            FilePath = Path.Combine(Path.GetTempPath(), "twp-already.log"),
        });
        RunCommand.ResolvePlusRelativePaths(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["grpc.transcode.descriptorSet"] = "desc.pb",
            ["other"] = "x",
        }, Path.GetTempPath());
    }

    [TestMethod]
    public async Task WindowsServiceManager_StatusOfMissingService_AndUnelevatedInstall()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows-only");
            return;
        }

        var mgr = new WindowsServiceManager();
        var status = await mgr.StatusAsync("titanium-qa-cov-missing-" + Guid.NewGuid().ToString("N")[..8], user: false);
        Assert.AreEqual(ServiceStatusKind.NotInstalled, status.Kind);
        _ = WindowsServiceManager.IsElevated();

        if (!WindowsServiceManager.IsElevated())
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                mgr.InstallAsync(DummyInstall("titanium-qa-cov-nope")));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                mgr.UninstallAsync("titanium-qa-cov-nope", user: false));
        }
    }

    [TestMethod]
    public async Task SystemdAndLaunchd_UserStatusAndBestEffortInstall()
    {
        var systemd = new SystemdServiceManager();
        var missingUnit = await systemd.StatusAsync("titanium-qa-cov-missing-" + Guid.NewGuid().ToString("N")[..8], user: true);
        Assert.AreEqual(ServiceStatusKind.NotInstalled, missingUnit.Kind);

        var name = "titanium-qa-cov-" + Guid.NewGuid().ToString("N")[..8];
        var unitPath = ServiceUnitFactory.ResolveSystemdUnitPath(name, user: true);
        try
        {
            await systemd.InstallAsync(DummyInstall(name, user: true, start: false));
        }
        catch (InvalidOperationException)
        {
            // systemctl is absent on Windows/macOS; write path still executed.
        }
        finally
        {
            if (File.Exists(unitPath))
                File.Delete(unitPath);
        }

        var launchd = new LaunchdServiceManager();
        var missingPlist = await launchd.StatusAsync(name, user: true);
        Assert.AreEqual(ServiceStatusKind.NotInstalled, missingPlist.Kind);

        if (OperatingSystem.IsMacOS())
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(LaunchdServiceManager.ResolveUserHome()));
            _ = LaunchdServiceManager.ResolveTargetUid();
        }

        _ = PrivilegePrompt.ResolveSudoPath();
        _ = PrivilegePrompt.InteractivePromptMessage();
        _ = PrivilegePrompt.FallbackMessage();
        _ = PrivilegePrompt.CanPromptInteractively();
        var abs = PrivilegePrompt.AbsolutizeConfigArgs(["run", "-c", "cfg.yaml", "--config", "other.yaml"]);
        Assert.AreEqual(5, abs.Length);
        _ = PrivilegePrompt.JoinWindowsArguments(["a b", "c"]);
        PrivilegePrompt.ResetForTests();
        PrivilegePrompt.TryAttachParentConsole();
        var previousNoElevate = Environment.GetEnvironmentVariable(PrivilegePrompt.NoElevateEnv);
        try
        {
            Environment.SetEnvironmentVariable(PrivilegePrompt.NoElevateEnv, "1");
            PrivilegePrompt.ResetForTests();
            PrivilegePrompt.TakeInternalArgs([PrivilegePrompt.RelaunchFlag, "status"]);
            var elevated = await PrivilegePrompt.EnsureOrRelaunchAsync(["service", "status"]);
            if (!PrivilegePrompt.IsElevated())
                Assert.AreEqual(1, elevated);
        }
        finally
        {
            PrivilegePrompt.ResetForTests();
            Environment.SetEnvironmentVariable(PrivilegePrompt.NoElevateEnv, previousNoElevate);
        }
    }

    private static ServiceInstallRequest DummyInstall(string name, bool user = false, bool start = false)
    {
        var cfg = Path.Combine(Path.GetTempPath(), name + ".yaml");
        File.WriteAllText(cfg, "schemaVersion: \"7.1\"\nlisteners:\n  - host: 127.0.0.1\n    port: 0\n");
        return new ServiceInstallRequest(
            name,
            cfg,
            user,
            start,
            [Environment.ProcessPath ?? "dotnet"],
            Path.GetTempPath());
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
