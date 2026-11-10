#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
using System.Net.Quic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http3ExtendedConnectTunnel</c> suite: H1 origin 403 on WebSocket upgrade relays to the H3 client.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3ExtendedConnectTunnelConnect403FatGapTests
{
    private static TestServer sharedServer = null!;
    private static readonly Encoding Ascii = Encoding.ASCII;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    private static (ProxyServer Proxy, TransparentQuicProxyEndPoint Ep) StartProxy(TestSuite testSuite, int forwardPort)
    {
        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = forwardPort,
        };
        quicEp.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp3 = true,
            EnableHttpsSvcbDnsDiscovery = false,
            EnableRfc8441 = true,
        };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(quicEp);
        proxy.Start();
        return (proxy, quicEp);
    }

    /// <summary>
    ///     Bug class: H3 extended CONNECT relays origin 403 as 500 when the H1 upgrade answer is not 101.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_ExtendedConnect_H1CleartextOrigin403_Returns403()
    {
        RequireQuic();
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        DenyWebSocketUpgrade(server);

        var (proxy, quicEp) = StartProxy(testSuite, server.TcpListeningPort);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();

            await using var tunnel = await client.OpenExtendedConnectAsync(
                $"localhost:{server.TcpListeningPort}", "/deny", "websocket", scheme: "http");
            Assert.AreEqual(403, tunnel.Status);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_ExtendedConnect_H1TlsOrigin403_Returns403()
    {
        RequireQuic();
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(ctx =>
        {
            if (string.Equals(ctx.Request.Headers.Upgrade, "websocket", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 403;
                return Task.CompletedTask;
            }

            return ctx.Response.WriteAsync("unexpected");
        });

        var (proxy, quicEp) = StartProxy(testSuite, server.HttpsListeningPort);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();

            await using var tunnel = await client.OpenExtendedConnectAsync(
                $"localhost:{server.HttpsListeningPort}", "/deny", "websocket");
            Assert.AreEqual(403, tunnel.Status);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    private static void DenyWebSocketUpgrade(TestServer server)
    {
        server.HandleTcpRequest(async ctx =>
        {
            var text = await ReadHeadersAsync(ctx);
            if (text.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase))
            {
                var deny = "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await ctx.Transport.Output.WriteAsync(Ascii.GetBytes(deny));
            }

            ctx.Transport.Output.Complete();
        });
    }

    private static async Task<string> ReadHeadersAsync(ConnectionContext ctx)
    {
        using var ms = new System.IO.MemoryStream();
        while (true)
        {
            var result = await ctx.Transport.Input.ReadAsync();
            foreach (var block in result.Buffer)
                ms.Write(block.Span);
            ctx.Transport.Input.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted || Ascii.GetString(ms.ToArray()).Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }

        return Ascii.GetString(ms.ToArray());
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
