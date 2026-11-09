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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Udp;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat Http3RequestStream / UdpRelayListener session edges: H3 OPTIONS/PATCH,
///     BeforeRequest Ok short-circuit, UDP rate-limit drop, and multi-client sessions.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3UdpRelaySessionFatGapTests
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
    public async Task H3_OptionsAndPatch_RoundTrip()
    {
        RequireQuic();
        string? seenMethod = null;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            seenMethod = req.Method;
            if (req.Method == "OPTIONS")
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

            var options = await client.SendAsync("OPTIONS", $"localhost:{origin.Port}", "/opt", Array.Empty<byte>());
            Assert.AreEqual(204, options.StatusCode, options.TextBody);
            Assert.AreEqual("OPTIONS", seenMethod);

            var patchBody = Encoding.UTF8.GetBytes("patch-me");
            var patch = await client.SendAsync("PATCH", $"localhost:{origin.Port}", "/patch", patchBody);
            Assert.AreEqual(200, patch.StatusCode, patch.TextBody);
            Assert.AreEqual("PATCH", seenMethod);
            Assert.AreEqual($"n={patchBody.Length}", patch.TextBody);
        }
        finally
        {
            proxy.Stop();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_BeforeRequest_Ok_SkipsOrigin()
    {
        RequireQuic();
        var contacted = false;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ =>
        {
            contacted = true;
            return Task.FromResult(new QuicHttp3Response(200, "origin"));
        });

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
        using var proxy = CreateH3Proxy(ep, p =>
        {
            p.BeforeRequest += (_, e) =>
            {
                e.Ok("h3-ok");
                return Task.CompletedTask;
            };
        });
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var response = await client.SendAsync("GET", $"localhost:{origin.Port}", "/ok", Array.Empty<byte>());
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual("h3-ok", response.TextBody);
            Assert.IsFalse(contacted);
        }
        finally
        {
            proxy.Stop();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task UdpRelay_RateLimit_DropsExcess_ThenRelaysAgain()
    {
        var backendPort = FreeUdpPort();
        using var backend = new UdpClient(new IPEndPoint(IPAddress.Loopback, backendPort));
        var received = 0;
        var backendTask = Task.Run(async () =>
        {
            while (true)
            {
                var result = await backend.ReceiveAsync();
                Interlocked.Increment(ref received);
                await backend.SendAsync(result.Buffer, result.Buffer.Length, result.RemoteEndPoint);
            }
        });

        var options = new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            MaxDatagramsPerSecondPerClient = 2,
            ForwardHost = "127.0.0.1",
            ForwardPort = backendPort,
            IdleTimeout = TimeSpan.FromSeconds(5),
        };
        await using var listener = new UdpRelayListener(options);
        listener.Start();
        var local = listener.LocalEndPoint!.Port;

        using var client = new UdpClient();
        var ep = new IPEndPoint(IPAddress.Loopback, local);
        for (var i = 0; i < 8; i++)
        {
            var payload = Encoding.ASCII.GetBytes("r" + i);
            await client.SendAsync(payload, payload.Length, ep);
        }

        // Wait briefly for rate-limited window; only a few should arrive.
        await Task.Delay(200);
        var mid = Volatile.Read(ref received);
        Assert.IsTrue(mid <= 4, $"Rate limit should drop excess; got {mid} datagrams");

        // Drain any late echoes already queued on the client socket from the burst.
        client.Client.ReceiveTimeout = 50;
        try
        {
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (true)
                _ = client.Client.ReceiveFrom(new byte[512], ref any);
        }
        catch (SocketException)
        {
            // No more queued datagrams.
        }

        // After the 1s window, a fresh datagram from a new client must still relay.
        await Task.Delay(1100);
        using var lateClient = new UdpClient();
        var late = Encoding.ASCII.GetBytes("late");
        await lateClient.SendAsync(late, late.Length, new IPEndPoint(IPAddress.Loopback, local));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reply = await lateClient.ReceiveAsync(cts.Token);
        CollectionAssert.AreEqual(late, reply.Buffer);

        listener.Dispose();
        try { await backendTask.WaitAsync(TimeSpan.FromMilliseconds(200)); } catch { }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task UdpRelay_TwoClients_IndependentSessions()
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
                    var echo = Encoding.ASCII.GetBytes("echo:" + Encoding.ASCII.GetString(result.Buffer));
                    await backend.SendAsync(echo, echo.Length, result.RemoteEndPoint);
                }
            }
            catch { }
        });

        var options = new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            ForwardHost = "127.0.0.1",
            ForwardPort = backendPort,
            IdleTimeout = TimeSpan.FromSeconds(5),
        };
        await using var listener = new UdpRelayListener(options);
        listener.Start();
        var local = listener.LocalEndPoint!.Port;
        var dest = new IPEndPoint(IPAddress.Loopback, local);

        using var c1 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var c2 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await c1.SendAsync(Encoding.ASCII.GetBytes("a"), 1, dest);
        await c2.SendAsync(Encoding.ASCII.GetBytes("b"), 1, dest);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var r1 = await c1.ReceiveAsync(cts.Token);
        var r2 = await c2.ReceiveAsync(cts.Token);
        Assert.AreEqual("echo:a", Encoding.ASCII.GetString(r1.Buffer));
        Assert.AreEqual("echo:b", Encoding.ASCII.GetString(r2.Buffer));
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
