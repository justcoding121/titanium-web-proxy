#pragma warning disable TWP001
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <c>ProxyServer.SystemProxy</c> suite: disable helpers must not throw when the proxy is idle.
/// </summary>
[TestClass]
public class ProxyServerTryDisableSystemProxyFatGapTests
{
    /// <summary>
    ///     Bug class: <see cref="ProxyServer.TryDisableSystemProxy" /> throws before the proxy has started.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public void TryDisableSystemProxy_BeforeStart_ReturnsResultWithoutThrow()
    {
        using var proxy = new ProxyServer(false, false, false);
        _ = proxy.TryDisableSystemProxy(ProxyProtocolType.Http);
        _ = proxy.TryDisableAllSystemProxies();
        _ = proxy.TryRestoreOriginalProxySettings();
    }

    /// <summary>
    ///     Bug class: <see cref="ProxyServer.TryDisableAllSystemProxies" /> throws while endpoints are listening.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public void TryDisableAll_AfterStart_ReturnsResultWithoutThrow()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = IsolatedTestCa.NewRootName();
        proxy.CertificateManager.CreateRootCertificate(false);
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false));
        proxy.Start();
        try
        {
            var r1 = proxy.TryDisableSystemProxy(ProxyProtocolType.Http);
            var r2 = proxy.TryDisableAllSystemProxies();
            var r3 = proxy.TryRestoreOriginalProxySettings();
            Assert.IsNotNull(r1.Message);
            Assert.IsNotNull(r2.Message);
            Assert.IsNotNull(r3.Message);
        }
        finally
        {
            proxy.Stop();
        }
    }
}
#pragma warning restore TWP001
