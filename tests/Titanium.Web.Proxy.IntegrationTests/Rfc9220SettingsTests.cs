#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
using System.Net.Quic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

[DoNotParallelize]
[TestClass]
public class Rfc9220SettingsTests
{
    private static TestServer sharedServer = null!;

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

    private static ProxyServer StartH3Proxy(bool enableRfc8441, int forwardPort, out TransparentQuicProxyEndPoint quicEp)
    {
        quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = forwardPort
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
            EnableRfc8441 = enableRfc8441
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
        return proxy;
    }

    [TestMethod]
    public async Task Settings_EnableRfc8441Off_DoesNotAdvertise0x08()
    {
        RequireQuic();
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async ctx => await ctx.Response.WriteAsync("ok"));

        var proxy = StartH3Proxy(enableRfc8441: false, server.HttpsListeningPort, out var quicEp);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();
            Assert.IsFalse(client.PeerSettings.ContainsKey(0x08UL));
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    public async Task Settings_EnableRfc8441On_Advertises0x08Value1()
    {
        RequireQuic();
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async ctx => await ctx.Response.WriteAsync("ok"));

        var proxy = StartH3Proxy(enableRfc8441: true, server.HttpsListeningPort, out var quicEp);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();
            Assert.IsTrue(client.PeerSettings.TryGetValue(0x08UL, out var value));
            Assert.AreEqual(1UL, value);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    public async Task ExtendedConnect_Rfc8441Off_IsRejectedAsMalformed()
    {
        RequireQuic();
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async ctx => await ctx.Response.WriteAsync("ok"));

        var proxy = StartH3Proxy(enableRfc8441: false, server.HttpsListeningPort, out var quicEp);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();

            await Assert.ThrowsExactlyAsync<QuicException>(async () =>
            {
                await using var tunnel = await client.OpenExtendedConnectAsync(
                    $"localhost:{server.HttpsListeningPort}", "/ws", "websocket");
            });
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    public async Task PlainConnectWithoutProtocol_BehaviorUnchanged()
    {
        RequireQuic();
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async ctx => await ctx.Response.WriteAsync("connect-ok"));

        var proxy = StartH3Proxy(enableRfc8441: true, server.HttpsListeningPort, out var quicEp);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            var response = await client.SendAsync("CONNECT", $"localhost:{server.HttpsListeningPort}", "/",
                extraRequestHeaders: null);
            Assert.AreNotEqual(0, response.StatusCode);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}

#pragma warning restore TWP001
#pragma warning restore CA1416
