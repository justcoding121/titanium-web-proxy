using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests;

/// <summary>
///     In-process <see cref="RunCommand.ExecuteCoreAsync" /> behaviors that Sonar still marks thin on develop:
///     watch reload keeping the previous config, service-mode logging, drain timeout, and early validation exits.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RunCommandExecuteCoreBehaviorTests
{
    [TestMethod]
    [Timeout(90_000)]
    public async Task Watch_InvalidRewrite_LeavesPreviousListenerServing()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var port = GetFreePort();
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, ValidListenerYaml(port));

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

            await WaitForPredicateAsync(
                () => stdout.ToString().Contains("config-watch-enabled", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20),
                () => stdout.ToString() + stderr);
            Assert.IsTrue(await CanConnectAsync(port), "listener must be up before the bad rewrite");

            await File.WriteAllTextAsync(path, "listeners: [this is not valid yaml");

            await WaitForPredicateAsync(
                () => stderr.ToString().Contains("Config reload failed", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30),
                () => stdout.ToString() + stderr);
            Assert.IsFalse(
                stdout.ToString().Contains("Config reloaded.", StringComparison.Ordinal),
                "an invalid rewrite must not report a successful reload");
            Assert.IsTrue(await CanConnectAsync(port), "the previous listener must keep serving after a failed reload");

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

    [TestMethod]
    [Timeout(60_000)]
    public async Task ServiceMode_LogsServiceBanner_AppliesDefaults_AndDrainsWithZeroTimeout()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-svc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var port = GetFreePort();
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {port}
                decryptSsl: false
            server:
              drainTimeoutSeconds: 0
            """);

        using var proxyForDefaults = new ProxyServer(userTrustRootCertificate: false);
        proxyForDefaults.Logging.Enabled = false;
        proxyForDefaults.Logging.EnableConsole = false;
        proxyForDefaults.Logging.MinimumLevel = Microsoft.Extensions.Logging.LogLevel.Warning;
        RunCommand.ApplyServiceLoggingDefaults(proxyForDefaults, logging: null);
        if (OperatingSystem.IsWindows())
        {
            Assert.IsTrue(proxyForDefaults.Logging.Enabled);
            Assert.IsTrue(proxyForDefaults.Logging.EnableFile);
            StringAssert.Contains(proxyForDefaults.Logging.FilePath, Path.Combine("Titanium", "logs"));
            Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Information, proxyForDefaults.Logging.MinimumLevel);
        }
        else
        {
            Assert.IsTrue(proxyForDefaults.Logging.Enabled);
            Assert.IsTrue(proxyForDefaults.Logging.EnableConsole);
            Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Information, proxyForDefaults.Logging.MinimumLevel);
        }

        var stdout = new StringWriter();
        var previousOut = Console.Out;
        using var cts = new CancellationTokenSource();
        try
        {
            Console.SetOut(stdout);
            var run = RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: true, cts.Token);
            await WaitForPredicateAsync(
                () => stdout.ToString().Contains("Titanium proxy running (service mode).", StringComparison.Ordinal)
                      || stdout.ToString().Contains("awaiting-shutdown-or-reload", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20),
                () => stdout.ToString());
            Assert.IsTrue(await CanConnectAsync(port));

            await cts.CancelAsync();
            Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(30)));
            StringAssert.Contains(stdout.ToString(), "Titanium proxy running (service mode).");
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                await cts.CancelAsync();
            }

            Console.SetOut(previousOut);
            Directory.SetCurrentDirectory(previousCwd);
            TryDelete(dir);
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task AdminEnabled_ServesHealthz_AndHonorsDrainTimeoutOnCancel()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-admin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var proxyPort = GetFreePort();
        var adminPort = GetFreePort();
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {proxyPort}
                decryptSsl: false
            server:
              drainTimeoutSeconds: 0
              admin:
                enabled: true
                host: "127.0.0.1"
                port: {adminPort}
                preStopDelaySeconds: 0
            """);

        using var cts = new CancellationTokenSource();
        try
        {
            var run = RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: true, cts.Token);
            await WaitForAsyncPredicateAsync(
                async () => (await GetStatusAsync(adminPort, "/healthz")) == 200,
                TimeSpan.FromSeconds(20),
                () => "admin /healthz never became ready");
            Assert.IsTrue(await CanConnectAsync(proxyPort));

            await cts.CancelAsync();
            Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                await cts.CancelAsync();
            }

            Directory.SetCurrentDirectory(previousCwd);
            TryDelete(dir);
        }
    }

    [TestMethod]
    public async Task GeoIpWithoutPlus_ReturnsOne()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-geo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {GetFreePort()}
                decryptSsl: false
            server:
              geoIpDatabasePath: "{Path.Combine(dir, "missing.mmdb").Replace('\\', '/')}"
            """);
        try
        {
            var code = await RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, CancellationToken.None);
            Assert.AreEqual(1, code);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            TryDelete(dir);
        }
    }

    [TestMethod]
    public async Task MissingAdminAuthTokenFile_ReturnsOne()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-secret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {GetFreePort()}
                decryptSsl: false
            server:
              admin:
                enabled: true
                host: "127.0.0.1"
                port: {GetFreePort()}
                authTokenFile: "{Path.Combine(dir, "missing.token").Replace('\\', '/')}"
            """);
        try
        {
            var code = await RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, CancellationToken.None);
            Assert.AreEqual(1, code);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            TryDelete(dir);
        }
    }

    [TestMethod]
    public void ApplyServiceLoggingDefaults_LeavesExistingFileSinkAlone()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var existing = Path.Combine(Path.GetTempPath(), "twp-existing-" + Guid.NewGuid().ToString("N") + ".log");
        RunCommand.ApplyServiceLoggingDefaults(proxy, new LoggingConfig
        {
            EnableFile = true,
            FilePath = existing,
        });
        // When YAML already opted into a file sink, the helper returns without mutating the proxy.
        Assert.IsFalse(proxy.Logging.EnableFile);
        Assert.AreNotEqual(existing, proxy.Logging.FilePath ?? string.Empty);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Watch_ValidRewrite_ReloadsAndKeepsServing()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-watch-ok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var port = GetFreePort();
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, ValidListenerYaml(port));

        var stdout = new StringWriter();
        var previousOut = Console.Out;
        using var cts = new CancellationTokenSource();
        try
        {
            Console.SetOut(stdout);
            var run = RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, cts.Token, watch: true);
            await WaitForPredicateAsync(
                () => stdout.ToString().Contains("config-watch-enabled", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20),
                () => stdout.ToString());
            Assert.IsTrue(await CanConnectAsync(port));

            // Keep the same listen port; only bump a server field so reload applies without a rebind race.
            await File.WriteAllTextAsync(path, $"""
                schemaVersion: "7.1"
                listeners:
                  - host: "127.0.0.1"
                    port: {port}
                    decryptSsl: false
                server:
                  drainTimeoutSeconds: 0
                  localZone: "watch-reload-zone"
                """);
            await WaitForPredicateAsync(
                () => stdout.ToString().Contains("Config reloaded.", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30),
                () => stdout.ToString());
            Assert.IsTrue(await CanConnectAsync(port), "the listener must keep serving after a successful reload");

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
            Directory.SetCurrentDirectory(previousCwd);
            TryDelete(dir);
        }
    }

    [TestMethod]
    public void ResolveLocalZone_PrefersOverrideThenServerThenNull()
    {
        var previous = Environment.GetEnvironmentVariable("TITANIUM_ZONE");
        try
        {
            Environment.SetEnvironmentVariable("TITANIUM_ZONE", null);
            Assert.AreEqual("override", RunCommand.ResolveLocalZone(new ServerConfig { LocalZone = "server" }, "override"));
            Assert.AreEqual("server", RunCommand.ResolveLocalZone(new ServerConfig { LocalZone = "server" }, null));
            Assert.IsNull(RunCommand.ResolveLocalZone(null, null));
            Assert.IsNull(RunCommand.ResolveLocalZone(new ServerConfig(), "  "));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TITANIUM_ZONE", previous);
        }
    }

    [TestMethod]
    public void ShouldEnableHttp3_AndSessionPath_FollowConfigGates()
    {
        Assert.IsTrue(RunCommand.ShouldEnableHttp3(new TwpConfig()), "no listener opts out of HTTP/3");
        Assert.IsFalse(RunCommand.ShouldEnableHttp3(new TwpConfig
        {
            Listeners = [new ListenerConfig { Port = 443, EnableHttp3 = false }],
        }));
        Assert.IsFalse(RunCommand.ConfigNeedsSessionPath(new TwpConfig()));
        Assert.IsTrue(RunCommand.ConfigNeedsRequestTimingCapture(new TwpConfig
        {
            Clusters =
            [
                new ClusterConfig
                {
                    Id = "c",
                    Algorithm = LoadBalanceAlgorithm.LeastTime,
                    Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 80 }],
                },
            ],
        }));
    }

    [TestMethod]
    public void ReplaceRoutes_SwapsTheListInPlace()
    {
        var routes = new List<RouteConfig>
        {
            new() { Id = "old", ClusterId = "c", Match = new RouteMatch { Path = "/" } },
        };
        RunCommand.ReplaceRoutes(
            routes,
            [new RouteConfig { Id = "new", ClusterId = "c", Match = new RouteMatch { Path = "/" } }]);
        Assert.AreEqual(1, routes.Count);
        Assert.AreEqual("new", routes[0].Id);
    }

    [TestMethod]
    public void ApplyLogging_VerboseForcesInformationFloor()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        RunCommand.ApplyLogging(proxy, new LoggingConfig { MinimumLevel = "Error" }, verbose: true);
        Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Debug, proxy.Logging.MinimumLevel);
        Assert.IsTrue(proxy.Logging.Enabled);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task MetricsAndAccessLog_StartFromConfig_AndStopCleanly()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-metrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var proxyPort = GetFreePort();
        var metricsPort = GetFreePort();
        var accessLog = Path.Combine(dir, "access.ndjson");
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {proxyPort}
                decryptSsl: false
            server:
              drainTimeoutSeconds: 0
              metrics:
                enabled: true
                host: "127.0.0.1"
                port: {metricsPort}
              accessLog:
                path: "{accessLog.Replace('\\', '/')}"
                sampleRate: 1.0
            """);

        var stdout = new StringWriter();
        var previousOut = Console.Out;
        using var cts = new CancellationTokenSource();
        try
        {
            Console.SetOut(stdout);
            var run = RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, cts.Token);
            await WaitForPredicateAsync(
                () => stdout.ToString().Contains("Metrics:", StringComparison.Ordinal)
                      && stdout.ToString().Contains("Access log:", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20),
                () => stdout.ToString());
            Assert.IsTrue(await CanConnectAsync(proxyPort));
            Assert.AreEqual(200, await GetStatusAsync(metricsPort, "/metrics"));

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
            Directory.SetCurrentDirectory(previousCwd);
            TryDelete(dir);
        }
    }

    [TestMethod]
    public async Task MissingScriptPlugin_ReturnsOne()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {GetFreePort()}
                decryptSsl: false
            plugins:
              - name: ghost
                kind: lua
                file: "{Path.Combine(dir, "missing.lua").Replace('\\', '/')}"
            scriptPipeline: ["ghost"]
            """);
        try
        {
            var code = await RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, CancellationToken.None);
            Assert.AreEqual(1, code);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            TryDelete(dir);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_Help_ReturnsZero()
    {
        Assert.AreEqual(0, await RunCommand.ExecuteAsync(["run", "--help"]));
    }

    private static string ValidListenerYaml(int port) => $"""
        schemaVersion: "7.1"
        listeners:
          - host: "127.0.0.1"
            port: {port}
            decryptSsl: false
        server:
          drainTimeoutSeconds: 0
        """;

    private static int GetFreePort()
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
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(3));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int?> GetStatusAsync(int port, string path)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(2));
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\n\r\n"));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var first = text.Split('\n')[0];
            var parts = first.Split(' ');
            return parts.Length > 1 && int.TryParse(parts[1], out var status) ? status : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task WaitForPredicateAsync(Func<bool> ready, TimeSpan timeout, Func<string> detail)
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

    private static async Task WaitForAsyncPredicateAsync(Func<Task<bool>> ready, TimeSpan timeout, Func<string> detail)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await ready())
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
