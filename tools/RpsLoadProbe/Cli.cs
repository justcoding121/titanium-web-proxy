using System.Globalization;

namespace Titanium.Web.Proxy.RpsLoadProbe;

internal static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            PrintHelp();
            return args.Length == 0 ? 2 : 0;
        }

        string? command = null;
        string? modeText = null;
        string? nginxPath = null;
        string? resultsDir = null;
        var concurrency = new List<int>();
        var warmupSec = 5;
        var durationSec = 20;
        var enableHttps = false;
        var originHttpPort = 0;
        var originHttpsPort = 0;
        int? maxCachedConnections = null;
        var repeats = 1;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--serve":
                    command = "serve";
                    break;
                case "--serve-origin":
                    command = "serve-origin";
                    break;
                case "--serve-proxy":
                    command = "serve-proxy";
                    break;
                case "--ramp":
                    command = "ramp";
                    break;
                case "--mode":
                    modeText = RequireValue(args, ref i, "--mode");
                    break;
                case "--https":
                    enableHttps = true;
                    break;
                case "--origin-http-port":
                    originHttpPort = int.Parse(RequireValue(args, ref i, "--origin-http-port"),
                        CultureInfo.InvariantCulture);
                    break;
                case "--origin-https-port":
                    originHttpsPort = int.Parse(RequireValue(args, ref i, "--origin-https-port"),
                        CultureInfo.InvariantCulture);
                    break;
                case "--nginx-path":
                    nginxPath = RequireValue(args, ref i, "--nginx-path");
                    break;
                case "--results-dir":
                    resultsDir = RequireValue(args, ref i, "--results-dir");
                    break;
                case "--max-cached-connections":
                    maxCachedConnections = int.Parse(RequireValue(args, ref i, "--max-cached-connections"),
                        CultureInfo.InvariantCulture);
                    break;
                case "--concurrency":
                    foreach (var part in RequireValue(args, ref i, "--concurrency")
                                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        concurrency.Add(int.Parse(part, CultureInfo.InvariantCulture));
                    }

                    break;
                case "--warmup-sec":
                    warmupSec = int.Parse(RequireValue(args, ref i, "--warmup-sec"), CultureInfo.InvariantCulture);
                    break;
                case "--duration-sec":
                    durationSec = int.Parse(RequireValue(args, ref i, "--duration-sec"), CultureInfo.InvariantCulture);
                    break;
                case "--repeats":
                    repeats = int.Parse(RequireValue(args, ref i, "--repeats"), CultureInfo.InvariantCulture);
                    break;
                default:
                    ProbeLog.Error($"Unknown argument: {args[i]}");
                    PrintHelp();
                    return 2;
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return command switch
            {
                "serve-origin" => ServeOriginHost.RunAsync(enableHttps, cts.Token).GetAwaiter().GetResult(),
                "serve-proxy" => RunServeProxy(modeText, originHttpPort, originHttpsPort, nginxPath,
                    maxCachedConnections, cts.Token),
                "serve" => RunServe(modeText, nginxPath, maxCachedConnections, cts.Token),
                "ramp" => RunRamp(modeText, nginxPath, resultsDir, concurrency, warmupSec, durationSec,
                    maxCachedConnections, repeats, cts.Token),
                _ => Fail("Required: --serve | --serve-origin | --serve-proxy | --ramp")
            };
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception ex)
        {
            ProbeLog.Error(ex.ToString());
            return 1;
        }
    }

    private static int RunServe(string? modeText, string? nginxPath, int? maxCachedConnections, CancellationToken ct)
    {
        if (modeText == null || !TryParseMode(modeText, out var mode))
            return Fail("Required: --serve --mode <see --help>");
        return ServeHost.RunAsync(mode, nginxPath, maxCachedConnections, ct).GetAwaiter().GetResult();
    }

    private static int RunServeProxy(string? modeText, int originHttpPort, int originHttpsPort, string? nginxPath,
        int? maxCachedConnections, CancellationToken ct)
    {
        if (modeText == null || !TryParseMode(modeText, out var mode) ||
            mode is ProbeMode.Compare or ProbeMode.CompareHttp2 or ProbeMode.CompareTls
                or ProbeMode.CompareTerminate or ProbeMode.CompareSame or ProbeMode.CompareBridges
                or ProbeMode.ExplicitPoolSweep)
            return Fail("Required: --serve-proxy --mode <single arm>");
        return ServeProxyHost.RunAsync(mode, originHttpPort, originHttpsPort, nginxPath, maxCachedConnections, ct)
            .GetAwaiter().GetResult();
    }

    private static int RunRamp(string? modeText, string? nginxPath, string? resultsDir, List<int> concurrency,
        int warmupSec, int durationSec, int? maxCachedConnections, int repeats, CancellationToken ct)
    {
        if (modeText == null || !TryParseMode(modeText, out var mode))
            return Fail("Required: --ramp --mode <see --help>");

        var options = new RampOptions
        {
            Mode = mode,
            NginxPath = nginxPath,
            ResultsDir = resultsDir ?? Path.Combine(AppContext.BaseDirectory, "results"),
            Warmup = TimeSpan.FromSeconds(warmupSec),
            StepDuration = TimeSpan.FromSeconds(durationSec),
            MaxCachedConnections = maxCachedConnections,
            Repeats = Math.Max(1, repeats),
            ConcurrencySteps = concurrency.Count > 0
                ? concurrency.ToArray()
                : [8, 16, 24, 32, 48, 64, 128, 256, 512]
        };
        return RampOrchestrator.RunAsync(options, ct).GetAwaiter().GetResult();
    }

    private static int Fail(string message)
    {
        ProbeLog.Error(message);
        PrintHelp();
        return 2;
    }

    private static string RequireValue(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {name}");
        return args[++i];
    }

    private static bool TryParseMode(string text, out ProbeMode mode)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "reverse-http1":
                mode = ProbeMode.ReverseHttp1;
                return true;
            case "nginx-reverse-http1":
                mode = ProbeMode.NginxReverseHttp1;
                return true;
            case "https-mitm":
                mode = ProbeMode.HttpsMitm;
                return true;
            case "reverse-http1-tls":
                mode = ProbeMode.ReverseHttp1Tls;
                return true;
            case "nginx-reverse-http1-tls":
                mode = ProbeMode.NginxReverseHttp1Tls;
                return true;
            case "reverse-http2":
                mode = ProbeMode.ReverseHttp2;
                return true;
            case "reverse-http2-cleartext":
                mode = ProbeMode.ReverseHttp2Cleartext;
                return true;
            case "nginx-reverse-http2":
                mode = ProbeMode.NginxReverseHttp2;
                return true;
            case "reverse-http3":
                mode = ProbeMode.ReverseHttp3;
                return true;
            case "reverse-http3-cleartext":
                mode = ProbeMode.ReverseHttp3Cleartext;
                return true;
            case "reverse-http11-to-http2":
                mode = ProbeMode.ReverseHttp11ToHttp2;
                return true;
            case "reverse-http1-to-http3":
                mode = ProbeMode.ReverseHttp1ToHttp3;
                return true;
            case "reverse-http2-to-http3":
                mode = ProbeMode.ReverseHttp2ToHttp3;
                return true;
            case "reverse-http3-to-http2":
                mode = ProbeMode.ReverseHttp3ToHttp2;
                return true;
            case "explicit-http1-multi":
                mode = ProbeMode.ExplicitHttp1Multi;
                return true;
            case "explicit-http2-multi":
                mode = ProbeMode.ExplicitHttp2Multi;
                return true;
            case "compare":
                mode = ProbeMode.Compare;
                return true;
            case "compare-http2":
                mode = ProbeMode.CompareHttp2;
                return true;
            case "compare-tls":
                mode = ProbeMode.CompareTls;
                return true;
            case "compare-terminate":
                mode = ProbeMode.CompareTerminate;
                return true;
            case "compare-same":
                mode = ProbeMode.CompareSame;
                return true;
            case "compare-bridges":
                mode = ProbeMode.CompareBridges;
                return true;
            case "explicit-pool-sweep":
                mode = ProbeMode.ExplicitPoolSweep;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    private static void PrintHelp()
    {
        ProbeLog.Info(
            """
            RpsLoadProbe — saturation RPS harness for Titanium.Web.Proxy (and nginx control arm)

            Usage:
              RpsLoadProbe --serve --mode <mode> [--nginx-path PATH] [--max-cached-connections N]
              RpsLoadProbe --serve-origin [--https]
              RpsLoadProbe --serve-proxy --mode <http1 mode> --origin-http-port N
              RpsLoadProbe --ramp  --mode <mode> [options]

            Modes:
              reverse-http1           TWP TransparentProxyEndPoint -> Kestrel HTTP/1
              nginx-reverse-http1     nginx proxy_pass -> same Kestrel HTTP/1 origin
              reverse-http1-tls       TWP TLS-terminating reverse -> Kestrel HTTPS (h1)
              nginx-reverse-http1-tls nginx TLS reverse -> same Kestrel HTTPS origin (h1)
              https-mitm              TWP Explicit MITM -> Kestrel HTTPS
              reverse-http2           TWP Transparent TLS+h2 MITM -> Kestrel HTTPS (h2)
              reverse-http2-cleartext TWP TLS+h2 terminate -> H2→H1 bridge -> Kestrel HTTP/1 (nginx parity)
              nginx-reverse-http2     nginx ssl+http2 -> cleartext HTTP/1 origin
              reverse-http3           TWP TransparentQuic (h3) -> Quic HTTPS/h3 origin (no nginx/Windows)
              reverse-http3-cleartext TWP QUIC/h3 terminate -> cleartext HTTP/1 origin
              reverse-http11-to-http2 TWP H1 TLS -> H1→H2 bridge -> Kestrel HTTPS/h2
              reverse-http1-to-http3  TWP H1 TLS -> H1→H3 bridge -> Quic/h3 origin
              reverse-http2-to-http3  TWP H2 TLS -> H2→H3 bridge -> Quic/h3 origin
              reverse-http3-to-http2  TWP H3 -> H3→H2 bridge -> Kestrel HTTPS/h2
              explicit-http1-multi    Explicit MITM across 16 HTTPS origins (fan-out)
              explicit-http2-multi    Same fan-out forcing HTTP/2
              compare                 Sequential HTTP/1 compare (+ MITM)
              compare-http2           Sequential: TWP h2 MITM, nginx h2, TWP h3
              compare-tls             Sequential: TWP h1-tls, nginx h1-tls, TWP h2 MITM, nginx h2, TWP h3
              compare-terminate       Fair terminate: H1 TLS, H2→H1, H3→H1 (+ nginx H1/H2)
              compare-same            Same-protocol: H1 cleartext, H1 TLS, H1 MITM, H2 MITM, H3 MITM (+ nginx)
              compare-bridges         Cross-version bridges only (H1↔H2↔H3; no nginx)
              explicit-pool-sweep     Fan-out with MaxCachedConnections 4 / 32 / 128

            Options:
              --nginx-path PATH
              --results-dir DIR
              --concurrency LIST      Default: 8,16,24,32,48,64,128,256,512
              --warmup-sec N
              --duration-sec N
              --repeats N             Full arm sequence N times; print median peaks (default 1)
              --max-cached-connections N   Override ProxyServer.MaxCachedConnections for TWP arms
            """);
    }
}
