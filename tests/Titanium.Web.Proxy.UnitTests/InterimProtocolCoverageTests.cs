#pragma warning disable TWP001
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Grpc;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.WebTransport;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>New-code branches the quality gate measures and the happy-path tests do not enter.</summary>
[TestClass]
public class InterimProtocolCoverageTests
{
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    [TestMethod]
    public async Task ChannelDecompression_CoversSkipTooLargeCorruptAndSuccess()
    {
        using var proxy = new ProxyServer(false, false, false);
        var session = MakeSession(proxy);
        Assert.IsTrue(await Decompress(proxy, session, ChannelOf()));

        proxy.RequestDecompression = new RequestDecompressionConfig { Enabled = true, MaxDecompressedBytes = 1024 };
        session.HttpClient.Request.ExtendedConnectProtocol = "webtransport";
        session.HttpClient.Request.Headers.AddHeader("content-encoding", "gzip");
        Assert.IsTrue(await Decompress(proxy, session, ChannelOf(new byte[] { 1, 2, 3 })));

        session.HttpClient.Request.ExtendedConnectProtocol = null;
        session.HttpClient.Request.Headers.RemoveHeader("content-encoding");
        Assert.IsTrue(await Decompress(proxy, session, ChannelOf(new byte[] { 1 })));

        session.HttpClient.Request.Headers.AddHeader("content-encoding", "gzip");
        var corrupt = MakeSession(proxy);
        corrupt.HttpClient.Request.Headers.AddHeader("content-encoding", "gzip");
        Assert.IsFalse(await Decompress(proxy, corrupt, ChannelOf(new byte[] { 1, 2, 3, 4 })));
        Assert.AreEqual(400, corrupt.HttpClient.Response.StatusCode);

        proxy.RequestDecompression = new RequestDecompressionConfig { Enabled = true, MaxDecompressedBytes = 4 };
        var tooLarge = MakeSession(proxy);
        tooLarge.HttpClient.Request.Headers.AddHeader("content-encoding", "gzip");
        Assert.IsFalse(await Decompress(proxy, tooLarge, ChannelOf(new byte[8])));
        Assert.AreEqual(413, tooLarge.HttpClient.Response.StatusCode);

        proxy.RequestDecompression = new RequestDecompressionConfig
        {
            Enabled = true,
            MaxDecompressedBytes = 1024 * 1024,
            MaxRatio = 1,
        };
        var ratio = MakeSession(proxy);
        ratio.HttpClient.Request.Headers.AddHeader("content-encoding", "gzip");
        Assert.IsFalse(await Decompress(proxy, ratio, ChannelOf(Gzip(new byte[8000]))));
        Assert.AreEqual(413, ratio.HttpClient.Response.StatusCode);

        var plain = Encoding.ASCII.GetBytes("capsule");
        var success = MakeSession(proxy);
        success.HttpClient.Request.Headers.AddHeader("content-encoding", "gzip");
        Assert.IsTrue(await Decompress(proxy, success, ChannelOf(Array.Empty<byte>(), Gzip(plain))));
        CollectionAssert.AreEqual(plain, success.HttpClient.Request.Body);
        Assert.IsTrue(success.HttpClient.Request.IsBodyRead);
        Assert.IsNull(success.HttpClient.Request.Headers.GetFirstHeader("content-encoding"));
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task ConnectUdpAdmission_RejectsBeforeOpeningASocket()
    {
        using var proxy = new ProxyServer(false, false, false);
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        SessionEventArgs Fresh(string? path = null, string? authorization = null)
        {
            var next = MakeSession(proxy, endPoint);
            if (path is not null)
            {
                next.HttpClient.Request.RequestUriString = path;
            }

            if (authorization is not null)
            {
                next.HttpClient.Request.Headers.AddHeader("proxy-authorization", authorization);
            }

            return next;
        }

        var disabled = Fresh("/not-masque");
        Assert.IsNull(await proxy.TryOpenConnectUdpAsync(disabled, CancellationToken.None));
        Assert.AreEqual(501, disabled.HttpClient.Response.StatusCode);

        endPoint.AcceptConnectUdp = true;
        var missing = Fresh();
        Assert.IsNull(await proxy.TryOpenConnectUdpAsync(missing, CancellationToken.None));
        Assert.AreEqual(407, missing.HttpClient.Response.StatusCode);

        Assert.IsNull(await proxy.TryOpenConnectUdpAsync(Fresh(authorization: "Basic !!!"), CancellationToken.None));
        Assert.IsNull(await proxy.TryOpenConnectUdpAsync(Fresh(authorization: Basic("nocolon")), CancellationToken.None));

        endPoint.ConnectUdpOptions.AuthenticateFunc = (_, _) => Task.FromResult(false);
        Assert.IsNull(await proxy.TryOpenConnectUdpAsync(Fresh(authorization: Basic("ada:secret")), CancellationToken.None));

        endPoint.ConnectUdpOptions.AuthenticateFunc = null;
        proxy.ProxyBasicAuthenticateFunc = (_, _, _) => Task.FromResult(false);
        var basic = Fresh(authorization: Basic("ada:secret"));
        Assert.IsNull(await proxy.TryOpenConnectUdpAsync(basic, CancellationToken.None));
        Assert.AreEqual(407, basic.HttpClient.Response.StatusCode);

        endPoint.ConnectUdpOptions.RequireAuth = false;
        var badPath = Fresh("/not-masque");
        Assert.IsNull(await proxy.TryOpenConnectUdpAsync(badPath, CancellationToken.None));
        Assert.AreEqual(400, badPath.HttpClient.Response.StatusCode);

        var blocked = Fresh("/.well-known/masque/udp/127.0.0.1/9/");
        Assert.IsNull(await proxy.TryOpenConnectUdpAsync(blocked, CancellationToken.None));
        Assert.AreEqual(403, blocked.HttpClient.Response.StatusCode);

        endPoint.ConnectUdpOptions.MaxTunnels = 0;
        var capped = Fresh("/.well-known/masque/udp/example.test/443/");
        Assert.IsNull(await proxy.TryOpenConnectUdpAsync(capped, CancellationToken.None));
        Assert.AreEqual(429, capped.HttpClient.Response.StatusCode);

        var replaced = new MasqueOptions { RequireAuth = false, BlockPrivateTargets = true };
        endPoint.ConnectUdpOptions = replaced;
        Assert.IsFalse(endPoint.ConnectUdpOptions.RequireAuth);
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task WebTransportBeforeRequest_RejectsAndReleasesTheAdmitSlot()
    {
        using var proxy = new ProxyServer(false, false, false);
        var endPoint = new WebTransportProxyEndPoint(IPAddress.Loopback, 0);
        endPoint.Options.RequireAuth = true;
        endPoint.Options.OriginHost = "127.0.0.1";
        endPoint.Options.OriginHttps = false;
        var session = Prepared(proxy, endPoint);
        await InvokeBefore(proxy, session, endPoint, default);
        Assert.AreEqual(407, session.HttpClient.Response.StatusCode);

        endPoint.Options.AuthenticateFunc = (user, password) => Task.FromResult(user == "ada" && password == "secret");
        session = Prepared(proxy, endPoint, Basic("ada:nope"));
        await InvokeBefore(proxy, session, endPoint, default);
        Assert.AreEqual(407, session.HttpClient.Response.StatusCode);

        session = Prepared(proxy, endPoint, "Basic !!!");
        await InvokeBefore(proxy, session, endPoint, default);
        Assert.AreEqual(407, session.HttpClient.Response.StatusCode);

        session = Prepared(proxy, endPoint, Basic("nocolon"));
        await InvokeBefore(proxy, session, endPoint, default);
        Assert.AreEqual(407, session.HttpClient.Response.StatusCode);

        endPoint.Options.AuthenticateFunc = null;
        proxy.ProxyBasicAuthenticateFunc = (_, _, _) => Task.FromResult(false);
        session = Prepared(proxy, endPoint, Basic("ada:secret"));
        await InvokeBefore(proxy, session, endPoint, default);
        Assert.AreEqual(407, session.HttpClient.Response.StatusCode);

        endPoint.Options.RequireAuth = false;
        session = Prepared(proxy, endPoint);
        session.HttpClient.Request.Method = "GET";
        await InvokeBefore(proxy, session, endPoint, default);
        Assert.AreEqual(501, session.HttpClient.Response.StatusCode);

        session = Prepared(proxy, endPoint);
        session.HttpClient.Request.Headers.AddHeader("capsule-protocol", "0");
        await InvokeBefore(proxy, session, endPoint, default);
        Assert.AreEqual(400, session.HttpClient.Response.StatusCode);

        endPoint.Options.OriginHost = null;
        session = Prepared(proxy, endPoint);
        await InvokeBefore(proxy, session, endPoint, default);
        Assert.AreEqual(502, session.HttpClient.Response.StatusCode);

        endPoint.Options.OriginHost = "127.0.0.1";
        endPoint.Options.MaxSessions = 0;
        session = Prepared(proxy, endPoint);
        await InvokeBefore(proxy, session, endPoint, default);
        Assert.AreEqual(429, session.HttpClient.Response.StatusCode);

        endPoint.Options.MaxSessions = 4;
        using var cts = new CancellationTokenSource();
        var state = new Http2ConnectionState(1, cts);
        var ctx = new Http2StreamContext(1, state, Stream.Null, CancellationToken.None);
        session = Prepared(proxy, endPoint);
        await InvokeBefore(proxy, session, endPoint, ctx);
        Assert.AreEqual(500, session.HttpClient.Response.StatusCode);
        Assert.IsTrue(endPoint.TryAdmit(IPAddress.Loopback));
        endPoint.ReleaseSession(IPAddress.Loopback);

        state.Streams[1] = new Http2StreamState(1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        ctx = new Http2StreamContext(1, state, Stream.Null, cancelled.Token);
        session = Prepared(proxy, endPoint);
        await InvokeBefore(proxy, session, endPoint, ctx);
        Assert.AreEqual(502, session.HttpClient.Response.StatusCode);
        Assert.IsTrue(endPoint.TryAdmit(IPAddress.Loopback));
        endPoint.ReleaseSession(IPAddress.Loopback);
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task WebTransportHttp11_RejectsAndUpgrades()
    {
        using var proxy = new ProxyServer(false, false, false);
        var endPoint = new WebTransportProxyEndPoint(IPAddress.Loopback, 0);
        endPoint.Options.RequireAuth = false;
        endPoint.Options.OriginHost = null;
        var closed = ClientStream(proxy, "");
        await InvokeHttp11(proxy, endPoint, closed.Client);
        var blank = ClientStream(proxy, "\r\n");
        await InvokeHttp11(proxy, endPoint, blank.Client);

        var rejected = ClientStream(proxy, "POST /app HTTP/1.1\r\nHost: localhost\r\n\r\n");
        await InvokeHttp11(proxy, endPoint, rejected.Client);
        StringAssert.Contains(ReadAll(rejected.Buffer), "501");

        endPoint.Options.RequireAuth = true;
        var unauthenticated = ClientStream(proxy, "GET /app HTTP/1.1\r\nHost: localhost\r\nUpgrade: webtransport\r\n\r\n");
        await InvokeHttp11(proxy, endPoint, unauthenticated.Client);
        StringAssert.Contains(ReadAll(unauthenticated.Buffer), "407");

        endPoint.Options.RequireAuth = false;
        var noOrigin = ClientStream(proxy, "GET /app HTTP/1.1\r\nHost: localhost\r\nUpgrade: webtransport\r\n\r\n");
        await InvokeHttp11(proxy, endPoint, noOrigin.Client);
        StringAssert.Contains(ReadAll(noOrigin.Buffer), "502");

        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var accept = origin.AcceptTcpClientAsync();
        endPoint.Options.OriginHost = "127.0.0.1";
        endPoint.Options.OriginPort = originPort;
        endPoint.Options.OriginHttps = false;
        var upgraded = ClientStream(proxy, "GET /app?x=1 HTTP/1.1\r\nHost: localhost\r\nUpgrade: webtransport\r\n\r\nping");
        var handled = InvokeHttp11(proxy, endPoint, upgraded.Client);
        using (var originClient = await accept)
        {
            var originStream = originClient.GetStream();
            var header = await ReadUntilBlankAsync(originStream);
            StringAssert.Contains(header, "GET /app ");
            await originStream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\n\r\npong"));
        }

        await handled;
        StringAssert.Contains(ReadAll(upgraded.Buffer), "101");
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task WebTransportOrigin_CoversRejectedUpgradesAndRelay()
    {
        Assert.IsNull(await WebTransportOrigin.OpenHttp11Async("127.0.0.1", 9, false, "a\nb", CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.IsNull(await WebTransportOrigin.OpenHttp11Async("127.0.0.1", 9, true, "/", cancelled.Token));

        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = listener.AcceptTcpClientAsync();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var https = WebTransportOrigin.OpenHttp11Async("127.0.0.1", port, true, "/", budget.Token);
            using (var client = await accept)
            {
                client.Close();
            }

            Assert.IsNull(await https);
        }

        Assert.IsNull(await OpenAgainst(async stream =>
        {
            await stream.WriteAsync("HTTP/1.1 200 OK\r\n\r\n"u8.ToArray());
        }));
        Assert.IsNull(await OpenAgainst(async stream =>
        {
            await stream.WriteAsync("HTTP/1.1 101 Switching Protocols\r\n"u8.ToArray());
            stream.Close();
        }));
        Assert.IsNull(await OpenAgainst(stream =>
        {
            stream.Close();
            return Task.CompletedTask;
        }));
        Assert.IsNull(await OpenAgainst(async stream =>
        {
            await stream.WriteAsync(new byte[9000]);
        }));

        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        channel.Writer.TryWrite(ReadOnlyMemory<byte>.Empty);
        channel.Writer.TryWrite("hi"u8.ToArray());
        channel.Writer.TryComplete();
        var origin = new SplitStream("ok"u8.ToArray());
        var body = new MemoryStream();
        await WebTransportOrigin.RelayAsync(channel.Reader, origin, body, CancellationToken.None);
        CollectionAssert.AreEqual("ok"u8.ToArray(), body.ToArray());
        CollectionAssert.AreEqual("hi"u8.ToArray(), origin.Written);
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task WebTransportHttp2Bridge_RejectsABadCapsuleAndANonSwitchingOrigin()
    {
        using var proxy = new ProxyServer(false, false, false);
        var session = MakeSession(proxy);
        session.HttpClient.Request.Headers.AddHeader("capsule-protocol", "0");
        Assert.IsNull(await OpenBridge(proxy, session, 9));
        Assert.AreEqual(400, session.HttpClient.Response.StatusCode);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        session = MakeSession(proxy);
        PrepareOriginRequest(session, port);
        var opening = OpenBridge(proxy, session, port);
        using (var client = await accept)
        {
            await client.GetStream().WriteAsync("HTTP/1.1 500 No\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
        }

        Assert.IsNull(await opening);

        accept = listener.AcceptTcpClientAsync();
        session = MakeSession(proxy);
        PrepareOriginRequest(session, port);
        opening = OpenBridge(proxy, session, port);
        TcpServerConnection? opened;
        using (var client = await accept)
        {
            _ = ReadUntilBlankAsync(client.GetStream());
            await client.GetStream().WriteAsync(
                "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\n\r\n"u8.ToArray());
            opened = await opening;
        }

        Assert.IsNotNull(opened);
        opened!.Dispose();
    }

    [TestMethod]
    public void GrpcWebBufferedResponse_RewritesOnlyABufferedNativeBody()
    {
        using var proxy = new ProxyServer(false, false, false);
        var session = MakeSession(proxy);
        GrpcWebTranslation.TranslateBufferedResponse(session);

        session.GrpcWebClientContentType = "application/grpc-web";
        session.HttpClient.Response.StreamBodyWriter = (_, _) => Task.CompletedTask;
        session.HttpClient.Response.ContentType = "application/grpc";
        GrpcWebTranslation.TranslateBufferedResponse(session);

        session.HttpClient.Response.StreamBodyWriter = null;
        session.HttpClient.Response.ContentType = "text/plain";
        GrpcWebTranslation.TranslateBufferedResponse(session);

        session.HttpClient.Response.ContentType = "application/grpc";
        session.HttpClient.Response.IsBodyRead = false;
        GrpcWebTranslation.TranslateBufferedResponse(session);
        Assert.AreEqual("application/grpc-web", session.HttpClient.Response.ContentType);

        session.HttpClient.Response.ContentType = "application/grpc";
        session.HttpClient.Response.IsBodyRead = true;
        session.HttpClient.Response.Body = [1, 2, 3];
        session.HttpClient.Response.Headers.AddHeader("grpc-status", "0");
        GrpcWebTranslation.TranslateBufferedResponse(session);
        Assert.IsTrue(session.HttpClient.Response.Body!.Length > 3);
        Assert.AreEqual(session.HttpClient.Response.Body.Length, session.HttpClient.Response.ContentLength);
    }

    [TestMethod]
    public void Http3ConnectUdpHelpers_ClassifyEncodingsAndFlushEachWrite()
    {
        using var proxy = new ProxyServer(false, false, false);
        var method = typeof(Http3RequestStream).GetMethod("DeclaresDecompressibleBody", PrivateStatic)!;
        Assert.IsFalse((bool)method.Invoke(null, [proxy, new List<(string, string)> { ("content-encoding", "gzip") }])!);

        proxy.RequestDecompression = new RequestDecompressionConfig { Enabled = true };
        var headers = new List<(string Name, string Value)>
        {
            ("host", "example.test"),
            ("content-encoding", "gzip"),
        };
        Assert.IsTrue((bool)method.Invoke(null, [proxy, headers])!);
        Assert.IsTrue((bool)method.Invoke(null, [proxy, new List<(string, string)> { ("content-encoding", "x-gzip") }])!);
        Assert.IsTrue((bool)method.Invoke(null, [proxy, new List<(string, string)> { ("content-encoding", "br") }])!);
        Assert.IsFalse((bool)method.Invoke(null, [proxy, new List<(string, string)> { ("content-encoding", "identity") }])!);

        var nested = typeof(Http3RequestStream).GetNestedType("FlushingWriteStream", BindingFlags.NonPublic)!;
        var inner = new MemoryStream();
        var flushing = Activator.CreateInstance(nested, PrivateInstance, null, [inner], null)!;
        Assert.IsFalse((bool)nested.GetProperty("CanRead")!.GetValue(flushing)!);
        Assert.IsFalse((bool)nested.GetProperty("CanSeek")!.GetValue(flushing)!);
        Assert.IsTrue((bool)nested.GetProperty("CanWrite")!.GetValue(flushing)!);
        Assert.ThrowsExactly<TargetInvocationException>(() => nested.GetProperty("Length")!.GetValue(flushing));
        Assert.ThrowsExactly<TargetInvocationException>(() => nested.GetProperty("Position")!.GetValue(flushing));
        Assert.ThrowsExactly<TargetInvocationException>(() => nested.GetProperty("Position")!.SetValue(flushing, 0L));
        nested.GetMethod("Flush")!.Invoke(flushing, null);
        ((Task)nested.GetMethod("FlushAsync", [typeof(CancellationToken)])!.Invoke(flushing, [CancellationToken.None])!).GetAwaiter().GetResult();
        Assert.ThrowsExactly<TargetInvocationException>(() =>
            nested.GetMethod("Read", [typeof(byte[]), typeof(int), typeof(int)])!.Invoke(flushing, [new byte[1], 0, 1]));
        Assert.ThrowsExactly<TargetInvocationException>(() =>
            nested.GetMethod("Seek", [typeof(long), typeof(SeekOrigin)])!.Invoke(flushing, [0L, SeekOrigin.Begin]));
        Assert.ThrowsExactly<TargetInvocationException>(() =>
            nested.GetMethod("SetLength")!.Invoke(flushing, [0L]));
        Assert.ThrowsExactly<TargetInvocationException>(() =>
            nested.GetMethod("Write", [typeof(byte[]), typeof(int), typeof(int)])!.Invoke(flushing, [new byte[1], 0, 1]));
        var write = nested.GetMethod("WriteAsync", [typeof(ReadOnlyMemory<byte>), typeof(CancellationToken)])!;
        ((ValueTask)write.Invoke(flushing, [new ReadOnlyMemory<byte>([9]), CancellationToken.None])!).AsTask().GetAwaiter().GetResult();
        Assert.AreEqual(9, inner.ToArray()[0]);
    }

    [TestMethod]
    public void SocksUdpPackets_CoverAddressFamiliesAndAShortBuffer()
    {
        Assert.IsFalse(ProxyServer.TryParseSocksUdp([0, 0, 1, 1], out _, out _, out _));
        Assert.IsFalse(ProxyServer.TryParseSocksUdp([0, 0, 0, 3, 0], out _, out _, out _));
        Assert.IsFalse(ProxyServer.TryParseSocksUdp([0, 0, 0, 9], out _, out _, out _));

        var ipv6 = new byte[22];
        ipv6[3] = 4;
        IPAddress.IPv6Loopback.GetAddressBytes().CopyTo(ipv6, 4);
        ipv6[20] = 0;
        ipv6[21] = 9;
        Assert.IsTrue(ProxyServer.TryParseSocksUdp(ipv6, out var host, out var port, out var header));
        Assert.AreEqual(IPAddress.IPv6Loopback.ToString(), host);
        Assert.AreEqual(9, port);
        Assert.AreEqual(22, header);

        var domain = new byte[9];
        domain[3] = 3;
        domain[4] = 2;
        domain[5] = (byte)'a';
        domain[6] = (byte)'b';
        domain[8] = 9;
        Assert.IsTrue(ProxyServer.TryParseSocksUdp(domain, out host, out port, out header));
        Assert.AreEqual("ab", host);
        Assert.AreEqual(9, port);
        Assert.AreEqual(9, header);

        Assert.AreEqual(0, ProxyServer.WriteSocksUdp(new byte[1], new IPEndPoint(IPAddress.Loopback, 9), [1]));
        var wrapped = new byte[32];
        var written = ProxyServer.WriteSocksUdp(wrapped, new IPEndPoint(IPAddress.IPv6Loopback, 9), [7]);
        Assert.IsTrue(written > 0);
        Assert.AreEqual(4, wrapped[3]);
        Assert.AreEqual(7, wrapped[written - 1]);
    }

    [TestMethod]
    public void SmallNewLines_AreExecuted()
    {
        var request = new Request { ExtendedConnectProtocol = "webtransport" };
        Assert.IsTrue(request.UpgradeToWebTransport);

        var support = new ExtendedConnectSupport(webSocket: false, webTransport: true);
        StringAssert.Contains(ExtendedConnectProtocols.UnsupportedMessage("", support), "unknown");

        var describe = typeof(ProxyServer).GetMethod("DescribeEndPointKind", PrivateStatic)!;
        var endPoint = new WebTransportProxyEndPoint(IPAddress.Loopback, 0);
        Assert.AreEqual("webtransport", describe.Invoke(null, [endPoint]));

        var prepare = typeof(ProxyServer).GetMethod("PrepareWebTransportUpgradeForHttp2Origin", PrivateStatic)!;
        var upgrade = new Request();
        upgrade.Headers.AddHeader("Host", "origin.test");
        upgrade.Headers.AddHeader("Connection", "Upgrade");
        upgrade.Headers.AddHeader("Upgrade", "webtransport");
        upgrade.Headers.AddHeader("Capsule-Protocol", "?1");
        prepare.Invoke(null, [upgrade]);
        Assert.AreEqual("CONNECT", upgrade.Method);
        Assert.AreEqual("webtransport", upgrade.ExtendedConnectProtocol);
        Assert.IsNull(upgrade.Headers.GetFirstHeader("Upgrade"));
    }

    [TestMethod]
    public async Task Http3ContinuationAuthenticate_OmitsProxyConnection()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.ProxySchemeAuthenticateFunc = (_, _, _) => Task.FromResult(new ProxyAuthenticationContext
        {
            Result = ProxyAuthenticationResult.ContinuationNeeded,
            Continuation = "Negotiate more",
        });
        var session = MakeSession(proxy);
        session.HttpClient.Request.Headers.AddHeader("proxy-authorization", "Negotiate abc");

        Assert.IsFalse(await proxy.CheckHttp3AuthorizationAsync(session));
        Assert.AreEqual("Negotiate more", session.HttpClient.Response.Headers.GetFirstHeader("proxy-authenticate")!.Value);
        Assert.IsNull(session.HttpClient.Response.Headers.GetFirstHeader("proxy-connection"));
    }

    private static async Task<bool> Decompress(ProxyServer proxy, SessionEventArgs session,
        Channel<(byte[] Buffer, int Length)> channel)
    {
        var method = typeof(ProxyServer).GetMethod("TryDecompressChannelBodyAsync", PrivateInstance)!;
        var task = (Task<bool>)method.Invoke(proxy, [session, channel, CancellationToken.None])!;
        return await task;
    }

    private static Channel<(byte[] Buffer, int Length)> ChannelOf(params byte[][] chunks)
    {
        var channel = Channel.CreateUnbounded<(byte[] Buffer, int Length)>();
        foreach (var chunk in chunks)
        {
            var rented = ArrayPool<byte>.Shared.Rent(Math.Max(chunk.Length, 1));
            chunk.CopyTo(rented, 0);
            channel.Writer.TryWrite((rented, chunk.Length));
        }

        channel.Writer.TryComplete();
        return channel;
    }

    private static byte[] Gzip(byte[] plain)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(plain);
        }

        return output.ToArray();
    }

    private static SessionEventArgs MakeSession(ProxyServer proxy, ProxyEndPoint? endPoint = null)
    {
        endPoint ??= new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        return new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
    }

    private static SessionEventArgs Prepared(ProxyServer proxy, WebTransportProxyEndPoint endPoint, string? authorization = null)
    {
        var session = MakeSession(proxy, endPoint);
        var request = session.HttpClient.Request;
        request.Method = "CONNECT";
        request.ExtendedConnectProtocol = "webtransport";
        request.RequestUriString = "https://localhost/app";
        if (authorization is not null)
        {
            request.Headers.AddHeader("proxy-authorization", authorization);
        }

        return session;
    }

    private static Task InvokeBefore(ProxyServer proxy, SessionEventArgs session, WebTransportProxyEndPoint endPoint,
        Http2StreamContext ctx)
    {
        var method = typeof(ProxyServer).GetMethod("WebTransportOnBeforeRequestAsync", PrivateInstance)!;
        return (Task)method.Invoke(proxy, [session, ctx, endPoint])!;
    }

    private static (HttpClientStream Client, MemoryStream Buffer) ClientStream(ProxyServer proxy, string text)
    {
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var stream = new MemoryStream();
        var bytes = Encoding.ASCII.GetBytes(text);
        stream.Write(bytes);
        stream.Position = 0;
        return (new HttpClientStream(proxy, connection, stream, proxy.BufferPool, CancellationToken.None), stream);
    }

    private static Task InvokeHttp11(ProxyServer proxy, WebTransportProxyEndPoint endPoint, HttpClientStream client)
    {
        var method = typeof(ProxyServer).GetMethod("HandleWebTransportHttp11Async", PrivateInstance)!;
        return (Task)method.Invoke(proxy, [endPoint, client, CancellationToken.None])!;
    }

    private static async Task<TcpServerConnection?> OpenBridge(ProxyServer proxy, SessionEventArgs session, int port)
    {
        var method = typeof(ProxyServer).GetMethod("OpenWebTransportHttp11Async", PrivateInstance)!;
        var task = (Task)method.Invoke(proxy, [session, "127.0.0.1", port, CancellationToken.None])!;
        await task;
        return (TcpServerConnection?)task.GetType().GetProperty("Result")!.GetValue(task);
    }

    private static void PrepareOriginRequest(SessionEventArgs session, int port)
    {
        var request = session.HttpClient.Request;
        request.IsHttps = false;
        request.Host = $"127.0.0.1:{port}";
        request.RequestUriString = $"http://127.0.0.1:{port}/app";
    }

    private static async Task<WebTransportOriginSession?> OpenAgainst(Func<NetworkStream, Task> respond)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        var opening = WebTransportOrigin.OpenHttp11Async("127.0.0.1", port, false, "/", CancellationToken.None);
        using var client = await accept;
        await respond(client.GetStream());
        return await opening;
    }

    private static string Basic(string userPassword) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(userPassword));

    private static string ReadAll(MemoryStream buffer)
    {
        buffer.Position = 0;
        return new StreamReader(buffer, Encoding.ASCII, leaveOpen: true).ReadToEnd();
    }

    private static async Task<string> ReadUntilBlankAsync(NetworkStream stream)
    {
        var header = new MemoryStream();
        var one = new byte[1];
        while (header.Length < 8192)
        {
            if (await stream.ReadAsync(one) == 0)
            {
                break;
            }

            header.WriteByte(one[0]);
            if (header.Length >= 4)
            {
                var tail = header.GetBuffer();
                var n = (int)header.Length;
                if (tail[n - 4] == '\r' && tail[n - 3] == '\n' && tail[n - 2] == '\r' && tail[n - 1] == '\n')
                {
                    break;
                }
            }
        }

        return Encoding.ASCII.GetString(header.GetBuffer(), 0, (int)header.Length);
    }

    /// <summary>Reads a fixed payload and records writes on a separate buffer. Close throws once.</summary>
    private sealed class SplitStream : Stream
    {
        private readonly MemoryStream readable;
        private readonly MemoryStream written = new();
        private readonly TaskCompletionSource wrote = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int reads;

        public SplitStream(byte[] payload) => readable = new MemoryStream(payload);

        public byte[] Written => written.ToArray();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => readable.Read(buffer, offset, count);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                await wrote.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return readable.Read(buffer.Span);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => written.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            written.Write(buffer.Span);
            if (buffer.Length > 0)
            {
                wrote.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }

        public override void Close() => throw new IOException("close");
    }
}
