using System.Globalization;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Titanium.Web.Proxy.RpsLoadProbe;

internal static class ServeOriginHost
{
    public static async Task<int> RunAsync(bool enableHttps, bool enableH2c, CancellationToken cancellationToken,
        WorkloadOptions? workload = null)
    {
        if (enableHttps && enableH2c)
            throw new ArgumentException("--https and --h2c are mutually exclusive.");

        workload ??= WorkloadOptions.TinyGet;
        var responseBytes = workload.ResponseBytes > 0 ? workload.ResponseBytes : WorkloadOptions.TinyJsonBytes;

        await using var origin = enableH2c
            ? await OriginServer.StartAsync(new OriginListenOptions
            {
                EnableHttp = true,
                EnableHttps = false,
                HttpProtocols = HttpProtocols.Http2,
                ResponseBytes = responseBytes
            }, cancellationToken, workload)
            : await OriginServer.StartAsync(enableHttps, responseBytes, cancellationToken, workload);
        await ProbeLog.WriteProtocolLineAsync($"origin_http={origin.HttpUrl}", cancellationToken);
        if (enableHttps)
            await ProbeLog.WriteProtocolLineAsync($"origin_https={origin.HttpsUrl}", cancellationToken);
        await ProbeLog.WriteProtocolLineAsync($"response_bytes={responseBytes}", cancellationToken);
        await ProbeLog.WriteProtocolLineAsync("READY", cancellationToken);

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
        if (mode is ProbeMode.Compare or ProbeMode.CompareHttp2 or ProbeMode.CompareTls
            or ProbeMode.CompareTerminate or ProbeMode.CompareSame or ProbeMode.CompareBridges
            or ProbeMode.CompareMitm or ProbeMode.CompareCeiling or ProbeMode.CompareBodies
            or ProbeMode.ComparePost or ProbeMode.CompareLossy or ProbeMode.CompareTlsCost
            or ProbeMode.CompareArch or ProbeMode.ExplicitPoolSweep)
        {
            ProbeLog.Error("--serve-proxy requires a single arm mode");
            return 2;
        }

        // Cleartext-origin terminate arms may use --serve-proxy (split). HTTPS/QUIC origin arms need --serve.
        if (mode is ProbeMode.ReverseHttp2 or ProbeMode.ReverseHttp3
            or ProbeMode.ReverseHttp11ToHttp2 or ProbeMode.YarpReverseHttp11ToHttp2
            or ProbeMode.ReverseHttp1ToHttp3 or ProbeMode.YarpReverseHttp1ToHttp3
            or ProbeMode.ReverseHttp2ToHttp3 or ProbeMode.YarpReverseHttp2ToHttp3
            or ProbeMode.ReverseHttp3ToHttp2 or ProbeMode.YarpReverseHttp3ToHttp2
            or ProbeMode.YarpReverseHttp3ToHttp3
            or ProbeMode.ReverseH2c or ProbeMode.YarpReverseH2c
            or ProbeMode.ReverseH2cToH3 or ProbeMode.YarpReverseH2cToH3
            or ProbeMode.MitmHttp2ToHttp1 or ProbeMode.MitmHttp3ToHttp1
            or ProbeMode.HttpsMitm or ProbeMode.ReverseHttp1Mitm
            or ProbeMode.ReverseHttp1ToHttps or ProbeMode.YarpReverseHttp1ToHttps
            or ProbeMode.ExplicitHttp1Multi or ProbeMode.ExplicitHttp2Multi)
        {
            ProbeLog.Error(
                $"Mode {mode} requires --serve (combined origin+proxy) so the test CA is shared.");
            return 2;
        }

        IDisposable proxy;
        string listenUrl;
        string? explicitProxy = null;
        string targetForClient;
        string? nginxVersion = null;
        string? yarpVersion = null;

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
            case ProbeMode.BareReverseHttp1:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var bare = BareHttp1ReverseProxy.Start(originHttpPort);
                proxy = bare;
                listenUrl = bare.ListenUrl;
                targetForClient = bare.ListenUrl;
                break;
            }
            case ProbeMode.NginxReverseHttp1:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var nginx = await NginxHost.TryStartHttp1Async(originHttpPort, nginxPath)
                            ?? throw new InvalidOperationException(NginxHost.NginxMissingMessage());
                proxy = nginx;
                listenUrl = nginx.ListenUrl;
                targetForClient = nginx.ListenUrl;
                nginxVersion = nginx.Version;
                break;
            }
            case ProbeMode.YarpReverseHttp1:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var yarp = await YarpProxyHost.StartHttp1Async(originHttpPort);
                proxy = yarp;
                listenUrl = yarp.ListenUrl;
                targetForClient = yarp.ListenUrl;
                yarpVersion = yarp.Version;
                break;
            }
            case ProbeMode.HttpMitm:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var twp = TwpProxyHost.StartHttpMitm();
                proxy = twp;
                listenUrl = twp.ListenUrl;
                explicitProxy = twp.ListenUrl;
                targetForClient = $"http://127.0.0.1:{originHttpPort}/";
                break;
            }
            case ProbeMode.ReverseHttp1Tls:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var twp = TwpProxyHost.StartReverseHttp1Tls(originHttpPort);
                proxy = twp;
                listenUrl = twp.ListenUrl;
                targetForClient = twp.ListenUrl;
                break;
            }
            case ProbeMode.BareReverseHttp1Tls:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var bare = BareHttp1ReverseProxy.Start(originHttpPort, tlsTerminate: true);
                proxy = bare;
                listenUrl = bare.ListenUrl;
                targetForClient = bare.ListenUrl;
                break;
            }
            case ProbeMode.NginxReverseHttp1Tls:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var nginx = await NginxHost.TryStartHttp1TlsAsync(originHttpPort, nginxPath)
                            ?? throw new InvalidOperationException(NginxHost.NginxMissingMessage());
                proxy = nginx;
                listenUrl = nginx.ListenUrl;
                targetForClient = nginx.ListenUrl;
                nginxVersion = nginx.Version;
                break;
            }
            case ProbeMode.YarpReverseHttp1Tls:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var yarp = await YarpProxyHost.StartHttp1TlsAsync(originHttpPort);
                proxy = yarp;
                listenUrl = yarp.ListenUrl;
                targetForClient = yarp.ListenUrl;
                yarpVersion = yarp.Version;
                break;
            }
            case ProbeMode.ReverseHttp2Cleartext:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var twp = TwpProxyHost.StartReverseHttp2Cleartext(originHttpPort);
                proxy = twp;
                listenUrl = twp.ListenUrl;
                targetForClient = twp.ListenUrl;
                break;
            }
            case ProbeMode.YarpReverseHttp2:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var yarp = await YarpProxyHost.StartHttp2ToH1Async(originHttpPort);
                proxy = yarp;
                listenUrl = yarp.ListenUrl;
                targetForClient = yarp.ListenUrl;
                yarpVersion = yarp.Version;
                break;
            }
            case ProbeMode.ReverseHttp2ToH2c:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var twp = TwpProxyHost.StartReverseHttp2ToH2c(originHttpPort);
                proxy = twp;
                listenUrl = twp.ListenUrl;
                targetForClient = twp.ListenUrl;
                break;
            }
            case ProbeMode.YarpReverseHttp2ToH2c:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var yarp = await YarpProxyHost.StartHttp2ToH2cAsync(originHttpPort);
                proxy = yarp;
                listenUrl = yarp.ListenUrl;
                targetForClient = yarp.ListenUrl;
                yarpVersion = yarp.Version;
                break;
            }
            case ProbeMode.ReverseH2cToH2c:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var twp = TwpProxyHost.StartReverseH2cToH2c(originHttpPort);
                proxy = twp;
                listenUrl = twp.ListenUrl;
                targetForClient = twp.ListenUrl;
                break;
            }
            case ProbeMode.YarpReverseH2cToH2c:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var yarp = await YarpProxyHost.StartH2cToH2cAsync(originHttpPort);
                proxy = yarp;
                listenUrl = yarp.ListenUrl;
                targetForClient = yarp.ListenUrl;
                yarpVersion = yarp.Version;
                break;
            }
            case ProbeMode.ReverseH2cToH1:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var twp = TwpProxyHost.StartReverseH2cToH1(originHttpPort);
                proxy = twp;
                listenUrl = twp.ListenUrl;
                targetForClient = twp.ListenUrl;
                break;
            }
            case ProbeMode.YarpReverseH2cToH1:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var yarp = await YarpProxyHost.StartH2cToH1Async(originHttpPort);
                proxy = yarp;
                listenUrl = yarp.ListenUrl;
                targetForClient = yarp.ListenUrl;
                yarpVersion = yarp.Version;
                break;
            }
            case ProbeMode.NginxReverseHttp2:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var nginx = await NginxHost.TryStartHttp2Async(originHttpPort, nginxPath)
                            ?? throw new InvalidOperationException(NginxHost.NginxMissingMessage());
                proxy = nginx;
                listenUrl = nginx.ListenUrl;
                targetForClient = nginx.ListenUrl;
                nginxVersion = nginx.Version;
                break;
            }
            case ProbeMode.NginxReverseHttp3Cleartext:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var nginx = await NginxHost.TryStartHttp3CleartextAsync(originHttpPort, nginxPath)
                            ?? throw new InvalidOperationException(
                                "nginx HTTP/3 is not available (need --with-http_v3_module). " +
                                NginxHost.NginxMissingMessage());
                proxy = nginx;
                listenUrl = nginx.ListenUrl;
                targetForClient = nginx.ListenUrl;
                nginxVersion = nginx.Version;
                break;
            }
            case ProbeMode.ReverseHttp3Cleartext:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var twp = TwpProxyHost.StartReverseHttp3Cleartext(originHttpPort);
                proxy = twp;
                listenUrl = twp.ListenUrl;
                targetForClient = twp.ListenUrl;
                break;
            }
            case ProbeMode.YarpReverseHttp3Cleartext:
            {
                if (originHttpPort <= 0) throw new ArgumentException("origin-http-port required");
                var yarp = await YarpProxyHost.StartHttp3CleartextAsync(originHttpPort);
                proxy = yarp;
                listenUrl = yarp.ListenUrl;
                targetForClient = yarp.ListenUrl;
                yarpVersion = yarp.Version;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        var httpVersion = mode switch
        {
            ProbeMode.ReverseHttp2Cleartext or ProbeMode.ReverseHttp2ToH2c or ProbeMode.NginxReverseHttp2
                or ProbeMode.YarpReverseHttp2 or ProbeMode.YarpReverseHttp2ToH2c
                or ProbeMode.ReverseH2c or ProbeMode.ReverseH2cToH2c or ProbeMode.ReverseH2cToH1
                or ProbeMode.ReverseH2cToH3 or ProbeMode.YarpReverseH2c or ProbeMode.YarpReverseH2cToH2c
                or ProbeMode.YarpReverseH2cToH1 or ProbeMode.YarpReverseH2cToH3 => "2.0",
            ProbeMode.ReverseHttp3Cleartext or ProbeMode.YarpReverseHttp3Cleartext
                or ProbeMode.NginxReverseHttp3Cleartext => "3.0",
            _ => "1.1"
        };
        // Dual-listen reverse H3 (TWP TransparentProxyEndPoint.EnableHttp3 and reference .NET server stack) use HttpClient.
        // UDP-only TransparentQuic arms still advertise quic-http3 via combined ServeStack.
        string? loadGenerator = null;

        using (proxy)
        {
            await ProbeLog.WriteProtocolLineAsync($"mode={ModeName(mode)}", cancellationToken);
            await ProbeLog.WriteProtocolLineAsync($"listen={listenUrl}", cancellationToken);
            if (explicitProxy != null)
                await ProbeLog.WriteProtocolLineAsync($"explicit_proxy={explicitProxy}", cancellationToken);
            await ProbeLog.WriteProtocolLineAsync($"target_for_client={targetForClient}", cancellationToken);
            await ProbeLog.WriteProtocolLineAsync($"http_version={httpVersion}", cancellationToken);
            if (loadGenerator != null)
                await ProbeLog.WriteProtocolLineAsync($"load_generator={loadGenerator}", cancellationToken);
            if (nginxVersion != null)
                await ProbeLog.WriteProtocolLineAsync($"nginx={nginxVersion}", cancellationToken);
            if (yarpVersion != null)
                await ProbeLog.WriteProtocolLineAsync($"yarp={yarpVersion}", cancellationToken);
            if (maxCachedConnections is { } m)
                await ProbeLog.WriteProtocolLineAsync($"max_cached_connections={m}", cancellationToken);
            await ProbeLog.WriteProtocolLineAsync("READY", cancellationToken);

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
        ProbeMode.BareReverseHttp1 => "bare-reverse-http1",
        ProbeMode.NginxReverseHttp1 => "nginx-reverse-http1",
        ProbeMode.YarpReverseHttp1 => "yarp-reverse-http1",
        ProbeMode.ReverseHttp1Tls => "reverse-http1-tls",
        ProbeMode.ReverseHttp1ToHttps => "reverse-http1-to-https",
        ProbeMode.BareReverseHttp1Tls => "bare-reverse-http1-tls",
        ProbeMode.NginxReverseHttp1Tls => "nginx-reverse-http1-tls",
        ProbeMode.YarpReverseHttp1Tls => "yarp-reverse-http1-tls",
        ProbeMode.YarpReverseHttp1ToHttps => "yarp-reverse-http1-to-https",
        ProbeMode.HttpsMitm => "https-mitm",
        ProbeMode.HttpMitm => "http-mitm",
        ProbeMode.ReverseHttp1Mitm => "reverse-http1-mitm",
        ProbeMode.ReverseHttp2 => "reverse-http2",
        ProbeMode.ReverseHttp2Cleartext => "reverse-http2-cleartext",
        ProbeMode.ReverseHttp2ToH2c => "reverse-http2-to-h2c",
        ProbeMode.YarpReverseHttp2ToH2c => "yarp-reverse-http2-to-h2c",
        ProbeMode.ReverseH2c => "reverse-h2c",
        ProbeMode.YarpReverseH2c => "yarp-reverse-h2c",
        ProbeMode.ReverseH2cToH2c => "reverse-h2c-to-h2c",
        ProbeMode.YarpReverseH2cToH2c => "yarp-reverse-h2c-to-h2c",
        ProbeMode.ReverseH2cToH1 => "reverse-h2c-to-h1",
        ProbeMode.YarpReverseH2cToH1 => "yarp-reverse-h2c-to-h1",
        ProbeMode.ReverseH2cToH3 => "reverse-h2c-to-h3",
        ProbeMode.YarpReverseH2cToH3 => "yarp-reverse-h2c-to-h3",
        ProbeMode.NginxReverseHttp2 => "nginx-reverse-http2",
        ProbeMode.NginxReverseHttp3Cleartext => "nginx-reverse-http3-cleartext",
        ProbeMode.YarpReverseHttp2 => "yarp-reverse-http2",
        ProbeMode.YarpReverseHttp2ToHttps => "yarp-reverse-http2-to-https",
        ProbeMode.ReverseHttp3 => "reverse-http3",
        ProbeMode.ReverseHttp3Cleartext => "reverse-http3-cleartext",
        ProbeMode.YarpReverseHttp3Cleartext => "yarp-reverse-http3-cleartext",
        ProbeMode.ReverseHttp11ToHttp2 => "reverse-http11-to-http2",
        ProbeMode.YarpReverseHttp11ToHttp2 => "yarp-reverse-http11-to-http2",
        ProbeMode.ReverseHttp1ToHttp3 => "reverse-http1-to-http3",
        ProbeMode.YarpReverseHttp1ToHttp3 => "yarp-reverse-http1-to-http3",
        ProbeMode.ReverseHttp2ToHttp3 => "reverse-http2-to-http3",
        ProbeMode.YarpReverseHttp2ToHttp3 => "yarp-reverse-http2-to-http3",
        ProbeMode.ReverseHttp3ToHttp2 => "reverse-http3-to-http2",
        ProbeMode.YarpReverseHttp3ToHttp2 => "yarp-reverse-http3-to-http2",
        ProbeMode.YarpReverseHttp3ToHttp3 => "yarp-reverse-http3-to-http3",
        ProbeMode.MitmHttp2ToHttp1 => "mitm-http2-to-http1",
        ProbeMode.MitmHttp3ToHttp1 => "mitm-http3-to-http1",
        ProbeMode.ExplicitHttp1Multi => "explicit-http1-multi",
        ProbeMode.ExplicitHttp2Multi => "explicit-http2-multi",
        ProbeMode.Compare => "compare",
        ProbeMode.CompareHttp2 => "compare-http2",
        ProbeMode.CompareTls => "compare-tls",
        ProbeMode.CompareTerminate => "compare-terminate",
        ProbeMode.CompareSame => "compare-same",
        ProbeMode.CompareBridges => "compare-bridges",
        ProbeMode.CompareMitm => "compare-mitm",
        ProbeMode.CompareCeiling => "compare-ceiling",
        ProbeMode.CompareBodies => "compare-bodies",
        ProbeMode.ComparePost => "compare-post",
        ProbeMode.CompareLossy => "compare-lossy",
        ProbeMode.CompareTlsCost => "compare-tls-cost",
        ProbeMode.CompareArch => "compare-arch",
        ProbeMode.ExplicitPoolSweep => "explicit-pool-sweep",
        _ => mode.ToString()
    };
}

