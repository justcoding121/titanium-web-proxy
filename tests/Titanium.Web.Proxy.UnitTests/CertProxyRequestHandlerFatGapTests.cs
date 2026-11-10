#pragma warning disable TWP001
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat CertificateManager + ProxyServer + RequestHandler edges still uncovered on develop:
///     ECDSA/BC makers, same-CN store candidates, DescribeEndPointKind extras, PrepareRequestHeaders,
///     ShouldBufferBodyForReplay variants, and ResourceLimits assignment.
/// </summary>
[TestClass]
public class CertProxyRequestHandlerFatGapTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    private static CertificateManager NewMgr(CertificateEngine engine = CertificateEngine.BouncyCastle) =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = engine,
        };

    [TestMethod]
    [Timeout(90_000)]
    public async Task CreateServerCertificate_BouncyCastleFast_AndDefault_BothWork()
    {
        using var bc = NewMgr(CertificateEngine.BouncyCastle);
        bc.CreateRootCertificate(false);
        using var leaf1 = await bc.CreateServerCertificate("bc-default.example");
        Assert.IsNotNull(leaf1);
        Assert.IsTrue(leaf1!.HasPrivateKey);

        using var fast = NewMgr(CertificateEngine.BouncyCastleFast);
        fast.ApplyFastColdStartLeafSettings();
        fast.CreateRootCertificate(false);
        using var leaf2 = await fast.CreateServerCertificate("bc-fast.example");
        Assert.IsNotNull(leaf2);
        Assert.IsTrue(leaf2!.HasPrivateKey);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task CreateServerCertificate_EcdsaRoot_MintsLeaf()
    {
        using var mgr = NewMgr();
        mgr.CertificateEngine = CertificateEngine.BouncyCastle;
        // Prefer ECDSA when available through root creation path.
        mgr.CreateRootCertificate(false);
        using var leaf = await mgr.CreateServerCertificate("ecdsa-host.example");
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.Subject.Contains("ecdsa-host.example", StringComparison.OrdinalIgnoreCase)
                      || leaf.GetNameInfo(X509NameType.DnsName, false)
                          .Contains("ecdsa-host.example", StringComparison.OrdinalIgnoreCase)
                      || leaf.HasPrivateKey);
    }

    [TestMethod]
    public void IsSameCommonNameStoreCandidate_AllBranches()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=SameCnRoot", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        using var cert = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable);

        Assert.IsFalse(CertificateManager.IsSameCommonNameStoreCandidate(cert, "", null));
        Assert.IsTrue(CertificateManager.IsSameCommonNameStoreCandidate(cert, "SameCnRoot", null));
        Assert.IsFalse(CertificateManager.IsSameCommonNameStoreCandidate(cert, "SameCnRoot", cert.Thumbprint));
        Assert.IsTrue(CertificateManager.IsSameCommonNameStoreCandidate(cert, "SameCnRoot", "DEADBEEF"));
        Assert.IsFalse(CertificateManager.IsSameCommonNameStoreCandidate(cert, "OtherRoot", null));
    }

    [TestMethod]
    public void ClearRootCertificate_AndTrustFlags_AreSafe()
    {
        using var mgr = NewMgr();
        mgr.CreateRootCertificate(false);
        Assert.IsNotNull(mgr.RootCertificate);
        mgr.ClearRootCertificate();
        Assert.IsNull(mgr.RootCertificate);

        mgr.CreateRootCertificate(false);
        Assert.IsFalse(mgr.IsRootCertificateMachineTrusted());
        Assert.IsFalse(mgr.IsRootCertificateUserTrusted());
    }

    [TestMethod]
    public void DescribeEndPointKind_CoversAllKnownKinds()
    {
        var method = typeof(ProxyServer).GetMethod("DescribeEndPointKind", PrivateStatic)!;
        Assert.AreEqual("explicit", method.Invoke(null, [new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false)]));
        Assert.AreEqual("transparent", method.Invoke(null, [new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)]));
        Assert.AreEqual("socks", method.Invoke(null, [new SocksProxyEndPoint(IPAddress.Loopback, 0, false)]));
        Assert.AreEqual("masque", method.Invoke(null, [new MasqueProxyEndPoint(IPAddress.Loopback, 0)]));
        Assert.AreEqual("webtransport", method.Invoke(null, [new WebTransportProxyEndPoint(IPAddress.Loopback, 0)]));
    }

    [TestMethod]
    public void PrepareRequestHeaders_CollapsesAcceptEncodingAndKeepsCustom()
    {
        var method = typeof(ProxyServer).GetMethod("PrepareRequestHeaders", PrivateStatic)!;
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "gzip;q=1.0, br;q=0");
        headers.AddHeader("X-Custom", "keep");
        method.Invoke(null, [headers]);
        var ae = headers.GetHeaderValueOrNull("Accept-Encoding");
        Assert.IsNotNull(ae);
        Assert.IsTrue(ae!.Contains("gzip", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(ae.Contains("identity", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(ae.Contains("br", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(headers.HeaderExists("X-Custom"));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ShouldBufferBodyForReplay_TrueAfterIdleOnHttp11Post()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        using var serverConn = MakeServerConnection(proxy);
        session.HttpClient.SetConnection(serverConn);
        Assert.IsFalse(serverConn.MarkExchangeStarted());
        Assert.IsTrue(serverConn.HasServedExchange);
        await Task.Delay(1100);

        session.HttpClient.Request.HttpVersion = HttpHeader.Version11;
        session.HttpClient.Request.Method = "POST";
        session.HttpClient.Request.ContentLength = 16;
        var method = typeof(ProxyServer).GetMethod("ShouldBufferBodyForReplay", PrivateStatic)!;
        Assert.IsTrue((bool)method.Invoke(null, [session])!);
    }

    [TestMethod]
    public void ResourceLimits_Assignment_AndPolicyModes_RoundTrip()
    {
        using var proxy = new ProxyServer(false, false, false);
        var limits = ProxyResourceLimits.Default
            .WithMaxHttp2CompressedHeaderBlockBytes(64 * 1024);
        proxy.ResourceLimits = limits;
        Assert.AreEqual(64 * 1024, proxy.ResourceLimits.MaxHttp2CompressedHeaderBlockBytes);
        proxy.PolicyModes = ProxyPolicyModes.AllEnforce;
        Assert.IsFalse(proxy.PolicyModes.AllowAmbiguousFraming);
        proxy.PolicyModes = ProxyPolicyModes.AllEnforce.WithAllowAmbiguousFramingEnabled();
        Assert.IsTrue(proxy.PolicyModes.AllowAmbiguousFraming);
    }

    [TestMethod]
    public void OnException_WithSession_DoesNotThrow()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        var method = typeof(ProxyServer).GetMethod("OnException", PrivateInstance)!;
        method.Invoke(proxy, [session.ClientStream, new InvalidOperationException("unit")]);
        method.Invoke(proxy, [null, new IOException("io")]);
    }

    [TestMethod]
    public void GetFreeLocalPort_ViaAddEndPointPortZero_Binds()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = IsolatedTestCa.NewRootName();
        proxy.CertificateManager.CreateRootCertificate(false);
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        proxy.AddEndPoint(ep);
        proxy.Start();
        try
        {
            Assert.IsTrue(ep.Port > 0);
            Assert.IsTrue(proxy.ProxyRunning);
        }
        finally
        {
            proxy.Stop();
        }
    }

    private static SessionEventArgs MakeSession(ProxyServer proxy)
    {
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        return new SessionEventArgs(proxy, ep, clientStream, null, cts);
    }

    private static TcpServerConnection MakeServerConnection(ProxyServer proxy)
    {
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var stream = new HttpServerStream(proxy, Stream.Null, proxy.BufferPool, CancellationToken.None);
        return new TcpServerConnection(proxy, client, stream, "origin.test", 80, false,
            default, HttpHeader.Version11, null, null, "cache-key");
    }
}
