using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;

namespace Titanium.Cli.Tests;

/// <summary>
///     <see cref="RunCommand.ExecuteCoreAsync" /> start-up refusals and banners that an operator relies on: a run
///     that cannot bind or cannot wire its config must exit non-zero with a message that names the cause, never hang
///     and never leave a half-started listener behind.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RunCommandStartupFailureBehaviorTests
{
    // Bug class: a listener configured with port 0 (OS-assigned) is accepted, so the proxy binds a random port that
    // nothing is told about; the start must be refused before any socket is opened.
    [TestMethod]
    [Timeout(60_000)]
    public async Task PortZeroListener_IsRefusedAsOutOfRange()
    {
        using var scope = new RunScope("twp-run-port0-");
        var path = scope.WriteConfig("""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: 0
                decryptSsl: false
            """);

        var code = await RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(40));
        await AsyncConsole.FlushAsync();

        Assert.AreEqual(1, code, scope.Output);
        StringAssert.Contains(scope.Errors, "out of range");
    }

    // Bug class: an xds section on an edge without a loaded Plus module is accepted and silently ignored, so the
    // operator believes the control plane drives routing while the file routes do.
    [TestMethod]
    [Timeout(60_000)]
    public async Task XdsConfiguredWithoutPlusModule_ExitsOne()
    {
        using var scope = new RunScope("twp-run-xds-");
        var path = scope.WriteConfig($"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {FreePort()}
                decryptSsl: false
            plus:
              enabled: true
            xds:
              server: "127.0.0.1:{FreePort()}"
            """);

        var code = await RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(40));
        await AsyncConsole.FlushAsync();

        Assert.AreEqual(1, code, scope.Output);
        Assert.IsTrue(
            scope.Errors.Contains("xds", StringComparison.OrdinalIgnoreCase),
            "the refusal must name xds: " + scope.Errors);
    }

    // Bug class: a script pipeline that names a plugin nobody loaded starts the proxy anyway, so every request
    // later fails at runtime instead of the start being refused.
    [TestMethod]
    [Timeout(60_000)]
    public async Task ScriptPipelineNamingUnknownPlugin_ExitsOneAndNeverBinds()
    {
        var port = FreePort();
        using var scope = new RunScope("twp-run-ghost-pipeline-");
        var path = scope.WriteConfig($"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {port}
                decryptSsl: false
            scriptPipeline: ["ghost"]
            """);

        var code = await RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(40));
        await AsyncConsole.FlushAsync();

        Assert.AreEqual(1, code, scope.Output);
        StringAssert.Contains(scope.Errors, "ghost");
        Assert.IsFalse(await CanConnectAsync(port), "a refused start must not leave the listener bound");
    }

    // Bug class: a configured GeoIP database that is missing is skipped silently at start, so geo routing runs
    // with no data and every request is treated as "unknown country".
    [TestMethod]
    [Timeout(60_000)]
    public async Task PlusGeoIpDatabaseMissing_ExitsOneNamingTheFile()
    {
        using var scope = new RunScope("twp-run-geo-missing-");
        var missing = Path.Combine(scope.Dir, "absent.mmdb");
        var path = scope.WriteConfig($"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {FreePort()}
                decryptSsl: false
            plus:
              enabled: true
            server:
              geoIpDatabasePath: "{missing.Replace('\\', '/')}"
            """);

        var code = await RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(40));
        await AsyncConsole.FlushAsync();

        Assert.AreEqual(1, code, scope.Output);
        StringAssert.Contains(scope.Errors, "absent.mmdb");
    }

    // Bug class: telemetry that is switched on is not visible at start-up, and a collector that is down delays or
    // fails the shutdown of the proxy that is merely exporting to it.
    [TestMethod]
    [Timeout(90_000)]
    public async Task TelemetryEnabled_AnnouncesTheCollector_AndStopsCleanlyWithTheCollectorDown()
    {
        var collectorPort = FreePort();
        using var scope = new RunScope("twp-run-telemetry-");
        var path = scope.WriteConfig($"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {FreePort()}
                decryptSsl: false
            server:
              drainTimeoutSeconds: 0
              telemetry:
                otlp:
                  endpoint: "http://127.0.0.1:{collectorPort}"
                  protocol: http
                  timeoutMs: 500
                traces:
                  enabled: true
            """);

        using var cts = new CancellationTokenSource();
        var run = RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, cts.Token);
        try
        {
            await scope.WaitForOutputAsync("Telemetry: traces", TimeSpan.FromSeconds(30));
        }
        finally
        {
            await cts.CancelAsync();
        }

        Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(60)));
        StringAssert.Contains(scope.Output, collectorPort.ToString());
        StringAssert.Contains(scope.Output, "OTLP/http");
    }

    // Bug class: a flag helper that silently returns a default turns "titanium run" without -c into a run
    // against some other file, or drops a mode switch the operator typed.
    [TestMethod]
    public void ArgumentParsers_RejectMissingConfig_AndHonourEachSwitch()
    {
        var missing = Assert.ThrowsExactly<ArgumentException>(() => RunCommand.ParseConfigPath(["run"]));
        StringAssert.Contains(missing.Message, "-c");
        Assert.ThrowsExactly<ArgumentException>(() => RunCommand.ParseConfigPath(["run", "-c"]));

        Assert.AreEqual("a.yaml", RunCommand.ParseConfigPath(["run", "--config", "a.yaml"]));
        Assert.IsTrue(RunCommand.ParseVerbose(["run", "-c", "a", "--verbose"]));
        Assert.IsFalse(RunCommand.ParseVerbose(["run", "-c", "a"]));
        Assert.IsTrue(RunCommand.ParseServiceMode(["run", "-c", "a", "--service"]));
        Assert.IsFalse(RunCommand.ParseServiceMode(["run", "-c", "a"]));
        Assert.IsTrue(RunCommand.ParseWatch(["run", "-c", "a", "--watch"]));
        Assert.IsFalse(RunCommand.ParseWatch(["run", "-c", "a"]));
        Assert.AreEqual("svc", RunCommand.ParseServiceName(["run", "--name", "svc"]));
        Assert.IsNull(RunCommand.ParseServiceName(["run", "--name"]), "a dangling --name has no value to use");
        Assert.IsNull(RunCommand.ParseServiceName(["run", "-c", "a"]));
    }

    // Bug class: a feature gate that is missing from the session-path decision leaves request-body or middleware
    // features running on the lite path that never builds a session, so the feature silently does nothing.
    [TestMethod]
    public void SessionPath_IsRequiredByRequestDecompressionAndRouteMiddleware()
    {
        Assert.IsFalse(RunCommand.ConfigNeedsSessionPath(new Titanium.Web.Proxy.Configuration.Models.TwpConfig()));

        var decompression = new Titanium.Web.Proxy.Configuration.Models.TwpConfig
        {
            Server = new Titanium.Web.Proxy.Configuration.Models.ServerConfig
            {
                RequestDecompression = new Titanium.Web.Proxy.Abstractions.Routing.RequestDecompressionConfig { Enabled = true },
            },
        };
        Assert.IsTrue(RunCommand.ConfigNeedsSessionPath(decompression));

        var middleware = new Titanium.Web.Proxy.Configuration.Models.TwpConfig
        {
            Routes =
            [
                new Titanium.Web.Proxy.Abstractions.Routing.RouteConfig
                {
                    Id = "r",
                    ClusterId = "c",
                    Match = new Titanium.Web.Proxy.Abstractions.Routing.RouteMatch { Path = "/" },
                    Middleware = ["audit"],
                },
            ],
        };
        Assert.IsTrue(RunCommand.ConfigNeedsSessionPath(middleware));
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
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

    /// <summary>Temp directory, redirected console, and the working directory restored afterwards.</summary>
    private sealed class RunScope : IDisposable
    {
        private readonly string _previousCwd = System.IO.Directory.GetCurrentDirectory();
        private readonly TextWriter _previousOut = Console.Out;
        private readonly TextWriter _previousErr = Console.Error;
        private readonly StringWriter _stdout = new();
        private readonly StringWriter _stderr = new();

        public RunScope(string prefix)
        {
            Dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Dir);
            Console.SetOut(_stdout);
            Console.SetError(_stderr);
        }

        public string Dir { get; }

        public string Output => _stdout + _stderr.ToString();

        public string Errors => _stderr.ToString();

        public string WriteConfig(string yaml)
        {
            var path = Path.Combine(Dir, "twp.yaml");
            File.WriteAllText(path, yaml);
            return path;
        }

        public async Task WaitForOutputAsync(string needle, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                await AsyncConsole.FlushAsync();
                if (Output.Contains(needle, StringComparison.Ordinal))
                {
                    return;
                }

                await Task.Delay(50);
            }

            Assert.Fail($"'{needle}' never appeared. Output: {Output}");
        }

        public void Dispose()
        {
            Console.SetOut(_previousOut);
            Console.SetError(_previousErr);
            System.IO.Directory.SetCurrentDirectory(_previousCwd);
            try
            {
                System.IO.Directory.Delete(Dir, recursive: true);
            }
            catch
            {
                // The run may still hold a handle briefly.
            }
        }
    }
}
