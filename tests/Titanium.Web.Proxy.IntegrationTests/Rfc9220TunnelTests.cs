#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

[DoNotParallelize]
[TestClass]
public class Rfc9220TunnelTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static readonly Encoding Ascii = Encoding.ASCII;
    private static readonly string[] Separator = ["\r\n"];

    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    private static (ProxyServer Proxy, TransparentQuicProxyEndPoint Ep) StartProxy(
        TestSuite testSuite,
        int forwardPort,
        UpstreamHttpProtocol upstream = UpstreamHttpProtocol.Http11,
        Action<BeforeQuicAuthenticateEventArgs>? configureAuth = null)
    {
        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = forwardPort
        };
        quicEp.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = upstream;
            args.AllowHttpProtocolTranslation = true;
            configureAuth?.Invoke(args);
            return Task.CompletedTask;
        };

        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp3 = true,
            EnableHttpsSvcbDnsDiscovery = false,
            EnableRfc8441 = true
        };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(quicEp);
        proxy.Start();
        return (proxy, quicEp);
    }

    private static void AttachEchoWebSocketOrigin(TestServer server)
    {
        server.HandleTcpRequest(async context =>
        {
            var upgradeRequest = await ReadRequestHeadersAsync(context);
            var keyLine = upgradeRequest.Split(Separator, StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
            var wsKey = keyLine[(keyLine.IndexOf(':') + 1)..].Trim();
            var wsAccept = Convert.ToBase64String(SHA1.HashData(
                Ascii.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

            await context.Transport.Output.WriteAsync(Ascii.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {wsAccept}\r\n\r\n"));
            await context.Transport.Output.FlushAsync();

            while (true)
            {
                var result = await context.Transport.Input.ReadAsync();
                if (result.Buffer.IsEmpty && result.IsCompleted) break;
                foreach (var segment in result.Buffer)
                    await context.Transport.Output.WriteAsync(segment.ToArray());
                context.Transport.Input.AdvanceTo(result.Buffer.End);
                await context.Transport.Output.FlushAsync();
                if (result.IsCompleted) break;
            }

            context.Transport.Output.Complete();
        });
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebSocket_H3ClientToH1Origin_EchoesText()
    {
        RequireQuic();
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        AttachEchoWebSocketOrigin(server);

        var (proxy, quicEp) = StartProxy(testSuite, server.TcpListeningPort);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();

            await using var tunnel = await client.OpenExtendedConnectAsync(
                $"localhost:{server.TcpListeningPort}", "/ws", "websocket",
                extraHeaders: [("sec-websocket-version", "13")], scheme: "http");
            Assert.AreEqual(200, tunnel.Status);

            var frame = BuildMaskedTextFrame(Ascii.GetBytes("hello-h3-ws"));
            await tunnel.SendAsync(frame);
            var echoed = await tunnel.ReceiveAsync();
            CollectionAssert.AreEqual(frame, echoed);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebSocket_UnknownProtocol_Returns501()
    {
        RequireQuic();
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async ctx => await ctx.Response.WriteAsync("ok"));

        var (proxy, quicEp) = StartProxy(testSuite, server.HttpsListeningPort);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();

            await using var tunnel = await client.OpenExtendedConnectAsync(
                $"localhost:{server.HttpsListeningPort}", "/t", "webtransport");
            Assert.AreEqual(501, tunnel.Status);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebSocket_OriginNotListening_Returns502()
    {
        RequireQuic();
        using var testSuite = new TestSuite(sharedServer);
        var deadPort = TcpPortFinder.GetFreePort();

        var (proxy, quicEp) = StartProxy(testSuite, deadPort);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();

            await using var tunnel = await client.OpenExtendedConnectAsync(
                $"localhost:{deadPort}", "/ws", "websocket");
            Assert.AreEqual(502, tunnel.Status);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebSocket_H3ClientToH2Origin_EchoesText()
    {
        RequireQuic();
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithConnectProtocolAsync();
            var (reqStreamId, reqHeaders, endStream) = await originConn.ReadHeaderBlockAsync();
            Assert.AreEqual("CONNECT", reqHeaders.Single(h => h.Name == ":method").Value);
            Assert.AreEqual("websocket", reqHeaders.Single(h => h.Name == ":protocol").Value);
            Assert.IsFalse(endStream);
            Assert.IsFalse(reqHeaders.Any(h =>
                string.Equals(h.Name, "sec-websocket-key", StringComparison.OrdinalIgnoreCase)));

            var resp200 = originConn.EncodeHeaders(new[] { (":status", "200") },
                Array.Empty<(string, string)>());
            await originConn.WriteHeaderBlockAsync(reqStreamId, resp200, false);

            while (true)
            {
                var frame = await originConn.ReadFrameAsync();
                if (frame.Type == Http2FrameType.Data && frame.StreamId == reqStreamId)
                {
                    if (frame.Payload.Length > 0)
                        await originConn.WriteFrameAsync(Http2FrameType.Data, reqStreamId, 0, frame.Payload);
                    if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                        break;
                }
                else if (frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway)
                {
                    break;
                }
            }
        });

        using var testSuite = new TestSuite(sharedServer);
        var (proxy, quicEp) = StartProxy(testSuite, rawOrigin.Port, UpstreamHttpProtocol.Http2);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();

            await using var tunnel = await client.OpenExtendedConnectAsync(
                $"localhost:{rawOrigin.Port}", "/ws", "websocket",
                extraHeaders: [("sec-websocket-version", "13")]);
            Assert.AreEqual(200, tunnel.Status);

            var frame = BuildMaskedTextFrame(Ascii.GetBytes("hello-h3-to-h2"));
            await tunnel.SendAsync(frame);
            var echoed = await tunnel.ReceiveAsync();
            CollectionAssert.AreEqual(frame, echoed);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebSocket_H3ClientToH2Origin_WithoutSetting8_FallsBackToHttp11()
    {
        RequireQuic();
        using var dualOrigin = new DualAlpnWebSocketOrigin(TestCertificateAuthority.ServerCertificate);

        using var testSuite = new TestSuite(sharedServer);
        var (proxy, quicEp) = StartProxy(testSuite, dualOrigin.Port, UpstreamHttpProtocol.Http2);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();

            await using var tunnel = await client.OpenExtendedConnectAsync(
                $"localhost:{dualOrigin.Port}", "/ws", "websocket",
                extraHeaders: [("sec-websocket-version", "13")]);
            Assert.AreEqual(200, tunnel.Status);

            Assert.IsTrue(dualOrigin.Http2ConnectionsAccepted > 0,
                "Forced HTTP/2 upstream must still open the h2 origin connection.");
            Assert.IsTrue(dualOrigin.Http11ConnectionsAccepted > 0,
                "Without ENABLE_CONNECT_PROTOCOL the proxy must fall back to HTTP/1.1 for the WebSocket upgrade.");

            var frame = BuildMaskedTextFrame(Ascii.GetBytes("fallback-h3"));
            await tunnel.SendAsync(frame);
            var echoed = await tunnel.ReceiveAsync();
            CollectionAssert.AreEqual(frame, echoed);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebSocket_H3ClientToH2Origin_OriginRejectsExtendedConnect_RelaysStatus()
    {
        RequireQuic();
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithConnectProtocolAsync();
            var (reqStreamId, _, _) = await originConn.ReadHeaderBlockAsync();
            var resp403 = originConn.EncodeHeaders(new[] { (":status", "403") },
                Array.Empty<(string, string)>());
            await originConn.WriteHeaderBlockAsync(reqStreamId, resp403, true);
        });

        using var testSuite = new TestSuite(sharedServer);
        var (proxy, quicEp) = StartProxy(testSuite, rawOrigin.Port, UpstreamHttpProtocol.Http2);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();

            await using var tunnel = await client.OpenExtendedConnectAsync(
                $"localhost:{rawOrigin.Port}", "/ws", "websocket");
            Assert.AreEqual(403, tunnel.Status);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebSocket_H3ClientToH2Origin_OriginReset_AbortsWithConnectError()
    {
        RequireQuic();
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithConnectProtocolAsync();
            var (reqStreamId, _, _) = await originConn.ReadHeaderBlockAsync();
            var resp200 = originConn.EncodeHeaders(new[] { (":status", "200") },
                Array.Empty<(string, string)>());
            await originConn.WriteHeaderBlockAsync(reqStreamId, resp200, false);

            var frame = await originConn.ReadFrameAsync();
            if (frame.Type == Http2FrameType.Data && frame.StreamId == reqStreamId)
            {
                await originConn.WriteFrameAsync(Http2FrameType.RstStream, reqStreamId, 0,
                    new byte[] { 0, 0, 0, (byte)Http2ErrorCode.ConnectError });
            }
        });

        using var testSuite = new TestSuite(sharedServer);
        var (proxy, quicEp) = StartProxy(testSuite, rawOrigin.Port, UpstreamHttpProtocol.Http2);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();

            await using var tunnel = await client.OpenExtendedConnectAsync(
                $"localhost:{rawOrigin.Port}", "/ws", "websocket");
            Assert.AreEqual(200, tunnel.Status);

            await tunnel.SendAsync(BuildMaskedTextFrame(Ascii.GetBytes("reset-me")));

            var ex = await Assert.ThrowsExactlyAsync<QuicException>(async () =>
                _ = await tunnel.ReceiveAsync());
            Assert.AreEqual((long)Http3ErrorCode.ConnectError, ex.ApplicationErrorCode);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebSocket_H3ClientToH2Origin_PooledConnectionSurvivesTunnel()
    {
        RequireQuic();
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(HandlePooledGetAndWebSocketEchoAsync);

        using var testSuite = new TestSuite(sharedServer);
        var (proxy, quicEp) = StartProxy(testSuite, rawOrigin.Port, UpstreamHttpProtocol.Http2);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();

            var warm = await client.SendAsync("GET", $"localhost:{rawOrigin.Port}", "/");
            Assert.AreEqual(200, warm.StatusCode);
            Assert.AreEqual("get-ok", warm.TextBody);

            await using var tunnel = await client.OpenExtendedConnectAsync(
                $"localhost:{rawOrigin.Port}", "/ws", "websocket");
            Assert.AreEqual(200, tunnel.Status);
            var frame = BuildMaskedTextFrame(Ascii.GetBytes("pool-survives"));
            await tunnel.SendAsync(frame);
            CollectionAssert.AreEqual(frame, await tunnel.ReceiveAsync());
            // The raw origin handler is serial: finish the CONNECT stream so it can accept the next GET.
            tunnel.Finish();

            var after = await client.SendAsync("GET", $"localhost:{rawOrigin.Port}", "/");
            Assert.AreEqual(200, after.StatusCode);
            Assert.AreEqual("get-ok", after.TextBody);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    private static async Task HandlePooledGetAndWebSocketEchoAsync(Http2RawFrame.Connection originConn)
    {
        await originConn.SendInitialSettingsWithConnectProtocolAsync();
        while (true)
        {
            var (streamId, reqHeaders, endStream) = await originConn.ReadHeaderBlockAsync();
            var method = reqHeaders.Single(h => h.Name == ":method").Value;
            if (method == "GET")
            {
                var body = Ascii.GetBytes("get-ok");
                var resp = originConn.EncodeHeaders(
                    new[] { (":status", "200"), ("content-length", body.Length.ToString()) },
                    Array.Empty<(string, string)>());
                await originConn.WriteHeaderBlockAsync(streamId, resp, false);
                await originConn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, body);
                continue;
            }

            if (method != "CONNECT")
                continue;

            Assert.AreEqual("websocket", reqHeaders.Single(h => h.Name == ":protocol").Value);
            var resp200 = originConn.EncodeHeaders(new[] { (":status", "200") },
                Array.Empty<(string, string)>());
            await originConn.WriteHeaderBlockAsync(streamId, resp200, false);

            while (!endStream)
            {
                var frame = await originConn.ReadFrameAsync();
                if (frame.Type == Http2FrameType.Data && frame.StreamId == streamId)
                {
                    if (frame.Payload.Length > 0)
                        await originConn.WriteFrameAsync(Http2FrameType.Data, streamId, 0, frame.Payload);
                    if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                        break;
                }
                else if (frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway)
                {
                    break;
                }
            }
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebSocket_H3ClientToH3Origin_EchoesText()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate)
        {
            AdvertiseEnableConnectProtocol = true
        };

        using var testSuite = new TestSuite(sharedServer);
        var (proxy, quicEp) = StartProxy(testSuite, origin.Port, UpstreamHttpProtocol.Http3);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();

            await using var tunnel = await client.OpenExtendedConnectAsync(
                $"localhost:{origin.Port}", "/ws", "websocket",
                extraHeaders: [("sec-websocket-version", "13")]);
            Assert.AreEqual(200, tunnel.Status);
            Assert.IsTrue(origin.ExtendedConnectAcceptCount > 0);

            var frame = BuildMaskedTextFrame(Ascii.GetBytes("hello-h3-to-h3"));
            await tunnel.SendAsync(frame);
            CollectionAssert.AreEqual(frame, await tunnel.ReceiveAsync());
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebSocket_H3ClientToH3Origin_WithoutSetting8_FallsBackToHttp11()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        using var tcpEcho = new TcpWebSocketEchoOnPort(origin.Port);

        using var testSuite = new TestSuite(sharedServer);
        var (proxy, quicEp) = StartProxy(testSuite, origin.Port, UpstreamHttpProtocol.Http3);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();

            await using var tunnel = await client.OpenExtendedConnectAsync(
                $"localhost:{origin.Port}", "/ws", "websocket",
                extraHeaders: [("sec-websocket-version", "13")], scheme: "http");
            Assert.AreEqual(200, tunnel.Status);
            Assert.AreEqual(0, origin.ExtendedConnectAcceptCount,
                "Without ENABLE_CONNECT_PROTOCOL the proxy must not open an H3 extended CONNECT.");
            Assert.IsTrue(tcpEcho.ConnectionsAccepted > 0,
                "The proxy must fall back to HTTP/1.1 WebSocket upgrade on TCP.");

            var frame = BuildMaskedTextFrame(Ascii.GetBytes("fallback-h3-h3"));
            await tunnel.SendAsync(frame);
            CollectionAssert.AreEqual(frame, await tunnel.ReceiveAsync());
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebSocket_H3ClientToH3Origin_OriginRejectsExtendedConnect_RelaysStatus()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate)
        {
            AdvertiseEnableConnectProtocol = true,
            ExtendedConnectHandler = async ctx =>
            {
                await ctx.RespondAsync(403);
            }
        };

        using var testSuite = new TestSuite(sharedServer);
        var (proxy, quicEp) = StartProxy(testSuite, origin.Port, UpstreamHttpProtocol.Http3);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();

            await using var tunnel = await client.OpenExtendedConnectAsync(
                $"localhost:{origin.Port}", "/ws", "websocket");
            Assert.AreEqual(403, tunnel.Status);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebSocket_H3ClientToH3Origin_OriginReset_AbortsWithConnectError()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate)
        {
            AdvertiseEnableConnectProtocol = true,
            ExtendedConnectHandler = ctx => ResetOriginOnFirstDataAsync(ctx)
        };

        using var testSuite = new TestSuite(sharedServer);
        var (proxy, quicEp) = StartProxy(testSuite, origin.Port, UpstreamHttpProtocol.Http3);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();

            await using var tunnel = await client.OpenExtendedConnectAsync(
                $"localhost:{origin.Port}", "/ws", "websocket");
            Assert.AreEqual(200, tunnel.Status);

            await tunnel.SendAsync(BuildMaskedTextFrame(Ascii.GetBytes("reset-me")));

            var ex = await Assert.ThrowsExactlyAsync<QuicException>(async () =>
                _ = await tunnel.ReceiveAsync());
            Assert.AreEqual((long)Http3ErrorCode.ConnectError, ex.ApplicationErrorCode);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebSocket_H3ClientToH3Origin_OriginFin_ClosesCleanly()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate)
        {
            AdvertiseEnableConnectProtocol = true,
            ExtendedConnectHandler = async ctx =>
            {
                await ctx.RespondAsync(200);
                var frame = await Http3Frame.ReadAsync(ctx.Stream, maxPayloadBytes: 0, CancellationToken.None);
                frame?.ReturnPayload();
                ctx.Stream.CompleteWrites();
            }
        };

        using var testSuite = new TestSuite(sharedServer);
        var (proxy, quicEp) = StartProxy(testSuite, origin.Port, UpstreamHttpProtocol.Http3);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();

            await using var tunnel = await client.OpenExtendedConnectAsync(
                $"localhost:{origin.Port}", "/ws", "websocket");
            Assert.AreEqual(200, tunnel.Status);

            await tunnel.SendAsync(BuildMaskedTextFrame(Ascii.GetBytes("fin-me")));
            var echoed = await tunnel.ReceiveAsync();
            Assert.AreEqual(0, echoed.Length);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebSocket_BeforeRequestDeny_ClientSeesDenyResponse_OriginNeverContacted()
    {
        RequireQuic();
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var originHits = 0;
        server.HandleTcpRequest(_ =>
        {
            System.Threading.Interlocked.Increment(ref originHits);
            return Task.CompletedTask;
        });

        var (proxy, quicEp) = StartProxy(testSuite, server.TcpListeningPort);
        proxy.BeforeRequest += (_, e) =>
        {
            e.Respond(new Response { StatusCode = 403, Body = Ascii.GetBytes("denied") });
            return Task.CompletedTask;
        };
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();

            await using var tunnel = await client.OpenExtendedConnectAsync(
                $"localhost:{server.TcpListeningPort}", "/ws", "websocket", scheme: "http");
            Assert.AreEqual(403, tunnel.Status);
            Assert.AreEqual(0, originHits);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    private static async Task ResetOriginOnFirstDataAsync(QuicHttp3ExtendedConnectContext ctx)
    {
        await ctx.RespondAsync(200);
        var frame = await Http3Frame.ReadAsync(ctx.Stream, maxPayloadBytes: 0, CancellationToken.None);
        frame?.ReturnPayload();
        ctx.Stream.Abort(QuicAbortDirection.Both, (long)Http3ErrorCode.ConnectError);
    }

    private static async Task<string> ReadRequestHeadersAsync(ConnectionContext context)
    {
        var accumulated = string.Empty;
        while (!accumulated.Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var result = await context.Transport.Input.ReadAsync();
            foreach (var seg in result.Buffer)
                accumulated += Ascii.GetString(seg.Span);
            context.Transport.Input.AdvanceTo(result.Buffer.End);
        }

        return accumulated;
    }

    private static byte[] BuildMaskedTextFrame(byte[] payload)
    {
        var maskKey = new byte[] { 0x37, 0xfa, 0x21, 0x3d };
        var frame = new List<byte> { 0x81, (byte)(0x80 | payload.Length) };
        frame.AddRange(maskKey);
        for (var i = 0; i < payload.Length; i++)
            frame.Add((byte)(payload[i] ^ maskKey[i % 4]));
        return frame.ToArray();
    }

    /// <summary>Plain TCP WebSocket echo on a fixed port (pairs with a QUIC origin on the same port).</summary>
    private sealed class TcpWebSocketEchoOnPort : IDisposable
    {
        private readonly TcpListener listener;
        private bool disposed;

        public TcpWebSocketEchoOnPort(int port)
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            _ = AcceptLoopAsync();
        }

        public int ConnectionsAccepted { get; private set; }

        private async Task AcceptLoopAsync()
        {
            while (!disposed)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch
                {
                    return;
                }

                ConnectionsAccepted++;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await using var stream = client.GetStream();
                        var upgradeRequest = await ReadUntilDoubleCrLfAsync(stream);
                        var keyLine = upgradeRequest.Split(Separator, StringSplitOptions.RemoveEmptyEntries)
                            .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
                        var wsKey = keyLine[(keyLine.IndexOf(':') + 1)..].Trim();
                        var wsAccept = Convert.ToBase64String(SHA1.HashData(
                            Ascii.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

                        await stream.WriteAsync(Ascii.GetBytes(
                            "HTTP/1.1 101 Switching Protocols\r\n" +
                            "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                            $"Sec-WebSocket-Accept: {wsAccept}\r\n\r\n"));
                        await stream.FlushAsync();

                        var buffer = new byte[4096];
                        while (true)
                        {
                            var read = await stream.ReadAsync(buffer);
                            if (read <= 0) break;
                            await stream.WriteAsync(buffer.AsMemory(0, read));
                            await stream.FlushAsync();
                        }
                    }
                    catch
                    {
                        // test asserts
                    }
                    finally
                    {
                        client.Dispose();
                    }
                });
            }
        }

        private static async Task<string> ReadUntilDoubleCrLfAsync(Stream stream)
        {
            var accumulated = new StringBuilder();
            var buffer = new byte[1024];
            while (!accumulated.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                if (read <= 0) break;
                accumulated.Append(Ascii.GetString(buffer, 0, read));
            }

            return accumulated.ToString();
        }

        public void Dispose()
        {
            disposed = true;
            listener.Stop();
        }
    }

    /// <summary>
    ///     TLS origin with both h2 (no ENABLE_CONNECT_PROTOCOL) and http/1.1 WebSocket echo.
    /// </summary>
    private sealed class DualAlpnWebSocketOrigin : IDisposable
    {
        private readonly TcpListener listener;
        private readonly X509Certificate2 certificate;
        private bool disposed;

        public DualAlpnWebSocketOrigin(X509Certificate2 certificate)
        {
            this.certificate = certificate;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _ = AcceptLoopAsync();
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public int Http2ConnectionsAccepted { get; private set; }
        public int Http11ConnectionsAccepted { get; private set; }

        private async Task AcceptLoopAsync()
        {
            while (!disposed)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await using var ssl = new SslStream(client.GetStream(), false);
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = certificate,
                            ApplicationProtocols = new List<SslApplicationProtocol>
                            {
                                SslApplicationProtocol.Http2,
                                SslApplicationProtocol.Http11
                            },
                            EnabledSslProtocols = System.Security.Authentication.SslProtocols.None
                        });

                        if (ssl.NegotiatedApplicationProtocol.Equals(SslApplicationProtocol.Http2))
                        {
                            Http2ConnectionsAccepted++;
                            var preface = new byte[Http2Helper.ConnectionPreface.Length];
                            await Http2RawFrame.ReadExactAsync(ssl, preface, 0, preface.Length);
                            var conn = new Http2RawFrame.Connection(ssl);
                            await conn.SendInitialSettingsAsync();
                            try
                            {
                                while (true) _ = await conn.ReadFrameAsync();
                            }
                            catch
                            {
                                // closed
                            }
                        }
                        else
                        {
                            Http11ConnectionsAccepted++;
                            await HandleHttp11WebSocketAsync(ssl);
                        }
                    }
                    catch
                    {
                        // test asserts
                    }
                    finally
                    {
                        client.Dispose();
                    }
                });
            }
        }

        private static async Task HandleHttp11WebSocketAsync(Stream stream)
        {
            var upgradeRequest = await ReadUntilDoubleCrLfAsync(stream);
            var keyLine = upgradeRequest.Split(Separator, StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
            var wsKey = keyLine[(keyLine.IndexOf(':') + 1)..].Trim();
            var wsAccept = Convert.ToBase64String(SHA1.HashData(
                Ascii.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

            await stream.WriteAsync(Ascii.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {wsAccept}\r\n\r\n"));
            await stream.FlushAsync();

            var buffer = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read <= 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, read));
                await stream.FlushAsync();
            }
        }

        private static async Task<string> ReadUntilDoubleCrLfAsync(Stream stream)
        {
            var accumulated = new StringBuilder();
            var buffer = new byte[1024];
            while (!accumulated.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                if (read <= 0) break;
                accumulated.Append(Ascii.GetString(buffer, 0, read));
            }

            return accumulated.ToString();
        }

        public void Dispose()
        {
            disposed = true;
            listener.Stop();
        }
    }
}

internal static class TcpPortFinder
{
    public static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

#pragma warning restore TWP001
#pragma warning restore CA1416
