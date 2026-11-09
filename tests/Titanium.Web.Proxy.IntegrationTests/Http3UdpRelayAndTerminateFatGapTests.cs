#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
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
///     Fat Http3RequestStream / UdpRelay / H1Terminate edges: H3 GET+POST, OPTIONS,
///     UDP oversized drop, UDP drain mid-session, explicit PUT via terminate path.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3UdpRelayAndTerminateFatGapTests
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
    public async Task H3_GetAndOptions_RoundTrip()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            if (req.Method == "OPTIONS")
                return Task.FromResult(new QuicHttp3Response(204, ""));
            return Task.FromResult(new QuicHttp3Response(200, "h3-get"));
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
            var get = await client.SendAsync("GET", $"localhost:{origin.Port}", "/g");
            Assert.AreEqual(200, get.StatusCode, get.TextBody);
            Assert.AreEqual("h3-get", get.TextBody);

            var options = await client.SendAsync("OPTIONS", $"localhost:{origin.Port}", "/o");
            Assert.AreEqual(204, options.StatusCode, options.TextBody);
        }
        finally
        {
            proxy.Stop();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_PostLargeBody_RoundTrips()
    {
        RequireQuic();
        var payload = Encoding.UTF8.GetBytes(new string('z', 4096));
        byte[]? seen = null;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            seen = req.Body;
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
            var response = await client.SendAsync(
                "POST", $"localhost:{origin.Port}", "/big", payload, requestDataFrameSize: 512);
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
            Assert.AreEqual($"n={payload.Length}", response.TextBody);
            CollectionAssert.AreEqual(payload, seen);
        }
        finally
        {
            proxy.Stop();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task UdpRelay_OversizedDatagram_IsDropped_SmallStillWorks()
    {
        var backendPort = FreeUdpPort();
        using var backend = new UdpClient(new IPEndPoint(IPAddress.Loopback, backendPort));
        var backendTask = Task.Run(async () =>
        {
            var result = await backend.ReceiveAsync();
            await backend.SendAsync(result.Buffer, result.Buffer.Length, result.RemoteEndPoint);
        });

        var options = new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            MaxDatagramBytes = 64,
            ForwardHost = "127.0.0.1",
            ForwardPort = backendPort,
            IdleTimeout = TimeSpan.FromSeconds(5),
        };
        await using var listener = new UdpRelayListener(options);
        listener.Start();
        var local = listener.LocalEndPoint!.Port;

        using var client = new UdpClient();
        var huge = new byte[200];
        Array.Fill(huge, (byte)0xab);
        await client.SendAsync(huge, huge.Length, new IPEndPoint(IPAddress.Loopback, local));

        var small = Encoding.ASCII.GetBytes("ok");
        await client.SendAsync(small, small.Length, new IPEndPoint(IPAddress.Loopback, local));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var reply = await client.ReceiveAsync(cts.Token);
        CollectionAssert.AreEqual(small, reply.Buffer);
        await backendTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task UdpRelay_DrainDuringActiveSession_Completes()
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
            IdleTimeout = TimeSpan.FromSeconds(1),
            ForwardHost = "127.0.0.1",
            ForwardPort = backendPort,
        };
        await using var listener = new UdpRelayListener(options);
        listener.Start();
        var local = listener.LocalEndPoint!.Port;

        using var client = new UdpClient();
        var payload = Encoding.ASCII.GetBytes("live");
        await client.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, local));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = await client.ReceiveAsync(cts.Token);

        Assert.IsTrue(await listener.DrainAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(listener.IsDraining);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Patch_RoundTrips()
    {
        string? seen = null;
        using var suite = new TestSuite();
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            using var reader = new System.IO.StreamReader(ctx.Request.Body);
            seen = await reader.ReadToEndAsync();
            await ctx.Response.WriteAsync("patched");
        });

        using var client = suite.GetClient(suite.GetProxy());
        using var content = new System.Net.Http.StringContent("diff", Encoding.UTF8, "text/plain");
        using var request = new System.Net.Http.HttpRequestMessage(
            new System.Net.Http.HttpMethod("PATCH"), server.ListeningHttpsUrl + "/patch")
        {
            Content = content,
        };
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("diff", seen);
        Assert.AreEqual("patched", await response.Content.ReadAsStringAsync());
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
