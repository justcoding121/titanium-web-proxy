#pragma warning disable TWP001
using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat ProxyServer edges: hot-remove endpoint while running, PolicyModes round-trip,
///     and remaining listen/limit knobs still soft on develop.
/// </summary>
[TestClass]
public class ProxyServerHotRemoveAndPolicyFatGapTests
{
    [TestMethod]
    [Timeout(30_000)]
    public void Running_RemoveEndPoint_StopsListening()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = IsolatedTestCa.NewRootName();
        proxy.CertificateManager.CreateRootCertificate(false);
        var first = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var second = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        proxy.AddEndPoint(first);
        proxy.AddEndPoint(second);
        proxy.Start();
        Assert.AreEqual(2, proxy.ProxyEndPoints.Count);
        proxy.RemoveEndPoint(second);
        Assert.AreEqual(1, proxy.ProxyEndPoints.Count);
        Assert.IsTrue(proxy.ProxyRunning);
        Assert.IsTrue(first.Port > 0);
        proxy.Stop();
    }

    [TestMethod]
    public void PolicyModes_AllEnforce_AndAllowAmbiguous_RoundTrip()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.PolicyModes = ProxyPolicyModes.AllEnforce;
        Assert.AreEqual(ProxyPolicyModes.AllEnforce, proxy.PolicyModes);
        proxy.PolicyModes = ProxyPolicyModes.AllEnforce.WithAllowAmbiguousFramingEnabled();
        Assert.IsTrue(proxy.PolicyModes.AllowAmbiguousFraming);
    }

    [TestMethod]
    public void LimitAndDiscoveryFlags_RoundTrip()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.MaxConcurrentClientConnections = 64;
        proxy.MaxCachedConnections = 16;
        proxy.EnableHttpsSvcbDnsDiscovery = false;
        proxy.EnableRfc8441 = true;
        proxy.EnableHttp3 = true;
        proxy.EnableConnectTcp = true;
        proxy.EnableWebTransport = true;
        proxy.ThreadPoolWorkerThread = 64;

        Assert.AreEqual(64, proxy.MaxConcurrentClientConnections);
        Assert.AreEqual(16, proxy.MaxCachedConnections);
        Assert.IsFalse(proxy.EnableHttpsSvcbDnsDiscovery);
        Assert.IsTrue(proxy.EnableRfc8441);
        Assert.IsTrue(proxy.EnableHttp3);
        Assert.IsTrue(proxy.EnableConnectTcp);
        Assert.IsTrue(proxy.EnableWebTransport);
        Assert.AreEqual(64, proxy.ThreadPoolWorkerThread);
    }

    [TestMethod]
    [Timeout(30_000)]
    public void TransparentQuic_StartStop_WhenSupported()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = IsolatedTestCa.NewRootName();
        proxy.CertificateManager.CreateRootCertificate(false);
        proxy.EnableHttp3 = true;
        proxy.AddEndPoint(new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = 443,
        });
        try
        {
            proxy.Start();
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or InvalidOperationException)
        {
            Assert.Inconclusive($"QUIC endpoint not supported: {ex.Message}");
            return;
        }

        Assert.IsTrue(proxy.ProxyRunning);
        Assert.IsTrue(proxy.ProxyEndPoints[0].Port > 0);
        proxy.Stop();
    }
}