internal static class ServeHost
{
    public static async Task<int> RunAsync(ProbeMode mode, string? nginxPath, int? maxCachedConnections,
        CancellationToken cancellationToken, WorkloadOptions? workload = null)
    {
        workload ??= WorkloadOptions.TinyGet;
        if (mode is ProbeMode.Compare or ProbeMode.CompareHttp2 or ProbeMode.CompareTls
            or ProbeMode.CompareTerminate or ProbeMode.CompareSame or ProbeMode.CompareBridges
            or ProbeMode.CompareMitm or ProbeMode.CompareCeiling or ProbeMode.CompareBodies
            or ProbeMode.ComparePost or ProbeMode.CompareLossy or ProbeMode.CompareTlsCost
            or ProbeMode.CompareArch or ProbeMode.ExplicitPoolSweep)
        {
            ProbeLog.Error("--serve requires a single mode");
            return 2;
        }

        if ((mode is ProbeMode.NginxReverseHttp1 or ProbeMode.NginxReverseHttp1Tls or ProbeMode.NginxReverseHttp2
                or ProbeMode.NginxReverseHttp3Cleartext)
            && NginxHost.ResolveNginxExecutable(nginxPath) == null)
        {
            ProbeLog.Error(NginxHost.NginxMissingMessage());
            return 3;
        }

        ServeStack stack;
        try
        {
            stack = await ServeStack.StartAsync(mode, nginxPath, maxCachedConnections, cancellationToken, workload);
        }
        catch (Exception ex)
        {
            // Surface to parent ChildProcessStack (stderr was empty when native reverse conf failed on Linux).
            await Console.Error.WriteLineAsync(ex.ToString());
            ProbeLog.Error(ex.Message);
            return 1;
        }

        await using (stack)
        {
            ProbeLog.Info(MachineInfo.FormatReport(stack.NginxVersion));
            await ProbeLog.WriteProtocolLineAsync($"mode={ServeProxyHost.ModeName(mode)}", cancellationToken);
            await ProbeLog.WriteProtocolLineAsync($"origin_http={stack.OriginHttpUrl}", cancellationToken);
            if (stack.OriginHttpsUrl != null)
                await ProbeLog.WriteProtocolLineAsync($"origin_https={stack.OriginHttpsUrl}", cancellationToken);
            foreach (var url in stack.ExtraOriginHttpsUrls)
                await ProbeLog.WriteProtocolLineAsync($"origin_https_extra={url}", cancellationToken);
            await ProbeLog.WriteProtocolLineAsync($"listen={stack.ListenUrl}", cancellationToken);
            if (stack.ExplicitProxyUrl != null)
                await ProbeLog.WriteProtocolLineAsync($"explicit_proxy={stack.ExplicitProxyUrl}", cancellationToken);
            await ProbeLog.WriteProtocolLineAsync($"target_for_client={stack.ClientTargetUrl}", cancellationToken);
            foreach (var url in stack.ClientTargetUrls.Skip(1))
                await ProbeLog.WriteProtocolLineAsync($"target_for_client_extra={url}", cancellationToken);
            if (stack.HttpVersion != null)
                await ProbeLog.WriteProtocolLineAsync($"http_version={stack.HttpVersion}", cancellationToken);
            if (stack.LoadGenerator != null)
                await ProbeLog.WriteProtocolLineAsync($"load_generator={stack.LoadGenerator}", cancellationToken);
            if (stack.QuicPort is { } qp)
                await ProbeLog.WriteProtocolLineAsync($"quic_port={qp}", cancellationToken);
            if (stack.OriginQuicPort is { } oqp)
                await ProbeLog.WriteProtocolLineAsync($"origin_quic_port={oqp}", cancellationToken);
            if (stack.NginxVersion != null)
                await ProbeLog.WriteProtocolLineAsync($"nginx={stack.NginxVersion}", cancellationToken);
            if (stack.YarpVersion != null)
                await ProbeLog.WriteProtocolLineAsync($"yarp={stack.YarpVersion}", cancellationToken);
            if (maxCachedConnections is { } m)
                await ProbeLog.WriteProtocolLineAsync($"max_cached_connections={m}", cancellationToken);
            if (stack.ServerConnectionProbe != null)
                await ProbeLog.WriteProtocolLineAsync("server_connection_probe=1", cancellationToken);
            await ProbeLog.WriteProtocolLineAsync("READY", cancellationToken);
            ProbeLog.Info(string.Empty);
            ProbeLog.Info("Ready. Example:");
            if (stack.ExplicitProxyUrl != null)
                ProbeLog.Info($"  bombardier -c 256 -d 30s -l -x {stack.ExplicitProxyUrl} {stack.ClientTargetUrl}");
            else
                ProbeLog.Info($"  bombardier -c 256 -d 30s -l {stack.ClientTargetUrl}");

            ProbeLog.Info(string.Empty);
            ProbeLog.Info("Press Ctrl+C to stop.");

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
        public string? YarpVersion { get; }
        public string? HttpVersion { get; }
        public string? LoadGenerator { get; }
        public int? QuicPort { get; }
        public int? OriginQuicPort { get; }
        public Func<int>? ServerConnectionProbe { get; }

        private ServeStack(IAsyncDisposable origin, IDisposable? proxy, TwpProxyHost? twp, string originHttpUrl,
            string? originHttpsUrl, IReadOnlyList<string> extraOriginHttpsUrls, string listenUrl,
            string? explicitProxyUrl, string clientTargetUrl, IReadOnlyList<string> clientTargetUrls,
            string? nginxVersion, string? httpVersion, string? loadGenerator = null, int? quicPort = null,
            int? originQuicPort = null, string? yarpVersion = null)
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
            YarpVersion = yarpVersion;
            HttpVersion = httpVersion;
            LoadGenerator = loadGenerator;
            QuicPort = quicPort;
            OriginQuicPort = originQuicPort;
            ServerConnectionProbe = twp == null ? null : () => twp.Server.ServerConnectionCount;
        }

        public static async Task<ServeStack> StartAsync(ProbeMode mode, string? nginxPath,
            int? maxCachedConnections, CancellationToken cancellationToken, WorkloadOptions? workload = null)
        {
            workload ??= WorkloadOptions.TinyGet;
            var responseBytes = workload.ResponseBytes;
            switch (mode)
            {
                case ProbeMode.ReverseHttp1:
                {
                    var origin = await OriginServer.StartAsync(false, responseBytes, cancellationToken, workload);
                    var twp = TwpProxyHost.StartReverseHttp1(origin.HttpPort);
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, null, [], twp.ListenUrl, null,
                        twp.ListenUrl, [twp.ListenUrl], null, "1.1");
                }
                case ProbeMode.BareReverseHttp1:
                {
                    var origin = await OriginServer.StartAsync(false, responseBytes, cancellationToken, workload);
                    var bare = BareHttp1ReverseProxy.Start(origin.HttpPort);
                    return new ServeStack(origin, bare, null, origin.HttpUrl, null, [], bare.ListenUrl, null,
                        bare.ListenUrl, [bare.ListenUrl], null, "1.1");
                }
                case ProbeMode.NginxReverseHttp1:
                {
                    var origin = await OriginServer.StartAsync(false, responseBytes, cancellationToken, workload);
                    var nginx = await NginxHost.TryStartHttp1Async(origin.HttpPort, nginxPath)
                                ?? throw new InvalidOperationException("nginx not available.");
                    return new ServeStack(origin, nginx, null, origin.HttpUrl, null, [], nginx.ListenUrl, null,
                        nginx.ListenUrl, [nginx.ListenUrl], nginx.Version, "1.1");
                }
                case ProbeMode.HttpsMitm:
                {
                    var origin = await OriginServer.StartAsync(true, responseBytes, cancellationToken, workload);
                    var twp = TwpProxyHost.StartHttpsMitm(maxCachedConnections);
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, origin.HttpsUrl, [], twp.ListenUrl,
                        twp.ListenUrl, origin.HttpsUrl, [origin.HttpsUrl], null, "1.1");
                }
                case ProbeMode.HttpMitm:
                {
                    var origin = await OriginServer.StartAsync(false, responseBytes, cancellationToken, workload);
                    var twp = TwpProxyHost.StartHttpMitm(maxCachedConnections);
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, null, [], twp.ListenUrl,
                        twp.ListenUrl, origin.HttpUrl, [origin.HttpUrl], null, "1.1");
                }
                case ProbeMode.ReverseHttp1Mitm:
                {
                    var origin = await OriginServer.StartAsync(true, responseBytes, cancellationToken, workload);
                    var twp = TwpProxyHost.StartReverseHttp1Mitm(origin.HttpsPort);
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, origin.HttpsUrl, [], twp.ListenUrl, null,
                        twp.ListenUrl, [twp.ListenUrl], null, "1.1");
                }
                case ProbeMode.ReverseHttp1Tls:
                {
                    var origin = await OriginServer.StartAsync(false, responseBytes, cancellationToken, workload);
                    var twp = TwpProxyHost.StartReverseHttp1Tls(origin.HttpPort);
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, null, [], twp.ListenUrl, null,
                        twp.ListenUrl, [twp.ListenUrl], null, "1.1");
                }
                case ProbeMode.ReverseHttp1ToHttps:
                {
                    var origin = await OriginServer.StartAsync(true, responseBytes, cancellationToken, workload);
                    var twp = TwpProxyHost.StartReverseHttp1ToHttps(origin.HttpsPort);
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, origin.HttpsUrl, [], twp.ListenUrl, null,
                        twp.ListenUrl, [twp.ListenUrl], null, "1.1");
                }
                case ProbeMode.BareReverseHttp1Tls:
                {
                    var origin = await OriginServer.StartAsync(false, responseBytes, cancellationToken, workload);
                    var bare = BareHttp1ReverseProxy.Start(origin.HttpPort, tlsTerminate: true);
                    return new ServeStack(origin, bare, null, origin.HttpUrl, null, [], bare.ListenUrl, null,
                        bare.ListenUrl, [bare.ListenUrl], null, "1.1");
                }
                case ProbeMode.NginxReverseHttp1Tls:
                {
                    var origin = await OriginServer.StartAsync(false, responseBytes, cancellationToken, workload);
                    var nginx = await NginxHost.TryStartHttp1TlsAsync(origin.HttpPort, nginxPath)
                                ?? throw new InvalidOperationException("nginx not available.");
                    return new ServeStack(origin, nginx, null, origin.HttpUrl, null, [], nginx.ListenUrl,
                        null, nginx.ListenUrl, [nginx.ListenUrl], nginx.Version, "1.1");
                }
                case ProbeMode.ReverseHttp2:
                {
                    var origin = await OriginServer.StartAsync(new OriginListenOptions
                    {
                        EnableHttp = false,
                        EnableHttps = true,
                        HttpsProtocols = HttpProtocols.Http1AndHttp2,
                        ResponseBytes = responseBytes
                    }, cancellationToken, workload);
                    var twp = TwpProxyHost.StartReverseHttp2(origin.HttpsPort);
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, origin.HttpsUrl, [], twp.ListenUrl, null,
                        twp.ListenUrl, [twp.ListenUrl], null, "2.0");
                }
                case ProbeMode.MitmHttp2ToHttp1:
                {
                    var origin = await OriginServer.StartAsync(new OriginListenOptions
                    {
                        EnableHttp = false,
                        EnableHttps = true,
                        HttpsProtocols = HttpProtocols.Http1,
                        ResponseBytes = responseBytes
                    }, cancellationToken, workload);
                    var twp = TwpProxyHost.StartMitmHttp2ToHttp1(origin.HttpsPort);
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, origin.HttpsUrl, [], twp.ListenUrl, null,
                        twp.ListenUrl, [twp.ListenUrl], null, "2.0");
                }
                case ProbeMode.ReverseHttp2Cleartext:
                {
                    // Native reverse peer parity: client TLS+h2 → terminate → cleartext HTTP/1 origin via H2→H1 bridge.
                    var origin = await OriginServer.StartAsync(false, responseBytes, cancellationToken, workload);
                    var twp = TwpProxyHost.StartReverseHttp2Cleartext(origin.HttpPort);
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, null, [], twp.ListenUrl, null,
                        twp.ListenUrl, [twp.ListenUrl], null, "2.0");
                }
                case ProbeMode.ReverseHttp2ToH2c:
                {
                    // Client TLS+h2 → terminate → prior-knowledge h2c origin (HttpProtocols.Http2 on the managed origin).
                    var origin = await OriginServer.StartAsync(new OriginListenOptions
                    {
                        EnableHttp = true,
                        EnableHttps = false,
                        HttpProtocols = HttpProtocols.Http2,
                        ResponseBytes = responseBytes
                    }, cancellationToken, workload);
                    var twp = TwpProxyHost.StartReverseHttp2ToH2c(origin.HttpPort);
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, null, [], twp.ListenUrl, null,
                        twp.ListenUrl, [twp.ListenUrl], null, "2.0");
                }
                case ProbeMode.ReverseH2cToH2c:
                {
                    var origin = await OriginServer.StartAsync(new OriginListenOptions
                    {
                        EnableHttp = true,
                        EnableHttps = false,
                        HttpProtocols = HttpProtocols.Http2,
                        ResponseBytes = responseBytes
                    }, cancellationToken, workload);
                    var twp = TwpProxyHost.StartReverseH2cToH2c(origin.HttpPort);
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, null, [], twp.ListenUrl, null,
                        twp.ListenUrl, [twp.ListenUrl], null, "2.0");
                }
                case ProbeMode.ReverseH2cToH1:
                {
                    var origin = await OriginServer.StartAsync(false, responseBytes, cancellationToken, workload);
                    var twp = TwpProxyHost.StartReverseH2cToH1(origin.HttpPort);
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, null, [], twp.ListenUrl, null,
                        twp.ListenUrl, [twp.ListenUrl], null, "2.0");
                }
                case ProbeMode.ReverseH2c:
                {
                    var origin = await OriginServer.StartAsync(new OriginListenOptions
                    {
                        EnableHttp = false,
                        EnableHttps = true,
                        HttpsProtocols = HttpProtocols.Http1AndHttp2,
                        ResponseBytes = responseBytes
                    }, cancellationToken, workload);
                    var twp = TwpProxyHost.StartReverseH2c(origin.HttpsPort);
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, origin.HttpsUrl, [], twp.ListenUrl, null,
                        twp.ListenUrl, [twp.ListenUrl], null, "2.0");
                }
                case ProbeMode.ReverseH2cToH3:
                {
                    if (!System.Net.Quic.QuicListener.IsSupported)
                        throw new PlatformNotSupportedException("QuicListener is not supported.");

                    var origin = new QuicHttp3OriginHost(responseBytes);
                    var twp = TwpProxyHost.StartReverseH2cToH3(origin.Port);
                    return new ServeStack(origin, twp, twp, $"quic://localhost:{origin.Port}/",
                        $"quic://localhost:{origin.Port}/", [], twp.ListenUrl, null, twp.ListenUrl, [twp.ListenUrl],
                        null, "2.0", originQuicPort: origin.Port);
                }
                case ProbeMode.NginxReverseHttp2:
                {
                    // Native reverse: client TLS+h2 → cleartext HTTP/1.1 origin (same as TryStartHttp2 conf).
                    var origin = await OriginServer.StartAsync(false, responseBytes, cancellationToken, workload);
                    var nginx = await NginxHost.TryStartHttp2Async(origin.HttpPort, nginxPath)
                                ?? throw new InvalidOperationException("nginx not available.");
                    return new ServeStack(origin, nginx, null, origin.HttpUrl, null, [], nginx.ListenUrl,
                        null, nginx.ListenUrl, [nginx.ListenUrl], nginx.Version, "2.0");
                }
                case ProbeMode.NginxReverseHttp3Cleartext:
                {
                    if (!System.Net.Quic.QuicListener.IsSupported)
                        throw new PlatformNotSupportedException("QuicListener is not supported.");

                    var origin = await OriginServer.StartAsync(false, responseBytes, cancellationToken, workload);
                    var nginx = await NginxHost.TryStartHttp3CleartextAsync(origin.HttpPort, nginxPath)
                                ?? throw new InvalidOperationException(
                                    "nginx HTTP/3 is not available (need --with-http_v3_module).");
                    return new ServeStack(origin, nginx, null, origin.HttpUrl, null, [], nginx.ListenUrl,
                        null, nginx.ListenUrl, [nginx.ListenUrl], nginx.Version, "3.0");
                }
                case ProbeMode.ReverseHttp3:
                {
                    if (!System.Net.Quic.QuicListener.IsSupported)
                        throw new PlatformNotSupportedException("QuicListener is not supported.");

                    var origin = new QuicHttp3OriginHost(responseBytes);
                    var twp = TwpProxyHost.StartReverseHttp3(origin.Port);
                    return new ServeStack(origin, twp, twp, $"quic://localhost:{origin.Port}/",
                        $"quic://localhost:{origin.Port}/", [], twp.ListenUrl, null, twp.ListenUrl, [twp.ListenUrl],
                        null, "3.0", originQuicPort: origin.Port);
                }
                case ProbeMode.MitmHttp3ToHttp1:
                {
                    if (!System.Net.Quic.QuicListener.IsSupported)
                        throw new PlatformNotSupportedException("QuicListener is not supported.");

                    var origin = await OriginServer.StartAsync(new OriginListenOptions
                    {
                        EnableHttp = false,
                        EnableHttps = true,
                        HttpsProtocols = HttpProtocols.Http1,
                        ResponseBytes = responseBytes
                    }, cancellationToken, workload);
                    var twp = TwpProxyHost.StartMitmHttp3ToHttp1(origin.HttpsPort);
                    // Matched HttpClient generator (same as reverse-http3-cleartext) for fair MITM÷twin ratios.
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, origin.HttpsUrl, [], twp.ListenUrl, null,
                        twp.ListenUrl, [twp.ListenUrl], null, "3.0");
                }
                case ProbeMode.ReverseHttp3Cleartext:
                {
                    if (!System.Net.Quic.QuicListener.IsSupported)
                        throw new PlatformNotSupportedException("QuicListener is not supported.");

                    // Client QUIC/h3 → terminate → cleartext HTTP/1 origin (ForwardCleartext + Http11).
                    var origin = await OriginServer.StartAsync(false, responseBytes, cancellationToken, workload);
                    var twp = TwpProxyHost.StartReverseHttp3Cleartext(origin.HttpPort);
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, null, [], twp.ListenUrl, null,
                        twp.ListenUrl, [twp.ListenUrl], null, "3.0");
                }
                case ProbeMode.ReverseHttp11ToHttp2:
                {
                    var origin = await OriginServer.StartAsync(new OriginListenOptions
                    {
                        EnableHttp = false,
                        EnableHttps = true,
                        HttpsProtocols = HttpProtocols.Http1AndHttp2,
                        ResponseBytes = responseBytes
                    }, cancellationToken, workload);
                    var twp = TwpProxyHost.StartReverseHttp11ToHttp2(origin.HttpsPort);
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, origin.HttpsUrl, [], twp.ListenUrl, null,
                        twp.ListenUrl, [twp.ListenUrl], null, "1.1");
                }
                case ProbeMode.ReverseHttp1ToHttp3:
                {
                    if (!System.Net.Quic.QuicListener.IsSupported)
                        throw new PlatformNotSupportedException("QuicListener is not supported.");

                    var origin = new QuicHttp3OriginHost(responseBytes);
                    var twp = TwpProxyHost.StartReverseHttp1ToHttp3(origin.Port);
                    return new ServeStack(origin, twp, twp, $"quic://localhost:{origin.Port}/",
                        $"quic://localhost:{origin.Port}/", [], twp.ListenUrl, null, twp.ListenUrl, [twp.ListenUrl],
                        null, "1.1", originQuicPort: origin.Port);
                }
                case ProbeMode.ReverseHttp2ToHttp3:
                {
                    if (!System.Net.Quic.QuicListener.IsSupported)
                        throw new PlatformNotSupportedException("QuicListener is not supported.");

                    var origin = new QuicHttp3OriginHost(responseBytes);
                    var twp = TwpProxyHost.StartReverseHttp2ToHttp3(origin.Port);
                    return new ServeStack(origin, twp, twp, $"quic://localhost:{origin.Port}/",
                        $"quic://localhost:{origin.Port}/", [], twp.ListenUrl, null, twp.ListenUrl, [twp.ListenUrl],
                        null, "2.0", originQuicPort: origin.Port);
                }
                case ProbeMode.ReverseHttp3ToHttp2:
                {
                    if (!System.Net.Quic.QuicListener.IsSupported)
                        throw new PlatformNotSupportedException("QuicListener is not supported.");

                    var origin = await OriginServer.StartAsync(new OriginListenOptions
                    {
                        EnableHttp = false,
                        EnableHttps = true,
                        HttpsProtocols = HttpProtocols.Http1AndHttp2,
                        ResponseBytes = responseBytes
                    }, cancellationToken, workload);
                    var twp = TwpProxyHost.StartReverseHttp3ToHttp2(origin.HttpsPort);
                    return new ServeStack(origin, twp, twp, origin.HttpUrl, origin.HttpsUrl, [], twp.ListenUrl, null,
                        twp.ListenUrl, [twp.ListenUrl], null, "3.0");
                }
                case ProbeMode.YarpReverseHttp1:
                {
                    var origin = await OriginServer.StartAsync(false, responseBytes, cancellationToken, workload);
                    var yarp = await YarpProxyHost.StartHttp1Async(origin.HttpPort);
                    return new ServeStack(origin, yarp, null, origin.HttpUrl, null, [], yarp.ListenUrl, null,
                        yarp.ListenUrl, [yarp.ListenUrl], null, "1.1", yarpVersion: yarp.Version);
                }
                case ProbeMode.YarpReverseHttp1Tls:
                {
                    var origin = await OriginServer.StartAsync(false, responseBytes, cancellationToken, workload);
                    var yarp = await YarpProxyHost.StartHttp1TlsAsync(origin.HttpPort);
                    return new ServeStack(origin, yarp, null, origin.HttpUrl, null, [], yarp.ListenUrl, null,
                        yarp.ListenUrl, [yarp.ListenUrl], null, "1.1", yarpVersion: yarp.Version);
                }
                case ProbeMode.YarpReverseHttp1ToHttps:
                {
                    var origin = await OriginServer.StartAsync(true, responseBytes, cancellationToken, workload);
                    var yarp = await YarpProxyHost.StartHttp1ToHttpsAsync(origin.HttpsPort);
                    return new ServeStack(origin, yarp, null, origin.HttpUrl, origin.HttpsUrl, [], yarp.ListenUrl, null,
                        yarp.ListenUrl, [yarp.ListenUrl], null, "1.1", yarpVersion: yarp.Version);
                }
                case ProbeMode.YarpReverseHttp2:
                {
                    var origin = await OriginServer.StartAsync(false, responseBytes, cancellationToken, workload);
                    var yarp = await YarpProxyHost.StartHttp2ToH1Async(origin.HttpPort);
                    return new ServeStack(origin, yarp, null, origin.HttpUrl, null, [], yarp.ListenUrl, null,
                        yarp.ListenUrl, [yarp.ListenUrl], null, "2.0", yarpVersion: yarp.Version);
                }
                case ProbeMode.YarpReverseHttp2ToHttps:
                {
                    var origin = await OriginServer.StartAsync(new OriginListenOptions
                    {
                        EnableHttp = false,
                        EnableHttps = true,
                        HttpsProtocols = HttpProtocols.Http1AndHttp2,
                        ResponseBytes = responseBytes
                    }, cancellationToken, workload);
                    var yarp = await YarpProxyHost.StartHttp2ToHttpsAsync(origin.HttpsPort);
                    return new ServeStack(origin, yarp, null, origin.HttpUrl, origin.HttpsUrl, [], yarp.ListenUrl, null,
                        yarp.ListenUrl, [yarp.ListenUrl], null, "2.0", yarpVersion: yarp.Version);
                }
                case ProbeMode.YarpReverseHttp2ToH2c:
                {
                    var origin = await OriginServer.StartAsync(new OriginListenOptions
                    {
                        EnableHttp = true,
                        EnableHttps = false,
                        HttpProtocols = HttpProtocols.Http2,
                        ResponseBytes = responseBytes
                    }, cancellationToken, workload);
                    var yarp = await YarpProxyHost.StartHttp2ToH2cAsync(origin.HttpPort);
                    return new ServeStack(origin, yarp, null, origin.HttpUrl, null, [], yarp.ListenUrl, null,
                        yarp.ListenUrl, [yarp.ListenUrl], null, "2.0", yarpVersion: yarp.Version);
                }
                case ProbeMode.YarpReverseH2cToH2c:
                {
                    var origin = await OriginServer.StartAsync(new OriginListenOptions
                    {
                        EnableHttp = true,
                        EnableHttps = false,
                        HttpProtocols = HttpProtocols.Http2,
                        ResponseBytes = responseBytes
                    }, cancellationToken, workload);
                    var yarp = await YarpProxyHost.StartH2cToH2cAsync(origin.HttpPort);
                    return new ServeStack(origin, yarp, null, origin.HttpUrl, null, [], yarp.ListenUrl, null,
                        yarp.ListenUrl, [yarp.ListenUrl], null, "2.0", yarpVersion: yarp.Version);
                }
                case ProbeMode.YarpReverseH2cToH1:
                {
                    var origin = await OriginServer.StartAsync(false, responseBytes, cancellationToken, workload);
                    var yarp = await YarpProxyHost.StartH2cToH1Async(origin.HttpPort);
                    return new ServeStack(origin, yarp, null, origin.HttpUrl, null, [], yarp.ListenUrl, null,
                        yarp.ListenUrl, [yarp.ListenUrl], null, "2.0", yarpVersion: yarp.Version);
                }
                case ProbeMode.YarpReverseH2c:
                {
                    var origin = await OriginServer.StartAsync(new OriginListenOptions
                    {
                        EnableHttp = false,
                        EnableHttps = true,
                        HttpsProtocols = HttpProtocols.Http1AndHttp2,
                        ResponseBytes = responseBytes
                    }, cancellationToken, workload);
                    var yarp = await YarpProxyHost.StartH2cToHttpsAsync(origin.HttpsPort);
                    return new ServeStack(origin, yarp, null, origin.HttpUrl, origin.HttpsUrl, [], yarp.ListenUrl, null,
                        yarp.ListenUrl, [yarp.ListenUrl], null, "2.0", yarpVersion: yarp.Version);
                }
                case ProbeMode.YarpReverseH2cToH3:
                {
                    if (!System.Net.Quic.QuicListener.IsSupported)
                        throw new PlatformNotSupportedException("QuicListener is not supported.");

                    var origin = new QuicHttp3OriginHost(responseBytes);
                    var yarp = await YarpProxyHost.StartH2cToHttp3Async(origin.Port);
                    return new ServeStack(origin, yarp, null, $"quic://localhost:{origin.Port}/",
                        $"quic://localhost:{origin.Port}/", [], yarp.ListenUrl, null, yarp.ListenUrl, [yarp.ListenUrl],
                        null, "2.0", originQuicPort: origin.Port, yarpVersion: yarp.Version);
                }
                case ProbeMode.YarpReverseHttp3Cleartext:
                {
                    if (!System.Net.Quic.QuicListener.IsSupported)
                        throw new PlatformNotSupportedException("QuicListener is not supported.");

                    var origin = await OriginServer.StartAsync(false, responseBytes, cancellationToken, workload);
                    var yarp = await YarpProxyHost.StartHttp3CleartextAsync(origin.HttpPort);
                    return new ServeStack(origin, yarp, null, origin.HttpUrl, null, [], yarp.ListenUrl, null,
                        yarp.ListenUrl, [yarp.ListenUrl], null, "3.0", yarpVersion: yarp.Version);
                }
                case ProbeMode.YarpReverseHttp11ToHttp2:
                {
                    var origin = await OriginServer.StartAsync(new OriginListenOptions
                    {
                        EnableHttp = false,
                        EnableHttps = true,
                        HttpsProtocols = HttpProtocols.Http1AndHttp2,
                        ResponseBytes = responseBytes
                    }, cancellationToken, workload);
                    var yarp = await YarpProxyHost.StartHttp1ToHttp2Async(origin.HttpsPort);
                    return new ServeStack(origin, yarp, null, origin.HttpUrl, origin.HttpsUrl, [], yarp.ListenUrl, null,
                        yarp.ListenUrl, [yarp.ListenUrl], null, "1.1", yarpVersion: yarp.Version);
                }
                case ProbeMode.YarpReverseHttp1ToHttp3:
                {
                    if (!System.Net.Quic.QuicListener.IsSupported)
                        throw new PlatformNotSupportedException("QuicListener is not supported.");

                    var origin = new QuicHttp3OriginHost(responseBytes);
                    var yarp = await YarpProxyHost.StartHttp1ToHttp3Async(origin.Port);
                    return new ServeStack(origin, yarp, null, $"quic://localhost:{origin.Port}/",
                        $"quic://localhost:{origin.Port}/", [], yarp.ListenUrl, null, yarp.ListenUrl, [yarp.ListenUrl],
                        null, "1.1", originQuicPort: origin.Port, yarpVersion: yarp.Version);
                }
                case ProbeMode.YarpReverseHttp2ToHttp3:
                {
                    if (!System.Net.Quic.QuicListener.IsSupported)
                        throw new PlatformNotSupportedException("QuicListener is not supported.");

                    var origin = new QuicHttp3OriginHost(responseBytes);
                    var yarp = await YarpProxyHost.StartHttp2ToHttp3Async(origin.Port);
                    return new ServeStack(origin, yarp, null, $"quic://localhost:{origin.Port}/",
                        $"quic://localhost:{origin.Port}/", [], yarp.ListenUrl, null, yarp.ListenUrl, [yarp.ListenUrl],
                        null, "2.0", originQuicPort: origin.Port, yarpVersion: yarp.Version);
                }
                case ProbeMode.YarpReverseHttp3ToHttp2:
                {
                    if (!System.Net.Quic.QuicListener.IsSupported)
                        throw new PlatformNotSupportedException("QuicListener is not supported.");

                    var origin = await OriginServer.StartAsync(new OriginListenOptions
                    {
                        EnableHttp = false,
                        EnableHttps = true,
                        HttpsProtocols = HttpProtocols.Http1AndHttp2,
                        ResponseBytes = responseBytes
                    }, cancellationToken, workload);
                    var yarp = await YarpProxyHost.StartHttp3ToHttp2Async(origin.HttpsPort);
                    return new ServeStack(origin, yarp, null, origin.HttpUrl, origin.HttpsUrl, [], yarp.ListenUrl, null,
                        yarp.ListenUrl, [yarp.ListenUrl], null, "3.0", yarpVersion: yarp.Version);
                }
                case ProbeMode.YarpReverseHttp3ToHttp3:
                {
                    if (!System.Net.Quic.QuicListener.IsSupported)
                        throw new PlatformNotSupportedException("QuicListener is not supported.");

                    var origin = new QuicHttp3OriginHost(responseBytes);
                    var yarp = await YarpProxyHost.StartHttp3ToHttp3Async(origin.Port);
                    return new ServeStack(origin, yarp, null, $"quic://localhost:{origin.Port}/",
                        $"quic://localhost:{origin.Port}/", [], yarp.ListenUrl, null, yarp.ListenUrl, [yarp.ListenUrl],
                        null, "3.0", originQuicPort: origin.Port, yarpVersion: yarp.Version);
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
                        HttpsProtocols = HttpProtocols.Http1AndHttp2,
                        ResponseBytes = responseBytes
                    }, cancellationToken, workload);
                    var twp = TwpProxyHost.StartHttpsMitm(maxCachedConnections);
                    var targets = new List<string> { origin.HttpsUrl };
                    targets.AddRange(origin.ExtraHttpsPorts.Select(p => $"https://127.0.0.1:{p}/"));
                    var extras = origin.ExtraHttpsPorts.Select(p => $"https://127.0.0.1:{p}/").ToList();
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
