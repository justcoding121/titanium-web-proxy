#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
using System.Net.Quic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     HTTP/3 streaming-body coverage: <c>OnResponseBodyWrite</c>, <c>RespondStreaming</c>,
///     <c>OnRequestBodyWrite</c>, and <c>GetRequestBody</c> after headers-only BeforeRequest.
/// </summary>
[TestClass]
public class Http3StreamingBodyTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    private static ProxyServer CreateHttp3Proxy(TransparentQuicProxyEndPoint quicEndPoint)
    {
        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp3 = true,
            EnableHttpsSvcbDnsDiscovery = false
        };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };

        quicEndPoint.BeforeQuicAuthenticate += async (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            await Task.CompletedTask;
        };

        proxy.AddEndPoint(quicEndPoint);
        proxy.Start();
        return proxy;
    }

    [TestMethod]
    public async Task OnResponseBodyWrite_Http3_Can_Rewrite_Body()
    {
        RequireQuic();

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "hello world",
            Encoding.ASCII.GetBytes("hello world"), dataFrameSize: 5)));

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port
        };

        using var proxy = CreateHttp3Proxy(quicEp);
        var hookCount = 0;
        proxy.OnResponseBodyWrite += (_, e) =>
        {
            Interlocked.Increment(ref hookCount);
            var text = Encoding.ASCII.GetString(e.BodyBytes);
            e.BodyBytes = Encoding.ASCII.GetBytes(text.ToUpperInvariant());
            return Task.CompletedTask;
        };

        await using var client = await QuicHttp3Client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");

        var response = await client.SendAsync("GET", $"localhost:{origin.Port}", "/rw");

        Assert.AreEqual(200, response.StatusCode);
        Assert.AreEqual("HELLO WORLD", response.TextBody);
        Assert.IsTrue(hookCount > 1, "Multi-frame origin response should invoke the hook more than once.");
    }

    [TestMethod]
    public async Task RespondStreaming_Http3_Generates_Body_Without_Contacting_Server()
    {
        RequireQuic();

        var originHits = 0;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ =>
        {
            Interlocked.Increment(ref originHits);
            return Task.FromResult(new QuicHttp3Response(200, "should-not-run"));
        });

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port
        };

        using var proxy = CreateHttp3Proxy(quicEp);
        proxy.BeforeRequest += (_, e) =>
        {
            var response = new Response
            {
                StatusCode = 200,
                StatusDescription = "OK",
                HttpVersion = e.HttpClient.Request.HttpVersion
            };
            e.RespondStreaming(response, async (stream, ct) =>
            {
                foreach (var part in new[] { "chunk1", "chunk2", "chunk3" })
                {
                    var bytes = Encoding.ASCII.GetBytes(part);
                    await stream.WriteAsync(bytes, ct);
                }
            });
            return Task.CompletedTask;
        };

        await using var client = await QuicHttp3Client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");

        var response = await client.SendAsync("GET", $"localhost:{origin.Port}", "/synth-stream");

        Assert.AreEqual(200, response.StatusCode);
        Assert.AreEqual("chunk1chunk2chunk3", response.TextBody);
        Assert.AreEqual(0, originHits);
    }

    [TestMethod]
    public async Task OnRequestBodyWrite_Http3_Streams_MultiFrame_Post_To_Origin()
    {
        RequireQuic();

        var payload = Encoding.UTF8.GetBytes("abcdefghij"); // 10 bytes → 5 frames of 2
        byte[]? seen = null;
        var originFrames = 0;

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            seen = req.Body;
            originFrames = req.DataFrameCount;
            return Task.FromResult(new QuicHttp3Response(200, $"len={req.Body.Length}"));
        });

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port
        };

        using var proxy = CreateHttp3Proxy(quicEp);
        var hookCount = 0;
        var beforeRequestSawBody = false;
        proxy.BeforeRequest += (_, e) =>
        {
            beforeRequestSawBody = e.HttpClient.Request.IsBodyRead;
            return Task.CompletedTask;
        };
        proxy.OnRequestBodyWrite += (_, e) =>
        {
            Interlocked.Increment(ref hookCount);
            return Task.CompletedTask;
        };

        await using var client = await QuicHttp3Client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");

        var response = await client.SendAsync("POST", $"localhost:{origin.Port}", "/echo", payload,
            requestDataFrameSize: 2);

        Assert.AreEqual(200, response.StatusCode);
        Assert.AreEqual($"len={payload.Length}", response.TextBody);
        CollectionAssert.AreEqual(payload, seen);
        Assert.IsFalse(beforeRequestSawBody, "BeforeRequest must run before the body is buffered.");
        Assert.IsTrue(hookCount > 1, "OnRequestBodyWrite should fire per DATA frame during live relay.");
        Assert.IsTrue(originFrames > 1, "Origin should receive multiple DATA frames.");
    }

    [TestMethod]
    public async Task GetRequestBody_Http3_In_BeforeRequest_Still_RoundTrips()
    {
        RequireQuic();

        var payload = Encoding.UTF8.GetBytes("buffered-h3-body");
        byte[]? seen = null;
        byte[]? fromHandler = null;

        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(req =>
        {
            seen = req.Body;
            return Task.FromResult(new QuicHttp3Response(200, "ok"));
        });

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port
        };

        using var proxy = CreateHttp3Proxy(quicEp);
        proxy.BeforeRequest += async (_, e) =>
        {
            fromHandler = await e.GetRequestBody();
        };

        await using var client = await QuicHttp3Client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");

        var response = await client.SendAsync("POST", $"localhost:{origin.Port}", "/buf", payload);

        Assert.AreEqual(200, response.StatusCode);
        CollectionAssert.AreEqual(payload, fromHandler);
        CollectionAssert.AreEqual(payload, seen);
    }

    [TestMethod]
    public async Task BeforeRequest_Ok_On_Post_Aborts_Unread_Body_Without_Hang()
    {
        RequireQuic();

        var originHits = 0;
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ =>
        {
            Interlocked.Increment(ref originHits);
            return Task.FromResult(new QuicHttp3Response(200, "should-not-run"));
        });

        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = origin.Port
        };

        using var proxy = CreateHttp3Proxy(quicEp);
        proxy.BeforeRequest += (_, e) =>
        {
            e.Ok("synthetic-post");
            return Task.CompletedTask;
        };

        await using var client = await QuicHttp3Client.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var payload = Encoding.UTF8.GetBytes(new string('x', 64 * 1024));
        var response = await client.SendAsync("POST", $"localhost:{origin.Port}", "/abort", payload,
            cts.Token);

        Assert.AreEqual(200, response.StatusCode);
        Assert.AreEqual("synthetic-post", response.TextBody);
        Assert.AreEqual(0, originHits);
    }
}

#pragma warning restore TWP001
#pragma warning restore CA1416
