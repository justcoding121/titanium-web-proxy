using System.Globalization;

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
        string? nginxPath, CancellationToken cancellationToken)
    {
        if (mode == ProbeMode.Compare)
        {
            Console.Error.WriteLine("--serve-proxy requires a single mode");
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
                var nginx = NginxHost.TryStart(originHttpPort, nginxPath)
                            ?? throw new InvalidOperationException(NginxHost.NginxMissingMessage());
                proxy = nginx;
                listenUrl = nginx.ListenUrl;
                targetForClient = nginx.ListenUrl;
                nginxVersion = nginx.Version;
                break;
            }
            case ProbeMode.HttpsMitm:
            {
                if (originHttpsPort <= 0) throw new ArgumentException("origin-https-port required for https-mitm");
                var twp = TwpProxyHost.StartHttpsMitm();
                proxy = twp;
                listenUrl = twp.ListenUrl;
                explicitProxy = twp.ListenUrl;
                targetForClient = $"https://127.0.0.1:{originHttpsPort}/";
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

    private static string ModeName(ProbeMode mode) => mode switch
    {
        ProbeMode.ReverseHttp1 => "reverse-http1",
        ProbeMode.NginxReverseHttp1 => "nginx-reverse-http1",
        ProbeMode.HttpsMitm => "https-mitm",
        _ => mode.ToString()
    };
}

internal static class ServeHost
{
    /// <summary>
    /// Combined origin+proxy for manual bombardier use (--serve).
    /// </summary>
    public static async Task<int> RunAsync(ProbeMode mode, string? nginxPath, CancellationToken cancellationToken)
    {
        if (mode == ProbeMode.Compare)
        {
            Console.Error.WriteLine("--serve requires a single mode: reverse-http1 | nginx-reverse-http1 | https-mitm");
            return 2;
        }

        if (mode == ProbeMode.NginxReverseHttp1 && NginxHost.ResolveNginxExecutable(nginxPath) == null)
        {
            Console.Error.WriteLine(NginxHost.NginxMissingMessage());
            return 3;
        }

        await using var stack = await ServeStack.StartAsync(mode, nginxPath, cancellationToken);
        Console.WriteLine(MachineInfo.FormatReport(stack.NginxVersion));
        Console.WriteLine($"mode={ModeName(mode)}");
        Console.WriteLine($"origin_http={stack.OriginHttpUrl}");
        if (stack.OriginHttpsUrl != null)
            Console.WriteLine($"origin_https={stack.OriginHttpsUrl}");
        Console.WriteLine($"listen={stack.ListenUrl}");
        if (stack.ExplicitProxyUrl != null)
            Console.WriteLine($"explicit_proxy={stack.ExplicitProxyUrl}");
        Console.WriteLine($"target_for_client={stack.ClientTargetUrl}");
        Console.WriteLine("READY");
        Console.WriteLine();
        Console.WriteLine("Ready. Example:");
        if (stack.ExplicitProxyUrl != null)
        {
            Console.WriteLine($"  bombardier -c 256 -d 30s -l -x {stack.ExplicitProxyUrl} {stack.ClientTargetUrl}");
        }
        else
        {
            Console.WriteLine($"  bombardier -c 256 -d 30s -l {stack.ClientTargetUrl}");
        }

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

    private static string ModeName(ProbeMode mode) => mode switch
    {
        ProbeMode.ReverseHttp1 => "reverse-http1",
        ProbeMode.NginxReverseHttp1 => "nginx-reverse-http1",
        ProbeMode.HttpsMitm => "https-mitm",
        _ => mode.ToString()
    };

    private sealed class ServeStack : IAsyncDisposable
    {
        private readonly OriginServer origin;
        private readonly IDisposable? proxy;

        public string OriginHttpUrl { get; }
        public string? OriginHttpsUrl { get; }
        public string ListenUrl { get; }
        public string? ExplicitProxyUrl { get; }
        public string ClientTargetUrl { get; }
        public string? NginxVersion { get; }

        private ServeStack(OriginServer origin, IDisposable? proxy, string originHttpUrl, string? originHttpsUrl,
            string listenUrl, string? explicitProxyUrl, string clientTargetUrl, string? nginxVersion)
        {
            this.origin = origin;
            this.proxy = proxy;
            OriginHttpUrl = originHttpUrl;
            OriginHttpsUrl = originHttpsUrl;
            ListenUrl = listenUrl;
            ExplicitProxyUrl = explicitProxyUrl;
            ClientTargetUrl = clientTargetUrl;
            NginxVersion = nginxVersion;
        }

        public static async Task<ServeStack> StartAsync(ProbeMode mode, string? nginxPath,
            CancellationToken cancellationToken)
        {
            switch (mode)
            {
                case ProbeMode.ReverseHttp1:
                {
                    var origin = await OriginServer.StartAsync(false, cancellationToken);
                    var twp = TwpProxyHost.StartReverseHttp1(origin.HttpPort);
                    return new ServeStack(origin, twp, origin.HttpUrl, null, twp.ListenUrl, null, twp.ListenUrl, null);
                }
                case ProbeMode.NginxReverseHttp1:
                {
                    var origin = await OriginServer.StartAsync(false, cancellationToken);
                    var nginx = NginxHost.TryStart(origin.HttpPort, nginxPath)
                                ?? throw new InvalidOperationException("nginx not available.");
                    return new ServeStack(origin, nginx, origin.HttpUrl, null, nginx.ListenUrl, null, nginx.ListenUrl,
                        nginx.Version);
                }
                case ProbeMode.HttpsMitm:
                {
                    var origin = await OriginServer.StartAsync(true, cancellationToken);
                    var twp = TwpProxyHost.StartHttpsMitm();
                    return new ServeStack(origin, twp, origin.HttpUrl, origin.HttpsUrl, twp.ListenUrl, twp.ListenUrl,
                        origin.HttpsUrl, null);
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
