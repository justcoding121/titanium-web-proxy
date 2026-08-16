using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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

    public int HttpPort { get; }
    public int HttpsPort { get; }
    public string HttpUrl => $"http://127.0.0.1:{HttpPort}/";
    public string HttpsUrl => $"https://127.0.0.1:{HttpsPort}/";

    private OriginServer(IHost host, int httpPort, int httpsPort, X509Certificate2? serverCertificate)
    {
        this.host = host;
        HttpPort = httpPort;
        HttpsPort = httpsPort;
        this.serverCertificate = serverCertificate;
    }

    public static async Task<OriginServer> StartAsync(bool enableHttps, CancellationToken cancellationToken = default)
    {
        var httpPort = GetFreeTcpPort();
        var httpsPort = enableHttps ? GetFreeTcpPort() : 0;
        X509Certificate2? cert = enableHttps ? LoopbackCertificateAuthority.ServerCertificate : null;

        var builder = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.ConfigureKestrel(options =>
                {
                    options.Listen(IPAddress.Loopback, httpPort);
                    if (cert != null)
                    {
                        options.Listen(IPAddress.Loopback, httpsPort, listenOptions =>
                        {
                            listenOptions.UseHttps(cert);
                        });
                    }
                });
                webBuilder.Configure(app =>
                {
                    app.Run(async context =>
                    {
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync(ResponseBody);
                    });
                });
            });

        var host = builder.Build();
        await host.StartAsync(cancellationToken);
        return new OriginServer(host, httpPort, httpsPort, cert);
    }

    public async ValueTask DisposeAsync()
    {
        await host.StopAsync(TimeSpan.FromSeconds(5));
        host.Dispose();
        serverCertificate?.Dispose();
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
