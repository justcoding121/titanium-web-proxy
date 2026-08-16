using System.Globalization;

namespace Titanium.Web.Proxy.RpsLoadProbe;

internal enum ProbeMode
{
    ReverseHttp1,
    NginxReverseHttp1,
    HttpsMitm,
    ReverseHttp1Tls,
    NginxReverseHttp1Tls,
    ReverseHttp2,
    /// <summary>TWP client TLS+h2 → ForwardCleartext H2→H1 bridge → cleartext HTTP/1 origin.</summary>
    ReverseHttp2Cleartext,
    NginxReverseHttp2,
    ReverseHttp3,
    /// <summary>TWP QUIC/h3 terminate → ForwardCleartext → cleartext HTTP/1 origin.</summary>
    ReverseHttp3Cleartext,
    ExplicitHttp1Multi,
    ExplicitHttp2Multi,
    Compare,
    CompareHttp2,
    CompareTls,
    /// <summary>Fair TLS-terminate compare: H1 TLS, H2→H1 cleartext, H3→H1 cleartext vs nginx where available.</summary>
    CompareTerminate,
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

        ProbeLog.Info(MachineInfo.FormatReport(nginxVersion));
        ProbeLog.Info("Close browsers and other heavy apps before a publishable run.");
        ProbeLog.Info("Process split: origin/proxy as children when possible; TLS arms use combined --serve.");
        ProbeLog.Info(string.Empty);

        await using var csv = new StreamWriter(csvPath);
        CsvWriter.WriteHeader(csv);

        var arms = ResolveArms(options.Mode, nginxExe != null).ToList();
        if (!System.Net.Quic.QuicListener.IsSupported)
        {
            var removed = arms.RemoveAll(a =>
                a.Mode is ProbeMode.ReverseHttp3 or ProbeMode.ReverseHttp3Cleartext);
            if (removed > 0)
                ProbeLog.Info("QuicListener is not supported on this host — skipping HTTP/3 arms.");
        }

        if (arms.Count == 0)
        {
            ProbeLog.Error("No arms to run for this mode/host combination.");
            return 2;
        }

        if ((options.Mode is ProbeMode.NginxReverseHttp1 or ProbeMode.NginxReverseHttp1Tls
                or ProbeMode.NginxReverseHttp2 or ProbeMode.Compare or ProbeMode.CompareHttp2
                or ProbeMode.CompareTls or ProbeMode.CompareTerminate)
            && nginxExe == null)
        {
            ProbeLog.Info(NginxHost.NginxMissingMessage());
            ProbeLog.Info(string.Empty);
        }

        foreach (var arm in arms)
        {
            ProbeLog.Info($"--- arm {arm.Name} ---");
            await RunArmAsync(arm, options, csv, nginxVersion, cancellationToken);
            ProbeLog.Info(string.Empty);
        }

        await csv.FlushAsync(cancellationToken);
        ProbeLog.Info($"CSV: {Path.GetFullPath(csvPath)}");
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
            ProbeMode.ReverseHttp1Tls => [new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null, "H1TLS")],
            ProbeMode.NginxReverseHttp1Tls => nginxAvailable
                ? [new("nginx-reverse-http1-tls", ProbeMode.NginxReverseHttp1Tls, null, "H1TLS")]
                : [],
            ProbeMode.ReverseHttp2 => [new("twp-reverse-http2", ProbeMode.ReverseHttp2, null, "B")],
            ProbeMode.ReverseHttp2Cleartext =>
                [new("twp-reverse-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null, "H2H1")],
            ProbeMode.NginxReverseHttp2 => nginxAvailable
                ? [new("nginx-reverse-http2", ProbeMode.NginxReverseHttp2, null, "B")]
                : [],
            ProbeMode.ReverseHttp3 => [new("twp-reverse-http3", ProbeMode.ReverseHttp3, null, "C")],
            ProbeMode.ReverseHttp3Cleartext =>
                [new("twp-reverse-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null, "H3H1")],
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
            ProbeMode.CompareTls => nginxAvailable
                ?
                [
                    new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null, "H1TLS"),
                    new("nginx-reverse-http1-tls", ProbeMode.NginxReverseHttp1Tls, null, "H1TLS"),
                    new("twp-reverse-http2", ProbeMode.ReverseHttp2, null, "B"),
                    new("nginx-reverse-http2", ProbeMode.NginxReverseHttp2, null, "B"),
                    new("twp-reverse-http3", ProbeMode.ReverseHttp3, null, "C")
                ]
                :
                [
                    new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null, "H1TLS"),
                    new("twp-reverse-http2", ProbeMode.ReverseHttp2, null, "B"),
                    new("twp-reverse-http3", ProbeMode.ReverseHttp3, null, "C")
                ],
            ProbeMode.CompareTerminate => nginxAvailable
                ?
                [
                    new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null, "H1TLS"),
                    new("nginx-reverse-http1-tls", ProbeMode.NginxReverseHttp1Tls, null, "H1TLS"),
                    new("twp-reverse-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null, "H2H1"),
                    new("nginx-reverse-http2", ProbeMode.NginxReverseHttp2, null, "B"),
                    new("twp-reverse-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null, "H3H1")
                ]
                :
                [
                    new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null, "H1TLS"),
                    new("twp-reverse-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null, "H2H1"),
                    new("twp-reverse-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null, "H3H1")
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

        ProbeLog.Info(
            $"  target={stack.TargetUrl} targets={stack.TargetUris.Count} proxy={(stack.ExplicitProxyUrl ?? "(direct-to-listen)")} http={stack.RequestHttpVersion} generator={(useQuic ? "quic-http3" : "dotnet-httpclient")} maxCached={(maxCached?.ToString() ?? "default")}");

        foreach (var concurrency in options.ConcurrencySteps)
        {
            ProbeLog.Info($"  warmup c={concurrency} for {options.Warmup.TotalSeconds:F0}s...");
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

            ProbeLog.Info($"  measure c={concurrency} for {options.StepDuration.TotalSeconds:F0}s...");
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

            ProbeLog.Info(string.Create(CultureInfo.InvariantCulture,
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
                ProbeLog.Info($"    (breaking-point candidate at c={lastGoodConcurrency})");
            }
        }

        ProbeLog.Info(string.Create(CultureInfo.InvariantCulture,
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
        ProbeMode.ReverseHttp2 or ProbeMode.ReverseHttp2Cleartext or ProbeMode.NginxReverseHttp2 =>
            options.Http2P99MsSlo,
        ProbeMode.ReverseHttp3 or ProbeMode.ReverseHttp3Cleartext => options.Http3P99MsSlo,
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
