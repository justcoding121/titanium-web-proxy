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
    public void WindowsReloadEvent_SignalReachesWaiter()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named EventWaitHandle path is Windows-only.");
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
}
