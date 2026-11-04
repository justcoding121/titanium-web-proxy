using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>The listener's own TLS handshake follows <c>server.tls</c>; unset keeps today's behavior.</summary>
[TestClass]
[DoNotParallelize]
public class ListenerTlsPolicyTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(15);

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Unset_AcceptsTls12AndTls13()
    {
        using var rig = StartRig(proxy => { });

        var tls12 = await HandshakeAsync(rig.Port, SslProtocols.Tls12);
        var tls13 = await HandshakeAsync(rig.Port, SslProtocols.Tls13);

        Assert.AreEqual(SslProtocols.Tls12, tls12.Protocol);
        Assert.AreEqual(SslProtocols.Tls13, tls13.Protocol);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task MinVersionTls13_RefusesATls12OnlyClient_AndServesTls13()
    {
        using var rig = StartRig(proxy => proxy.ListenerMinimumSslProtocol = SslProtocols.Tls13);

        Assert.IsTrue(await HandshakeFailsAsync(rig.Port, SslProtocols.Tls12));
        var tls13 = await HandshakeAsync(rig.Port, SslProtocols.Tls13);

        Assert.AreEqual(SslProtocols.Tls13, tls13.Protocol);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task MinVersionTls12_KeepsBothVersions()
    {
        using var rig = StartRig(proxy => proxy.ListenerMinimumSslProtocol = SslProtocols.Tls12);

        Assert.AreEqual(SslProtocols.Tls12, (await HandshakeAsync(rig.Port, SslProtocols.Tls12)).Protocol);
        Assert.AreEqual(SslProtocols.Tls13, (await HandshakeAsync(rig.Port, SslProtocols.Tls13)).Protocol);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task CipherSuites_RestrictTheNegotiatedSuite_OnPlatformsWithACipherPolicy()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows has no cipher suite policy; the setting is ignored.");
        }

        using var rig = StartRig(proxy =>
        {
            proxy.SupportedSslProtocols = SslProtocols.Tls13;
            proxy.ListenerCipherSuites = [TlsCipherSuite.TLS_AES_128_GCM_SHA256];
        });

        var result = await HandshakeAsync(rig.Port, SslProtocols.Tls13);

        Assert.AreEqual(TlsCipherSuite.TLS_AES_128_GCM_SHA256, result.Cipher);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task CipherSuites_OnWindows_AreIgnored_AndTheHandshakeStillWorks()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Only Windows ignores the cipher suite list.");
        }

        using var rig = StartRig(proxy => proxy.ListenerCipherSuites = [TlsCipherSuite.TLS_AES_128_GCM_SHA256]);

        var result = await HandshakeAsync(rig.Port, SslProtocols.Tls13);

        Assert.AreEqual(SslProtocols.Tls13, result.Protocol);
    }

    private static Rig StartRig(Action<ProxyServer> configure)
    {
        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = false };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        configure(proxy);
        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
            GenericCertificateName = "localhost",
            ForwardHost = "127.0.0.1",
            ForwardPort = 9,
            ForwardCleartext = true,
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return new Rig(proxy, endPoint);
    }

    private static async Task<(SslProtocols Protocol, TlsCipherSuite Cipher)> HandshakeAsync(
        int port, SslProtocols protocols)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Step);
        await using var ssl = new SslStream(tcp.GetStream(), false);
        await ssl.AuthenticateAsClientAsync(ClientOptions(protocols)).WaitAsync(Step);
        return (ssl.SslProtocol, ssl.NegotiatedCipherSuite);
    }

    private static async Task<bool> HandshakeFailsAsync(int port, SslProtocols protocols)
    {
        try
        {
            await HandshakeAsync(port, protocols);
            return false;
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            return true;
        }
    }

    private static SslClientAuthenticationOptions ClientOptions(SslProtocols protocols) => new()
    {
        TargetHost = "localhost",
        EnabledSslProtocols = protocols,
        RemoteCertificateValidationCallback = (_, _, _, _) => true,
    };

    private sealed class Rig(ProxyServer proxy, TransparentProxyEndPoint endPoint) : IDisposable
    {
        public int Port => endPoint.Port;

        public void Dispose()
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
