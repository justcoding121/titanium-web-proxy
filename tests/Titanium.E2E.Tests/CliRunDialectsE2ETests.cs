using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

[TestClass]
public class CliRunDialectsE2ETests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-e2e-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Run_NativeJson_ProxiesHttp()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var cfg = ConfigFixtures.WriteNativeJson(_tempDir, listen, origin.Port);
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: false);
        await harness.StartRunAsync(cfg);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var response = await http.GetAsync($"http://127.0.0.1:{listen}/json");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Test_PlusBlock_ValidatesWithoutStartingListeners()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        var cfg = ConfigFixtures.WritePlus(_tempDir, listen, origin.Port, control, "test-secret");
        using var harness = new CliProcessHarness();
        var (code, stdout, _) = await harness.RunOnceAsync(["test", "-c", cfg]);
        Assert.AreEqual(0, code);
        StringAssert.Contains(stdout, "Config OK");

        // Control plane must not be listening after `test`.
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var resp = await probe.GetAsync($"http://127.0.0.1:{control}/v1/snapshot");
            Assert.Fail($"Control plane unexpectedly responded: {(int)resp.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // expected — nothing listening
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Run_SiteFileDialect_LiveViaCompanionYaml()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var twp = ConfigFixtures.WriteSiteFile(_tempDir, listen, origin.Port);
        using var harness = new CliProcessHarness();
        var (testCode, _, _) = await harness.RunOnceAsync(["test", "-c", twp]);
        Assert.AreEqual(0, testCode);

        var live = ConfigFixtures.WriteSiteFileWithListen(_tempDir, listen, origin.Port);
        harness.EnsurePlusDllBesideCli(copy: false);
        await harness.StartRunAsync(live);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var response = await http.GetAsync($"http://127.0.0.1:{listen}/site");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Run_SocksListener_AcceptsTcpHandshake()
    {
        var listen = CliProcessHarness.GetFreePort();
        var cfg = ConfigFixtures.WriteSocks(_tempDir, listen);
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: false);
        await harness.StartRunAsync(cfg);
        try
        {
            // SOCKS5 greeting: VER=5, NMETHODS=1, METHOD=NO AUTH (0)
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listen);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
            var buf = new byte[2];
            var read = await stream.ReadAsync(buf);
            Assert.IsTrue(read >= 2, $"expected SOCKS greeting reply, read={read}");
            Assert.AreEqual(0x05, buf[0]);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Run_TlsLeaf_HttpsGet()
    {
        var (certPath, keyPath) = CreateSelfSignedPem(_tempDir);
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var cfg = ConfigFixtures.WriteTls(_tempDir, listen, origin.Port, certPath, keyPath);
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: false);
        await harness.StartRunAsync(cfg);
        try
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            var response = await http.GetAsync($"https://127.0.0.1:{listen}/tls");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), "echo:");
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Run_ServiceMode_StartsAndStops()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var cfg = ConfigFixtures.WriteForwardHost(_tempDir, listen, origin.Port);
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: false);
        await harness.StartRunAsync(cfg, serviceMode: true);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var response = await http.GetAsync($"http://127.0.0.1:{listen}/svc-mode");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            StringAssert.Contains(harness.StdOut, "service mode", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            harness.SendSigterm();
            await Task.Delay(500);
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Run_SIGHUP_ReloadFail_KeepsProcessUp()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("SIGHUP config reload is Unix-only.");
        }

        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var cfg = ConfigFixtures.WriteTransforms(_tempDir, listen, origin.Port, pathPrefix: "/v1");
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: false);
        await harness.StartRunAsync(cfg);
        await harness.WaitForOutputAsync("sighup-handler-registered", TimeSpan.FromSeconds(15));
        try
        {
            // Corrupt the config then SIGHUP — process should stay up with a failure message.
            await File.WriteAllTextAsync(cfg, "{ not-json");
            harness.SendSighup();
            await harness.WaitForOutputAsync("Config reload failed", TimeSpan.FromSeconds(15));

            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{listen}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            var after = await http.GetAsync($"http://127.0.0.1:{origin.Port}/api");
            Assert.AreEqual(HttpStatusCode.OK, after.StatusCode);
            StringAssert.Contains(await after.Content.ReadAsStringAsync(), "/v1/api");
        }
        finally
        {
            harness.Dispose();
        }
    }

    private static (string CertPath, string KeyPath) CreateSelfSignedPem(string dir)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var certPath = Path.Combine(dir, "leaf.pem");
        var keyPath = Path.Combine(dir, "leaf.key");
        File.WriteAllText(certPath, PemEncoding.WriteString("CERTIFICATE", cert.RawData));
        File.WriteAllText(keyPath, PemEncoding.WriteString("PRIVATE KEY", rsa.ExportPkcs8PrivateKey()));
        return (certPath, keyPath);
    }
}
