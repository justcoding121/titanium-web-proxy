#pragma warning disable TWP001
using System;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat CertificateManager + ProxyServer property / lifecycle edges still soft on develop:
///     valid-days, cache timeout, EnsureRoot, ClearRoot, decrypt-bypass knobs, timeouts,
///     connection-pool flags, and multi-endpoint start/stop.
/// </summary>
[TestClass]
public class CertProxyServerPropertyFatGapTests
{
    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };

    [TestMethod]
    [Timeout(60_000)]
    public async Task CertificateValidDays_AndGraceDays_AffectIssuedLeaf()
    {
        using var mgr = NewMgr();
        mgr.CertificateValidDays = 30;
        mgr.CertificateGraceDays = 1;
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("valid-days-gap.test");
        Assert.IsNotNull(leaf);
        var span = leaf!.NotAfter - leaf.NotBefore;
        Assert.IsTrue(span.TotalDays is >= 28 and <= 40, $"Unexpected validity span {span.TotalDays}");
    }

    [TestMethod]
    public void CertificateCacheTimeout_AndSaveFake_RoundTrip()
    {
        using var mgr = NewMgr();
        mgr.CertificateCacheTimeOutMinutes = 15;
        mgr.SaveFakeCertificates = false;
        mgr.OverwritePfxFile = false;
        mgr.PfxPassword = "gap-pw";
        Assert.AreEqual(15, mgr.CertificateCacheTimeOutMinutes);
        Assert.IsFalse(mgr.SaveFakeCertificates);
        Assert.IsFalse(mgr.OverwritePfxFile);
        Assert.AreEqual("gap-pw", mgr.PfxPassword);
    }

    [TestMethod]
    [Timeout(30_000)]
    public void EnsureRootCertificate_CreatesRoot_ClearRoot_NullsIt()
    {
        using var mgr = NewMgr();
        mgr.EnsureRootCertificate();
        Assert.IsNotNull(mgr.RootCertificate);
        mgr.ClearRootCertificate();
        Assert.IsNull(mgr.RootCertificate);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task IntermediateCertificates_EmptyCollection_StillIssuesLeaf()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        mgr.IntermediateCertificates = new X509Certificate2Collection();
        using var leaf = await mgr.CreateServerCertificate("intermediate-gap.test");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task DisableWildCard_IssuesExactHostLeaf()
    {
        using var mgr = NewMgr();
        mgr.DisableWildCardCertificates = true;
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("exact.host.gap.test");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.Subject.Contains("exact.host.gap.test", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ProxyServer_TimeoutAndPoolProperties_RoundTrip()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.ConnectionTimeOutSeconds = 12;
        proxy.ConnectTimeOutSeconds = 7;
        proxy.ClientHeaderTimeoutSeconds = 9;
        proxy.ResponseHeaderTimeoutSeconds = 11;
        proxy.IdleReadTimeoutSeconds = 13;
        proxy.IdleWriteTimeoutSeconds = 14;
        proxy.RequestTimeoutSeconds = 17;
        proxy.TcpTimeWaitSeconds = 2;
        proxy.ListenerBackLog = 128;
        proxy.NetworkFailureRetryAttempts = 3;
        proxy.MaxBufferedBodyBytes = 1024 * 1024;
        proxy.MaxDecodedHeaderListBytes = 32 * 1024;
        proxy.EnableConnectionPool = false;
        proxy.EnableTcpServerConnectionPrefetch = false;
        proxy.EnableTcpKeepAlive = false;
        proxy.NoDelay = false;
        proxy.ReuseSocket = false;
        proxy.Enable100ContinueBehaviour = true;
        proxy.CompatibilityMode100Continue = true;
        proxy.EnableDecryptFailureBypass = true;
        proxy.DecryptFailureBypassThreshold = 4;
        proxy.DecryptFailureBypassMaxEntries = 32;
        proxy.DecryptFailureBypassTtl = TimeSpan.FromMinutes(2);
        proxy.BlockPrivateNetworkDestinations = true;
        proxy.BlockLoopbackDestinations = false;
        proxy.ProxyAuthenticationRealm = "GapRealm";
        proxy.ViaHeaderPseudonym = "gap-proxy";
        proxy.RequestIdHeader = "x-request-id";
        proxy.TracingEnabled = true;
        proxy.PropagateTraceContext = true;
        proxy.EnableRequestTimingCapture = true;
        proxy.IgnoreServerCertificateErrors = true;
        proxy.ForwardToUpstreamGateway = true;
        proxy.EnableWinAuth = false;
        proxy.EnableRfc8441 = true;
        proxy.EnableConnectTcp = true;
        proxy.EnableWebTransport = false;
        proxy.TryEnableHttp3IfSupported();
        _ = proxy.SetHttp3Enabled(false);

        Assert.AreEqual(12, proxy.ConnectionTimeOutSeconds);
        Assert.AreEqual(7, proxy.ConnectTimeOutSeconds);
        Assert.AreEqual(9, proxy.ClientHeaderTimeoutSeconds);
        Assert.AreEqual(3, proxy.NetworkFailureRetryAttempts);
        Assert.IsFalse(proxy.EnableConnectionPool);
        Assert.IsTrue(proxy.Enable100ContinueBehaviour);
        Assert.AreEqual("GapRealm", proxy.ProxyAuthenticationRealm);
        Assert.AreEqual("gap-proxy", proxy.ViaHeaderPseudonym);
        Assert.IsTrue(proxy.EnableRfc8441);
        Assert.AreEqual(0, proxy.ClientConnectionCount);
        Assert.AreEqual(0, proxy.ServerConnectionCount);
        Assert.AreEqual(0, proxy.Http3ClientConnectionCount);
        Assert.AreEqual(0, proxy.AdmittedClientConnectionCount);
    }

    [TestMethod]
    public void ProxyServer_DecryptFailureBypass_CacheApis_AreSafe()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.EnableDecryptFailureBypass = true;
        Assert.IsFalse(proxy.ShouldBypassDecryptForLearnedHost("missing.example"));
        Assert.IsFalse(proxy.RemoveDecryptFailureBypass("missing.example"));
        _ = proxy.ForceDecryptFailureBypass("learned.example");
        _ = proxy.RemoveDecryptFailureBypass("learned.example");
    }

    [TestMethod]
    [Timeout(20_000)]
    public void ProxyServer_TwoEndpoints_StartStop_Succeeds()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = IsolatedTestCa.NewRootName();
        proxy.CertificateManager.CreateRootCertificate(false);
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false));
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false));
        proxy.Start();
        Assert.IsTrue(proxy.ProxyRunning);
        Assert.AreEqual(2, proxy.ProxyEndPoints.Count);
        Assert.IsTrue(proxy.ProxyEndPoints[0].Port > 0);
        Assert.IsTrue(proxy.ProxyEndPoints[1].Port > 0);
        Assert.AreNotEqual(proxy.ProxyEndPoints[0].Port, proxy.ProxyEndPoints[1].Port);
        proxy.Stop();
        Assert.IsFalse(proxy.ProxyRunning);
    }

    [TestMethod]
    public void ProxyServer_MaxConcurrentClientConnections_RoundTrip()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.MaxConcurrentClientConnections = 42;
        Assert.AreEqual(42, proxy.MaxConcurrentClientConnections);
        proxy.MaxConcurrentClientConnections = null;
        Assert.IsNull(proxy.MaxConcurrentClientConnections);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task CreateCertificate_SameHostTwice_ReturnsCachedOrDistinctUsableLeaf()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        using var a = await mgr.CreateServerCertificate("cache-host.gap.test");
        using var b = await mgr.CreateServerCertificate("cache-host.gap.test");
        Assert.IsNotNull(a);
        Assert.IsNotNull(b);
        Assert.IsTrue(a!.HasPrivateKey && b!.HasPrivateKey);
    }
}
#pragma warning restore TWP001
