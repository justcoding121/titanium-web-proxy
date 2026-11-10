using System;
using System.IO;
using System.Linq;
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
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     CertificateManager bulk leaf minting + ProxyServer lifecycle helpers still showing as
///     develop uncovered lines: multi-host ECDSA cache, Pfx path round-trip, DescribeEndPointKind,
///     and SetThreadPoolMinThread.
/// </summary>
[TestClass]
public class CertificateManagerBulkAndProxyLifecycleGapTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    private static CertificateManager NewMgr() =>
        new(IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };

    [TestMethod]
    [Timeout(120_000)]
    public async Task CreateServerCertificate_ManyHosts_AllDistinctAndUsable()
    {
        // Bug class: cache collisions reuse the wrong leaf across hosts under burst minting.
        using var mgr = NewMgr();
        mgr.ApplyFastColdStartLeafSettings();
        mgr.CreateRootCertificate(persistToFile: false);

        var hosts = Enumerable.Range(0, 12).Select(i => $"bulk-{i}.example").ToArray();
        var tasks = hosts.Select(h => mgr.CreateServerCertificate(h)).ToArray();
        var certs = await Task.WhenAll(tasks);
        try
        {
            Assert.IsTrue(certs.All(c => c is not null && c.HasPrivateKey));
            var thumbs = certs.Select(c => c!.Thumbprint).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Assert.AreEqual(hosts.Length, thumbs.Length,
                "Each host must mint a distinct leaf under concurrent CreateServerCertificate.");
        }
        finally
        {
            foreach (var c in certs) c?.Dispose();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public void CreateRootCertificate_PersistToTempPfx_LoadRootCertificate_RoundTrips()
    {
        // Bug class: LoadRootCertificate ignores PfxFilePath / password and leaves RootCertificate null.
        var dir = Path.Combine(Path.GetTempPath(), "twp-cert-gap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var pfx = Path.Combine(dir, "root.pfx");
        try
        {
            using var mgr = NewMgr();
            mgr.PfxFilePath = pfx;
            mgr.PfxPassword = "gap-test";
            mgr.OverwritePfxFile = true;
            Assert.IsTrue(mgr.CreateRootCertificate(persistToFile: true));
            Assert.IsTrue(File.Exists(pfx));
            var thumb = mgr.RootCertificate!.Thumbprint;

            using var mgr2 = NewMgr();
            mgr2.PfxFilePath = pfx;
            mgr2.PfxPassword = "gap-test";
            var loaded = mgr2.LoadRootCertificate();
            Assert.IsNotNull(loaded);
            Assert.AreEqual(thumb, loaded!.Thumbprint, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public void DescribeEndPointKind_CoversExplicitTransparentSocks()
    {
        var method = typeof(ProxyServer).GetMethod("DescribeEndPointKind", PrivateStatic)!;
        Assert.AreEqual("explicit", (string)method.Invoke(null,
            [new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false)])!);
        Assert.AreEqual("transparent", (string)method.Invoke(null,
            [new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)])!);
        Assert.AreEqual("socks", (string)method.Invoke(null,
            [new SocksProxyEndPoint(IPAddress.Loopback, 0, false)])!);
    }

    [TestMethod]
    [Timeout(15_000)]
    public void SetThreadPoolMinThread_IsSafeForSmallValues()
    {
        var method = typeof(ProxyServer).GetMethod("SetThreadPoolMinThread", PrivateStatic)!;
        method.Invoke(null, [1]);
        method.Invoke(null, [Environment.ProcessorCount]);
    }

    [TestMethod]
    [Timeout(15_000)]
    public void OnException_WithNullClientStream_DoesNotThrow()
    {
        using var proxy = new ProxyServer(false, false, false);
        var method = typeof(ProxyServer).GetMethod("OnException",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(proxy, [null, new InvalidOperationException("unit")]);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task ShouldBufferBodyForReplay_False_WhenHttp2OrUpgrade()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        using var serverConn = MakeServerConnection(proxy);
        session.HttpClient.SetConnection(serverConn);
        Assert.IsFalse(serverConn.MarkExchangeStarted());
        await Task.Delay(1100);

        session.HttpClient.Request.HttpVersion = HttpHeader.Version20;
        session.HttpClient.Request.Method = "POST";
        session.HttpClient.Request.ContentLength = 8;
        var method = typeof(ProxyServer).GetMethod("ShouldBufferBodyForReplay", PrivateStatic)!;
        Assert.IsFalse((bool)method.Invoke(null, [session])!, "HTTP/2 must not buffer for H1 keep-alive replay.");

        session.HttpClient.Request.HttpVersion = HttpHeader.Version11;
        session.HttpClient.Request.Headers.AddHeader(KnownHeaders.Upgrade, "websocket");
        Assert.IsFalse((bool)method.Invoke(null, [session])!, "WebSocket upgrade must not buffer for replay.");
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
