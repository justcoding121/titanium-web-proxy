#pragma warning disable TWP001
using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat CertificateManager + ProxyServer lifecycle edges still uncovered on develop.
/// </summary>
[TestClass]
public class CertProxyTcpFactoryFatGapTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };

    [TestMethod]
    [Timeout(60_000)]
    public async Task CreateCertificate_TwoHosts_DistinctThumbprints()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        using var a = await mgr.CreateServerCertificate("tcp-factory-a.test");
        using var b = await mgr.CreateServerCertificate("tcp-factory-b.test");
        Assert.IsNotNull(a);
        Assert.IsNotNull(b);
        Assert.AreNotEqual(a!.Thumbprint, b!.Thumbprint, StringComparer.OrdinalIgnoreCase);
    }

    [TestMethod]
    [Timeout(30_000)]
    public void RootCertificate_TrustQueries_DoNotThrow()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        Assert.IsFalse(mgr.IsRootCertificateUserTrusted());
        Assert.IsFalse(mgr.IsRootCertificateMachineTrusted());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CreateCertificate_WildcardHost_IssuesLeaf()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("*.wildcard-gap.test");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey);
    }

    [TestMethod]
    public void ProxyServer_DisposeWithoutStart_IsSafe()
    {
        using var proxy = new ProxyServer(false, false, false);
        Assert.IsFalse(proxy.ProxyRunning);
        proxy.Dispose();
    }

    [TestMethod]
    public void ProxyServer_AddRemoveEndPoint_BeforeStart()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        proxy.AddEndPoint(ep);
        Assert.AreEqual(1, proxy.ProxyEndPoints.Count);
        proxy.RemoveEndPoint(ep);
        Assert.AreEqual(0, proxy.ProxyEndPoints.Count);
    }

    [TestMethod]
    public void ProxyServer_EnableHttp2Http3Flags_RoundTrip()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.EnableHttp2 = true;
        proxy.EnableHttp3 = true;
        proxy.EnableHttpInterception = true;
        Assert.IsTrue(proxy.EnableHttp2 && proxy.EnableHttp3 && proxy.EnableHttpInterception);
        proxy.EnableHttp2 = false;
        proxy.EnableHttp3 = false;
        Assert.IsFalse(proxy.EnableHttp2 || proxy.EnableHttp3);
    }

    [TestMethod]
    public void SetThreadPoolMinThread_MultipleCalls_AreSafe()
    {
        var method = typeof(ProxyServer).GetMethod("SetThreadPoolMinThread", PrivateStatic)!;
        ThreadPool.GetMinThreads(out _, out var ioBefore);
        method.Invoke(null, [2]);
        method.Invoke(null, [Environment.ProcessorCount * 2]);
        method.Invoke(null, [1]);
        ThreadPool.GetMinThreads(out var workersAfter, out var ioAfter);
        Assert.IsTrue(workersAfter >= 1);
        Assert.IsTrue(ioAfter >= ioBefore);
    }

    [TestMethod]
    public void CloseAcceptedSocketWithoutResponse_DrainsBytes()
    {
        var method = typeof(ProxyServer).GetMethod("CloseAcceptedSocketWithoutResponse", PrivateStatic)!;
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(IPAddress.Loopback, port);
        var accepted = listener.Accept();
        _ = client.Send(new byte[] { 9, 8, 7, 6, 5 });
        method.Invoke(null, [accepted]);
        Assert.ThrowsExactly<ObjectDisposedException>(() => accepted.Send(new byte[] { 1 }));
    }

    [TestMethod]
    [Timeout(15_000)]
    public void ProxyServer_StartStop_LoopbackExplicit_Succeeds()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = IsolatedTestCa.NewRootName();
        proxy.CertificateManager.CreateRootCertificate(false);
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false));
        proxy.Start();
        Assert.IsTrue(proxy.ProxyRunning);
        Assert.IsTrue(proxy.ProxyEndPoints[0].Port > 0);
        proxy.Stop();
        Assert.IsFalse(proxy.ProxyRunning);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task CreateCertificate_BouncyCastleFast_IssuesUsableLeaf()
    {
        using var mgr = NewMgr();
        mgr.CertificateEngine = CertificateEngine.BouncyCastleFast;
        mgr.ApplyFastColdStartLeafSettings();
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("fast-gap.test");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey);
    }
}
#pragma warning restore TWP001
