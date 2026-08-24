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
        var enableH2c = false;
        var enableQuic = false;
        var httpsOnly = false;
        var httpsProtocols = "http1and2";
        var extraHttpsOrigins = 0;
        var originHttpPort = 0;
        var originHttpsPort = 0;
        var originQuicPort = 0;
        var originHttpsExtraPorts = new List<int>();
        int? maxCachedConnections = null;
        var repeats = 1;
        var method = "GET";
        var responseBytes = WorkloadOptions.TinyJsonBytes;
        var requestBytes = 0;
        var keepAlive = true;
        var delayMs = 0;
        var lossPercent = 0.0;
        var earlyResponseAfter = 0;
        var enableWebSocket = false;
        var clientReadChunkBytes = 0;
        var clientReadSleepMs = 0;

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
                case "--https-only":
                    httpsOnly = true;
                    break;
                case "--https-protocols":
                    httpsProtocols = RequireValue(args, ref i, "--https-protocols");
                    break;
                case "--h2c":
                    enableH2c = true;
                    break;
                case "--quic":
                    enableQuic = true;
                    break;
                case "--extra-https-origins":
                    extraHttpsOrigins = int.Parse(RequireValue(args, ref i, "--extra-https-origins"),
                        CultureInfo.InvariantCulture);
                    break;
                case "--origin-http-port":
                    originHttpPort = int.Parse(RequireValue(args, ref i, "--origin-http-port"),
                        CultureInfo.InvariantCulture);
                    break;
                case "--origin-https-port":
                    originHttpsPort = int.Parse(RequireValue(args, ref i, "--origin-https-port"),
                        CultureInfo.InvariantCulture);
                    break;
                case "--origin-quic-port":
                    originQuicPort = int.Parse(RequireValue(args, ref i, "--origin-quic-port"),
                        CultureInfo.InvariantCulture);
                    break;
                case "--origin-https-extra-port":
                    originHttpsExtraPorts.Add(int.Parse(RequireValue(args, ref i, "--origin-https-extra-port"),
                        CultureInfo.InvariantCulture));
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
                case "--method":
                    method = RequireValue(args, ref i, "--method").ToUpperInvariant();
                    if (method is not ("GET" or "POST"))
                        return Fail("--method must be GET or POST");
                    break;
                case "--response-bytes":
                    responseBytes = int.Parse(RequireValue(args, ref i, "--response-bytes"),
                        CultureInfo.InvariantCulture);
                    break;
                case "--request-bytes":
                    requestBytes = int.Parse(RequireValue(args, ref i, "--request-bytes"),
                        CultureInfo.InvariantCulture);
                    break;
                case "--no-keepalive":
                    keepAlive = false;
                    break;
                case "--delay-ms":
                    delayMs = int.Parse(RequireValue(args, ref i, "--delay-ms"), CultureInfo.InvariantCulture);
                    break;
                case "--loss-percent":
                    lossPercent = double.Parse(RequireValue(args, ref i, "--loss-percent"),
                        CultureInfo.InvariantCulture);
                    break;
                case "--early-response-after":
                    earlyResponseAfter = int.Parse(RequireValue(args, ref i, "--early-response-after"),
                        CultureInfo.InvariantCulture);
                    break;
                case "--websocket":
                    enableWebSocket = true;
                    break;
                case "--client-read-chunk":
                    clientReadChunkBytes = int.Parse(RequireValue(args, ref i, "--client-read-chunk"),
                        CultureInfo.InvariantCulture);
                    break;
                case "--client-read-sleep-ms":
                    clientReadSleepMs = int.Parse(RequireValue(args, ref i, "--client-read-sleep-ms"),
                        CultureInfo.InvariantCulture);
                    break;
                default:
                    ProbeLog.Error($"Unknown argument: {args[i]}");
                    PrintHelp();
                    return 2;
            }
        }

        var workload = new WorkloadOptions
        {
            Method = method,
            ResponseBytes = Math.Max(1, responseBytes),
            RequestBytes = Math.Max(0, requestBytes),
            KeepAlive = keepAlive,
            DelayMs = Math.Max(0, delayMs),
            LossPercent = Math.Clamp(lossPercent, 0, 100),
            EarlyResponseAfterBytes = Math.Max(0, earlyResponseAfter),
            IsWebSocket = enableWebSocket,
            ClientReadChunkBytes = Math.Max(0, clientReadChunkBytes),
            ClientReadSleepMs = Math.Max(0, clientReadSleepMs)
        };

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
                "serve-origin" => ServeOriginHost.RunAsync(new ServeOriginFlags
                    {
                        EnableHttps = enableHttps,
                        EnableH2c = enableH2c,
                        EnableQuic = enableQuic,
                        HttpsOnly = httpsOnly,
                        HttpsProtocols = httpsProtocols,
                        ExtraHttpsOrigins = extraHttpsOrigins
                    }, cts.Token, workload).GetAwaiter().GetResult(),
                "serve-proxy" => RunServeProxy(modeText, originHttpPort, originHttpsPort, originQuicPort,
                    originHttpsExtraPorts, nginxPath, maxCachedConnections, cts.Token, workload),
                "serve" => RunServe(modeText, nginxPath, maxCachedConnections, cts.Token, workload),
                "ramp" => RunRamp(modeText, nginxPath, resultsDir, concurrency, warmupSec, durationSec,
                    maxCachedConnections, repeats, workload, cts.Token),
                _ => Fail("Required: --serve | --serve-origin | --serve-proxy | --ramp")
            };
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception ex)
        {
            // ChildProcessStack only reads stderr on early exit; ProbeLog is stdout.
            Console.Error.WriteLine(ex.ToString());
            ProbeLog.Error(ex.ToString());
            return 1;
        }
    }

    private static int RunServe(string? modeText, string? nginxPath, int? maxCachedConnections, CancellationToken ct,
        WorkloadOptions workload)
    {
        if (modeText == null || !TryParseMode(modeText, out var mode))
            return Fail("Required: --serve --mode <see --help>");
        return ServeHost.RunAsync(mode, nginxPath, maxCachedConnections, ct, workload).GetAwaiter().GetResult();
    }

    private static int RunServeProxy(string? modeText, int originHttpPort, int originHttpsPort, int originQuicPort,
        IReadOnlyList<int> extraHttpsPorts, string? nginxPath, int? maxCachedConnections, CancellationToken ct,
        WorkloadOptions workload)
    {
        if (modeText == null || !TryParseMode(modeText, out var mode) || IsMultiArmMode(mode))
            return Fail("Required: --serve-proxy --mode <single arm>");
        return ServeProxyHost.RunAsync(mode, originHttpPort, originHttpsPort, originQuicPort, extraHttpsPorts,
                nginxPath, maxCachedConnections, ct, workload)
            .GetAwaiter().GetResult();
    }

    private static bool IsMultiArmMode(ProbeMode mode) => mode is ProbeMode.Compare or ProbeMode.CompareHttp2
        or ProbeMode.CompareTls or ProbeMode.CompareTerminate or ProbeMode.CompareSame or ProbeMode.CompareBridges
        or ProbeMode.CompareHttp3Cleartext
        or ProbeMode.CompareMitm or ProbeMode.CompareCeiling or ProbeMode.CompareBodies or ProbeMode.ComparePost
        or ProbeMode.CompareLossy or ProbeMode.CompareTlsCost or ProbeMode.CompareArch
        or ProbeMode.CompareSaturation
        or ProbeMode.ExplicitPoolSweep;

    private static int RunRamp(string? modeText, string? nginxPath, string? resultsDir, List<int> concurrency,
        int warmupSec, int durationSec, int? maxCachedConnections, int repeats, WorkloadOptions workload,
        CancellationToken ct)
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
                : [8, 16, 24, 32, 48, 64, 128, 256, 512],
            Workload = workload
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
            case "bare-reverse-http1":
                mode = ProbeMode.BareReverseHttp1;
                return true;
            case "nginx-reverse-http1":
                mode = ProbeMode.NginxReverseHttp1;
                return true;
            case "yarp-reverse-http1":
                mode = ProbeMode.YarpReverseHttp1;
                return true;
            case "https-mitm":
                mode = ProbeMode.HttpsMitm;
                return true;
            case "http-mitm":
                mode = ProbeMode.HttpMitm;
                return true;
            case "reverse-http1-mitm":
                mode = ProbeMode.ReverseHttp1Mitm;
                return true;
            case "reverse-http1-tls":
                mode = ProbeMode.ReverseHttp1Tls;
                return true;
            case "reverse-http1-to-https":
                mode = ProbeMode.ReverseHttp1ToHttps;
                return true;
            case "bare-reverse-http1-tls":
                mode = ProbeMode.BareReverseHttp1Tls;
                return true;
            case "nginx-reverse-http1-tls":
                mode = ProbeMode.NginxReverseHttp1Tls;
                return true;
            case "yarp-reverse-http1-tls":
                mode = ProbeMode.YarpReverseHttp1Tls;
                return true;
            case "yarp-reverse-http1-to-https":
                mode = ProbeMode.YarpReverseHttp1ToHttps;
                return true;
            case "reverse-http2":
                mode = ProbeMode.ReverseHttp2;
                return true;
            case "reverse-http2-cleartext":
                mode = ProbeMode.ReverseHttp2Cleartext;
                return true;
            case "reverse-http2-to-h2c":
                mode = ProbeMode.ReverseHttp2ToH2c;
                return true;
            case "yarp-reverse-http2-to-h2c":
                mode = ProbeMode.YarpReverseHttp2ToH2c;
                return true;
            case "reverse-h2c":
                mode = ProbeMode.ReverseH2c;
                return true;
            case "yarp-reverse-h2c":
                mode = ProbeMode.YarpReverseH2c;
                return true;
            case "reverse-h2c-to-h2c":
                mode = ProbeMode.ReverseH2cToH2c;
                return true;
            case "yarp-reverse-h2c-to-h2c":
                mode = ProbeMode.YarpReverseH2cToH2c;
                return true;
            case "reverse-h2c-to-h1":
                mode = ProbeMode.ReverseH2cToH1;
                return true;
            case "yarp-reverse-h2c-to-h1":
                mode = ProbeMode.YarpReverseH2cToH1;
                return true;
            case "reverse-h2c-to-h3":
                mode = ProbeMode.ReverseH2cToH3;
                return true;
            case "yarp-reverse-h2c-to-h3":
                mode = ProbeMode.YarpReverseH2cToH3;
                return true;
            case "nginx-reverse-http2":
                mode = ProbeMode.NginxReverseHttp2;
                return true;
            case "nginx-reverse-http3-cleartext":
                mode = ProbeMode.NginxReverseHttp3Cleartext;
                return true;
            case "yarp-reverse-http2":
                mode = ProbeMode.YarpReverseHttp2;
                return true;
            case "reverse-http3":
                mode = ProbeMode.ReverseHttp3;
                return true;
            case "reverse-http3-cleartext":
                mode = ProbeMode.ReverseHttp3Cleartext;
                return true;
            case "yarp-reverse-http3-cleartext":
                mode = ProbeMode.YarpReverseHttp3Cleartext;
                return true;
            case "reverse-http11-to-http2":
                mode = ProbeMode.ReverseHttp11ToHttp2;
                return true;
            case "yarp-reverse-http11-to-http2":
                mode = ProbeMode.YarpReverseHttp11ToHttp2;
                return true;
            case "reverse-http1-to-http3":
                mode = ProbeMode.ReverseHttp1ToHttp3;
                return true;
            case "yarp-reverse-http1-to-http3":
                mode = ProbeMode.YarpReverseHttp1ToHttp3;
                return true;
            case "reverse-http2-to-http3":
                mode = ProbeMode.ReverseHttp2ToHttp3;
                return true;
            case "yarp-reverse-http2-to-http3":
                mode = ProbeMode.YarpReverseHttp2ToHttp3;
                return true;
            case "reverse-http3-to-http2":
                mode = ProbeMode.ReverseHttp3ToHttp2;
                return true;
            case "yarp-reverse-http3-to-http2":
                mode = ProbeMode.YarpReverseHttp3ToHttp2;
                return true;
            case "yarp-reverse-http3-to-http3":
                mode = ProbeMode.YarpReverseHttp3ToHttp3;
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
            case "compare-http3-cleartext":
                mode = ProbeMode.CompareHttp3Cleartext;
                return true;
            case "compare-mitm":
                mode = ProbeMode.CompareMitm;
                return true;
            case "compare-ceiling":
                mode = ProbeMode.CompareCeiling;
                return true;
            case "compare-bodies":
                mode = ProbeMode.CompareBodies;
                return true;
            case "compare-post":
                mode = ProbeMode.ComparePost;
                return true;
            case "compare-lossy":
                mode = ProbeMode.CompareLossy;
                return true;
            case "compare-tls-cost":
                mode = ProbeMode.CompareTlsCost;
                return true;
            case "compare-arch":
                mode = ProbeMode.CompareArch;
                return true;
            case "compare-saturation":
                mode = ProbeMode.CompareSaturation;
                return true;
            case "origin-direct":
                mode = ProbeMode.OriginDirect;
                return true;
            case "yarp-reverse-http2-to-https":
                mode = ProbeMode.YarpReverseHttp2ToHttps;
                return true;
            case "mitm-http2-to-http1":
                mode = ProbeMode.MitmHttp2ToHttp1;
                return true;
            case "mitm-http3-to-http1":
                mode = ProbeMode.MitmHttp3ToHttp1;
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
            RpsLoadProbe — saturation RPS harness for Titanium.Web.Proxy.
            Published comparisons: wiki/Performance.md

            Usage:
              RpsLoadProbe --serve --mode <mode> [--nginx-path PATH] [--max-cached-connections N] [--response-bytes N]
              RpsLoadProbe --serve-origin [--https [--https-only] [--https-protocols http1|http1and2] [--extra-https-origins N] | --h2c | --quic]
              RpsLoadProbe --serve-proxy --mode <arm> [--origin-http-port N] [--origin-https-port N] [--origin-quic-port N]
              RpsLoadProbe --ramp  --mode <mode> [options]
              --ramp always uses three processes (load gen + origin child + proxy child). --serve is debug-only.

            Modes:
              reverse-http1           TWP TransparentProxyEndPoint -> Kestrel HTTP/1
              bare-reverse-http1      Thin C# HTTP/1 reverse (runtime-ceiling control)
              nginx-reverse-http1     Control arm: native reverse -> same Kestrel HTTP/1 origin
              yarp-reverse-http1      Control arm: managed reverse -> same Kestrel HTTP/1 origin
              reverse-http1-tls       TWP TLS terminate -> cleartext HTTP/1 origin
              reverse-http1-to-https  TWP cleartext HTTP/1 -> HTTPS HTTP/1 origin
              bare-reverse-http1-tls  Thin C# TLS-terminate HTTP/1 reverse
              nginx-reverse-http1-tls Control arm: TLS reverse -> cleartext HTTP/1 origin
              yarp-reverse-http1-tls  Control arm: TLS terminate -> cleartext HTTP/1 origin
              yarp-reverse-http1-to-https Control arm: cleartext HTTP/1 -> HTTPS HTTP/1
              https-mitm              TWP Explicit CONNECT MITM -> Kestrel HTTPS
              http-mitm               TWP Explicit intercepting proxy -> cleartext HTTP/1 origin
              reverse-http1-mitm      TWP transparent dual-TLS H1 MITM -> Kestrel HTTPS
              mitm-http2-to-http1     TWP H2 TLS MITM -> H2→H1 bridge -> Kestrel HTTPS/h1
              mitm-http3-to-http1     TWP H3 MITM -> bridge -> Kestrel HTTPS/h1
              reverse-http2           TWP Transparent TLS+h2 MITM -> Kestrel HTTPS (h2)
              reverse-http2-cleartext TWP TLS+h2 terminate -> H2→H1 bridge -> Kestrel HTTP/1
              reverse-http2-to-h2c     TWP TLS+h2 terminate -> prior-knowledge h2c -> Kestrel HTTP/2 cleartext
              yarp-reverse-http2-to-h2c Control arm: TLS+h2 -> prior-knowledge h2c
              reverse-h2c             TWP cleartext h2c reverse -> Kestrel HTTPS/h2
              yarp-reverse-h2c        Control arm: cleartext h2c -> HTTPS/h2
              reverse-h2c-to-h2c      TWP cleartext h2c reverse -> Kestrel HTTP/2 cleartext
              yarp-reverse-h2c-to-h2c Control arm: cleartext h2c -> h2c
              reverse-h2c-to-h1       TWP cleartext h2c reverse -> H2→H1 bridge -> Kestrel HTTP/1
              yarp-reverse-h2c-to-h1  Control arm: cleartext h2c -> HTTP/1
              reverse-h2c-to-h3       TWP cleartext h2c reverse -> H2→H3 bridge -> Quic/h3 origin
              yarp-reverse-h2c-to-h3  Control arm: cleartext h2c -> HTTP/3 origin
              nginx-reverse-http2     Control arm: ssl+http2 -> cleartext HTTP/1 origin
              nginx-reverse-http3-cleartext Control arm: QUIC/h3 -> cleartext HTTP/1 (needs http_v3_module)
              yarp-reverse-http2      Control arm: TLS+h2 -> cleartext HTTP/1 origin
              reverse-http3           TWP TransparentQuic (h3) -> Quic HTTPS/h3 origin
              reverse-http3-cleartext TWP QUIC/h3 terminate -> cleartext HTTP/1 origin
              yarp-reverse-http3-cleartext Control arm: HTTP/3 terminate -> cleartext HTTP/1
              reverse-http11-to-http2 TWP H1 TLS -> H1→H2 bridge -> Kestrel HTTPS/h2
              yarp-reverse-http11-to-http2 Control arm: H1 TLS -> HTTPS/h2
              reverse-http1-to-http3  TWP H1 TLS -> H1→H3 bridge -> Quic/h3 origin
              yarp-reverse-http1-to-http3 Control arm: H1 TLS -> HTTP/3 origin
              reverse-http2-to-http3  TWP H2 TLS -> H2→H3 bridge -> Quic/h3 origin
              yarp-reverse-http2-to-http3 Control arm: H2 TLS -> HTTP/3 origin
              reverse-http3-to-http2  TWP H3 -> H3→H2 bridge -> Kestrel HTTPS/h2
              yarp-reverse-http3-to-http2 Control arm: H3 -> HTTPS/h2
              yarp-reverse-http3-to-http3 Control arm: H3 -> HTTP/3 origin
              explicit-http1-multi    Explicit MITM across 16 HTTPS origins (fan-out)
              explicit-http2-multi    Same fan-out forcing HTTP/2
              compare                 Sequential HTTP/1 compare (+ MITM)
              compare-http2           Sequential: TWP h2 MITM, control H2 terminate, TWP h3
              compare-tls             Sequential: H1 TLS terminate, TWP h2 MITM, H2 terminate, TWP h3
              compare-terminate       Fair terminate: H1 TLS, H2→H1, H3→H1 (+ control arms)
              compare-same            Same-protocol: H1 cleartext, H1 TLS, H1 MITM, H2 MITM, H3 MITM (+ control arms)
              compare-bridges         Cross-version bridges (H1↔H2↔H3; TWP + control arms)
              compare-http3-cleartext H3→H1 cleartext only (TWP + YARP + nginx when available)
              compare-mitm            MITM matrix: same 15 Client×Origin pairs as reverse (inspectable/decrypt) + dual-crypto extras (TWP only)
              compare-ceiling         TWP vs bare C# vs control arms on H1 / H1 TLS / H2→H1 reverse
              compare-bodies          Heavier reverse GET (64 KiB + 256 KiB) vs control arms
              compare-post            POST 64 KiB request+response reverse vs control arms
              compare-lossy           64 KiB GET under userspace delay/loss vs control arms
              compare-tls-cost        H1 TLS terminate: keep-alive tiny / new-conn tiny / keep-alive 256 KiB
              compare-arch            Slow consumer, early response, H2 duplex, WebSocket echo vs control arms
              compare-saturation      Calibration: origin-direct (+ bombardier) + H1 plain peers;
                                      then H2 TLS→H1 and H3→H1 peers; CSV proxy_rss/cpu columns
                                      (proxy child + descendants; origin PID on origin-direct)
              origin-direct           Load generator → origin child only (no proxy)
              yarp-reverse-http2-to-https Control arm: TLS+h2 -> HTTPS/h2
              explicit-pool-sweep     Fan-out with MaxCachedConnections 4 / 32 / 128

            Options:
              --nginx-path PATH
              --results-dir DIR
              --https / --https-only / --https-protocols http1|http1and2 / --extra-https-origins N
              --h2c / --quic         Origin listen shape for --serve-origin
              --origin-http-port N / --origin-https-port N / --origin-quic-port N
              --origin-https-extra-port N   Repeatable; explicit-multi extras
              --concurrency LIST      Default: 8,16,24,32,48,64,128,256,512
              --warmup-sec N
              --duration-sec N
              --repeats N             Full arm sequence N times; print median peaks (default 1)
              --max-cached-connections N   Override ProxyServer.MaxCachedConnections for TWP arms
              --method GET|POST       Default GET (compare-post sets POST per arm)
              --response-bytes N      Origin response size (default ~64 B tiny JSON)
              --request-bytes N       POST body size (default 0)
              --no-keepalive          New TCP/TLS connection per request (handshake cost)
              --delay-ms N            Userspace one-way delay via lossy shim (0 = off)
              --loss-percent P        TCP connection stall % or UDP datagram drop % (0 = off)
              --early-response-after N  Origin starts response after N request bytes (0 = drain-then-write)
              --websocket             Origin /ws echo; client uses ClientWebSocket
            """);
    }
}
