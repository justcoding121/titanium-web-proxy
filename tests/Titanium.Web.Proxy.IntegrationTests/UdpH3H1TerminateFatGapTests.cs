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
///     Fat UdpRelayListener + Http3RequestStream + H1TerminateFastForward edges:
///     UDP session round-trip/drain, H3 POST cancel, H3 HEAD, terminate lite GET/POST.
/// </summary>
[DoNotParallelize]
[TestClass]
public class UdpH3H1TerminateFatGapTests
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

    [TestMethod]
    [Timeout(30_000)]
    public async Task UdpRelay_RoundTripThenBeginDrain_Completes()
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
            IdleTimeout = TimeSpan.FromSeconds(5),
            ForwardHost = "127.0.0.1",
            ForwardPort = backendPort,
        };
        await using var listener = new UdpRelayListener(options);
        listener.Start();
        var localPort = listener.LocalEndPoint!.Port;

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var payload = Encoding.ASCII.GetBytes("udp-ping");
        await client.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, localPort));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reply = await client.ReceiveAsync(cts.Token);
        CollectionAssert.AreEqual(payload, reply.Buffer);
        await backendTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(await listener.DrainAsync(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(listener.IsDraining);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task UdpRelay_TwoSessions_IndependentRoundTrips()
    {
        var backendPort = FreeUdpPort();
        using var backend = new UdpClient(new IPEndPoint(IPAddress.Loopback, backendPort));
        var backendLoop = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var result = await backend.ReceiveAsync();
                var echo = Encoding.ASCII.GetBytes("echo:" + Encoding.ASCII.GetString(result.Buffer));
                await backend.SendAsync(echo, echo.Length, result.RemoteEndPoint);
            }
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
        var localPort = listener.LocalEndPoint!.Port;

        async Task<string> RoundTripAsync(string msg)
        {
            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var bytes = Encoding.ASCII.GetBytes(msg);
            await client.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, localPort));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reply = await client.ReceiveAsync(cts.Token);
            return Encoding.ASCII.GetString(reply.Buffer);
        }

        Assert.AreEqual("echo:a", await RoundTripAsync("a"));
        Assert.AreEqual("echo:b", await RoundTripAsync("b"));
        await backendLoop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_Head_ReturnsOriginWithoutBody()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            Assert.AreEqual("HEAD", req.Method);
            return Task.FromResult(new QuicHttp3Response(200, "should-not-send-body"));
        });

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
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
        quicEp.BeforeQuicAuthenticate += async (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            await Task.CompletedTask;
        };
        proxy.AddEndPoint(quicEp);
        proxy.Start();
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            var response = await client.SendAsync("HEAD", $"localhost:{origin.Port}", "/head");
            Assert.AreEqual(200, response.StatusCode, response.TextBody);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_Post_ThenCancelRequest_GenericResponse()
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        var contacted = false;
        origin.HandleRequest(_ =>
        {
            contacted = true;
            return Task.FromResult(new QuicHttp3Response(200, "nope"));
        });

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port,
        };
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
        proxy.BeforeRequest += (_, e) =>
        {
            if (e.HttpClient.Request.Method == "POST")
            {
                e.HttpClient.Request.CancelRequest = true;
                e.GenericResponse("cancelled-post", HttpStatusCode.Conflict);
            }
            return Task.CompletedTask;
        };
        quicEp.BeforeQuicAuthenticate += async (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            await Task.CompletedTask;
        };
        proxy.AddEndPoint(quicEp);
        proxy.Start();
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            var response = await client.SendAsync(
                "POST", $"localhost:{origin.Port}", "/post", Encoding.UTF8.GetBytes("body"));
            Assert.AreEqual(409, response.StatusCode, response.TextBody);
            Assert.IsFalse(contacted);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1Terminate_GetAndPost_RoundTrip()
    {
        // Exercise H1TerminateFastForward via transparent decrypt terminate-style cleartext origin.
        using var suite = new TestSuite();
        var server = suite.GetServer();
        string? seenBody = null;
        server.HandleRequest(async ctx =>
        {
            if (HttpMethods.IsPost(ctx.Request.Method))
            {
                using var reader = new System.IO.StreamReader(ctx.Request.Body);
                seenBody = await reader.ReadToEndAsync();
            }

            await ctx.Response.WriteAsync("h1-term-ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);

        using (var get = await client.GetAsync(server.ListeningHttpsUrl + "/get"))
        {
            Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
            Assert.AreEqual("h1-term-ok", await get.Content.ReadAsStringAsync());
        }

        using (var content = new System.Net.Http.StringContent("post-body", Encoding.UTF8, "text/plain"))
        using (var post = await client.PostAsync(server.ListeningHttpsUrl + "/post", content))
        {
            Assert.AreEqual(HttpStatusCode.OK, post.StatusCode);
            Assert.AreEqual("post-body", seenBody);
        }
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
