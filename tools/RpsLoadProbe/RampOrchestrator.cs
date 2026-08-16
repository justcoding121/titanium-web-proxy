using System.Globalization;

namespace Titanium.Web.Proxy.RpsLoadProbe;

internal enum ProbeMode
{
    ReverseHttp1,
    NginxReverseHttp1,
    HttpsMitm,
    Compare
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
        Console.WriteLine("Process split: origin process + proxy process + load-gen (this process).");
        Console.WriteLine();

        await using var csv = new StreamWriter(csvPath);
        CsvWriter.WriteHeader(csv);

        var arms = ResolveArms(options.Mode, nginxExe != null);
        if (options.Mode is ProbeMode.NginxReverseHttp1 or ProbeMode.Compare && nginxExe == null)
        {
            Console.WriteLine(NginxHost.NginxMissingMessage());
            Console.WriteLine();
        }

        foreach (var arm in arms)
        {
            Console.WriteLine($"--- arm {arm} ---");
            await RunArmAsync(arm, options, csv, nginxVersion, cancellationToken);
            Console.WriteLine();
        }

        await csv.FlushAsync(cancellationToken);
        Console.WriteLine($"CSV: {Path.GetFullPath(csvPath)}");
        return 0;
    }

    private static IReadOnlyList<string> ResolveArms(ProbeMode mode, bool nginxAvailable) => mode switch
    {
        ProbeMode.ReverseHttp1 => ["twp-reverse-http1"],
        ProbeMode.NginxReverseHttp1 => nginxAvailable ? ["nginx-reverse-http1"] : [],
        ProbeMode.HttpsMitm => ["twp-https-mitm"],
        ProbeMode.Compare => nginxAvailable
            ? ["twp-reverse-http1", "nginx-reverse-http1", "twp-https-mitm"]
            : ["twp-reverse-http1", "twp-https-mitm"],
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static async Task RunArmAsync(string arm, RampOptions options, StreamWriter csv,
        string? nginxVersionHint, CancellationToken cancellationToken)
    {
        await using var stack = await ChildProcessStack.StartAsync(arm, options.NginxPath, cancellationToken);
        var nginxVersion = stack.NginxVersion ?? nginxVersionHint;

        var p99Slo = arm == "twp-https-mitm" ? options.HttpsMitmP99MsSlo : options.Http1P99MsSlo;
        LoadResult? lastGood = null;
        LoadResult? peak = null;
        var lastGoodConcurrency = 0;

        Console.WriteLine(
            $"  target={stack.TargetUrl} proxy={(stack.ExplicitProxyUrl ?? "(direct-to-listen)")} generator=dotnet-httpclient");

        foreach (var concurrency in options.ConcurrencySteps)
        {
            Console.WriteLine($"  warmup c={concurrency} for {options.Warmup.TotalSeconds:F0}s...");
            await EmbeddedLoadGenerator.WarmupAsync(stack.TargetUri, stack.ExplicitProxyUrl, concurrency,
                options.Warmup, cancellationToken);

            Console.WriteLine($"  measure c={concurrency} for {options.StepDuration.TotalSeconds:F0}s...");
            var result = await EmbeddedLoadGenerator.RunAsync(stack.TargetUri, stack.ExplicitProxyUrl, concurrency,
                options.StepDuration, cancellationToken);

            var meetsSlo = result.ErrorRatePercent < options.MaxErrorRatePercent && result.P99Ms <= p99Slo;
            CsvWriter.WriteRow(csv, arm, result, meetsSlo, nginxVersion);
            await csv.FlushAsync(cancellationToken);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    rps={result.Rps:F0} err%={result.ErrorRatePercent:F3} p50={result.P50Ms:F1}ms p99={result.P99Ms:F1}ms max={result.MaxMs:F1}ms slo={(meetsSlo ? "PASS" : "FAIL")}"));

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
            $"  summary arm={arm} sustainable_rps={(lastGood?.Rps ?? 0):F0} @ c={lastGoodConcurrency} peak_rps={(peak?.Rps ?? 0):F0} @ c={peak?.Concurrency ?? 0} p99_slo_ms={p99Slo:F0}"));
    }

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
