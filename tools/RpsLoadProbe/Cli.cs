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
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
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
                "serve-proxy" => RunServeProxy(modeText, originHttpPort, originHttpsPort, nginxPath, cts.Token),
                "serve" => RunServe(modeText, nginxPath, cts.Token),
                "ramp" => RunRamp(modeText, nginxPath, resultsDir, concurrency, warmupSec, durationSec, cts.Token),
                _ => Fail("Required: --serve | --serve-origin | --serve-proxy | --ramp")
            };
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int RunServe(string? modeText, string? nginxPath, CancellationToken ct)
    {
        if (modeText == null || !TryParseMode(modeText, out var mode))
            return Fail("Required: --serve --mode <reverse-http1|nginx-reverse-http1|https-mitm>");
        return ServeHost.RunAsync(mode, nginxPath, ct).GetAwaiter().GetResult();
    }

    private static int RunServeProxy(string? modeText, int originHttpPort, int originHttpsPort, string? nginxPath,
        CancellationToken ct)
    {
        if (modeText == null || !TryParseMode(modeText, out var mode) || mode == ProbeMode.Compare)
            return Fail("Required: --serve-proxy --mode <reverse-http1|nginx-reverse-http1|https-mitm>");
        return ServeProxyHost.RunAsync(mode, originHttpPort, originHttpsPort, nginxPath, ct).GetAwaiter().GetResult();
    }

    private static int RunRamp(string? modeText, string? nginxPath, string? resultsDir, List<int> concurrency,
        int warmupSec, int durationSec, CancellationToken ct)
    {
        if (modeText == null || !TryParseMode(modeText, out var mode))
            return Fail("Required: --ramp --mode <reverse-http1|nginx-reverse-http1|https-mitm|compare>");

        var options = new RampOptions
        {
            Mode = mode,
            NginxPath = nginxPath,
            ResultsDir = resultsDir ?? Path.Combine(AppContext.BaseDirectory, "results"),
            Warmup = TimeSpan.FromSeconds(warmupSec),
            StepDuration = TimeSpan.FromSeconds(durationSec),
            ConcurrencySteps = concurrency.Count > 0
                ? concurrency.ToArray()
                : [8, 16, 24, 32, 48, 64, 128, 256, 512]
        };
        return RampOrchestrator.RunAsync(options, ct).GetAwaiter().GetResult();
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
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
            case "compare":
                mode = ProbeMode.Compare;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            RpsLoadProbe — saturation RPS harness for Titanium.Web.Proxy (and nginx control arm)

            Usage:
              RpsLoadProbe --serve --mode <reverse-http1|nginx-reverse-http1|https-mitm> [--nginx-path PATH]
              RpsLoadProbe --serve-origin [--https]
              RpsLoadProbe --serve-proxy --mode <...> --origin-http-port N [--origin-https-port N]
              RpsLoadProbe --ramp  --mode <reverse-http1|nginx-reverse-http1|https-mitm|compare> [options]

            --ramp spawns origin and proxy as separate processes; the parent only generates load.

            Options:
              --nginx-path PATH       Path to nginx[.exe] (otherwise PATH is searched)
              --results-dir DIR       CSV output directory (ramp only)
              --concurrency LIST      Default: 8,16,24,32,48,64,128,256,512
              --warmup-sec N          Warmup seconds per step (default: 5)
              --duration-sec N        Measure seconds per step (default: 20)

            Modes:
              reverse-http1           TWP TransparentProxyEndPoint -> Kestrel HTTP
              nginx-reverse-http1     nginx proxy_pass -> same Kestrel origin
              https-mitm              TWP ExplicitProxyEndPoint MITM -> Kestrel HTTPS
              compare                 Sequential: TWP reverse, nginx (if present), TWP MITM

            SLO defaults (breaking point = last concurrency meeting all):
              error rate < 0.1%
              p99 <= 50 ms (HTTP/1) or 100 ms (HTTPS MITM)
            """);
    }
}
