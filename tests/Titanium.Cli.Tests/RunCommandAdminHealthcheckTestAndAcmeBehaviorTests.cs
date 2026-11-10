using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;
using Titanium.Cli.Certificates;
using Titanium.Cli.Config;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;

namespace Titanium.Cli.Tests;

/// <summary>
///     RunCommand admin+healthcheck, <c>titanium test</c> session/timing banners, ACME challenge/issue hooks,
///     and Windows service logging defaults that still show thin on develop.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RunCommandAdminHealthcheckTestAndAcmeBehaviorTests
{
    // Bug class: admin binds but healthcheck against -c still skips / probes the wrong port, so containers
    // mark the process unhealthy while /healthz is live.
    [TestMethod]
    [Timeout(90_000)]
    public async Task AdminListener_ThroughExecuteCore_HealthcheckProbesOk()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-admin-hc-" + Guid.NewGuid().ToString("N"));
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
                TimeSpan.FromSeconds(30),
                () => stdout.ToString());

            Assert.AreEqual(0, await HealthcheckCommand.ExecuteAsync(
                ["healthcheck", "-c", path, "--timeout", "5"]));
            Assert.AreEqual(0, await HealthcheckCommand.ExecuteAsync(
                ["healthcheck", "--url", $"http://127.0.0.1:{adminPort}/healthz", "--timeout", "5"]));

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

    // Bug class: `titanium test` hides LeastTime / ACME / staticFiles session-path implications, so operators
    // think interception stays off until run fails later.
    [TestMethod]
    public async Task TestCommand_ReportsSessionAndTimingFlags_AndRejectsBadConfigs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-test-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var www = Path.Combine(dir, "www");
        Directory.CreateDirectory(www);
        var listenPort = FreePort();
        var good = Path.Combine(dir, "good.yaml");
        await File.WriteAllTextAsync(good, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {listenPort}
                decryptSsl: false
            staticFiles:
              root: "{www.Replace('\\', '/')}"
            certificates:
              acmeDomain: "example.test"
              acmeEmail: "ops@example.test"
            clusters:
              - id: c1
                algorithm: leastTime
                destinations:
                  - id: d1
                    address: "127.0.0.1"
                    port: 9
            """);

        var unknown = Path.Combine(dir, "unknown.yaml");
        await File.WriteAllTextAsync(unknown, $"""
            schemaVersion: "7.1"
            totallyUnknownKey: true
            listeners:
              - host: "127.0.0.1"
                port: {FreePort()}
                decryptSsl: false
            """);

        var invalid = Path.Combine(dir, "invalid.yaml");
        await File.WriteAllTextAsync(invalid, """
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: -1
            """);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Assert.AreEqual(0, await TestCommand.ExecuteAsync(["test", "-c", good]));
            await AsyncConsole.FlushAsync();
            var ok = stdout.ToString();
            StringAssert.Contains(ok, "Config OK:");
            StringAssert.Contains(ok, "EnableHttpInterception would be: True");
            StringAssert.Contains(ok, "EnableRequestTimingCapture would be: True");

            stdout.GetStringBuilder().Clear();
            stderr.GetStringBuilder().Clear();
            Assert.AreEqual(1, await TestCommand.ExecuteAsync(["test", "-c", unknown, "--strict"]));
            await AsyncConsole.FlushAsync();
            StringAssert.Contains(stderr.ToString() + stdout.ToString(), "warning:");

            stdout.GetStringBuilder().Clear();
            stderr.GetStringBuilder().Clear();
            Assert.AreEqual(1, await TestCommand.ExecuteAsync(["test", "-c", invalid]));
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            TryDelete(dir);
        }
    }

    // Bug class: IssueOverride / short poll never replaces the leaf, so decrypt endpoints keep a blank cert
    // after IssueOrRenewAsync returns.
    [TestMethod]
    public async Task CertificateBootstrap_IssueOrRenew_UsesOverrideAndLoadsPem()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-acme-issue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var certPath = Path.Combine(dir, "leaf.pem");
        var keyPath = Path.Combine(dir, "leaf.key");
        WriteSelfSignedPem(certPath, keyPath);

        var previous = CertificateBootstrap.IssueOverride;
        CertificateBootstrap.IssueOverride = (_, _, _) => Task.CompletedTask;
        var previousPoll = Environment.GetEnvironmentVariable("TITANIUM_ACME_POLL_SECONDS");
        Environment.SetEnvironmentVariable("TITANIUM_ACME_POLL_SECONDS", "2");
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        try
        {
            Console.SetOut(stdout);
            using var proxy = new ProxyServer(false, false, false);
            var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true);
            proxy.AddEndPoint(ep);

            await CertificateBootstrap.IssueOrRenewAsync(
                proxy,
                new CertificatesConfig
                {
                    AcmeDomain = "example.test",
                    AcmeEmail = "ops@example.test",
                    CertificatePath = certPath,
                    PrivateKeyPath = keyPath,
                },
                pollTimeout: TimeSpan.FromSeconds(3));
            await AsyncConsole.FlushAsync();
            Assert.IsNotNull(ep.GenericCertificate);
            StringAssert.Contains(stdout.ToString(), "Replaced GenericCertificate");
        }
        finally
        {
            CertificateBootstrap.IssueOverride = previous;
            Environment.SetEnvironmentVariable("TITANIUM_ACME_POLL_SECONDS", previousPoll);
            Console.SetOut(previousOut);
            TryDelete(dir);
        }
    }

    // Bug class: ACME HTTP-01 token from SetChallengeToken is never served, so Let's Encrypt validation fails
    // while the challenge URL returns 404.
    [TestMethod]
    [Timeout(60_000)]
    public async Task CertificateBootstrap_AcmeChallenge_ServesTokenOverProxy()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false);
        proxy.AddEndPoint(ep);
        var token = "tok-" + Guid.NewGuid().ToString("N");
        CertificateBootstrap.SetChallengeToken(token, "key-authorization-value");
        CertificateBootstrap.Apply(proxy, new CertificatesConfig
        {
            AcmeDomain = "example.test",
            AcmeEmail = "ops@example.test",
        });
        proxy.Start(changeSystemProxySettings: false);
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{ep.Port}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var body = await http.GetStringAsync(
                $"http://example.test/.well-known/acme-challenge/{token}");
            Assert.AreEqual("key-authorization-value", body);

            using var missing = await http.GetAsync(
                "http://example.test/.well-known/acme-challenge/missing-token");
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            proxy.Stop();
        }
    }

    // Bug class: Windows service mode with no YAML file sink leaves logging disabled, so SCM has an empty log.
    [TestMethod]
    public void ApplyServiceLoggingDefaults_Windows_EnablesFileSinkWhenYamlOmitsFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows service file sink only.");
        }

        using var proxy = new ProxyServer();
        proxy.Logging.Enabled = false;
        proxy.Logging.EnableFile = false;
        proxy.Logging.MinimumLevel = Microsoft.Extensions.Logging.LogLevel.Warning;
        RunCommand.ApplyServiceLoggingDefaults(proxy, logging: null);
        Assert.IsTrue(proxy.Logging.Enabled);
        Assert.IsTrue(proxy.Logging.EnableFile);
        StringAssert.Contains(proxy.Logging.FilePath, "titanium.log");
        Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Information, proxy.Logging.MinimumLevel);
    }

    // Bug class: process-level `titanium test -c` never prints the Config OK banner (only in-process unit calls).
    [TestMethod]
    [Timeout(60_000)]
    public async Task Process_TestValidConfig_PrintsConfigOk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-test-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {FreePort()}
                decryptSsl: false
            """);
        try
        {
            var (code, stdout, stderr) = await RunCliAsync(["test", "-c", path], TimeSpan.FromSeconds(45));
            Assert.AreEqual(0, code, stderr);
            StringAssert.Contains(stdout, "Config OK:");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    private static void WriteSelfSignedPem(string certPath, string keyPath)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=twp-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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

            Assert.Fail("CLI timed out");
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
            // Best-effort.
        }
    }
}
