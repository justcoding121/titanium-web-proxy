using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class TcpConnectionFactoryAuthCoverageTests
{
    private static readonly BindingFlags PrivateStatic =
        BindingFlags.Static | BindingFlags.NonPublic;

    private static bool TryGetChallenge(HeaderCollection headers, out string? scheme, out string? challenge)
    {
        var method = typeof(TcpConnectionFactory).GetMethod("TryGetUpstreamProxyAuthenticationChallenge",
            PrivateStatic)!;
        var args = new object?[] { headers, null, null };
        var ok = (bool)method.Invoke(null, args)!;
        scheme = (string?)args[1];
        challenge = (string?)args[2];
        return ok;
    }

    [TestMethod]
    public void TryGetUpstreamProxyAuthenticationChallenge_PrefersNegotiate()
    {
        var headers = new HeaderCollection();
        headers.AddHeader(KnownHeaders.ProxyAuthenticate, "NTLM TlRMTVNT");
        headers.AddHeader(KnownHeaders.ProxyAuthenticate, "Negotiate YII");
        Assert.IsTrue(TryGetChallenge(headers, out var scheme, out var challenge));
        Assert.AreEqual("Negotiate", scheme);
        Assert.AreEqual("YII", challenge);
    }

    [TestMethod]
    public void TryGetUpstreamProxyAuthenticationChallenge_SchemeOnly_AndIgnoresBasic()
    {
        var headers = new HeaderCollection();
        headers.AddHeader(KnownHeaders.ProxyAuthenticate, "Basic realm=x");
        headers.AddHeader(KnownHeaders.ProxyAuthenticate, "NTLM");
        Assert.IsTrue(TryGetChallenge(headers, out var scheme, out var challenge));
        Assert.AreEqual("NTLM", scheme);
        Assert.IsNull(challenge);
    }

    [TestMethod]
    public void TryGetUpstreamProxyAuthenticationChallenge_NoHeader_ReturnsFalse()
    {
        Assert.IsFalse(TryGetChallenge(new HeaderCollection(), out _, out _));
    }

    [TestMethod]
    public void TryGetUpstreamProxyAuthenticationChallenge_RejectsSchemePrefixWithoutSpace()
    {
        var headers = new HeaderCollection();
        headers.AddHeader(KnownHeaders.ProxyAuthenticate, "NTLMxyz");
        Assert.IsFalse(TryGetChallenge(headers, out _, out _));
    }

    [TestMethod]
    public void CreateUpstreamProxyConnectException_CapturesSnapshotAndOverride()
    {
        var method = typeof(TcpConnectionFactory).GetMethod("CreateUpstreamProxyConnectException",
            PrivateStatic)!;
        var headers = new HeaderCollection();
        headers.AddHeader("Proxy-Authenticate", "NTLM");
        headers.AddHeader("Server", "squid");
        var status = new ResponseStatusInfo
        {
            StatusCode = 407,
            Description = "Proxy Auth Required",
            Version = HttpHeader.Version11
        };

        var ex = (UpstreamProxyConnectException)method.Invoke(null,
            [status, headers, "body-preview", "custom fail"])!;

        Assert.AreEqual(407, ex.StatusCode);
        Assert.AreEqual("Proxy Auth Required", ex.StatusDescription);
        Assert.AreEqual("custom fail", ex.Message);
        Assert.AreEqual("body-preview", ex.BodyPreview);
        Assert.AreEqual("NTLM", ex.Headers["Proxy-Authenticate"]);
        Assert.AreEqual("squid", ex.Headers["Server"]);

        var defaultEx = (UpstreamProxyConnectException)method.Invoke(null,
            [status, headers, null, null])!;
        StringAssert.Contains(defaultEx.Message, "407");
        Assert.IsNull(defaultEx.BodyPreview);
    }

    [TestMethod]
    public async Task DrainUpstreamProxyResponseBody_ChunkedAndContentLength()
    {
        using var proxy = new ProxyServer(false, false, false);
        var drain = typeof(TcpConnectionFactory).GetMethod("DrainUpstreamProxyResponseBody", PrivateStatic)!;
        var drainChunked = typeof(TcpConnectionFactory).GetMethod("DrainChunkedBody", PrivateStatic)!;
        var drainBytes = typeof(TcpConnectionFactory).GetMethod("DrainBytes", PrivateStatic)!;

        await using (var ms = new MemoryStream(Encoding.ASCII.GetBytes(
                         "5;ext\r\nhello\r\n0\r\nX-Trailer: 1\r\n\r\n")))
        {
            var stream = new HttpServerStream(proxy, ms, new DefaultBufferPool(), CancellationToken.None);
            using var preview = new MemoryStream();
            await (Task)drainChunked.Invoke(null, [stream, preview, CancellationToken.None])!;
            Assert.AreEqual("hello", Encoding.UTF8.GetString(preview.ToArray()));
        }

        await using (var ms = new MemoryStream(Encoding.ASCII.GetBytes("not-hex\r\n")))
        {
            var stream = new HttpServerStream(proxy, ms, new DefaultBufferPool(), CancellationToken.None);
            using var preview = new MemoryStream();
            try
            {
                await (Task)drainChunked.Invoke(null, [stream, preview, CancellationToken.None])!;
                Assert.Fail("expected invalid chunk header");
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException : ex;
                Assert.IsInstanceOfType<IOException>(inner);
            }
        }

        await using (var ms = new MemoryStream(Encoding.ASCII.GetBytes("ab")))
        {
            var stream = new HttpServerStream(proxy, ms, new DefaultBufferPool(), CancellationToken.None);
            using var preview = new MemoryStream();
            try
            {
                await (Task)drainBytes.Invoke(null, [stream, 8L, preview, CancellationToken.None])!;
                Assert.Fail("expected premature close");
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException : ex;
                Assert.IsInstanceOfType<IOException>(inner);
            }
        }

        var chunkedHeaders = new HeaderCollection();
        chunkedHeaders.AddHeader(KnownHeaders.TransferEncoding, "chunked");
        await using (var ms = new MemoryStream(Encoding.ASCII.GetBytes("3\r\nxyz\r\n0\r\n\r\n")))
        {
            var stream = new HttpServerStream(proxy, ms, new DefaultBufferPool(), CancellationToken.None);
            var preview = await (Task<string?>)drain.Invoke(null,
                [stream, chunkedHeaders, CancellationToken.None])!;
            Assert.AreEqual("xyz", preview);
        }

        var clHeaders = new HeaderCollection();
        clHeaders.AddHeader(KnownHeaders.ContentLength, "4");
        await using (var ms = new MemoryStream(Encoding.ASCII.GetBytes("body")))
        {
            var stream = new HttpServerStream(proxy, ms, new DefaultBufferPool(), CancellationToken.None);
            var preview = await (Task<string?>)drain.Invoke(null,
                [stream, clHeaders, CancellationToken.None])!;
            Assert.AreEqual("body", preview);
        }

        await using (var ms = new MemoryStream())
        {
            var stream = new HttpServerStream(proxy, ms, new DefaultBufferPool(), CancellationToken.None);
            Assert.IsNull(await (Task<string?>)drain.Invoke(null,
                [stream, new HeaderCollection(), CancellationToken.None])!);
        }
    }

    [TestMethod]
    public void ResolveConnectTarget_AndUpStreamEndPoints_CoverSessionOverrides()
    {
        using var proxy = new ProxyServer(false, false, false)
        {
            UpStreamEndPoint = new IPEndPoint(IPAddress.Loopback, 1),
            UpStreamEndPointIPv4 = new IPEndPoint(IPAddress.Loopback, 2),
            UpStreamEndPointIPv6 = new IPEndPoint(IPAddress.IPv6Loopback, 3),
        };
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        using var session = new SessionEventArgs(proxy, ep, clientStream, null, cts);

        var resolveConnect = typeof(TcpConnectionFactory).GetMethod("ResolveConnectTarget", PrivateStatic)!;
        var none = ((string? Host, int? Port))resolveConnect.Invoke(null, [session])!;
        Assert.IsNull(none.Host);

        session.UpstreamConnectHost = "routed.example";
        session.UpstreamConnectPort = 8443;
        var routed = ((string? Host, int? Port))resolveConnect.Invoke(null, [session])!;
        Assert.AreEqual("routed.example", routed.Host);
        Assert.AreEqual(8443, routed.Port);

        var transparent = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            ForwardHost = "fwd.example",
            ForwardPort = 9443,
        };
        using var transparentSession = new SessionEventArgs(proxy, transparent, clientStream, null,
            new CancellationTokenSource());
        var fwd = ((string? Host, int? Port))resolveConnect.Invoke(null, [transparentSession])!;
        Assert.AreEqual("fwd.example", fwd.Host);
        Assert.AreEqual(9443, fwd.Port);

        var resolveEps = typeof(TcpConnectionFactory).GetMethod("ResolveConfiguredUpStreamEndPoints", PrivateStatic)!;
        var eps = ((IPEndPoint? Generic, IPEndPoint? IPv4, IPEndPoint? IPv6))resolveEps.Invoke(null, [session, proxy])!;
        Assert.AreEqual(1, eps.Generic!.Port);
        Assert.AreEqual(2, eps.IPv4!.Port);
        Assert.AreEqual(3, eps.IPv6!.Port);
    }

    [TestMethod]
    public void AbandonLosingAttempts_DisposesWinningSocketOnCompletedTask()
    {
        var abandon = typeof(TcpConnectionFactory).GetMethod("AbandonLosingAttempts", PrivateStatic)!;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        sock.Connect(listener.LocalEndpoint);
        var accepted = listener.AcceptSocket();
        accepted.Dispose();

        var done = Task.FromResult<(bool Ok, Socket? Socket, IPEndPoint? Bound, Exception? Error, IPAddress Address)>(
            (true, sock, (IPEndPoint)listener.LocalEndpoint, null, IPAddress.Loopback));
        var failed = Task.FromResult<(bool Ok, Socket? Socket, IPEndPoint? Bound, Exception? Error, IPAddress Address)>(
            (false, null, null, new IOException("x"), IPAddress.Loopback));
        abandon.Invoke(null, [new[] { done, failed }]);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = sock.RemoteEndPoint);
        try { sock.Dispose(); } catch { /* already abandoned */ }
        listener.Stop();
    }
}
