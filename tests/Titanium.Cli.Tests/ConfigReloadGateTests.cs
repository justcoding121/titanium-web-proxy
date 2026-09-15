using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;

namespace Titanium.Cli.Tests;

[TestClass]
public class ConfigReloadGateTests
{
    private string _tempDir = null!;
    private string _configPath = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-reload-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "twp.yaml");
        File.WriteAllText(_configPath, "schemaVersion: \"7.1\"\n");
    }

    [TestCleanup]
    public void Cleanup()
    {
        ConfigReloadGate.TryDeletePidFile(_configPath);
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    [TestMethod]
    public void ConfigKey_IsStableForSamePath()
    {
        var a = ConfigReloadGate.ConfigKey(_configPath);
        var b = ConfigReloadGate.ConfigKey(_configPath);
        Assert.AreEqual(a, b);
        Assert.AreEqual(16, a.Length);
    }

    [TestMethod]
    public void PidFile_RoundTrips()
    {
        ConfigReloadGate.WritePidFile(_configPath, 424242);
        Assert.AreEqual(424242, ConfigReloadGate.TryReadPid(_configPath));
        ConfigReloadGate.TryDeletePidFile(_configPath);
        Assert.IsNull(ConfigReloadGate.TryReadPid(_configPath));
    }

    [TestMethod]
    public void ParseWatch_DetectsFlag()
    {
        Assert.IsTrue(RunCommand.ParseWatch(["run", "-c", "x.yaml", "--watch"]));
        Assert.IsFalse(RunCommand.ParseWatch(["run", "-c", "x.yaml"]));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void WindowsReloadEvent_SignalReachesWaiter()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named EventWaitHandle path is Windows-only.");
            return;
        }

        using var handle = ConfigReloadGate.CreateWindowsReloadEvent(_configPath, out _);
        var signaled = new ManualResetEventSlim(false);
        var reg = ThreadPool.RegisterWaitForSingleObject(
            handle,
            (_, _) => signaled.Set(),
            null,
            -1,
            executeOnlyOnce: true);
        try
        {
            Assert.IsTrue(ConfigReloadGate.TrySignalWindowsReload(_configPath));
            Assert.IsTrue(signaled.Wait(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            reg.Unregister(null);
        }
    }

    [TestMethod]
    public async Task ReloadCommand_MissingArgs_Returns1()
    {
        var code = await ReloadCommand.ExecuteAsync(["reload"]);
        Assert.AreEqual(1, code);
    }

    [TestMethod]
    public async Task ReloadCommand_Help_Returns0()
    {
        var code = await ReloadCommand.ExecuteAsync(["reload", "--help"]);
        Assert.AreEqual(0, code);
    }

    [TestMethod]
    public void ConfigKey_IsCaseInsensitiveOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Case-normalize path is Windows-only.");
            return;
        }

        var upper = Path.Combine(_tempDir, "Foo.YAML");
        var lower = Path.Combine(_tempDir, "foo.yaml");
        Assert.AreEqual(ConfigReloadGate.ConfigKey(upper), ConfigReloadGate.ConfigKey(lower));
    }

    [TestMethod]
    public void TryReadPid_MalformedAndMissing_ReturnNull()
    {
        Assert.IsNull(ConfigReloadGate.TryReadPid(Path.Combine(_tempDir, "no-such.yaml")));
        ConfigReloadGate.WritePidFile(_configPath, 1);
        File.WriteAllText(ConfigReloadGate.PidFilePath(_configPath), "not-a-number");
        Assert.IsNull(ConfigReloadGate.TryReadPid(_configPath));
        ConfigReloadGate.TryDeletePidFile(_configPath);
        ConfigReloadGate.TryDeletePidFile(_configPath); // second delete is best-effort
    }

    [TestMethod]
    public void IsProcessAlive_AndTrySendSighup_CoverBranches()
    {
        Assert.IsTrue(ConfigReloadGate.IsProcessAlive(Environment.ProcessId));
        Assert.IsFalse(ConfigReloadGate.IsProcessAlive(int.MaxValue - 7));
        if (OperatingSystem.IsWindows())
            Assert.IsFalse(ConfigReloadGate.TrySendSighup(Environment.ProcessId));
    }

    [TestMethod]
    public void TrySignalWindowsReload_WithoutWaiter_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named EventWaitHandle path is Windows-only.");
            return;
        }

        Assert.IsFalse(ConfigReloadGate.TrySignalWindowsReload(
            Path.Combine(_tempDir, "no-waiter-" + Guid.NewGuid().ToString("N") + ".yaml")));
    }

    [TestMethod]
    public async Task ReloadCommand_Windows_NoWaiter_Returns1()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows reload event path.");
            return;
        }

        var code = await ReloadCommand.ExecuteAsync(["reload", "-c", _configPath]);
        Assert.AreEqual(1, code);
    }

    [TestMethod]
    public async Task ReloadCommand_Windows_WithWaiter_Returns0()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows reload event path.");
            return;
        }

        using var handle = ConfigReloadGate.CreateWindowsReloadEvent(_configPath, out _);
        ConfigReloadGate.WritePidFile(_configPath, Environment.ProcessId);
        var code = await ReloadCommand.ExecuteAsync(["reload", "-c", _configPath]);
        Assert.AreEqual(0, code);
        Assert.IsTrue(handle.WaitOne(0));
    }

    [TestMethod]
    public async Task ReloadCommand_Windows_PidWithoutConfig_Returns1()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows requires -c.");
            return;
        }

        var code = await ReloadCommand.ExecuteAsync(["reload", "--pid", "1"]);
        Assert.AreEqual(1, code);
    }

    [TestMethod]
    public void TryParsePid_RejectsInvalidValues()
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var parse = typeof(ReloadCommand).GetMethod("TryParsePid", flags)!;
        Assert.IsNull(parse.Invoke(null, [new[] { "reload", "--pid", "0" }]));
        Assert.IsNull(parse.Invoke(null, [new[] { "reload", "--pid" }]));
        Assert.IsNull(parse.Invoke(null, [new[] { "reload", "--pid", "abc" }]));
        Assert.AreEqual(42, (int)parse.Invoke(null, [new[] { "reload", "--pid", "42" }])!);
    }
}
