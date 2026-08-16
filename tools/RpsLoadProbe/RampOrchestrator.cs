using System.Globalization;

namespace Titanium.Web.Proxy.RpsLoadProbe;

internal enum ProbeMode
{
    ReverseHttp1,
    NginxReverseHttp1,
    HttpsMitm,
    ReverseHttp2,
    NginxReverseHttp2,
    ReverseHttp3,
    ExplicitHttp1Multi,
    ExplicitHttp2Multi,
    Compare,
    CompareHttp2,
    ExplicitPoolSweep
}

internal sealed class RampOptions
{
    public required ProbeMode Mode { get; init; }
    public string? NginxPath { get; init; }
    public string ResultsDir { get; init; } = Path.Combine("results");
    public int[] ConcurrencySteps { get; init; } = [8, 16, 24, 32, 48, 64, 128, 256, 512];
    public TimeSpan Warmup { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan StepDuration { get; init; } = TimeSpan.FromSeconds(20);
    public double MaxErrorRatePercent { get; init; } = 0.1;
    public double Http1P99MsSlo { get; init; } = 50;
    public double HttpsMitmP99MsSlo { get; init; } = 100;
    public double Http2P99MsSlo { get; init; } = 100;
    public double Http3P99MsSlo { get; init; } = 150;
    public int? MaxCachedConnections { get; init; }
}

internal static class RampOrchestrator
{
    public static async Task<int> RunAsync(RampOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.ResultsDir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var csvPath = Path.Combine(options.ResultsDir, $"rps-ramp-{stamp}.csv");

        string? nginxVersion = null;
        var nginxExe = NginxHost.ResolveNginxExecutable(options.NginxPath);
        if (nginxExe != null)
            nginxVersion = ProbeNginxVersion(nginxExe);

        Console.WriteLine(MachineInfo.FormatReport(nginxVersion));
        Console.WriteLine("Close browsers and other heavy apps before a publishable run.");
        Console.WriteLine("Process split: origin/proxy as children when possible; TLS arms use combined --serve.");
        Console.WriteLine();

        await using var csv = new StreamWriter(csvPath);
        CsvWriter.WriteHeader(csv);

        var arms = ResolveArms(options.Mode, nginxExe != null);
        if ((options.Mode is ProbeMode.NginxReverseHttp1 or ProbeMode.NginxReverseHttp2 or ProbeMode.Compare
                or ProbeMode.CompareHttp2)
            && nginxExe == null)
        {
            Console.WriteLine(NginxHost.NginxMissingMessage());
            Console.WriteLine();
        }

        foreach (var arm in arms)
        {
            Console.WriteLine($"--- arm {arm.Name} ---");
            await RunArmAsync(arm, options, csv, nginxVersion, cancellationToken);
            Console.WriteLine();
        }

        await csv.FlushAsync(cancellationToken);
        Console.WriteLine($"CSV: {Path.GetFullPath(csvPath)}");
        return 0;
    }

    private sealed record ArmSpec(string Name, ProbeMode Mode, int? MaxCachedConnections, string HypothesisId);

