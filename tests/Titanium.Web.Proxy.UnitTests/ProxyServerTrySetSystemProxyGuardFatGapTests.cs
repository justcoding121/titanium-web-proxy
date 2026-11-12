#pragma warning disable TWP001
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="ProxyServer" /> system-proxy guards: <see cref="ProxyServer.TrySetAsSystemProxy" />
///     must fail closed before start and for endpoints that were never registered.
/// </summary>
[TestClass]
public class ProxyServerTrySetSystemProxyGuardFatGapTests
{
    /// <summary>
    ///     Bug class: <see cref="ProxyServer.TrySetAsSystemProxy" /> mutates OS settings before <see cref="ProxyServer.Start" />.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public void TrySetAsSystemProxy_BeforeStart_ReturnsFailure()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 18080, false);
        proxy.AddEndPoint(ep);
        var result = proxy.TrySetAsSystemProxy(ep, ProxyProtocolType.Http);
        Assert.IsFalse(result.Succeeded,
            "System proxy must not be enabled until the proxy is listening.");
    }

    /// <summary>
    ///     Bug class: an unregistered <see cref="ExplicitProxyEndPoint" /> is accepted as system proxy.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public void TrySetAsSystemProxy_UnregisteredEndPoint_ReturnsFailure()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = IsolatedTestCa.NewRootName();
        proxy.CertificateManager.CreateRootCertificate(false);
        var registered = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        proxy.AddEndPoint(registered);
        proxy.Start();
        try
        {
            var stray = new ExplicitProxyEndPoint(IPAddress.Loopback, registered.Port, false);
            var result = proxy.TrySetAsSystemProxy(stray, ProxyProtocolType.Http);
            Assert.IsFalse(result.Succeeded);
        }
        finally
        {
            proxy.Stop();
        }
    }
}
#pragma warning restore TWP001
