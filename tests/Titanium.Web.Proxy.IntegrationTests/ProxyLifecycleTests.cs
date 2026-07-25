using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Separate characterizations for issues #919 (shutdown cleanup), #799 (restart same port),
///     and #809 (aborted TLS must not leave a spinning handler). These are not treated as duplicates.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ProxyLifecycleTests
{
    private static TestServer sharedServer;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    /// <summary>
    ///     #919: after StopAsync, active client count drains and pooled upstream sockets are cleared.
    /// </summary>
    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task StopAsync_DrainsActiveSessions_AndClearsServerConnectionCount()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async context =>
        {
            await Task.Delay(200);
            await context.Response.WriteAsync("ok");
        });

        var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0));
        proxy.Start();

        try
        {
            using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
            var pending = client.GetStringAsync(server.ListeningHttpUrl);

            await Task.Delay(50);
            await proxy.StopAsync(TimeSpan.FromSeconds(5));

            Assert.IsFalse(proxy.ProxyRunning);
            Assert.AreEqual(0, proxy.ClientConnectionCount,
                "Active client handlers should drain after StopAsync cancellation.");
            Assert.AreEqual(0, proxy.ServerConnectionCount,
                "Pooled upstream connections should be cleared on stop.");

            try { await pending; }
            catch { /* client may fail once the listener is gone */ }
        }
        finally
        {
            proxy.Dispose();
        }
    }

    /// <summary>
    ///     #799: Stop then Start on the same ProxyServer instance and listening port must work again.
    /// </summary>
    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Stop_Then_Start_SamePort_ServesTrafficAgain()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("after-restart"));

        var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0));
        proxy.Start();
        var port = proxy.ProxyEndPoints[0].Port;

        try
        {
            using (var client1 = TestHelper.GetHttpClient(port))
                Assert.AreEqual("after-restart", await client1.GetStringAsync(server.ListeningHttpUrl));

            proxy.Stop();
            Assert.IsFalse(proxy.ProxyRunning);
            Assert.AreEqual(1, proxy.ProxyEndPoints.Count, "Stop must retain registered endpoints.");

            proxy.Start();
            Assert.IsTrue(proxy.ProxyRunning);
            Assert.AreEqual(port, proxy.ProxyEndPoints[0].Port, "Endpoint should re-bind the same port.");

            using var client2 = TestHelper.GetHttpClient(port);
            Assert.AreEqual("after-restart", await client2.GetStringAsync(server.ListeningHttpUrl));
        }
        finally
        {
            if (proxy.ProxyRunning) proxy.Stop();
            proxy.Dispose();
        }
    }

    /// <summary>
    ///     #809: client aborting during TLS authenticate-as-server must not leave a hot-spinning handler
    ///     (CPU pegged / connection never released).
    /// </summary>
    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task AbortedTlsHandshake_ReleasesClientConnection_WithoutSpin()
    {
        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        var port = proxy.ProxyEndPoints[0].Port;

        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;

        for (var i = 0; i < 20; i++)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var stream = tcp.GetStream();

            // Complete CONNECT, then abort before finishing the client TLS handshake.
            var connect = System.Text.Encoding.ASCII.GetBytes(
                "CONNECT abort-tls.example:443 HTTP/1.1\r\nHost: abort-tls.example:443\r\n\r\n");
            await stream.WriteAsync(connect);

            var buffer = new byte[1];
            var matched = 0;
            const string terminator = "\r\n\r\n";
            while (matched < terminator.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, 1));
                if (read == 0) break;
                matched = buffer[0] == (byte)terminator[matched] ? matched + 1 : buffer[0] == (byte)terminator[0] ? 1 : 0;
            }

            // Send a junk ClientHello prefix then close abruptly.
            await stream.WriteAsync(new byte[] { 0x16, 0x03, 0x01, 0x00, 0x05, 0x01, 0x00, 0x00, 0x01, 0x00 });
            tcp.Close();
        }

        // Allow handlers to observe the aborts and unwind.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (proxy.ClientConnectionCount > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.AreEqual(0, proxy.ClientConnectionCount,
            "Aborted TLS handshakes must release client connections.");

        await Task.Delay(500);
        var cpuDelta = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
        Assert.IsTrue(cpuDelta < TimeSpan.FromSeconds(3),
            $"Aborted-TLS storm should not burn CPU; observed {cpuDelta.TotalMilliseconds:F0}ms processor time.");
    }
}
