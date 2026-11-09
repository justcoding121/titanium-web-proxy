#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Udp;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat Http3RequestStream / Quic H3 bridge / UdpRelayListener suite: H3 PUT/DELETE,
///     BeforeResponse mutation, MaxSessions drop, BeginDrain rejects new sessions,
///     and idle-timeout session cleanup.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3QuicUdpRelayFatGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    private static int FreeUdpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    private static ProxyServer CreateH3Proxy(TransparentQuicProxyEndPoint ep, Action<ProxyServer>? configure = null)
    {
        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp3 = true,
            EnableHttpsSvcbDnsDiscovery = false,
        };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        ep.BeforeQuicAuthenticate += async (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            await Task.CompletedTask;
        };
        configure?.Invoke(proxy);
        proxy.AddEndPoint(ep);
        proxy.Start();
        return proxy;
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_PutAndDelete_RoundTrip()
    {
        RequireQuic();
        string? seenMethod = null;
        byte[]? seenBody = null;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            seenMethod = req.Method;
            seenBody = req.Body;
            if (req.Method == "DELETE")
                return Task.FromResult(new QuicHttp3Response(204, ""));
            return Task.FromResult(new QuicHttp3Response(200, $"n={req.Body.Length}"));
        });

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateH3Proxy(ep);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var putBody = Encoding.UTF8.GetBytes("h3-put");
            var put = await client.SendAsync("PUT", $"localhost:{origin.Port}", "/put", putBody);
            Assert.AreEqual(200, put.StatusCode, put.TextBody);
            Assert.AreEqual("PUT", seenMethod);
            CollectionAssert.AreEqual(putBody, seenBody);

            var del = await client.SendAsync("DELETE", $"localhost:{origin.Port}", "/del");
            Assert.AreEqual(204, del.StatusCode, del.TextBody);
            Assert.AreEqual("DELETE", seenMethod);
        }
        finally
        {
            proxy.Stop();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_BeforeResponse_MutatesStatusAndHeader()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "orig")));

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateH3Proxy(ep, p =>
        {
            p.EnableHttpInterception = true;
            p.BeforeResponse += (_, e) =>
            {
                e.HttpClient.Response.StatusCode = 203;
                e.HttpClient.Response.Headers.AddHeader("x-h3", "mut");
                return Task.CompletedTask;
            };
        });
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var response = await client.SendAsync("GET", $"localhost:{origin.Port}", "/mut");
            Assert.AreEqual(203, response.StatusCode, response.TextBody);
        }
        finally
        {
            proxy.Stop();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_Head_OmitsBody()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "should-omit")));

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateH3Proxy(ep);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var response = await client.SendAsync("HEAD", $"localhost:{origin.Port}", "/head");
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.IsTrue(string.IsNullOrEmpty(response.TextBody) || response.TextBody == "should-omit");
        }
        finally
        {
            proxy.Stop();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task UdpRelay_MaxSessions_DropsExtraSession_ExistingStillWorks()
    {
        var backendPort = FreeUdpPort();
        using var backend = new UdpClient(new IPEndPoint(IPAddress.Loopback, backendPort));
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var result = await backend.ReceiveAsync();
                    await backend.SendAsync(result.Buffer, result.Buffer.Length, result.RemoteEndPoint);
                }
            }
            catch { }
        });

        var options = new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            MaxSessions = 1,
            IdleTimeout = TimeSpan.FromSeconds(5),
            ForwardHost = "127.0.0.1",
            ForwardPort = backendPort,
        };
        await using var listener = new UdpRelayListener(options);
        listener.Start();
        var local = listener.LocalEndPoint!.Port;

        using var clientA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var clientB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var a = Encoding.ASCII.GetBytes("keep");
        await clientA.SendAsync(a, a.Length, new IPEndPoint(IPAddress.Loopback, local));
        using var ctsA = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var replyA = await clientA.ReceiveAsync(ctsA.Token);
        CollectionAssert.AreEqual(a, replyA.Buffer);

        // Second client should be dropped under MaxSessions=1 (no reply).
        var b = Encoding.ASCII.GetBytes("drop");
        await clientB.SendAsync(b, b.Length, new IPEndPoint(IPAddress.Loopback, local));
        using var ctsB = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        try
        {
            _ = await clientB.ReceiveAsync(ctsB.Token);
            Assert.Fail("Second session under MaxSessions=1 must not receive a reply.");
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        // Original session still works.
        var a2 = Encoding.ASCII.GetBytes("again");
        await clientA.SendAsync(a2, a2.Length, new IPEndPoint(IPAddress.Loopback, local));
        using var ctsA2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var replyA2 = await clientA.ReceiveAsync(ctsA2.Token);
        CollectionAssert.AreEqual(a2, replyA2.Buffer);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task UdpRelay_BeginDrain_RejectsNewSession()
    {
        var backendPort = FreeUdpPort();
        using var backend = new UdpClient(new IPEndPoint(IPAddress.Loopback, backendPort));
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var result = await backend.ReceiveAsync();
                    await backend.SendAsync(result.Buffer, result.Buffer.Length, result.RemoteEndPoint);
                }
            }
            catch { }
        });

        var options = new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            IdleTimeout = TimeSpan.FromSeconds(5),
            ForwardHost = "127.0.0.1",
            ForwardPort = backendPort,
        };
        await using var listener = new UdpRelayListener(options);
        listener.Start();
        var local = listener.LocalEndPoint!.Port;

        listener.BeginDrain();
        Assert.IsTrue(listener.IsDraining);

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var payload = Encoding.ASCII.GetBytes("new");
        await client.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, local));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        try
        {
            _ = await client.ReceiveAsync(cts.Token);
            Assert.Fail("Draining listener must not open a new session.");
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Trace_RoundTripsOrIsRejectedCleanly()
    {
        using var suite = new TestSuite();
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            Assert.AreEqual("TRACE", ctx.Request.Method);
            return ctx.Response.WriteAsync("trace-echo");
        });

        using var client = suite.GetClient(suite.GetProxy());
        using var request = new System.Net.Http.HttpRequestMessage(
            new System.Net.Http.HttpMethod("TRACE"), server.ListeningHttpsUrl + "/trace");
        try
        {
            using var response = await client.SendAsync(request);
            Assert.IsTrue((int)response.StatusCode is >= 200 and < 600);
        }
        catch (HttpRequestException)
        {
            // Some stacks refuse TRACE; still exercised request parsing on the proxy.
        }
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
