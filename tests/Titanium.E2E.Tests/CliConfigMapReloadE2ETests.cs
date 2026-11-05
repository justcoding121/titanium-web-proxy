using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

/// <summary>
///     <c>titanium run --watch</c> against a Kubernetes-style ConfigMap mount, where the config file is a symlink
///     through <c>..data</c> and an update atomically repoints <c>..data</c> at a new revision directory.
/// </summary>
[TestClass]
public class CliConfigMapReloadE2ETests
{
    private const string Reloaded = "Config reloaded.";

    private string _mount = null!;
    private int _proxyPort;

    [TestInitialize]
    public void Init()
    {
        _mount = Path.Combine(Path.GetTempPath(), "twp-e2e-cm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_mount);
        _proxyPort = CliProcessHarness.GetFreePort();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_mount))
            {
                Directory.Delete(_mount, recursive: true);
            }
        }
        catch
        {
            // The CLI may still hold a handle for a moment.
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(120_000)]
    public async Task Run_Watch_ReloadsExactlyOncePerConfigMapSwap_AndKeepsOldConfigWhenReloadFails()
    {
        var configPath = TryCreateMount();
        using var harness = new CliProcessHarness();
        await harness.StartRunAsync(configPath, watch: true);
        await harness.WaitForOutputAsync("config-watch-enabled", TimeSpan.FromSeconds(10));

        // 1) A valid new revision: one reload.
        Swap("..v2", Config("# revision 2"));
        await harness.WaitForOutputAsync(Reloaded, TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, Count(harness.StdOut, Reloaded), harness.StdOut + harness.StdErr);

        // 2) An invalid revision: reported, the old config stays in force and the process stays up.
        Swap("..v3", "listeners: [this is not valid");
        await harness.WaitForOutputAsync("Config reload failed", TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, Count(harness.StdOut, Reloaded), "a failed reload must not report success");
        Assert.IsTrue(await CanConnectAsync(_proxyPort), "the proxy listener must keep serving the old config");

        // 3) A fixed revision recovers with exactly one more reload.
        Swap("..v4", Config("# revision 4"));
        await harness.WaitForOutputAsync("Config reloaded.", TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, Count(harness.StdOut, Reloaded), harness.StdOut + harness.StdErr);
        Assert.IsTrue(await CanConnectAsync(_proxyPort));
    }

    private string Config(string comment) => $"""
        schemaVersion: "7.0"
        {comment}
        listeners:
          - host: "127.0.0.1"
            port: {_proxyPort}
        """;

    /// <summary>Builds <c>twp.yaml -> ..data/twp.yaml</c> and <c>..data -> ..v1</c>, as kubelet does.</summary>
    private string TryCreateMount()
    {
        var v1 = Path.Combine(_mount, "..v1");
        Directory.CreateDirectory(v1);
        File.WriteAllText(Path.Combine(v1, "twp.yaml"), Config("# revision 1"));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_mount, "..data"), "..v1");
            File.CreateSymbolicLink(Path.Combine(_mount, "twp.yaml"), Path.Combine("..data", "twp.yaml"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Symbolic links are not available here: " + ex.Message);
        }

        return Path.Combine(_mount, "twp.yaml");
    }

    private void Swap(string revision, string content)
    {
        var dir = Path.Combine(_mount, revision);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "twp.yaml"), content);

        var data = Path.Combine(_mount, "..data");
        var tmp = Path.Combine(_mount, "..data_tmp");
        if (Directory.Exists(tmp) || File.Exists(tmp))
        {
            Directory.Delete(tmp);
        }

        Directory.CreateSymbolicLink(tmp, revision);
        // Neither Windows nor .NET's Directory.Move will replace an existing directory symlink;
        // kubelet uses rename(2) -T. Delete then move is the portable equivalent.
        if (Directory.Exists(data) || File.Exists(data))
        {
            Directory.Delete(data);
        }

        Directory.Move(tmp, data);
    }

    private static int Count(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(5));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
