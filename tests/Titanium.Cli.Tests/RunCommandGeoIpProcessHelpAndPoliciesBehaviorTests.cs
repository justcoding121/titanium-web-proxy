using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;

namespace Titanium.Cli.Tests;

/// <summary>
///     ExecuteCore edges still thin on develop: GeoIP without Plus, routes/policies/compression through a live run,
///     admin pre-stop drain delay, and process-level help for sibling commands.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RunCommandGeoIpProcessHelpAndPoliciesBehaviorTests
{
    // Bug class: server.geoIpDatabasePath is accepted without plus.enabled, so operators think GeoIP is live while
    // every request is treated as unknown country.
    [TestMethod]
    [Timeout(60_000)]
    public async Task GeoIpDatabaseWithoutPlus_ExitsOneNamingPlus()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-geo-noplus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {FreePort()}
                decryptSsl: false
            server:
              geoIpDatabasePath: "{Path.Combine(dir, "geo.mmdb").Replace('\\', '/')}"
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
            StringAssert.Contains(stderr.ToString(), "plus.enabled");
            StringAssert.Contains(stderr.ToString(), "geoIpDatabasePath");
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            Directory.SetCurrentDirectory(previousCwd);
            TryDelete(dir);
        }
    }

    // Bug class: routes/policies/responseCompression never reach BuildReverseProxyOptions / ServerConfigApplier in a
    // live ExecuteCore, so operators only discover missing wiring under traffic.
    [TestMethod]
    [Timeout(90_000)]
    public async Task RoutesPoliciesAndCompression_ThroughExecuteCore_ProxyToBackend()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-policies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var proxyPort = FreePort();
        var backendPort = FreePort();
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {proxyPort}
                decryptSsl: false
            clusters:
              - id: backend
                destinations:
                  - id: d1
                    address: "127.0.0.1"
                    port: {backendPort}
            routes:
              - id: api
                match:
                  path: /api
                  pathKind: Prefix
                clusterId: backend
                policies: [req-size]
            policies:
              - name: req-size
                kind: maxRequestBodySize
                maxRequestBodyBytes: 1048576
            server:
              drainTimeoutSeconds: 0
              responseCompression:
                enabled: true
                algorithms: ["br"]
            """);

        using var backend = new HttpListener();
        backend.Prefixes.Add($"http://127.0.0.1:{backendPort}/");
        backend.Start();
        var backendLoop = Task.Run(async () =>
        {
            while (backend.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await backend.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                ctx.Response.StatusCode = 200;
                var bytes = Encoding.UTF8.GetBytes("ok-policies");
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
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
            var body = await http.GetStringAsync($"http://127.0.0.1:{proxyPort}/api");
            Assert.AreEqual("ok-policies", body);

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
            try
            {
                backend.Stop();
            }
            catch
            {
                // Already stopped.
            }

            try
            {
                await backendLoop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Best-effort.
            }

            Directory.SetCurrentDirectory(previousCwd);
            TryDelete(dir);
        }
    }

    // Bug class: admin.preStopDelaySeconds is ignored on cancel, so load balancers keep sending traffic into a process
    // that already stopped accepting.
    [TestMethod]
    [Timeout(90_000)]
    public async Task AdminPreStopDelay_KeepsRunAliveBrieflyThenExitsZero()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-prestop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var proxyPort = FreePort();
        var adminPort = FreePort();
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
                preStopDelaySeconds: 1
            """);

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

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{adminPort}/healthz")).StatusCode);

            var sw = Stopwatch.StartNew();
            await cts.CancelAsync();
            Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(45)));
            Assert.IsTrue(sw.Elapsed >= TimeSpan.FromMilliseconds(700), $"pre-stop delay too short: {sw.Elapsed}");
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

    // Bug class: process-level help for sibling commands falls through into ExecuteCore / exits non-zero.
    [TestMethod]
    [Timeout(90_000)]
    public async Task Process_SiblingCommandHelp_ExitsZero()
    {
        foreach (var (args, needle) in new[]
                 {
                     (new[] { "version", "--help" }, "titanium version"),
                     (new[] { "service", "--help" }, "titanium service"),
                     (new[] { "update", "--help" }, "titanium update"),
                     (new[] { "plus", "--help" }, "titanium plus"),
                     (new[] { "http3-deps", "--help" }, "titanium http3-deps"),
                     (new[] { "scripting-deps", "--help" }, "titanium scripting-deps"),
                 })
        {
            var (code, stdout, stderr) = await RunCliAsync(args, TimeSpan.FromSeconds(30));
            Assert.AreEqual(0, code, $"{string.Join(' ', args)} stderr={stderr}");
            StringAssert.Contains(stdout, needle);
        }
    }

    // Bug class: a real process with metrics never scrapes cleanly (only in-process ExecuteCore tests do).
    [TestMethod]
    [Timeout(120_000)]
    public async Task Process_RunWithMetrics_ScrapesThenStops()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-metrics-e2e-" + Guid.NewGuid().ToString("N"));
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

        Process? proc = null;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        try
        {
            proc = StartCli(["run", "-c", path], stdout, stderr);
            await WaitForAsync(
                () => stdout.ToString().Contains("awaiting-shutdown-or-reload", StringComparison.Ordinal)
                      || stdout.ToString().Contains("Metrics:", StringComparison.Ordinal),
                TimeSpan.FromSeconds(45),
                () => stdout + Environment.NewLine + stderr);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var body = await http.GetStringAsync($"http://127.0.0.1:{metricsPort}/metrics");
            StringAssert.Contains(body, "twp_");
        }
        finally
        {
            await StopProcessAsync(proc);
            ConfigReloadGate.TryDeletePidFile(path);
            TryDelete(dir);
        }
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

        Assert.Fail("titanium.dll not found; build Titanium.Cli first.");
        return "";
    }

    private static ProcessStartInfo CreatePsi(string[] args)
    {
        var dll = FindTitaniumDll();
        var psi = new ProcessStartInfo
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

    private static Process StartCli(string[] args, StringBuilder stdout, StringBuilder stderr)
    {
        var proc = Process.Start(CreatePsi(args))
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
        using var proc = Process.Start(CreatePsi(args))
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

            Assert.Fail($"CLI timed out: dotnet titanium.dll {string.Join(' ', args)}");
        }

        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task StopProcessAsync(Process? proc)
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
