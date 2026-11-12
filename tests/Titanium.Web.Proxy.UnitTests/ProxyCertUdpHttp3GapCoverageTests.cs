using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Udp;
using Titanium.Web.Proxy.UnitTests.Udp;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Additional CertificateManager / ProxyServer / UdpRelay / Http3OriginBridge edges that still
///     show as fat Sonar gaps: ECDSA leaf minting, idle-body replay buffering, admission helpers,
///     PrefersHttp2Origin, and UDP drain-after-idle.
/// </summary>
[TestClass]
public class ProxyCertUdpHttp3GapCoverageTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    private static CertificateManager NewCertManager()
    {
        return new CertificateManager(
            IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
    }

    [TestMethod]
    [Timeout(90_000)]
    public void CreateCertificate_EcdsaP256_FastEngine_IssuesLeafWithPrivateKey()
    {
        // Bug class: BouncyCastleFast + ECDSA leaves are minted without a usable private key,
        // so MITM TLS handshakes fail after ApplyFastColdStartLeafSettings.
        using var mgr = NewCertManager();
        mgr.ApplyFastColdStartLeafSettings();
        mgr.CreateRootCertificate(persistToFile: false);

        using var leaf = mgr.CreateCertificate("ecdsa-fast.example", false);
        Assert.IsNotNull(leaf);
        Assert.IsTrue(leaf!.HasPrivateKey, "ECDSA fast leaf must carry a private key for MITM.");
        Assert.AreEqual(CertificateKeyAlgorithm.EcdsaP256, mgr.LeafCertificateKeyAlgorithm);
        Assert.AreEqual(CertificateEngine.BouncyCastleFast, mgr.CertificateEngine);
    }

    [TestMethod]
    [Timeout(60_000)]
    public void CreateServerCertificate_DistinctHosts_ReturnDistinctLeaves()
    {
        // Bug class: host cache key collisions return the wrong leaf across unrelated hosts.
        using var mgr = NewCertManager();
        mgr.CreateRootCertificate(persistToFile: false);

        using var a = mgr.CreateServerCertificate("gap-a.example").GetAwaiter().GetResult();
        using var b = mgr.CreateServerCertificate("gap-b.example").GetAwaiter().GetResult();
        Assert.IsNotNull(a);
        Assert.IsNotNull(b);
        Assert.AreNotEqual(a!.Thumbprint, b!.Thumbprint,
            "Distinct hosts must not share a cached leaf thumbprint.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public void CertificateManager_PropertyEdges_RoundTripWithoutSideEffects()
    {
        using var mgr = NewCertManager();
        mgr.CertificateValidDays = 30;
        mgr.CertificateGraceDays = 1;
        mgr.CertificateCacheTimeOutMinutes = 15;
        mgr.OverwritePfxFile = false;
        mgr.StorageFlag = X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet;
        mgr.PfxPassword = "unit-test-only";

        Assert.AreEqual(30, mgr.CertificateValidDays);
        Assert.AreEqual(1, mgr.CertificateGraceDays);
        Assert.AreEqual(15, mgr.CertificateCacheTimeOutMinutes);
        Assert.IsFalse(mgr.OverwritePfxFile);
        Assert.AreEqual("unit-test-only", mgr.PfxPassword);
        Assert.IsTrue(mgr.StorageFlag.HasFlag(X509KeyStorageFlags.EphemeralKeySet));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ShouldBufferBodyForReplay_True_WhenIdleKeepAlivePost()
    {
        // Bug class: keep-alive replay buffering never arms for idle HTTP/1.1 POSTs, so a 502/503
        // retry loses the body and cannot replay.
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        using var serverConn = MakeServerConnection(proxy);
        session.HttpClient.SetConnection(serverConn);
        // First MarkExchangeStarted records the exchange (returns false = was not previously used).
        Assert.IsFalse(serverConn.MarkExchangeStarted());
        Assert.IsTrue(serverConn.HasServedExchange);

        session.HttpClient.Request.HttpVersion = HttpHeader.Version11;
        session.HttpClient.Request.Method = "POST";
        session.HttpClient.Request.ContentLength = 16;
        session.HttpClient.Request.IsChunked = false;
        session.HttpClient.Request.IsBodyRead = false;

        await Task.Delay(TimeSpan.FromMilliseconds(1100));

        var method = typeof(ProxyServer).GetMethod("ShouldBufferBodyForReplay", PrivateStatic)!;
        Assert.IsTrue((bool)method.Invoke(null, [session])!,
            "Idle HTTP/1.1 POST with Content-Length must buffer for keep-alive replay.");
    }

    [TestMethod]
    [Timeout(10_000)]
    public void IsAddressAlreadyInUse_DetectsWsaEaddrInUseAndNested()
    {
        var method = typeof(ProxyServer).GetMethod("IsAddressAlreadyInUse", PrivateStatic)!;
        Assert.IsTrue((bool)method.Invoke(null,
            [new SocketException((int)SocketError.AddressAlreadyInUse)])!);
        Assert.IsTrue((bool)method.Invoke(null,
            [new AggregateException(new SocketException(10048))])!);
        Assert.IsFalse((bool)method.Invoke(null,
            [new SocketException((int)SocketError.ConnectionRefused)])!);
        Assert.IsFalse((bool)method.Invoke(null, [new InvalidOperationException("no")])!);
    }

    [TestMethod]
    [Timeout(10_000)]
    public void CloseAcceptedSocketWithoutResponse_DrainsAndDisposes()
    {
        // Bug class: rejected accepts after Stop leave sockets in TIME_WAIT with unread bytes,
        // causing RST storms under load.
        var method = typeof(ProxyServer).GetMethod("CloseAcceptedSocketWithoutResponse", PrivateStatic)!;
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(IPAddress.Loopback, port);
        var accepted = listener.Accept();
        _ = client.Send(new byte[] { 1, 2, 3, 4 });

        method.Invoke(null, [accepted]);
        Assert.ThrowsExactly<ObjectDisposedException>(() => accepted.Send(new byte[] { 9 }));
    }

    [TestMethod]
    [Timeout(10_000)]
    public void PrefersHttp2Origin_RespectsExplicitPolicyAndClientVersion()
    {
        var method = typeof(Http3OriginBridge).GetMethod("PrefersHttp2Origin", PrivateStatic)!;
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);

        session.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
        session.HttpClient.Request.HttpVersion = HttpHeader.Version11;
        Assert.IsTrue((bool)method.Invoke(null, [session])!);

        session.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
        session.HttpClient.Request.HttpVersion = HttpHeader.Version20;
        Assert.IsFalse((bool)method.Invoke(null, [session])!,
            "Explicit HTTP/1.1 policy must not prefer H2 origin.");

        session.UpstreamHttpProtocol = UpstreamHttpProtocol.Auto;
        session.HttpClient.Request.HttpVersion = HttpHeader.Version20;
        Assert.IsTrue((bool)method.Invoke(null, [session])!);

        session.HttpClient.Request.HttpVersion = HttpHeader.Version11;
        Assert.IsFalse((bool)method.Invoke(null, [session])!);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task UdpRelayListener_BeginDrain_ThenIdleDrainCompletes()
    {
        // Bug class: BeginDrain leaves IsDraining stuck true forever when no sessions exist.
        using var backend = new UdpBackend();
        var options = new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            ForwardHost = "127.0.0.1",
            ForwardPort = backend.Port,
        };
        await using var listener = new UdpRelayListener(options);
        listener.Start();
        Assert.IsFalse(listener.IsDraining);
        listener.BeginDrain();
        Assert.IsTrue(listener.IsDraining);
        Assert.IsTrue(await listener.DrainAsync(TimeSpan.FromSeconds(2)),
            "Idle relay must drain immediately after BeginDrain.");
    }

    [TestMethod]
    [Timeout(10_000)]
    public void PrepareRequestHeaders_EmptyAcceptEncoding_CollapsesToIdentity()
    {
        // Bug class: empty Accept-Encoding is left empty and response compression assumes
        // the client listed encodings it never did.
        var headers = new HeaderCollection();
        headers.AddHeader(KnownHeaders.AcceptEncoding, "");
        typeof(ProxyServer).GetMethod("PrepareRequestHeaders", PrivateStatic)!
            .Invoke(null, [headers]);
        Assert.AreEqual("identity", headers.GetHeaderValueOrNull(KnownHeaders.AcceptEncoding));
    }

    [TestMethod]
    [Timeout(10_000)]
    public void PrepareRequestHeaders_AllZeroWeight_CollapsesToIdentityOnly()
    {
        // Bug class: q=0-only Accept-Encoding still advertises gzip/br to response compression.
        var headers = new HeaderCollection();
        headers.AddHeader(KnownHeaders.AcceptEncoding, "gzip;q=0, br;q=0.0");
        typeof(ProxyServer).GetMethod("PrepareRequestHeaders", PrivateStatic)!
            .Invoke(null, [headers]);
        Assert.AreEqual("identity", headers.GetHeaderValueOrNull(KnownHeaders.AcceptEncoding),
            "All-zero Accept-Encoding must collapse to identity only.");
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
