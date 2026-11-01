#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
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
        bool httpsOrigin = false,
        Action<BeforeQuicAuthenticateEventArgs>? configureAuth = null)
    {
        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = forwardPort
        };
        quicEp.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
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
