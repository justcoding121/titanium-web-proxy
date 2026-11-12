using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;

namespace Titanium.Cli.Tests;

/// <summary>
///     Cli cushion above 85% Sonar: ValidateScriptWiring refusals through ExecuteCore and an access-log file that
///     must open when a request is proxied.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RunCommandScriptWiringAndAccessLogCushionBehaviorTests
{
    // Bug class: a passthrough listener naming a missing stream filter starts anyway, so the first CONNECT fails at
    // runtime instead of the start being refused with the plugin name.
    [TestMethod]
    [Timeout(60_000)]
    public async Task StreamFilterNamingUnknownPlugin_ExitsOneBeforeBind()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-stream-ghost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var port = FreePort();
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {port}
                passthrough: true
                streamFilters: ["ghost-filter"]
                decryptSsl: false
            server:
              drainTimeoutSeconds: 0
            """);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var code = await RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(40));
            await AsyncConsole.FlushAsync();

            Assert.AreEqual(1, code, stdout.ToString() + stderr);
            StringAssert.Contains(stderr.ToString(), "ghost-filter");
            Assert.IsFalse(await CanConnectAsync(port));
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            Directory.SetCurrentDirectory(previousCwd);
            TryDelete(dir);
        }
    }

    // Bug class: accessLog.path is ignored unless a session-path feature is also set, so operators get an empty file.
    [TestMethod]
    [Timeout(90_000)]
    public async Task AccessLogAlone_ThroughExecuteCore_WritesALineForProxiedGet()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var proxyPort = FreePort();
        var originPort = FreePort();
        var accessPath = Path.Combine(dir, "access.ndjson");
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {proxyPort}
                decryptSsl: false
            clusters:
              - id: origin
                destinations:
                  - id: d1
                    address: "127.0.0.1"
                    port: {originPort}
            routes:
              - id: all
                match:
                  path: /
                  pathKind: Prefix
                clusterId: origin
            server:
              drainTimeoutSeconds: 0
              accessLog:
                path: "{accessPath.Replace('\\', '/')}"
            """);

        using var origin = new HttpListener();
        origin.Prefixes.Add($"http://127.0.0.1:{originPort}/");
        origin.Start();
        var originLoop = Task.Run(async () =>
        {
            while (origin.IsListening)
            {
                try
                {
                    var ctx = await origin.GetContextAsync();
                    ctx.Response.StatusCode = 204;
                    ctx.Response.Close();
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        });

        var stdout = new StringWriter();
        var previousOut = Console.Out;
        using var cts = new CancellationTokenSource();
        try
        {
            Console.SetOut(stdout);
            var run = RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, cts.Token);
            await WaitForAsync(
                () => stdout.ToString().Contains("awaiting-shutdown-or-reload", StringComparison.Ordinal),
                TimeSpan.FromSeconds(35),
                () => stdout.ToString());

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var resp = await http.GetAsync($"http://127.0.0.1:{proxyPort}/ping");
            Assert.AreEqual(HttpStatusCode.NoContent, resp.StatusCode);

            await WaitForAsync(
                () => File.Exists(accessPath) && new FileInfo(accessPath).Length > 0,
                TimeSpan.FromSeconds(15),
                () => File.Exists(accessPath) ? "empty" : "(missing)");

            await cts.CancelAsync();
            Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(45)));

            // Read after the run releases the access-log handle.
            var line = await File.ReadAllTextAsync(accessPath);
            StringAssert.Contains(line, "ping");
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                await cts.CancelAsync();
            }

            Console.SetOut(previousOut);
            try
            {
                origin.Stop();
            }
            catch
            {
                // Already stopped.
            }

            try
            {
                await originLoop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Best-effort.
            }

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
            // Best-effort.
        }
    }
}
