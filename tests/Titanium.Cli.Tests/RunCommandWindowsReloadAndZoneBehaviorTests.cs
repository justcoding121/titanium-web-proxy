using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;

namespace Titanium.Cli.Tests;

/// <summary>
///     Remaining <see cref="RunCommand.ExecuteCoreAsync" /> operator paths: Windows named-event reload while the
///     process is serving, and a TITANIUM_ZONE / <c>--zone</c> override that must win over <c>server.localZone</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RunCommandWindowsReloadAndZoneBehaviorTests
{
    // Bug class: `titanium reload -c` finds no waiter (handler never registered), or the signal is swallowed so the
    // running process never reloads routes while the operator is told success.
    [TestMethod]
    [Timeout(90_000)]
    public async Task WindowsReloadSignal_WhileServing_ReloadsAndKeepsTheListener()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named EventWaitHandle reload is Windows-only.");
        }

        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-win-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var port = FreePort();
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, ListenerYaml(port, localZone: "before"));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        using var cts = new CancellationTokenSource();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var run = RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, cts.Token);

            await WaitForAsync(
                () => stdout.ToString().Contains("windows-reload-handler-registered", StringComparison.Ordinal)
                      || stdout.ToString().Contains("awaiting-shutdown-or-reload", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30),
                () => stdout + Environment.NewLine + stderr);
            Assert.IsTrue(await CanConnectAsync(port), "listener must be up before reload");

            await File.WriteAllTextAsync(path, ListenerYaml(port, localZone: "after-reload"));
            Assert.AreEqual(0, await ReloadCommand.ExecuteAsync(["reload", "-c", path]), stderr.ToString());

            await WaitForAsync(
                () => stdout.ToString().Contains("Config reloaded.", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30),
                () => stdout + Environment.NewLine + stderr);
            Assert.IsTrue(await CanConnectAsync(port), "the listener must keep serving across a live reload");

            await cts.CancelAsync();
            Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(45)), stderr.ToString());
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                await cts.CancelAsync();
            }

            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            Directory.SetCurrentDirectory(previousCwd);
            ConfigReloadGate.TryDeletePidFile(path);
            TryDelete(dir);
        }
    }

    // Bug class: an unknown top-level key is ignored with no stderr warning, so a typo in the YAML looks like a
    // successful start with the wrong knobs.
    [TestMethod]
    [Timeout(60_000)]
    public async Task UnknownConfigKey_IsWarnedOnStderr_AndTheProxyStillServes()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-unknown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var port = FreePort();
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            notARealSection: true
            listeners:
              - host: "127.0.0.1"
                port: {port}
                decryptSsl: false
            server:
              drainTimeoutSeconds: 0
            """);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        using var cts = new CancellationTokenSource();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var run = RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, cts.Token);
            await WaitForAsync(
                () => stdout.ToString().Contains("awaiting-shutdown-or-reload", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30),
                () => stdout + Environment.NewLine + stderr);
            await AsyncConsole.FlushAsync();
            StringAssert.Contains(stderr.ToString(), "warning:");
            StringAssert.Contains(stderr.ToString(), "notARealSection");
            Assert.IsTrue(await CanConnectAsync(port));

            await cts.CancelAsync();
            Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                await cts.CancelAsync();
            }

            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            Directory.SetCurrentDirectory(previousCwd);
            TryDelete(dir);
        }
    }

    // Bug class: zoneOverride is ignored and the cluster local zone stays on server.localZone / TITANIUM_ZONE, so a
    // `titanium run --zone` launch routes to the wrong locality.
    [TestMethod]
    [Timeout(60_000)]
    public async Task ZoneOverride_IsAcceptedAndTheProcessStarts()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var previousZone = Environment.GetEnvironmentVariable("TITANIUM_ZONE");
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-zone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var port = FreePort();
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, ListenerYaml(port, localZone: "from-file"));

        var stdout = new StringWriter();
        var previousOut = Console.Out;
        using var cts = new CancellationTokenSource();
        try
        {
            Environment.SetEnvironmentVariable("TITANIUM_ZONE", "from-env");
            Console.SetOut(stdout);
            var run = RunCommand.ExecuteCoreAsync(
                path, verbose: true, serviceMode: false, cts.Token, watch: false, zoneOverride: "from-cli");
            await WaitForAsync(
                () => stdout.ToString().Contains("awaiting-shutdown-or-reload", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30),
                () => stdout.ToString());
            Assert.IsTrue(await CanConnectAsync(port));
            Assert.AreEqual("from-cli", RunCommand.ResolveLocalZone(new Titanium.Web.Proxy.Configuration.Models.ServerConfig
            {
                LocalZone = "from-file",
            }, "from-cli"));

            await cts.CancelAsync();
            Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                await cts.CancelAsync();
            }

            Console.SetOut(previousOut);
            Environment.SetEnvironmentVariable("TITANIUM_ZONE", previousZone);
            Directory.SetCurrentDirectory(previousCwd);
            TryDelete(dir);
        }
    }

    private static string ListenerYaml(int port, string localZone) => $"""
        schemaVersion: "7.1"
        listeners:
          - host: "127.0.0.1"
            port: {port}
            decryptSsl: false
        server:
          drainTimeoutSeconds: 0
          localZone: "{localZone}"
        """;

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(2));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitForAsync(Func<bool> ready, TimeSpan timeout, Func<string> detail)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await AsyncConsole.FlushAsync();
            if (ready())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail(detail());
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // The run may still hold a handle briefly.
        }
    }
}
