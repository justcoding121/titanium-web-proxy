using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Titanium.Web.Proxy.RpsLoadProbe.Support;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Minimal YARP reverse proxy against the shared loopback origin.
/// Process-split from Kestrel origin (via --serve-proxy) for fair compare with nginx/TWP.
/// </summary>
internal sealed class YarpProxyHost : IDisposable
{
    private readonly WebApplication app;
    private readonly X509Certificate2? serverCertificate;

    public int Port { get; }
    public string ListenUrl { get; }
    public string Version { get; }

    private YarpProxyHost(WebApplication app, int port, string listenUrl, X509Certificate2? serverCertificate)
    {
        this.app = app;
        this.serverCertificate = serverCertificate;
        Port = port;
        ListenUrl = listenUrl;
        Version = ResolveVersion();
    }

    public static string ResolveVersion()
    {
        var asm = typeof(IProxyConfigProvider).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? asm.GetName().Version?.ToString()
                   ?? "unknown";
        var plus = info.IndexOf('+');
        if (plus > 0)
            info = info[..plus];
        return "YARP " + info;
    }

    /// <summary>H1 cleartext → H1 cleartext.</summary>
    public static Task<YarpProxyHost> StartHttp1Async(int originHttpPort) =>
        StartAsync(new YarpListenOptions
        {
            UseTls = false,
            InboundProtocols = HttpProtocols.Http1,
            DestinationAddress = $"http://127.0.0.1:{originHttpPort}/",
            OutboundVersion = HttpVersion.Version11,
            OutboundVersionPolicy = HttpVersionPolicy.RequestVersionExact
        });

    /// <summary>H1 TLS terminate → H1 cleartext.</summary>
    public static Task<YarpProxyHost> StartHttp1TlsAsync(int originHttpPort) =>
        StartAsync(new YarpListenOptions
        {
            UseTls = true,
            InboundProtocols = HttpProtocols.Http1,
            DestinationAddress = $"http://127.0.0.1:{originHttpPort}/",
            OutboundVersion = HttpVersion.Version11,
            OutboundVersionPolicy = HttpVersionPolicy.RequestVersionExact
        });

    /// <summary>H2 TLS → H1 cleartext (nginx parity).</summary>
    public static Task<YarpProxyHost> StartHttp2ToH1Async(int originHttpPort) =>
        StartAsync(new YarpListenOptions
        {
            UseTls = true,
            InboundProtocols = HttpProtocols.Http1AndHttp2,
            DestinationAddress = $"http://127.0.0.1:{originHttpPort}/",
            OutboundVersion = HttpVersion.Version11,
            OutboundVersionPolicy = HttpVersionPolicy.RequestVersionExact
        });

    /// <summary>H2 TLS → prior-knowledge h2c.</summary>
    public static Task<YarpProxyHost> StartHttp2ToH2cAsync(int originHttpPort) =>
        StartAsync(new YarpListenOptions
        {
            UseTls = true,
            InboundProtocols = HttpProtocols.Http1AndHttp2,
            DestinationAddress = $"http://127.0.0.1:{originHttpPort}/",
            OutboundVersion = HttpVersion.Version20,
            OutboundVersionPolicy = HttpVersionPolicy.RequestVersionExact
        });

    /// <summary>h2c → H1 cleartext.</summary>
    public static Task<YarpProxyHost> StartH2cToH1Async(int originHttpPort) =>
        StartAsync(new YarpListenOptions
        {
            UseTls = false,
            InboundProtocols = HttpProtocols.Http2,
            DestinationAddress = $"http://127.0.0.1:{originHttpPort}/",
            OutboundVersion = HttpVersion.Version11,
            OutboundVersionPolicy = HttpVersionPolicy.RequestVersionExact
        });

    /// <summary>h2c → h2c.</summary>
    public static Task<YarpProxyHost> StartH2cToH2cAsync(int originHttpPort) =>
        StartAsync(new YarpListenOptions
        {
            UseTls = false,
            InboundProtocols = HttpProtocols.Http2,
            DestinationAddress = $"http://127.0.0.1:{originHttpPort}/",
            OutboundVersion = HttpVersion.Version20,
            OutboundVersionPolicy = HttpVersionPolicy.RequestVersionExact
        });

    /// <summary>h2c → HTTPS h2.</summary>
    public static Task<YarpProxyHost> StartH2cToHttpsAsync(int originHttpsPort) =>
        StartAsync(new YarpListenOptions
        {
            UseTls = false,
            InboundProtocols = HttpProtocols.Http2,
            DestinationAddress = $"https://127.0.0.1:{originHttpsPort}/",
            OutboundVersion = HttpVersion.Version20,
            OutboundVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            AcceptAnyServerCertificate = true
        });

    /// <summary>h2c → HTTP/3 origin.</summary>
    public static Task<YarpProxyHost> StartH2cToHttp3Async(int originQuicPort) =>
        StartAsync(new YarpListenOptions
        {
            UseTls = false,
            InboundProtocols = HttpProtocols.Http2,
            DestinationAddress = $"https://127.0.0.1:{originQuicPort}/",
            OutboundVersion = HttpVersion.Version30,
            OutboundVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            AcceptAnyServerCertificate = true
        });

    /// <summary>H3 → H1 cleartext. Client uses HttpClient HTTP/3 (not transparent QUIC).</summary>
    public static Task<YarpProxyHost> StartHttp3CleartextAsync(int originHttpPort) =>
        StartAsync(new YarpListenOptions
        {
            UseTls = true,
            InboundProtocols = HttpProtocols.Http1AndHttp2AndHttp3,
            DestinationAddress = $"http://127.0.0.1:{originHttpPort}/",
            OutboundVersion = HttpVersion.Version11,
            OutboundVersionPolicy = HttpVersionPolicy.RequestVersionExact
        });

