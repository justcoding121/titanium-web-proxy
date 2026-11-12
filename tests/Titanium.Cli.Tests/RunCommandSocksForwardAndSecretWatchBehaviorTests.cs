using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;

namespace Titanium.Cli.Tests;

/// <summary>
///     High-line <see cref="RunCommand.ExecuteCoreAsync" /> listener and watch paths that still show thin on develop:
///     SOCKS and forwardHost listeners through the full run loop, and a secret-file content watch that must reload
///     without rewriting the YAML.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RunCommandSocksForwardAndSecretWatchBehaviorTests
{
    // Bug class: type: socks never reaches CreateSocksEndPoint / AddListener in a live run (only unit-invoked), so a
    // SOCKS listen stanza starts as an explicit HTTP proxy and clients hang on the greeting.
    [TestMethod]
    [Timeout(60_000)]
    public async Task SocksListener_ThroughExecuteCore_BindsAndAnnouncesSocks()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-socks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var port = FreePort();
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {port}
                type: socks
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
                () => stdout.ToString().Contains("socks decryptSsl=False", StringComparison.Ordinal)
                      || stdout.ToString().Contains("awaiting-shutdown-or-reload", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30),
                () => stdout + Environment.NewLine + stderr);
            StringAssert.Contains(stdout.ToString(), "socks");
            Assert.IsTrue(await CanConnectAsync(port), "SOCKS listener must accept TCP");

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
            TryDelete(dir);
        }
    }

    // Bug class: forwardHost listeners are started as explicit endpoints (or never log the mode), so operators cannot
    // tell TLS-terminate vs plain transparent from the banner and SNI never reaches the origin.
    [TestMethod]
    [Timeout(60_000)]
    public async Task ForwardHostListener_ThroughExecuteCore_AnnouncesTransparentMode()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-fwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var port = FreePort();
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {port}
                decryptSsl: false
                forwardHost: "127.0.0.1"
                forwardPort: 9
            server:
              drainTimeoutSeconds: 0
              enableHttp2: false
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
            var run = RunCommand.ExecuteCoreAsync(path, verbose: true, serviceMode: false, cts.Token);

            await WaitForAsync(
                () => stdout.ToString().Contains("ForwardHost=", StringComparison.Ordinal)
                      || stdout.ToString().Contains("awaiting-shutdown-or-reload", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30),
                () => stdout + Environment.NewLine + stderr);
            StringAssert.Contains(stdout.ToString(), "transparent");
            StringAssert.Contains(stdout.ToString(), "ForwardHost=127.0.0.1:9");
            Assert.IsTrue(await CanConnectAsync(port));

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
            TryDelete(dir);
        }
    }

    // Bug class: secret files are listed for ContentWatcher but a rotation of admin.authTokenFile never fires reload,
    // so /admin keeps the old token until the YAML itself is touched.
    [TestMethod]
    [Timeout(90_000)]
    public async Task Watch_AuthTokenFileChange_ReloadsWithoutTouchingYaml()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-secret-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var port = FreePort();
        var adminPort = FreePort();
        var tokenPath = Path.Combine(dir, "admin.token");
        await File.WriteAllTextAsync(tokenPath, "token-before");
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {port}
                decryptSsl: false
            server:
              drainTimeoutSeconds: 0
              admin:
                enabled: true
                host: "127.0.0.1"
                port: {adminPort}
                authTokenFile: "admin.token"
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
            var run = RunCommand.ExecuteCoreAsync(
                path, verbose: false, serviceMode: false, cts.Token, watch: true);

            await WaitForAsync(
                () => stdout.ToString().Contains("config-watch-enabled", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30),
                () => stdout + Environment.NewLine + stderr);
            Assert.IsTrue(await CanConnectAsync(port), "listener must be up before the secret rotation");

            // ContentWatcher baselines on start; a second write after a short pause must differ from that baseline.
            await Task.Delay(400);
            await File.WriteAllTextAsync(tokenPath, "token-after-" + Guid.NewGuid().ToString("N"));

            await WaitForAsync(
                () => stdout.ToString().Contains("Config reloaded.", StringComparison.Ordinal),
                TimeSpan.FromSeconds(40),
                () => stdout + Environment.NewLine + stderr);
            Assert.IsTrue(await CanConnectAsync(port), "secret reload must keep the listener");

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
            TryDelete(dir);
        }
    }

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
