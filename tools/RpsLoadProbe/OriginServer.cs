using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.RpsLoadProbe.Support;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Tiny Kestrel origin that serves a fixed body. Shared by every proxy arm under test.
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

        var protocols = options.HttpsProtocols;
        var builder = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.ConfigureKestrel(kestrel =>
                {
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
                        var body = System.Text.Encoding.UTF8.GetBytes(ResponseBody);
                        context.Response.ContentType = "application/json";
                        // Fixed Content-Length avoids Transfer-Encoding: chunked, which stressed the
                        // H2→H1 bridge keep-alive path under multiplexed load.
                        context.Response.ContentLength = body.Length;
                        await context.Response.Body.WriteAsync(body);
                    });
                });
            });

        var host = builder.Build();
        await host.StartAsync(cancellationToken);
        return new OriginServer(host, httpPort, httpsPort, extraHttpsPorts, cert);
    }

    /// <summary>Backward-compatible helper used by HTTP/1 arms. </summary>
    public static Task<OriginServer> StartAsync(bool enableHttps, CancellationToken cancellationToken = default) =>
        StartAsync(new OriginListenOptions
        {
            EnableHttp = true,
            EnableHttps = enableHttps,
            HttpsProtocols = HttpProtocols.Http1AndHttp2
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
}
