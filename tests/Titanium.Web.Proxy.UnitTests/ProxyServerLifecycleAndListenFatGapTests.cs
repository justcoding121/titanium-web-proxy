#pragma warning disable TWP001
using System;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat ProxyServer lifecycle / listen / policy knobs still soft on develop:
///     transparent + socks endpoints, hot-add endpoint while running, and remaining flags.
/// </summary>
[TestClass]
public class ProxyServerLifecycleAndListenFatGapTests
{
    [TestMethod]
    [Timeout(30_000)]
    public void Explicit_StartStop_Twice_Succeeds()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = IsolatedTestCa.NewRootName();
        proxy.CertificateManager.CreateRootCertificate(false);
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false));
        proxy.Start();
        Assert.IsTrue(proxy.ProxyRunning);
        proxy.Stop();
        Assert.IsFalse(proxy.ProxyRunning);
        proxy.Start();
        Assert.IsTrue(proxy.ProxyRunning);
        proxy.Stop();
    }

    [TestMethod]
    [Timeout(30_000)]
    public void Transparent_StartStop_Succeeds()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = IsolatedTestCa.NewRootName();
        proxy.CertificateManager.CreateRootCertificate(false);
        proxy.AddEndPoint(new TransparentProxyEndPoint(IPAddress.Loopback, 0, false));
        proxy.Start();
        Assert.IsTrue(proxy.ProxyRunning);
        Assert.AreEqual(1, proxy.ProxyEndPoints.Count);
        Assert.IsTrue(proxy.ProxyEndPoints[0].Port > 0);
        proxy.Stop();
    }

    [TestMethod]
    [Timeout(30_000)]
    public void Socks_StartStop_Succeeds()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = IsolatedTestCa.NewRootName();
        proxy.CertificateManager.CreateRootCertificate(false);
        proxy.AddEndPoint(new SocksProxyEndPoint(IPAddress.Loopback, 0, false));
        proxy.Start();
        Assert.IsTrue(proxy.ProxyRunning);
        proxy.Stop();
    }

    [TestMethod]
    [Timeout(30_000)]
    public void Running_AddSecondExplicitEndPoint_Listens()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = IsolatedTestCa.NewRootName();
        proxy.CertificateManager.CreateRootCertificate(false);
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false));
        proxy.Start();
        var second = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        proxy.AddEndPoint(second);
        Assert.AreEqual(2, proxy.ProxyEndPoints.Count);
        Assert.IsTrue(second.Port > 0);
        proxy.Stop();
    }

    [TestMethod]
    public void ProtocolAndSslFlags_RoundTrip()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.EnableHttp2 = true;
        proxy.EnableHttp3 = false;
        proxy.EnableHttpInterception = true;
        proxy.CheckCertificateRevocation = X509RevocationMode.Online;
        proxy.SupportedSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        proxy.MaxWebSocketFramePayloadBytes = 1024 * 1024;

        Assert.IsTrue(proxy.EnableHttp2);
        Assert.IsFalse(proxy.EnableHttp3);
        Assert.IsTrue(proxy.EnableHttpInterception);
        Assert.AreEqual(X509RevocationMode.Online, proxy.CheckCertificateRevocation);
        Assert.AreEqual(1024 * 1024, proxy.MaxWebSocketFramePayloadBytes);
        Assert.AreEqual(0, proxy.ClientConnectionCount);
        Assert.AreEqual(0, proxy.ServerConnectionCount);
    }

    [TestMethod]
    public void BufferPool_AndUpstreamProxyFuncs_Assignable()
    {
        using var proxy = new ProxyServer(false, false, false);
        Assert.IsNotNull(proxy.BufferPool);
        proxy.GetCustomUpStreamProxyFunc = _ => Task.FromResult<IExternalProxy?>(null);
        proxy.CustomUpStreamProxyFailureFunc = _ => Task.FromResult<IExternalProxy?>(null);
        Assert.IsNotNull(proxy.GetCustomUpStreamProxyFunc);
        Assert.IsNotNull(proxy.CustomUpStreamProxyFailureFunc);
    }

    [TestMethod]
    [Timeout(30_000)]
    public void MixedEndpoints_ExplicitTransparentSocks_StartStop()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = IsolatedTestCa.NewRootName();
        proxy.CertificateManager.CreateRootCertificate(false);
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false));
        proxy.AddEndPoint(new TransparentProxyEndPoint(IPAddress.Loopback, 0, false));
        proxy.AddEndPoint(new SocksProxyEndPoint(IPAddress.Loopback, 0, false));
        proxy.Start();
        Assert.IsTrue(proxy.ProxyRunning);
        Assert.AreEqual(3, proxy.ProxyEndPoints.Count);
        foreach (var ep in proxy.ProxyEndPoints)
            Assert.IsTrue(ep.Port > 0);
        proxy.Stop();
    }
}
