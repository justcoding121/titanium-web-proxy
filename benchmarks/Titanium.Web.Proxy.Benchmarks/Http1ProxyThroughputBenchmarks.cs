using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Benchmarks;

/// <summary>
///     End-to-end HTTP/1 throughput through a real <see cref="ProxyServer" /> explicit endpoint,
///     against a plain-HTTP loopback origin (<see cref="HttpListener" />). Plain HTTP is deliberate:
///     it isolates proxying/interception cost from TLS handshake cost, which the HTTP/2 benchmark
///     already covers. <see cref="InterceptBody" /> toggles a <c>BeforeRequest</c>/<c>BeforeResponse</c>
///     hook that calls <c>GetRequestBody()</c>/<c>GetResponseBody()</c> - the buffering path the
///     plan's cumulative body-budget work centers on - so the interception overhead itself is a
///     directly comparable number, not just an inferred difference between two unrelated runs.
///
///     Each request's upstream leg goes through the real connection pool
///     (<c>TcpConnectionFactory.GetServerConnection</c>/<c>Release</c>), so this benchmark also
///     stands in for "connection-pool acquire/release" rather than that being a separate,
///     artificially isolated scenario - a repeated GET against the same loopback origin is exactly
///     the case the pool exists to speed up.
/// </summary>
[MemoryDiagnoser]
public class Http1ProxyThroughputBenchmarks
{
    private const string ResponseBody = "{\"ok\":true,\"payload\":\"0123456789abcdef0123456789abcdef\"}";

    [Params(false, true)]
    public bool InterceptBody { get; set; }

    private ProxyServer proxyServer = null!;
    private HttpListener originListener = null!;
    private HttpClient client = null!;
    private Uri targetUri = null!;

    [GlobalSetup]
    public void Setup()
    {
        originListener = new HttpListener();
        var originPort = GetFreeTcpPort();
        originListener.Prefixes.Add($"http://127.0.0.1:{originPort}/");
        originListener.Start();
        _ = Task.Run(RunOriginLoop);

        proxyServer = new ProxyServer(false, false, false);
        proxyServer.CertificateManager.SaveFakeCertificates = false;
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0);
        proxyServer.AddEndPoint(endPoint);

        if (InterceptBody)
        {
            proxyServer.BeforeRequest += async (_, args) =>
            {
                if (args.HttpClient.Request.HasBody) await args.GetRequestBody();
            };
            proxyServer.BeforeResponse += async (_, args) =>
            {
                if (args.HttpClient.Response.HasBody) await args.GetResponseBody();
            };
        }

        proxyServer.Start();

        client = new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{endPoint.Port}"),
            UseProxy = true
        });
        targetUri = new Uri($"http://127.0.0.1:{originPort}/bench");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        client.Dispose();
        proxyServer.Stop();
        proxyServer.Dispose();
        originListener.Stop();
        originListener.Close();
    }

    [Benchmark]
    public async Task<string> GetThroughProxy() => await client.GetStringAsync(targetUri);

    private async Task RunOriginLoop()
    {
        while (originListener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await originListener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(ResponseBody);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.OutputStream.Close();
        }
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
