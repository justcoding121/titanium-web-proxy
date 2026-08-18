using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.RpsLoadProbe.Support;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Managed loopback origin that serves a fixed-size body and drains request bodies. Shared by every proxy arm.
/// </summary>
internal sealed class OriginServer : IAsyncDisposable
{
    public const string ResponseBody = "{\"ok\":true,\"payload\":\"0123456789abcdef0123456789abcdef\"}";

    private readonly IHost host;
    private readonly X509Certificate2? serverCertificate;
    private readonly List<X509Certificate2> extraCertificates = new();

    public int HttpPort { get; }
    public int HttpsPort { get; }
    public IReadOnlyList<int> ExtraHttpsPorts { get; }
    public string HttpUrl => $"http://127.0.0.1:{HttpPort}/";
    public string HttpsUrl => $"https://127.0.0.1:{HttpsPort}/";

    private OriginServer(IHost host, int httpPort, int httpsPort, IReadOnlyList<int> extraHttpsPorts,
        X509Certificate2? serverCertificate)
    {
        this.host = host;
        HttpPort = httpPort;
        HttpsPort = httpsPort;
        ExtraHttpsPorts = extraHttpsPorts;
        this.serverCertificate = serverCertificate;
    }

    public static byte[] BuildResponseBody(int responseBytes)
    {
        var tiny = Encoding.UTF8.GetBytes(ResponseBody);
        if (responseBytes <= tiny.Length)
            return tiny;

        var body = new byte[responseBytes];
        tiny.CopyTo(body, 0);
        for (var i = tiny.Length; i < body.Length; i++)
            body[i] = (byte)'x';
        return body;
    }

    public static async Task<OriginServer> StartAsync(OriginListenOptions options,
        CancellationToken cancellationToken = default)
    {
        var httpPort = options.EnableHttp ? GetFreeTcpPort() : 0;
        var httpsPort = options.EnableHttps ? GetFreeTcpPort() : 0;
        var extraHttpsPorts = new List<int>();
        for (var i = 0; i < options.ExtraHttpsOriginCount; i++)
            extraHttpsPorts.Add(GetFreeTcpPort());

        X509Certificate2? cert = options.EnableHttps || options.ExtraHttpsOriginCount > 0
            ? LoopbackCertificateAuthority.ServerCertificate
            : null;

        var responseBody = BuildResponseBody(options.ResponseBytes);
        var protocols = options.HttpsProtocols;
        var builder = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.ConfigureKestrel(kestrel =>
                {
                    // Large POST/GET bodies under reverse proxy load.
                    kestrel.Limits.MaxRequestBodySize = 10 * 1024 * 1024;
                    kestrel.Limits.Http2.MaxStreamsPerConnection = 1024;
                    kestrel.Limits.Http2.InitialConnectionWindowSize = 1024 * 1024;
                    kestrel.Limits.Http2.InitialStreamWindowSize = 768 * 1024;
                    if (options.EnableHttp)
                    {
                        kestrel.Listen(IPAddress.Loopback, httpPort, listenOptions =>
                        {
                            listenOptions.Protocols = options.HttpProtocols;
                        });
                    }

                    if (cert != null && httpsPort > 0)
                    {
                        kestrel.Listen(IPAddress.Loopback, httpsPort, listenOptions =>
                        {
                            listenOptions.Protocols = protocols;
                            listenOptions.UseHttps(cert);
                        });
                    }

                    foreach (var port in extraHttpsPorts)
                    {
                        kestrel.Listen(IPAddress.Loopback, port, listenOptions =>
                        {
                            listenOptions.Protocols = protocols;
                            listenOptions.UseHttps(cert!);
                        });
                    }
                });
                webBuilder.Configure(app =>
                {
                    app.Run(async context =>
                    {
                        // Drain request body so POST/PUT complete cleanly through proxies.
                        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
                            await context.Request.Body.CopyToAsync(Stream.Null);

                        context.Response.ContentType = "application/octet-stream";
                        // Fixed Content-Length avoids Transfer-Encoding: chunked, which stressed the
                        // H2→H1 bridge keep-alive path under multiplexed load.
                        context.Response.ContentLength = responseBody.Length;
                        await context.Response.Body.WriteAsync(responseBody);
                    });
                });
            });

        var host = builder.Build();
        await host.StartAsync(cancellationToken);
        return new OriginServer(host, httpPort, httpsPort, extraHttpsPorts, cert);
    }

    /// <summary> Backward-compatible helper used by HTTP/1 arms. </summary>
    public static Task<OriginServer> StartAsync(bool enableHttps, CancellationToken cancellationToken = default) =>
        StartAsync(new OriginListenOptions
        {
            EnableHttp = true,
            EnableHttps = enableHttps,
            HttpsProtocols = HttpProtocols.Http1AndHttp2
        }, cancellationToken);

    public static Task<OriginServer> StartAsync(bool enableHttps, int responseBytes,
        CancellationToken cancellationToken = default) =>
        StartAsync(new OriginListenOptions
        {
            EnableHttp = true,
            EnableHttps = enableHttps,
            HttpsProtocols = HttpProtocols.Http1AndHttp2,
            ResponseBytes = responseBytes
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await host.StopAsync(TimeSpan.FromSeconds(5));
        host.Dispose();
        serverCertificate?.Dispose();
        foreach (var c in extraCertificates)
            c.Dispose();
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

internal sealed class OriginListenOptions
{
    public bool EnableHttp { get; init; } = true;
    public bool EnableHttps { get; init; }
    public int ExtraHttpsOriginCount { get; init; }
    /// <summary>Protocols on the cleartext HTTP listen (default HTTP/1; use <see cref="HttpProtocols.Http2"/> for prior-knowledge h2c).</summary>
    public HttpProtocols HttpProtocols { get; init; } = HttpProtocols.Http1;
    public HttpProtocols HttpsProtocols { get; init; } = HttpProtocols.Http1AndHttp2;
    public int ResponseBytes { get; init; } = WorkloadOptions.TinyJsonBytes;
}
