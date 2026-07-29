using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Benchmarks.Support;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Benchmarks;

/// <summary>
///     End-to-end HTTP/2 throughput through a real <see cref="ProxyServer" /> explicit endpoint,
///     MITM-decrypting the client leg and re-encrypting to a Kestrel origin that also speaks HTTP/2.
///     <see cref="ConcurrentStreams" /> issues that many requests concurrently over one
///     <see cref="HttpClient" />, which HTTP/2 multiplexes onto a single TCP connection on both legs -
///     this is what item 11's proxy-owned concurrent-stream cap and CONTINUATION/reset budgets need a
///     real number for, rather than a guess at what "normal" concurrency looks like.
/// </summary>
[MemoryDiagnoser]
public class Http2ProxyThroughputBenchmarks
{
    private const string ResponseBody = "{\"ok\":true,\"payload\":\"0123456789abcdef0123456789abcdef\"}";

    [Params(1, 10, 50)]
    public int ConcurrentStreams { get; set; }

    private ProxyServer proxyServer = null!;
    private WebApplication originApp = null!;
    private HttpClient client = null!;
    private Uri targetUri = null!;

    [GlobalSetup]
    public void Setup()
    {
        var originPort = GetFreeTcpPort();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, originPort, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
                listenOptions.UseHttps(LoopbackCertificateAuthority.ServerCertificate);
            });
        });
        originApp = builder.Build();
        originApp.MapGet("/bench", () => ResponseBody);
        originApp.Start();

        proxyServer = new ProxyServer(false, false, false);
        proxyServer.CertificateManager.RootCertificateName = LoopbackCertificateAuthority.RootCertificateName;
        proxyServer.CertificateManager.RootCertificate = LoopbackCertificateAuthority.RootCertificate;
        proxyServer.CertificateManager.SaveFakeCertificates = false;
        proxyServer.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = LoopbackCertificateAuthority.Validate(args.Certificate);
            return Task.CompletedTask;
        };

        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0);
        proxyServer.AddEndPoint(endPoint);
        proxyServer.Start();

        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{endPoint.Port}"),
            UseProxy = true,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                LoopbackCertificateAuthority.Validate(cert)
        };
        client = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        targetUri = new Uri($"https://localhost:{originPort}/bench");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        client.Dispose();
        proxyServer.Stop();
        proxyServer.Dispose();
        originApp.StopAsync().GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task MultiplexedGets()
    {
        var tasks = new Task<string>[ConcurrentStreams];
        for (var i = 0; i < ConcurrentStreams; i++)
            tasks[i] = client.GetStringAsync(targetUri);
        await Task.WhenAll(tasks);
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
