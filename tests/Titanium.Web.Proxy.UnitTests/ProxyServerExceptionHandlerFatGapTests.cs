#pragma warning disable TWP001
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="ProxyServer" /> edges: connection-count events and proxy-auth func wiring.
/// </summary>
[TestClass]
public class ProxyServerExceptionHandlerFatGapTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task ProxyBasicAuthenticateFunc_CanBeAssigned()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.ProxyBasicAuthenticateFunc = (_, user, pass) => Task.FromResult(user == "u" && pass == "p");
        Assert.IsNotNull(proxy.ProxyBasicAuthenticateFunc);
        Assert.IsTrue(await proxy.ProxyBasicAuthenticateFunc!(null, "u", "p"));
        Assert.IsFalse(await proxy.ProxyBasicAuthenticateFunc(null, "bad", "p"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public void ClientConnectionCountChanged_CanSubscribe()
    {
        using var proxy = new ProxyServer(false, false, false);
        var hits = 0;
        proxy.ClientConnectionCountChanged += (_, _) => hits++;
        proxy.CertificateManager.RootCertificateName = IsolatedTestCa.NewRootName();
        proxy.CertificateManager.CreateRootCertificate(false);
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false));
        proxy.Start();
        try
        {
            Assert.AreEqual(0, hits);
            Assert.AreEqual(0, proxy.ClientConnectionCount);
        }
        finally
        {
            proxy.Stop();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public void AdmittedClientConnectionCount_InitiallyZero()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = IsolatedTestCa.NewRootName();
        proxy.CertificateManager.CreateRootCertificate(false);
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false));
        proxy.Start();
        try
        {
            Assert.AreEqual(0, proxy.AdmittedClientConnectionCount);
        }
        finally
        {
            proxy.Stop();
        }
    }
}
#pragma warning restore TWP001
