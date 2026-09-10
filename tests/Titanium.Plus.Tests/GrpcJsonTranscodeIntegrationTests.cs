using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Grpc;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Models;

namespace Titanium.Plus.Tests;

[TestClass]
public class GrpcJsonTranscodeIntegrationTests
{
    private static string FixturePb =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "GrpcTranscode", "greeter.pb");

    [TestMethod]
    [Timeout(60_000)]
    public async Task ExplicitProxy_TranscodesGet_ToUnaryGrpc()
    {
        await using var origin = await StartFakeGrpcOriginAsync();
        var transcoder = LoadTranscoder();

        using var proxy = CreateExplicitProxy(transcoder);
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}"),
            UseProxy = true
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        var response = await client.GetAsync($"http://127.0.0.1:{origin.Port}/v1/greeter/world");
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        StringAssert.Contains(body, "Hello world");
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ExplicitProxy_MapsNotFoundGrpcStatus()
    {
        await using var origin = await StartFakeGrpcOriginAsync(grpcStatus: 5, grpcMessage: "missing");
        var transcoder = LoadTranscoder();

        using var proxy = CreateExplicitProxy(transcoder);
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}"),
            UseProxy = true
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        var response = await client.GetAsync($"http://127.0.0.1:{origin.Port}/v1/greeter/x");
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, body);
        StringAssert.Contains(body, "\"code\":5");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ExplicitProxy_NullTranscoder_IsInert()
    {
        await using var origin = await StartPlainOriginAsync();
        using var proxy = CreateExplicitProxy(transcoder: null);
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}"),
            UseProxy = true
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        var response = await client.GetAsync($"http://127.0.0.1:{origin.Port}/");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("plain", await response.Content.ReadAsStringAsync());
    }

    private static GrpcJsonTranscoderImpl LoadTranscoder() =>
        GrpcJsonTranscoderImpl.LoadFromDescriptorSet(
            FixturePb,
            ["helloworld.Greeter"],
            convertGrpcStatus: true,
            ignoreUnknownQueryParameters: true,
            preserveProtoFieldNames: false,
            alwaysPrintPrimitiveFields: false);

    private static ProxyServer CreateExplicitProxy(IGrpcJsonTranscoder? transcoder)
    {
        var proxy = new ProxyServer();
        proxy.EnableHttpInterception = transcoder is not null;
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false));
        if (transcoder is not null)
            proxy.ReverseProxy = new ReverseProxyOptions { GrpcJsonTranscoder = transcoder };

        proxy.Start();
        return proxy;
    }

    private static async Task<RunningHost> StartPlainOriginAsync()
    {
        var host = new WebHostBuilder()
            .UseKestrel(o => o.Listen(IPAddress.Loopback, 0))
            .Configure(app => app.Run(async ctx =>
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.WriteAsync("plain");
            }))
            .Build();
        await host.StartAsync();
        return new RunningHost(host, ReadPort(host));
    }

    private static async Task<RunningHost> StartFakeGrpcOriginAsync(int grpcStatus = 0, string? grpcMessage = null)
    {
        var host = new WebHostBuilder()
            .UseKestrel(o => o.Listen(IPAddress.Loopback, 0))
            .Configure(app =>
            {
                app.Run(async context =>
                {
                    if (!context.Request.Path.StartsWithSegments("/helloworld.Greeter"))
                    {
                        context.Response.StatusCode = 404;
                        await context.Response.WriteAsync("not grpc path: " + context.Request.Path);
                        return;
                    }

                    var msg = Encoding.UTF8.GetBytes("Hello world");
                    var proto = new byte[2 + msg.Length];
                    proto[0] = 0x0A;
                    proto[1] = (byte)msg.Length;
                    Buffer.BlockCopy(msg, 0, proto, 2, msg.Length);
                    var framed = GrpcFrames.Encode(proto);

                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/grpc";
                    context.Response.ContentLength = framed.Length;
#pragma warning disable ASP0015 // Fake gRPC origin uses indexer headers.
                    context.Response.Headers["grpc-status"] = grpcStatus.ToString();
                    if (!string.IsNullOrEmpty(grpcMessage))
                        context.Response.Headers["grpc-message"] = grpcMessage;
#pragma warning restore ASP0015

                    await context.Response.Body.WriteAsync(framed);
                });
            })
            .Build();

        await host.StartAsync();
        return new RunningHost(host, ReadPort(host));
    }

    private static int ReadPort(IWebHost host) =>
        host.ServerFeatures.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.Select(a => new Uri(a).Port).First();

    private sealed class RunningHost(IWebHost host, int port) : IAsyncDisposable
    {
        public int Port { get; } = port;

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}