    private static IReadOnlyList<ArmSpec> ResolveArms(ProbeMode mode, bool nginxAvailable)
    {
        return mode switch
        {
            ProbeMode.ReverseHttp1 => [new("twp-reverse-http1", ProbeMode.ReverseHttp1, null, "H1")],
            ProbeMode.NginxReverseHttp1 => nginxAvailable
                ? [new("nginx-reverse-http1", ProbeMode.NginxReverseHttp1, null, "H1")]
                : [],
            ProbeMode.HttpsMitm => [new("twp-https-mitm", ProbeMode.HttpsMitm, null, "MITM")],
            ProbeMode.ReverseHttp2 => [new("twp-reverse-http2", ProbeMode.ReverseHttp2, null, "B")],
            ProbeMode.NginxReverseHttp2 => nginxAvailable
                ? [new("nginx-reverse-http2", ProbeMode.NginxReverseHttp2, null, "B")]
                : [],
            ProbeMode.ReverseHttp3 => [new("twp-reverse-http3", ProbeMode.ReverseHttp3, null, "C")],
            ProbeMode.ExplicitHttp1Multi =>
                [new("twp-explicit-http1-multi", ProbeMode.ExplicitHttp1Multi, null, "A")],
            ProbeMode.ExplicitHttp2Multi =>
                [new("twp-explicit-http2-multi", ProbeMode.ExplicitHttp2Multi, null, "A")],
            ProbeMode.Compare => nginxAvailable
                ?
                [
                    new("twp-reverse-http1", ProbeMode.ReverseHttp1, null, "H1"),
                    new("nginx-reverse-http1", ProbeMode.NginxReverseHttp1, null, "H1"),
                    new("twp-https-mitm", ProbeMode.HttpsMitm, null, "MITM")
                ]
                :
                [
                    new("twp-reverse-http1", ProbeMode.ReverseHttp1, null, "H1"),
                    new("twp-https-mitm", ProbeMode.HttpsMitm, null, "MITM")
                ],
            ProbeMode.CompareHttp2 => nginxAvailable
                ?
                [
                    new("twp-reverse-http2", ProbeMode.ReverseHttp2, null, "B"),
                    new("nginx-reverse-http2", ProbeMode.NginxReverseHttp2, null, "B"),
                    new("twp-reverse-http3", ProbeMode.ReverseHttp3, null, "C")
                ]
                :
                [
                    new("twp-reverse-http2", ProbeMode.ReverseHttp2, null, "B"),
                    new("twp-reverse-http3", ProbeMode.ReverseHttp3, null, "C")
                ],
            ProbeMode.ExplicitPoolSweep =>
            [
                new("twp-explicit-http1-multi-c4", ProbeMode.ExplicitHttp1Multi, 4, "A"),
                new("twp-explicit-http1-multi-c32", ProbeMode.ExplicitHttp1Multi, 32, "A"),
                new("twp-explicit-http1-multi-c128", ProbeMode.ExplicitHttp1Multi, 128, "A")
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    private static async Task RunArmAsync(ArmSpec arm, RampOptions options, StreamWriter csv,
        string? nginxVersionHint, CancellationToken cancellationToken)
    {
        var maxCached = arm.MaxCachedConnections ?? options.MaxCachedConnections;
        await using var stack = await ChildProcessStack.StartAsync(arm.Mode, options.NginxPath, maxCached,
            cancellationToken);
        var nginxVersion = stack.NginxVersion ?? nginxVersionHint;

        var p99Slo = ResolveP99Slo(arm.Mode, options);
        LoadResult? lastGood = null;
        LoadResult? peak = null;
        var lastGoodConcurrency = 0;

        var useQuic = string.Equals(stack.LoadGenerator, "quic-http3", StringComparison.OrdinalIgnoreCase)
                      && stack.QuicPort is > 0;
        var loadOptions = new LoadRequestOptions
        {
            Target = stack.TargetUri,
            Targets = stack.TargetUris.Count > 1 ? stack.TargetUris : null,
            ExplicitProxyUrl = stack.ExplicitProxyUrl,
            HttpVersion = stack.RequestHttpVersion,
            VersionPolicy = stack.VersionPolicy
        };

        Console.WriteLine(
            $"  target={stack.TargetUrl} targets={stack.TargetUris.Count} proxy={(stack.ExplicitProxyUrl ?? "(direct-to-listen)")} http={stack.RequestHttpVersion} generator={(useQuic ? "quic-http3" : "dotnet-httpclient")} maxCached={(maxCached?.ToString() ?? "default")}");

        foreach (var concurrency in options.ConcurrencySteps)
        {
            Console.WriteLine($"  warmup c={concurrency} for {options.Warmup.TotalSeconds:F0}s...");
            if (useQuic)
            {
                var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, stack.QuicPort!.Value);
                var authority = stack.OriginQuicPort is { } op
                    ? $"localhost:{op}"
                    : "localhost";
                await QuicHttp3LoadGenerator.WarmupAsync(ep, "localhost", authority,
                    concurrency, options.Warmup, cancellationToken);
            }
            else
            {
                await EmbeddedLoadGenerator.WarmupAsync(loadOptions, concurrency, options.Warmup, cancellationToken);
            }

            Console.WriteLine($"  measure c={concurrency} for {options.StepDuration.TotalSeconds:F0}s...");
            LoadResult result;
            if (useQuic)
            {
                var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, stack.QuicPort!.Value);
                var authority = stack.OriginQuicPort is { } op
                    ? $"localhost:{op}"
                    : "localhost";
                result = await QuicHttp3LoadGenerator.RunAsync(ep, "localhost", authority,
                    concurrency, options.StepDuration, cancellationToken);
            }
            else
            {
                result = await EmbeddedLoadGenerator.RunAsync(loadOptions, concurrency, options.StepDuration,
                    cancellationToken);
            }

            var meetsSlo = result.ErrorRatePercent < options.MaxErrorRatePercent && result.P99Ms <= p99Slo;
            CsvWriter.WriteRow(csv, arm.Name, result, meetsSlo, nginxVersion, maxCached);
            await csv.FlushAsync(cancellationToken);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    rps={result.Rps:F0} err%={result.ErrorRatePercent:F3} p50={result.P50Ms:F1}ms p99={result.P99Ms:F1}ms max={result.MaxMs:F1}ms ver={result.NegotiatedVersionHint} slo={(meetsSlo ? "PASS" : "FAIL")}"));

            // #region agent log
            DebugSessionLog.WriteResult(arm.HypothesisId, arm.Name, result, meetsSlo,
                maxCachedConnections: maxCached);
            // #endregion

            if (peak == null || result.Rps > peak.Rps)
                peak = result;

            if (meetsSlo)
            {
                lastGood = result;
                lastGoodConcurrency = concurrency;
            }
            else if (lastGood != null)
            {
                Console.WriteLine($"    (breaking-point candidate at c={lastGoodConcurrency})");
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  summary arm={arm.Name} sustainable_rps={(lastGood?.Rps ?? 0):F0} @ c={lastGoodConcurrency} peak_rps={(peak?.Rps ?? 0):F0} @ c={peak?.Concurrency ?? 0} p99_slo_ms={p99Slo:F0}"));

        // #region agent log
        DebugSessionLog.Write(arm.HypothesisId, "RampOrchestrator.summary", "arm-complete",
            new
            {
                arm = arm.Name,
                peakRps = peak?.Rps,
                peakC = peak?.Concurrency,
                sustainRps = lastGood?.Rps,
                sustainC = lastGoodConcurrency,
                maxCached
            });
        // #endregion
    }

    private static double ResolveP99Slo(ProbeMode mode, RampOptions options) => mode switch
    {
        ProbeMode.HttpsMitm or ProbeMode.ExplicitHttp1Multi or ProbeMode.ExplicitHttp2Multi =>
            options.HttpsMitmP99MsSlo,
        ProbeMode.ReverseHttp2 or ProbeMode.NginxReverseHttp2 => options.Http2P99MsSlo,
        ProbeMode.ReverseHttp3 => options.Http3P99MsSlo,
        _ => options.Http1P99MsSlo
    };

    private static string ProbeNginxVersion(string exe)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-v",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (p == null) return "unknown";
            var err = p.StandardError.ReadToEnd();
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var text = string.IsNullOrWhiteSpace(err) ? stdout : err;
            return text.Trim().Replace('\n', ' ');
        }
        catch
        {
            return "unknown";
        }
    }
}
