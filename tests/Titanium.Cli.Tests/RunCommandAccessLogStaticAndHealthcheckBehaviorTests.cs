using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;
using Titanium.Cli.Certificates;
using Titanium.Cli.Config;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests;

/// <summary>
///     RunCommand access-log AfterResponse path, static-file registration through ExecuteCore, healthcheck help/
///     parse edges, and certificate bootstrap announcements that still show thin on develop.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RunCommandAccessLogStaticAndHealthcheckBehaviorTests
{
    // Bug class: Access log starts but AfterResponse never writes (sampleRate path only), so operators see an empty
    // NDJSON after traffic and blame the sink instead of the run hook.
    [TestMethod]
    [Timeout(90_000)]
    public async Task AccessLog_AfterResponse_WritesNdjsonForProxiedRequest()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var proxyPort = FreePort();
        var backendPort = FreePort();
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
              accessLog:
                path: "{accessLog.Replace('\\', '/')}"
                sampleRate: 1.0
            """);

        using var backend = new HttpListener();
        backend.Prefixes.Add($"http://127.0.0.1:{backendPort}/");
        backend.Start();
        var backendServe = Task.Run(async () =>
        {
            var ctx = await backend.GetContextAsync();
            var bytes = Encoding.ASCII.GetBytes("ok");
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        var stdout = new StringWriter();
        var previousOut = Console.Out;
        using var cts = new CancellationTokenSource();
        try
        {
            Console.SetOut(stdout);
            var run = RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, cts.Token);
            await WaitForAsync(
                () => stdout.ToString().Contains("Access log:", StringComparison.Ordinal)
                      && stdout.ToString().Contains("awaiting-shutdown-or-reload", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30),
                () => stdout.ToString());

            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await http.GetAsync($"http://127.0.0.1:{backendPort}/");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            await backendServe.WaitAsync(TimeSpan.FromSeconds(10));

            await WaitForAsync(
                () => File.Exists(accessLog) && new FileInfo(accessLog).Length > 0,
                TimeSpan.FromSeconds(20),
                () => "access log missing or empty\n" + stdout);

            await cts.CancelAsync();
            Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(30)));

            var line = (await File.ReadAllTextAsync(accessLog)).Trim();
            Assert.IsFalse(string.IsNullOrWhiteSpace(line), line);
            Assert.IsTrue(line.Contains('"') || line.Contains('{') || line.Contains("127.0.0.1"), line);
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

            Directory.SetCurrentDirectory(previousCwd);
            TryDelete(dir);
        }
    }

    // Bug class: sampleRate <= 0 still opens the writer / banners Access log, then never samples.
    [TestMethod]
    [Timeout(60_000)]
    public async Task AccessLog_SampleRateZero_DoesNotStartWriter()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-access0-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var proxyPort = FreePort();
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
              accessLog:
                path: "{accessLog.Replace('\\', '/')}"
                sampleRate: 0
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
                TimeSpan.FromSeconds(25),
                () => stdout.ToString());
            Assert.IsFalse(
                stdout.ToString().Contains("Access log:", StringComparison.Ordinal),
                stdout.ToString());
            Assert.IsFalse(File.Exists(accessLog));

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

    // Bug class: staticFiles through ExecuteCore never registers (ConfigNeedsSessionPath false) so the root 404s.
    [TestMethod]
    [Timeout(60_000)]
    public async Task StaticFiles_ThroughExecuteCore_ServesIndex()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-static-" + Guid.NewGuid().ToString("N"));
        var www = Path.Combine(dir, "www");
        Directory.CreateDirectory(www);
        await File.WriteAllTextAsync(Path.Combine(www, "index.html"), "<html>static-ok</html>");
        var proxyPort = FreePort();
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {proxyPort}
                decryptSsl: false
            staticFiles:
              root: "{www.Replace('\\', '/')}"
            server:
              drainTimeoutSeconds: 0
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
                TimeSpan.FromSeconds(25),
                () => stdout.ToString());

            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var body = await http.GetStringAsync("http://static.test/");
            StringAssert.Contains(body, "static-ok");

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

    // Bug class: healthcheck --help falls through and exits 1; bad --timeout / non-IP admin host give opaque errors.
    [TestMethod]
    public async Task Healthcheck_HelpTimeoutAndNonIpHost_AreActionable()
    {
        Assert.AreEqual(0, await HealthcheckCommand.ExecuteAsync(["healthcheck", "--help"]));
        Assert.AreEqual(0, await HealthcheckCommand.ExecuteAsync(["healthcheck", "-h"]));

        Assert.AreEqual(1, await HealthcheckCommand.ExecuteAsync(
            ["healthcheck", "--url", "http://127.0.0.1:9/healthz", "--timeout", "0"]));
        Assert.AreEqual(1, await HealthcheckCommand.ExecuteAsync(
            ["healthcheck", "--url", "http://127.0.0.1:9/healthz", "--timeout", "301"]));

        var badHost = new TwpConfig
        {
            Server = new ServerConfig
            {
                Admin = new AdminConfig { Enabled = true, Host = "localhost", Port = 9901 },
            },
        };
        var ex = Assert.ThrowsExactly<ArgumentException>(() => HealthcheckCommand.ResolveUrl(badHost, out _));
        StringAssert.Contains(ex.Message, "not an IP address");
    }

    // Bug class: YAML already has a file sink, but service mode still rewrites CommonApplicationData and drops it.
    [TestMethod]
    public void ApplyServiceLoggingDefaults_KeepsExplicitFileSink()
    {
        using var proxy = new ProxyServer();
        proxy.Logging.Enabled = false;
        var logging = new LoggingConfig
        {
            EnableFile = true,
            FilePath = Path.Combine(Path.GetTempPath(), "twp-keep-log-" + Guid.NewGuid().ToString("N") + ".log"),
            MinimumLevel = "Warning",
        };
        RunCommand.ApplyServiceLoggingDefaults(proxy, logging);
        Assert.IsFalse(proxy.Logging.EnableFile, "explicit YAML file sink means defaults must no-op");
    }

    // Bug class: CertificatePath present but unloadable still starts decrypt endpoints with a blank leaf.
    [TestMethod]
    public async Task CertificateBootstrap_AnnounceUnloadableAndMissingLeaf()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-cert-boot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var junk = Path.Combine(dir, "not-a-cert.txt");
        File.WriteAllText(junk, "nope");
        var stdout = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(stdout);
            using var proxy = new ProxyServer();
            CertificateBootstrap.Apply(proxy, new CertificatesConfig { CertificatePath = junk });
            await AsyncConsole.FlushAsync();
            var unloadable = stdout.ToString();
            StringAssert.Contains(unloadable, "Certificate path configured");
            StringAssert.Contains(unloadable, "could not load");

            stdout.GetStringBuilder().Clear();
            CertificateBootstrap.Apply(proxy, new CertificatesConfig
            {
                CertificatePath = Path.Combine(dir, "missing.pfx"),
                AcmeDomain = "example.test",
                AcmeEmail = "ops@example.test",
            });
            await AsyncConsole.FlushAsync();
            var acme = stdout.ToString();
            StringAssert.Contains(acme, "ACME domain configured");
            StringAssert.Contains(acme, "certificate files are missing");
        }
        finally
        {
            Console.SetOut(previous);
            TryDelete(dir);
        }
    }

    // Bug class: process-level healthcheck/test help never hits PrintHelp (only in-process unit calls do).
    [TestMethod]
    [Timeout(60_000)]
    public async Task Process_HealthcheckAndTestHelp_ExitZero()
    {
        var (hcCode, hcOut, hcErr) = await RunCliAsync(["healthcheck", "--help"], TimeSpan.FromSeconds(30));
        Assert.AreEqual(0, hcCode, hcErr);
        StringAssert.Contains(hcOut, "titanium healthcheck");

        var (testCode, testOut, testErr) = await RunCliAsync(["test", "--help"], TimeSpan.FromSeconds(30));
        Assert.AreEqual(0, testCode, testErr);
        StringAssert.Contains(testOut, "titanium test");
    }

    private static string FindTitaniumDll()
    {
        var testDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", "..", "src", "Titanium.Cli", "bin", "Release", "net10.0", "titanium.dll")),
            Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", "..", "src", "Titanium.Cli", "bin", "Debug", "net10.0", "titanium.dll")),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Fail("titanium.dll not found; build Titanium.Cli first.");
        return "";
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunCliAsync(string[] args, TimeSpan timeout)
    {
        var dll = FindTitaniumDll();
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
        };
        psi.ArgumentList.Add(dll);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = System.Diagnostics.Process.Start(psi)
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

            Assert.Fail($"CLI timed out: {string.Join(' ', args)}");
        }

        return (proc.ExitCode, await stdoutTask, await stderrTask);
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
            // Best-effort cleanup.
        }
    }
}