    /// <summary>H1 TLS → HTTPS h2.</summary>
    public static Task<YarpProxyHost> StartHttp1ToHttp2Async(int originHttpsPort) =>
        StartAsync(new YarpListenOptions
        {
            UseTls = true,
            InboundProtocols = HttpProtocols.Http1,
            DestinationAddress = $"https://127.0.0.1:{originHttpsPort}/",
            OutboundVersion = HttpVersion.Version20,
            OutboundVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            AcceptAnyServerCertificate = true
        });

    /// <summary>H1 TLS → HTTP/3 origin.</summary>
    public static Task<YarpProxyHost> StartHttp1ToHttp3Async(int originQuicPort) =>
        StartAsync(new YarpListenOptions
        {
            UseTls = true,
            InboundProtocols = HttpProtocols.Http1,
            DestinationAddress = $"https://127.0.0.1:{originQuicPort}/",
            OutboundVersion = HttpVersion.Version30,
            OutboundVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            AcceptAnyServerCertificate = true
        });

    /// <summary>H2 TLS → HTTP/3 origin.</summary>
    public static Task<YarpProxyHost> StartHttp2ToHttp3Async(int originQuicPort) =>
        StartAsync(new YarpListenOptions
        {
            UseTls = true,
            InboundProtocols = HttpProtocols.Http1AndHttp2,
            DestinationAddress = $"https://127.0.0.1:{originQuicPort}/",
            OutboundVersion = HttpVersion.Version30,
            OutboundVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            AcceptAnyServerCertificate = true
        });

    /// <summary>H3 → HTTPS h2.</summary>
    public static Task<YarpProxyHost> StartHttp3ToHttp2Async(int originHttpsPort) =>
        StartAsync(new YarpListenOptions
        {
            UseTls = true,
            InboundProtocols = HttpProtocols.Http1AndHttp2AndHttp3,
            DestinationAddress = $"https://127.0.0.1:{originHttpsPort}/",
            OutboundVersion = HttpVersion.Version20,
            OutboundVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            AcceptAnyServerCertificate = true
        });

    /// <summary>H3 → HTTP/3 origin.</summary>
    public static Task<YarpProxyHost> StartHttp3ToHttp3Async(int originQuicPort) =>
        StartAsync(new YarpListenOptions
        {
            UseTls = true,
            InboundProtocols = HttpProtocols.Http1AndHttp2AndHttp3,
            DestinationAddress = $"https://127.0.0.1:{originQuicPort}/",
            OutboundVersion = HttpVersion.Version30,
            OutboundVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            AcceptAnyServerCertificate = true
        });

    private sealed class YarpListenOptions
    {
        public bool UseTls { get; init; }
        public HttpProtocols InboundProtocols { get; init; }
        public required string DestinationAddress { get; init; }
        public required Version OutboundVersion { get; init; }
        public HttpVersionPolicy OutboundVersionPolicy { get; init; }
        public bool AcceptAnyServerCertificate { get; init; }
    }

    private static async Task<YarpProxyHost> StartAsync(YarpListenOptions options)
    {
        var port = GetFreeTcpPort();
        X509Certificate2? cert = null;
        if (options.UseTls)
            cert = LoopbackCertificateAuthority.ServerCertificate;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // Prevent default http://localhost:5000 from colliding with our Listen() port
        // (HTTP/3 especially fails hard when Kestrel tries both).
        builder.WebHost.UseUrls();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = 10 * 1024 * 1024;
            kestrel.Listen(IPAddress.Loopback, port, listen =>
            {
                listen.Protocols = options.InboundProtocols;
                if (options.UseTls)
                {
                    listen.UseHttps(https =>
                    {
                        https.ServerCertificate = cert!;
                        // HTTP/3 needs TLS 1.3; keep 1.2 for H1/H2 terminate arms.
                        https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                    });
                }
            });
        });

        var routes = new[]
        {
            new RouteConfig
            {
                RouteId = "catch-all",
                ClusterId = "origin",
                Match = new RouteMatch { Path = "{**catch-all}" }
            }
        };
        var clusters = new[]
        {
            new ClusterConfig
            {
                ClusterId = "origin",
                Destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["d1"] = new DestinationConfig { Address = options.DestinationAddress }
                },
                HttpRequest = new ForwarderRequestConfig
                {
                    Version = options.OutboundVersion,
                    VersionPolicy = options.OutboundVersionPolicy,
                    ActivityTimeout = TimeSpan.FromSeconds(100)
                },
                HttpClient = options.AcceptAnyServerCertificate
                    ? new HttpClientConfig
                    {
                        DangerousAcceptAnyServerCertificate = true,
                        MaxConnectionsPerServer = 256
                    }
                    : new HttpClientConfig { MaxConnectionsPerServer = 256 }
            }
        };

        builder.Services.AddReverseProxy().LoadFromMemory(routes, clusters);

        var app = builder.Build();
        app.MapReverseProxy();
        await app.StartAsync();

        var scheme = options.UseTls ? "https" : "http";
        return new YarpProxyHost(app, port, $"{scheme}://127.0.0.1:{port}/", cert);
    }

    public void Dispose()
    {
        try
        {
            app.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch
        {
            // best effort
        }

        app.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
