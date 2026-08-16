using System.Globalization;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Titanium.Web.Proxy.RpsLoadProbe;

internal static class ServeOriginHost
{
    public static async Task<int> RunAsync(bool enableHttps, CancellationToken cancellationToken)
    {
        await using var origin = await OriginServer.StartAsync(enableHttps, cancellationToken);
        Console.WriteLine($"origin_http={origin.HttpUrl}");
        if (enableHttps)
            Console.WriteLine($"origin_https={origin.HttpsUrl}");
        Console.WriteLine("READY");
        Console.Out.Flush();

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }
}

internal static class ServeProxyHost
{
    public static async Task<int> RunAsync(ProbeMode mode, int originHttpPort, int originHttpsPort,
        string? nginxPath, int? maxCachedConnections, CancellationToken cancellationToken)
    {
        if (mode is ProbeMode.Compare or ProbeMode.CompareHttp2 or ProbeMode.ExplicitPoolSweep)
        {
            Console.Error.WriteLine("--serve-proxy requires a single arm mode");
            return 2;
        }

        // TLS arms need shared CA with origin — use --serve combined instead.
        if (mode is ProbeMode.ReverseHttp2 or ProbeMode.NginxReverseHttp2 or ProbeMode.ReverseHttp3
            or ProbeMode.HttpsMitm or ProbeMode.ExplicitHttp1Multi or ProbeMode.ExplicitHttp2Multi)
        {
            Console.Error.WriteLine(
                $"Mode {mode} requires --serve (combined origin+proxy) so the test CA is shared.");
            return 2;
        }

        IDisposable proxy;
        string listenUrl;
        string? explicitProxy = null;
        string targetForClient;
        string? nginxVersion = null;

        switch (mode)
        {
            case ProbeMode.ReverseHttp1:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var twp = TwpProxyHost.StartReverseHttp1(originHttpPort);
                proxy = twp;
                listenUrl = twp.ListenUrl;
                targetForClient = twp.ListenUrl;
                break;
            }
            case ProbeMode.NginxReverseHttp1:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var nginx = NginxHost.TryStartHttp1(originHttpPort, nginxPath)
                            ?? throw new InvalidOperationException(NginxHost.NginxMissingMessage());
                proxy = nginx;
                listenUrl = nginx.ListenUrl;
                targetForClient = nginx.ListenUrl;
                nginxVersion = nginx.Version;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        using (proxy)
        {
            Console.WriteLine($"mode={ModeName(mode)}");
            Console.WriteLine($"listen={listenUrl}");
            if (explicitProxy != null)
                Console.WriteLine($"explicit_proxy={explicitProxy}");
            Console.WriteLine($"target_for_client={targetForClient}");
            if (nginxVersion != null)
                Console.WriteLine($"nginx={nginxVersion}");
            if (maxCachedConnections is { } m)
                Console.WriteLine($"max_cached_connections={m}");
            Console.WriteLine("READY");
            Console.Out.Flush();

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        return 0;
    }

    internal static string ModeName(ProbeMode mode) => mode switch
    {
        ProbeMode.ReverseHttp1 => "reverse-http1",
        ProbeMode.NginxReverseHttp1 => "nginx-reverse-http1",
        ProbeMode.HttpsMitm => "https-mitm",
        ProbeMode.ReverseHttp2 => "reverse-http2",
        ProbeMode.NginxReverseHttp2 => "nginx-reverse-http2",
        ProbeMode.ReverseHttp3 => "reverse-http3",
        ProbeMode.ExplicitHttp1Multi => "explicit-http1-multi",
        ProbeMode.ExplicitHttp2Multi => "explicit-http2-multi",
        ProbeMode.Compare => "compare",
        ProbeMode.CompareHttp2 => "compare-http2",
        ProbeMode.ExplicitPoolSweep => "explicit-pool-sweep",
        _ => mode.ToString()
    };
}

internal static class ServeHost
{
    public static async Task<int> RunAsync(ProbeMode mode, string? nginxPath, int? maxCachedConnections,
        CancellationToken cancellationToken)
    {
        if (mode is ProbeMode.Compare or ProbeMode.CompareHttp2 or ProbeMode.ExplicitPoolSweep)
        {
            Console.Error.WriteLine("--serve requires a single mode");
            return 2;
        }

        if ((mode is ProbeMode.NginxReverseHttp1 or ProbeMode.NginxReverseHttp2)
            && NginxHost.ResolveNginxExecutable(nginxPath) == null)
        {
            Console.Error.WriteLine(NginxHost.NginxMissingMessage());
            return 3;
        }

        await using var stack = await ServeStack.StartAsync(mode, nginxPath, maxCachedConnections, cancellationToken);
        Console.WriteLine(MachineInfo.FormatReport(stack.NginxVersion));
        Console.WriteLine($"mode={ServeProxyHost.ModeName(mode)}");
        Console.WriteLine($"origin_http={stack.OriginHttpUrl}");
        if (stack.OriginHttpsUrl != null)
            Console.WriteLine($"origin_https={stack.OriginHttpsUrl}");
        foreach (var url in stack.ExtraOriginHttpsUrls)
            Console.WriteLine($"origin_https_extra={url}");
        Console.WriteLine($"listen={stack.ListenUrl}");
        if (stack.ExplicitProxyUrl != null)
            Console.WriteLine($"explicit_proxy={stack.ExplicitProxyUrl}");
        Console.WriteLine($"target_for_client={stack.ClientTargetUrl}");
        foreach (var url in stack.ClientTargetUrls.Skip(1))
            Console.WriteLine($"target_for_client_extra={url}");
        if (stack.HttpVersion != null)
            Console.WriteLine($"http_version={stack.HttpVersion}");
        if (stack.LoadGenerator != null)
            Console.WriteLine($"load_generator={stack.LoadGenerator}");
        if (stack.QuicPort is { } qp)
            Console.WriteLine($"quic_port={qp}");
        if (stack.OriginQuicPort is { } oqp)
            Console.WriteLine($"origin_quic_port={oqp}");
        if (maxCachedConnections is { } m)
            Console.WriteLine($"max_cached_connections={m}");
        if (stack.ServerConnectionProbe != null)
            Console.WriteLine("server_connection_probe=1");
        Console.WriteLine("READY");
        Console.WriteLine();
        Console.WriteLine("Ready. Example:");
        if (stack.ExplicitProxyUrl != null)
            Console.WriteLine($"  bombardier -c 256 -d 30s -l -x {stack.ExplicitProxyUrl} {stack.ClientTargetUrl}");
        else
            Console.WriteLine($"  bombardier -c 256 -d 30s -l {stack.ClientTargetUrl}");

        Console.WriteLine();
        Console.WriteLine("Press Ctrl+C to stop.");

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    internal sealed class ServeStack : IAsyncDisposable
    {
        private readonly IAsyncDisposable origin;
        private readonly IDisposable? proxy;
        private readonly TwpProxyHost? twp;

        public string OriginHttpUrl { get; }
        public string? OriginHttpsUrl { get; }
        public IReadOnlyList<string> ExtraOriginHttpsUrls { get; }
        public string ListenUrl { get; }
        public string? ExplicitProxyUrl { get; }
        public string ClientTargetUrl { get; }
        public IReadOnlyList<string> ClientTargetUrls { get; }
        public string? NginxVersion { get; }
        public string? HttpVersion { get; }
        public string? LoadGenerator { get; }
        public int? QuicPort { get; }
        public int? OriginQuicPort { get; }
        public Func<int>? ServerConnectionProbe { get; }

        private ServeStack(IAsyncDisposable origin, IDisposable? proxy, TwpProxyHost? twp, string originHttpUrl,
            string? originHttpsUrl, IReadOnlyList<string> extraOriginHttpsUrls, string listenUrl,
            string? explicitProxyUrl, string clientTargetUrl, IReadOnlyList<string> clientTargetUrls,
            string? nginxVersion, string? httpVersion, string? loadGenerator = null, int? quicPort = null,
            int? originQuicPort = null)
        {
            this.origin = origin;
            this.proxy = proxy;
            this.twp = twp;
            OriginHttpUrl = originHttpUrl;
            OriginHttpsUrl = originHttpsUrl;
            ExtraOriginHttpsUrls = extraOriginHttpsUrls;
            ListenUrl = listenUrl;
            ExplicitProxyUrl = explicitProxyUrl;
            ClientTargetUrl = clientTargetUrl;
            ClientTargetUrls = clientTargetUrls;
            NginxVersion = nginxVersion;
            HttpVersion = httpVersion;
            LoadGenerator = loadGenerator;
            QuicPort = quicPort;
            OriginQuicPort = originQuicPort;
            ServerConnectionProbe = twp == null ? null : () => twp.Server.ServerConnectionCount;
        }

        public static async Task<ServeStack> StartAsync(ProbeMode mode, string? nginxPath,
            int? maxCachedConnections, CancellationToken cancellationToken)
        {
            switch (mode)
            {
                case ProbeMode.ReverseHttp1:
                {
                    var origin = await OriginServer.StartAsync(false, cancellationToken);
                    var twp = TwpProxyHost.StartReverseHttp1(origin.HttpPort);
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, null, [], twp.ListenUrl, null,
                        twp.ListenUrl, [twp.ListenUrl], null, "1.1");
                }
                case ProbeMode.NginxReverseHttp1:
                {
                    var origin = await OriginServer.StartAsync(false, cancellationToken);
                    var nginx = NginxHost.TryStartHttp1(origin.HttpPort, nginxPath)
                                ?? throw new InvalidOperationException("nginx not available.");
                    return new ServeStack(origin, nginx, null, origin.HttpUrl, null, [], nginx.ListenUrl, null,
                        nginx.ListenUrl, [nginx.ListenUrl], nginx.Version, "1.1");
                }
                case ProbeMode.HttpsMitm:
                {
                    var origin = await OriginServer.StartAsync(true, cancellationToken);
                    var twp = TwpProxyHost.StartHttpsMitm(maxCachedConnections);
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, origin.HttpsUrl, [], twp.ListenUrl,
                        twp.ListenUrl, origin.HttpsUrl, [origin.HttpsUrl], null, "1.1");
                }
                case ProbeMode.ReverseHttp2:
                {
                    var origin = await OriginServer.StartAsync(new OriginListenOptions
                    {
                        EnableHttp = false,
                        EnableHttps = true,
                        HttpsProtocols = HttpProtocols.Http1AndHttp2
                    }, cancellationToken);
                    var twp = TwpProxyHost.StartReverseHttp2(origin.HttpsPort);
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, origin.HttpsUrl, [], twp.ListenUrl, null,
                        twp.ListenUrl, [twp.ListenUrl], null, "2.0");
                }
                case ProbeMode.NginxReverseHttp2:
                {
                    var origin = await OriginServer.StartAsync(new OriginListenOptions
                    {
                        EnableHttp = false,
                        EnableHttps = true,
                        HttpsProtocols = HttpProtocols.Http1AndHttp2
                    }, cancellationToken);
                    var nginx = NginxHost.TryStartHttp2(origin.HttpsPort, nginxPath)
                                ?? throw new InvalidOperationException("nginx not available.");
                    return new ServeStack(origin, nginx, null, origin.HttpUrl, origin.HttpsUrl, [], nginx.ListenUrl,
                        null, nginx.ListenUrl, [nginx.ListenUrl], nginx.Version, "2.0");
                }
                case ProbeMode.ReverseHttp3:
                {
                    if (!System.Net.Quic.QuicListener.IsSupported)
                        throw new PlatformNotSupportedException("QuicListener is not supported.");

                    var origin = new QuicHttp3OriginHost();
                    var twp = TwpProxyHost.StartReverseHttp3(origin.Port);
                    return new ServeStack(origin, twp, twp, $"quic://localhost:{origin.Port}/",
                        $"quic://localhost:{origin.Port}/", [], twp.ListenUrl, null, twp.ListenUrl, [twp.ListenUrl],
                        null, "3.0", loadGenerator: "quic-http3", quicPort: twp.Port, originQuicPort: origin.Port);
                }
                case ProbeMode.ExplicitHttp1Multi:
                case ProbeMode.ExplicitHttp2Multi:
                {
                    var httpVersion = mode == ProbeMode.ExplicitHttp2Multi ? "2.0" : "1.1";
                    var origin = await OriginServer.StartAsync(new OriginListenOptions
                    {
                        EnableHttp = false,
                        EnableHttps = true,
                        ExtraHttpsOriginCount = 15,
                        HttpsProtocols = HttpProtocols.Http1AndHttp2
                    }, cancellationToken);
                    var twp = TwpProxyHost.StartHttpsMitm(maxCachedConnections);
                    var targets = new List<string> { origin.HttpsUrl };
                    targets.AddRange(origin.ExtraHttpsPorts.Select(p => $"https://127.0.0.1:{p}/"));
                    var extras = origin.ExtraHttpsPorts.Select(p => $"https://127.0.0.1:{p}/").ToList();
                    // #region agent log
                    DebugSessionLog.Write("A", "ServeStack.ExplicitMulti", "started",
                        new
                        {
                            hostCount = targets.Count,
                            maxCached = maxCachedConnections ?? twp.Server.MaxCachedConnections,
                            httpVersion
                        });
                    // #endregion
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, origin.HttpsUrl, extras, twp.ListenUrl,
                        twp.ListenUrl, targets[0], targets, null, httpVersion);
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        public async ValueTask DisposeAsync()
        {
            proxy?.Dispose();
            await origin.DisposeAsync();
        }
    }
}
