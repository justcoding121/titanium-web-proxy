using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;
using Titanium.Cli.Config;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests;

/// <summary>
///     Process-level metrics scrape, ExecuteCore pooling/limits/leaf-cert wiring, metrics route-label caps,
///     and a failed live reload that must keep the previous config.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RunCommandProcessMetricsPoolingAndReloadFailBehaviorTests
{
    // Bug class: a real `titanium run` process never binds /metrics (or scrapes empty), so container probes and
    // Grafana dashboards see connection refused while the proxy is otherwise healthy.
    [TestMethod]
    [Timeout(120_000)]
    public async Task Process_RunWithMetrics_ServesScrapeThenStops()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-proc-metrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var proxyPort = FreePort();
        var metricsPort = FreePort();
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
            """);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        System.Diagnostics.Process? proc = null;
        try
        {
            proc = StartCli(["run", "-c", path, "-v"], stdout, stderr);
            await WaitForAsync(
                () => stdout.ToString().Contains("Metrics:", StringComparison.Ordinal)
                      || stdout.ToString().Contains("awaiting-shutdown-or-reload", StringComparison.Ordinal),
                TimeSpan.FromSeconds(50),
                () => stdout + Environment.NewLine + stderr);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            string? body = null;
            for (var i = 0; i < 40 && body is null; i++)
            {
                try
                {
                    body = await http.GetStringAsync($"http://127.0.0.1:{metricsPort}/metrics");
                }
                catch
                {
                    await Task.Delay(100);
                }
            }

            Assert.IsNotNull(body, "process must serve /metrics\n" + stdout + stderr);
            StringAssert.Contains(body, "twp_");
            Assert.IsTrue(await CanConnectAsync(proxyPort));
        }
        finally
        {
            await StopProcessAsync(proc);
            TryDelete(dir);
        }
    }

    // Bug class: pooling/limits YAML is accepted but never applied on ExecuteCore, so MaxConcurrentClientConnections
    // stays at the profile default under load.
    [TestMethod]
    [Timeout(60_000)]
    public async Task PoolingLimitsAndLeafCert_ThroughExecuteCore_StartCleanly()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-pool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var certPath = Path.Combine(dir, "leaf.pem");
        var keyPath = Path.Combine(dir, "leaf.key");
        WriteSelfSignedPem(certPath, keyPath);
        var proxyPort = FreePort();
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {proxyPort}
                decryptSsl: false
            certificates:
              certificatePath: "{certPath.Replace('\\', '/')}"
              privateKeyPath: "{keyPath.Replace('\\', '/')}"
            server:
              drainTimeoutSeconds: 0
              pooling:
                enableConnectionPool: true
                maxCachedConnections: 32
                maxConcurrentClientConnections: 64
                noDelay: true
              limits:
                maxHeaderCount: 80
                maxConcurrentClients: 40
                maxConcurrentStreamsPerConnection: 50
            """);

        var stdout = new StringWriter();
        var previousOut = Console.Out;
        using var cts = new CancellationTokenSource();
        try
        {
            Console.SetOut(stdout);
            var run = RunCommand.ExecuteCoreAsync(path, verbose: true, serviceMode: false, cts.Token);
            await WaitForAsync(
                () => stdout.ToString().Contains("awaiting-shutdown-or-reload", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30),
                () => stdout.ToString());
            await AsyncConsole.FlushAsync();
            var text = stdout.ToString();
            StringAssert.Contains(text, "Certificate path configured");
            Assert.IsTrue(
                text.Contains("Loaded leaf", StringComparison.Ordinal)
                || text.Contains("Certificate", StringComparison.Ordinal));
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

            Console.SetOut(previousOut);
            Directory.SetCurrentDirectory(previousCwd);
            TryDelete(dir);
        }
    }

    // Bug class: a bad reload YAML is applied anyway (or kills the process) instead of keeping the previous config
    // and reporting failure on stderr.
    [TestMethod]
    [Timeout(90_000)]
    public async Task ReloadInvalidYaml_KeepsServingAndReportsFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named EventWaitHandle reload is Windows-only.");
        }

        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-bad-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var port = FreePort();
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {port}
                decryptSsl: false
            server:
              drainTimeoutSeconds: 0
              localZone: "good"
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

            await File.WriteAllTextAsync(path, "listeners: [this is not valid yaml");
            Assert.AreEqual(0, await ReloadCommand.ExecuteAsync(["reload", "-c", path]));
            await WaitForAsync(
                () => stderr.ToString().Contains("Config reload failed", StringComparison.Ordinal)
                      || stdout.ToString().Contains("Config reload failed", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30),
                () => stdout + Environment.NewLine + stderr);
            Assert.IsFalse(
                stdout.ToString().Contains("Config reloaded.", StringComparison.Ordinal),
                "invalid YAML must not claim success");
            Assert.IsTrue(await CanConnectAsync(port), "previous listeners must keep serving");

            await cts.CancelAsync();
            Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(45)));
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

    // Bug class: after MaxDistinctRoutesAndClusters, new route tags stay as raw strings and series explode.
    [TestMethod]
    public void MetricsListener_RouteLabelCap_FoldsIntoOther()
    {
        var meterName = "twp-cli-route-cap-" + Guid.NewGuid().ToString("N");
        using var meter = new Meter(meterName);
        using var exposition = new MetricsExposition(meterName);
        exposition.Start();

        var counter = meter.CreateCounter<long>("twp.proxy.route_cap_total");
        for (var i = 0; i < MetricsListener.MaxDistinctRoutesAndClusters + 5; i++)
        {
            counter.Add(1, new KeyValuePair<string, object?>("route", "r" + i));
        }

        var text = exposition.Render();
        StringAssert.Contains(text, "route=\"other\"");
        StringAssert.Contains(text, "twp_proxy_route_cap_total");
    }

    // Bug class: Gauge&lt;T&gt; / decimal instruments are accepted (or Counter&lt;decimal&gt; crashes TryCreate).
    [TestMethod]
    public void MetricsListener_GaugeAndUnsupportedValueTypes_AreHandled()
    {
        var meterName = "twp-cli-gauge-" + Guid.NewGuid().ToString("N");
        using var meter = new Meter(meterName);
        using var exposition = new MetricsExposition(meterName);
        exposition.Start();

        var gauge = meter.CreateGauge<double>("twp.cache.ratio");
        gauge.Record(0.42, new KeyValuePair<string, object?>("result", "hit"));
        var text = exposition.Render();
        StringAssert.Contains(text, "twp_cache_ratio");
        StringAssert.Contains(text, "0.42");
    }

    // Bug class: process-level `titanium test --strict` with unknown keys exits 0.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Process_TestStrictUnknownKey_ExitsOne()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-test-strict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            totallyUnknownKey: true
            listeners:
              - host: "127.0.0.1"
                port: {FreePort()}
                decryptSsl: false
            """);
        try
        {
            var (code, _, stderr) = await RunCliAsync(["test", "-c", path, "--strict"], TimeSpan.FromSeconds(45));
            Assert.AreEqual(1, code, stderr);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    private static void WriteSelfSignedPem(string certPath, string keyPath)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=twp-leaf", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, rsa.ExportRSAPrivateKeyPem());
    }

    private static string FindTitaniumDll()
    {
        var testDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        foreach (var candidate in new[]
                 {
                     Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", "..", "src", "Titanium.Cli", "bin", "Release", "net10.0", "titanium.dll")),
                     Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", "..", "src", "Titanium.Cli", "bin", "Debug", "net10.0", "titanium.dll")),
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Fail("titanium.dll not found");
        return "";
    }

    private static System.Diagnostics.ProcessStartInfo CreatePsi(string[] args)
    {
        var dll = FindTitaniumDll();
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
        };
        psi.ArgumentList.Add(dll);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        return psi;
    }

    private static System.Diagnostics.Process StartCli(string[] args, StringBuilder stdout, StringBuilder stderr)
    {
        var proc = System.Diagnostics.Process.Start(CreatePsi(args))
                   ?? throw new InvalidOperationException("Failed to start titanium.dll");
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stdout)
                {
                    stdout.AppendLine(e.Data);
                }
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return proc;
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunCliAsync(string[] args, TimeSpan timeout)
    {
        using var proc = System.Diagnostics.Process.Start(CreatePsi(args))
                         ?? throw new InvalidOperationException("Failed to start titanium.dll");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort.
            }

            Assert.Fail("CLI timed out");
        }

        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task StopProcessAsync(System.Diagnostics.Process? proc)
    {
        if (proc is null)
        {
            return;
        }

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        catch
        {
            // Already gone.
        }
        finally
        {
            proc.Dispose();
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
