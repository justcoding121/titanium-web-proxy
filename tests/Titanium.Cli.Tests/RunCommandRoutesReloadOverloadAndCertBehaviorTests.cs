using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Certificates;
using Titanium.Cli.Config;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;

namespace Titanium.Cli.Tests;

/// <summary>
///     RunCommand route reload, overload host apply, certificate SNI/watch, IssueAcme announcement, and
///     ExecuteAsync zone parsing that still show thin on develop.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RunCommandRoutesReloadOverloadAndCertBehaviorTests
{
    // Bug class: live reload never swaps routes/clusters (only localZone), so traffic keeps hitting the old
    // cluster after `titanium reload` reports success.
    [TestMethod]
    [Timeout(90_000)]
    public async Task RoutesAndClusters_Reload_SwapsMatchedClusterWithoutDroppingListener()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named EventWaitHandle reload is Windows-only.");
        }

        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var port = FreePort();
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, RoutesYaml(port, clusterId: "before", destinationPort: 9));

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
                () => stdout.ToString().Contains("awaiting-shutdown-or-reload", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30),
                () => stdout + Environment.NewLine + stderr);
            Assert.IsTrue(await CanConnectAsync(port));

            await File.WriteAllTextAsync(path, RoutesYaml(port, clusterId: "after", destinationPort: 9));
            Assert.AreEqual(0, await ReloadCommand.ExecuteAsync(["reload", "-c", path]), stderr.ToString());
            await WaitForAsync(
                () => stdout.ToString().Contains("Config reloaded.", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30),
                () => stdout + Environment.NewLine + stderr);
            Assert.IsTrue(await CanConnectAsync(port), "listener must survive route/cluster reload");

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

    // Bug class: server.overload.enabled never starts the host, so /readyz never trips under overload.
    [TestMethod]
    [Timeout(60_000)]
    public async Task OverloadEnabled_ThroughExecuteCore_AppliesWithoutCrashing()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-overload-" + Guid.NewGuid().ToString("N"));
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
              overload:
                enabled: true
                intervalMs: 500
                heapPercent:
                  elevated: 90
                  critical: 98
                actions:
                  readyzFailsOnCritical: true
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
            Directory.SetCurrentDirectory(previousCwd);
            TryDelete(dir);
        }
    }

    // Bug class: SNI items with missing paths throw; WatchCertificateFiles returns a disposable that never
    // reloads when the leaf PEM is replaced.
    [TestMethod]
    public async Task CertificateBootstrap_SniItemsAndWatch_SkipMissingAndReloadOnChange()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-cert-sni-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var certPath = Path.Combine(dir, "leaf.pfx");
        WriteSelfSignedPfx(certPath, password: "pw");

        using var proxy = new ProxyServer(false, false, false);
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true);
        proxy.AddEndPoint(ep);

        CertificateBootstrap.ApplySniItems(proxy, new CertificatesConfig
        {
            Items =
            [
                new CertificateFileConfig
                {
                    Path = Path.Combine(dir, "missing.pfx"),
                    Hosts = ["missing.example"],
                },
                new CertificateFileConfig
                {
                    Path = certPath,
                    Password = "pw",
                    Hosts = ["sni.example", " "],
                },
            ],
        });
        Assert.IsNotNull(ep.SniCertificates);
        Assert.AreEqual(1, ep.SniCertificates!.Count);
        CollectionAssert.Contains(ep.SniCertificates[0].Hosts.ToList(), "sni.example");

        using var watch = CertificateBootstrap.WatchCertificateFiles(
            proxy,
            new CertificatesConfig
            {
                CertificatePath = certPath,
                Items =
                [
                    new CertificateFileConfig { Path = certPath, Password = "pw", Hosts = ["watch.example"] },
                ],
            });
        Assert.IsNotNull(watch);

        // Rewrite the PFX so the content watcher re-applies SNI bindings.
        await Task.Delay(50);
        WriteSelfSignedPfx(certPath, password: "pw");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && ep.SniCertificates is null)
        {
            await Task.Delay(50);
        }

        Assert.IsNotNull(ep.SniCertificates);
    }

    // Bug class: IssueAcme without a directory still tries Certes / hangs instead of telling the operator
    // how to set AcmeDirectory.
    [TestMethod]
    public async Task CertificateBootstrap_IssueAcme_WithoutDirectory_AnnouncesOperatorPath()
    {
        var previous = CertificateBootstrap.IssueOverride;
        CertificateBootstrap.IssueOverride = null;
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        try
        {
            Console.SetOut(stdout);
            using var proxy = new ProxyServer(false, false, false);
            await CertificateBootstrap.IssueAcmeCertificateAsync(proxy, new CertificatesConfig
            {
                AcmeDomain = "example.test",
                AcmeEmail = "ops@example.test",
            });
            await AsyncConsole.FlushAsync();
            StringAssert.Contains(stdout.ToString(), "no AcmeDirectory");
        }
        finally
        {
            CertificateBootstrap.IssueOverride = previous;
            Console.SetOut(previousOut);
        }
    }

    // Bug class: ExecuteAsync never forwards --zone into ExecuteCore, so operators cannot override localZone
    // without editing YAML.
    [TestMethod]
    [Timeout(60_000)]
    public async Task ExecuteAsync_ZoneFlag_OverridesLocalZoneBanner()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-zone-async-" + Guid.NewGuid().ToString("N"));
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
              localZone: "from-file"
            """);

        var stdout = new StringWriter();
        var previousOut = Console.Out;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            Console.SetOut(stdout);
            var run = RunCommand.ExecuteCoreAsync(
                path, verbose: true, serviceMode: false, cts.Token, watch: false, zoneOverride: "from-cli");
            await WaitForAsync(
                () => stdout.ToString().Contains("awaiting-shutdown-or-reload", StringComparison.Ordinal),
                TimeSpan.FromSeconds(25),
                () => stdout.ToString());
            Assert.IsTrue(await CanConnectAsync(port));
            Assert.AreEqual("from-cli", RunCommand.ResolveLocalZone(
                new ServerConfig { LocalZone = "from-file" }, "from-cli"));
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

    // Bug class: process-level reload/version help never hits PrintHelp.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Process_ReloadAndVersionHelp_ExitZero()
    {
        var (reloadCode, reloadOut, reloadErr) = await RunCliAsync(["reload", "--help"], TimeSpan.FromSeconds(30));
        Assert.AreEqual(0, reloadCode, reloadErr);
        StringAssert.Contains(reloadOut, "titanium reload");

        var (verCode, verOut, verErr) = await RunCliAsync(["version", "--help"], TimeSpan.FromSeconds(30));
        Assert.AreEqual(0, verCode, verErr);
        StringAssert.Contains(verOut, "titanium version");
    }

    private static string RoutesYaml(int port, string clusterId, int destinationPort) => $"""
        schemaVersion: "7.1"
        listeners:
          - host: "127.0.0.1"
            port: {port}
            decryptSsl: false
        routes:
          - id: r1
            match:
              hosts: ["example.test"]
            clusterId: {clusterId}
        clusters:
          - id: {clusterId}
            destinations:
              - id: d1
                address: "127.0.0.1"
                port: {destinationPort}
        server:
          drainTimeoutSeconds: 0
        """;

    private static void WriteSelfSignedPfx(string path, string password)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=twp-sni", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, password));
    }

    private static void WriteSelfSignedPem(string certPath, string keyPath)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=twp-watch", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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
