using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Certificate;
using Titanium.Web.Proxy.Udp;
using Titanium.Web.Proxy.UnitTests.Udp;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Additional CertificateManager + UdpRelayListener edges for Sonar line coverage:
///     IPv6 SAN leaves, double-start, disposed Start, and options validation bounds.
/// </summary>
[TestClass]
public class CertificateManagerAndUdpEdgeTests
{
    [TestMethod]
    [Timeout(60_000)]
    public void CreateCertificate_Ipv6Literal_PutsIpAddressSan()
    {
        // Bug class: IPv6 literal hosts get a DNS SAN (or none) instead of an iPAddress SAN,
        // so browsers reject the MITM leaf for https://[::1]/.
        using var mgr = new CertificateManager(
            IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        mgr.CreateRootCertificate(false);

        using var cert = mgr.CreateCertificate("::1", false);
        Assert.IsNotNull(cert);
        var san = new X509SubjectAlternativeNameExtension(cert!.Extensions["2.5.29.17"]!.RawData, false);
        var ips = san.EnumerateIPAddresses().Select(ip => ip.ToString()).ToArray();
        Assert.IsTrue(ips.Any(ip => IPAddress.Parse(ip).Equals(IPAddress.IPv6Loopback)),
            $"Expected ::1 iPAddress SAN; got [{string.Join(",", ips)}]");
    }

    [TestMethod]
    [Timeout(60_000)]
    public void CreateServerCertificate_Ipv4Literal_ReturnsLeafWithIpSan()
    {
        using var mgr = new CertificateManager(
            IsolatedTestCa.NewRootName(), IsolatedTestCa.Issuer, false, false, false, NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle,
        };
        mgr.CreateRootCertificate(false);

        using var leaf = mgr.CreateServerCertificate("192.0.2.10").GetAwaiter().GetResult();
        Assert.IsNotNull(leaf);
        var san = new X509SubjectAlternativeNameExtension(leaf!.Extensions["2.5.29.17"]!.RawData, false);
        var ips = san.EnumerateIPAddresses().Select(ip => ip.ToString()).ToArray();
        CollectionAssert.Contains(ips, "192.0.2.10");
    }

    [TestMethod]
    [Timeout(15_000)]
    public void UdpRelayListener_StartTwice_Throws()
    {
        // Bug class: a second Start silently rebinds or leaks the first socket.
        using var backend = new UdpBackend();
        var options = new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            ForwardHost = "127.0.0.1",
            ForwardPort = backend.Port,
        };
        using var listener = new UdpRelayListener(options);
        listener.Start();
        Assert.ThrowsExactly<InvalidOperationException>(() => listener.Start());
    }

    [TestMethod]
    [Timeout(15_000)]
    public void UdpRelayListener_StartAfterDispose_Throws()
    {
        using var backend = new UdpBackend();
        var options = new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            ForwardHost = "127.0.0.1",
            ForwardPort = backend.Port,
        };
        var listener = new UdpRelayListener(options);
        listener.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => listener.Start());
    }

    [TestMethod]
    [Timeout(10_000)]
    public void UdpRelayOptions_IdleTimeoutOutOfRange_Throws()
    {
        var options = new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            ForwardHost = "127.0.0.1",
            ForwardPort = 9,
            IdleTimeout = TimeSpan.FromMilliseconds(500),
        };
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => options.Validate());

        options.IdleTimeout = TimeSpan.FromDays(2);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [TestMethod]
    [Timeout(10_000)]
    public void UdpRelayOptions_InvalidBindPort_Throws()
    {
        var options = new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 70000,
            ForwardHost = "127.0.0.1",
            ForwardPort = 9,
        };
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [TestMethod]
    [Timeout(10_000)]
    public void BindToLoopbackWhenPeerIsLoopback_IgnoresNonLoopbackPeer()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        UdpRelayListener.BindToLoopbackWhenPeerIsLoopback(socket, new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53));
        Assert.IsFalse(socket.IsBound, "Non-loopback peers must leave the socket unbound.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public void OnBeforeUpStreamConnectRequest_InvokesHandler()
    {
        using var proxy = new ProxyServer(false, false, false);
        var seen = false;
        proxy.BeforeUpStreamConnectRequest += (_, req) =>
        {
            seen = req.Authority.Equals((ByteString)"upstream.example:443");
            return System.Threading.Tasks.Task.CompletedTask;
        };

        var connect = new Titanium.Web.Proxy.Http.ConnectRequest((ByteString)"upstream.example:443");
        proxy.OnBeforeUpStreamConnectRequest(connect).GetAwaiter().GetResult();
        Assert.IsTrue(seen, "BeforeUpStreamConnectRequest must fire for CONNECT preparation.");
    }

    [TestMethod]
    [Timeout(10_000)]
    public void OnBeforeUpStreamConnectRequest_WithoutHandler_IsNoOp()
    {
        using var proxy = new ProxyServer(false, false, false);
        var connect = new Titanium.Web.Proxy.Http.ConnectRequest((ByteString)"noop.example:443");
        proxy.OnBeforeUpStreamConnectRequest(connect).GetAwaiter().GetResult();
    }
}
