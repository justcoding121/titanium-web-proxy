using System.Globalization;

namespace Titanium.Web.Proxy.RpsLoadProbe;

internal enum ProbeMode
{
    ReverseHttp1,
    BareReverseHttp1,
    NginxReverseHttp1,
    YarpReverseHttp1,
    HttpsMitm,
    ReverseHttp1Tls,
    BareReverseHttp1Tls,
    NginxReverseHttp1Tls,
    YarpReverseHttp1Tls,
    ReverseHttp2,
    /// <summary>TWP client TLS+h2 → ForwardCleartext H2→H1 bridge → cleartext HTTP/1 origin.</summary>
    ReverseHttp2Cleartext,
    /// <summary>TWP client TLS+h2 → ForwardCleartext prior-knowledge h2c → cleartext HTTP/2 origin.</summary>
    ReverseHttp2ToH2c,
    /// <summary>Client prior-knowledge h2c → HTTPS origin ALPN h2.</summary>
    ReverseH2c,
    /// <summary>Client prior-knowledge h2c → cleartext HTTP/2 origin.</summary>
    ReverseH2cToH2c,
    /// <summary>Client prior-knowledge h2c → H2→H1 bridge → cleartext HTTP/1.</summary>
    ReverseH2cToH1,
    /// <summary>Client prior-knowledge h2c → H2→H3 bridge → QUIC/h3.</summary>
    ReverseH2cToH3,
    NginxReverseHttp2,
    /// <summary>YARP client TLS+h2 → cleartext HTTP/1 origin (nginx parity).</summary>
    YarpReverseHttp2,
    YarpReverseHttp2ToH2c,
    YarpReverseH2c,
    YarpReverseH2cToH2c,
    YarpReverseH2cToH1,
    YarpReverseH2cToH3,
    ReverseHttp3,
    /// <summary>TWP QUIC/h3 terminate → ForwardCleartext → cleartext HTTP/1 origin.</summary>
    ReverseHttp3Cleartext,
    /// <summary>YARP HTTP/3 (Kestrel) terminate → cleartext HTTP/1. Client uses HttpClient H3.</summary>
    YarpReverseHttp3Cleartext,
    /// <summary>Client H1 TLS → H1→H2 bridge → origin HTTPS h2.</summary>
    ReverseHttp11ToHttp2,
    YarpReverseHttp11ToHttp2,
    /// <summary>Client H1 TLS → H1→H3 bridge → origin QUIC/h3.</summary>
    ReverseHttp1ToHttp3,
    YarpReverseHttp1ToHttp3,
    /// <summary>Client H2 TLS → H2→H3 cold bridge → origin QUIC/h3.</summary>
    ReverseHttp2ToHttp3,
    YarpReverseHttp2ToHttp3,
    /// <summary>Client H3 → H3→H2 bridge → origin HTTPS h2.</summary>
    ReverseHttp3ToHttp2,
    YarpReverseHttp3ToHttp2,
    /// <summary>YARP client H3 → origin HTTP/3.</summary>
    YarpReverseHttp3ToHttp3,
    /// <summary>Client H2 TLS → H2→H1 bridge → origin HTTPS HTTP/1 (MITM, both sides TLS).</summary>
    MitmHttp2ToHttp1,
    /// <summary>Client H3 QUIC → bridge → origin HTTPS HTTP/1 (MITM, both sides crypto).</summary>
    MitmHttp3ToHttp1,
    ExplicitHttp1Multi,
    ExplicitHttp2Multi,
    Compare,
    CompareHttp2,
    CompareTls,
    /// <summary>Fair TLS-terminate compare: H1 TLS, H2→H1 cleartext, H3→H1 cleartext vs nginx where available.</summary>
    CompareTerminate,
    /// <summary>
    /// Same-protocol matrix: H1 cleartext, H1 TLS terminate, H2 MITM, H3 MITM (+ nginx where comparable).
    /// </summary>
    CompareSame,
    /// <summary>All implemented cross-version bridges under load (no nginx).</summary>
    CompareBridges,
    /// <summary>
    /// MITM-only matrix: explicit H1 MITM, transparent H2/H3 MITM, and dual-crypto bridges (no nginx).
    /// </summary>
    CompareMitm,
    /// <summary>TWP vs bare C# reverse vs nginx on the three Linux nginx-winning reverse rows.</summary>
    CompareCeiling,
    /// <summary>Heavier reverse GET bodies (64 KiB / 256 KiB) vs nginx where possible.</summary>
    CompareBodies,
    /// <summary>POST 64 KiB request+response reverse vs nginx where possible.</summary>
    ComparePost,
    /// <summary>64 KiB GET under userspace delay/loss (H2/H3 conditions) vs nginx where possible.</summary>
    CompareLossy,
    /// <summary>H1 TLS terminate cost: keep-alive tiny, new-connection tiny, keep-alive 256 KiB.</summary>
    CompareTlsCost,
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
    /// <summary>How many full arm sequences to run; peaks are median-aggregated (L1 runner noise).</summary>
    public int Repeats { get; init; } = 1;
    /// <summary>Default workload when an arm does not override (preserves tiny-GET matrix).</summary>
    public WorkloadOptions Workload { get; init; } = WorkloadOptions.TinyGet;
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
        if (options.Repeats > 1)
            ProbeLog.Info($"Repeats={options.Repeats} (median peak RPS per arm — dampens GHA runner noise).");
        ProbeLog.Info(string.Empty);

        await using var csv = new StreamWriter(csvPath);
        await CsvWriter.WriteHeaderAsync(csv);

        var arms = ResolveArms(options.Mode, nginxExe != null).ToList();
        if (!System.Net.Quic.QuicListener.IsSupported)
        {
            var removed = arms.RemoveAll(a =>
                a.Mode is ProbeMode.ReverseHttp3 or ProbeMode.ReverseHttp3Cleartext
                    or ProbeMode.YarpReverseHttp3Cleartext
                    or ProbeMode.ReverseHttp1ToHttp3 or ProbeMode.YarpReverseHttp1ToHttp3
                    or ProbeMode.ReverseHttp2ToHttp3 or ProbeMode.YarpReverseHttp2ToHttp3
                    or ProbeMode.ReverseHttp3ToHttp2 or ProbeMode.YarpReverseHttp3ToHttp2
                    or ProbeMode.YarpReverseHttp3ToHttp3
                    or ProbeMode.ReverseH2cToH3 or ProbeMode.YarpReverseH2cToH3
                    or ProbeMode.MitmHttp3ToHttp1);
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
                or ProbeMode.CompareTls or ProbeMode.CompareTerminate or ProbeMode.CompareSame
                or ProbeMode.CompareBodies or ProbeMode.ComparePost or ProbeMode.CompareLossy
                or ProbeMode.CompareTlsCost)
            && nginxExe == null)
        {
            ProbeLog.Info(NginxHost.NginxMissingMessage());
            ProbeLog.Info(string.Empty);
        }

        var peakByArm = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var repeats = Math.Max(1, options.Repeats);
        for (var rep = 1; rep <= repeats; rep++)
        {
            if (repeats > 1)
                ProbeLog.Info($"=== repeat {rep}/{repeats} ===");

            foreach (var arm in arms)
            {
                ProbeLog.Info($"--- arm {arm.Name} ---");
                var peak = await RunArmAsync(arm, options, csv, nginxVersion, cancellationToken);
                if (!peakByArm.TryGetValue(arm.Name, out var list))
                {
                    list = [];
                    peakByArm[arm.Name] = list;
                }

                list.Add(peak);
                ProbeLog.Info(string.Empty);
            }
        }

        if (repeats > 1)
            WriteMedianSummary(peakByArm);

        await csv.FlushAsync(cancellationToken);
        ProbeLog.Info($"CSV: {Path.GetFullPath(csvPath)}");
        return 0;
    }

    private static void WriteMedianSummary(Dictionary<string, List<double>> peakByArm)
    {
        ProbeLog.Info("=== median peaks across repeats ===");
        double? twpH1Tls = null, nginxH1Tls = null, yarpH1Tls = null;
        foreach (var (name, peaks) in peakByArm.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var median = Median(peaks);
            ProbeLog.Info($"  {name}: median_peak_rps={median:F1} (n={peaks.Count})");
            if (name.Contains("twp-reverse-http1-tls", StringComparison.Ordinal))
                twpH1Tls = median;
            if (name.Contains("nginx-reverse-http1-tls", StringComparison.Ordinal))
                nginxH1Tls = median;
            if (name.Contains("yarp-reverse-http1-tls", StringComparison.Ordinal))
                yarpH1Tls = median;
        }

        if (twpH1Tls is > 0 && nginxH1Tls is > 0)
        {
            var ratio = twpH1Tls.Value / nginxH1Tls.Value;
            ProbeLog.Info($"  TWP÷nginx H1 TLS median ratio={ratio:F3}");
        }

        if (twpH1Tls is > 0 && yarpH1Tls is > 0)
        {
            var ratio = twpH1Tls.Value / yarpH1Tls.Value;
            ProbeLog.Info($"  TWP÷YARP H1 TLS median ratio={ratio:F3}");
        }
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }

    private sealed record ArmSpec(string Name, ProbeMode Mode, int? MaxCachedConnections,
        WorkloadOptions? Workload = null);

    private static IReadOnlyList<ArmSpec> HeavierReverseArms(bool nginxAvailable, WorkloadOptions workload,
        string nameSuffix, bool includeHttp3 = true)
    {
        var arms = new List<ArmSpec>
        {
            new($"twp-reverse-http1-tls-{nameSuffix}", ProbeMode.ReverseHttp1Tls, null, workload),
            new($"yarp-reverse-http1-tls-{nameSuffix}", ProbeMode.YarpReverseHttp1Tls, null, workload),
            new($"twp-reverse-http2-cleartext-{nameSuffix}", ProbeMode.ReverseHttp2Cleartext, null, workload),
            new($"yarp-reverse-http2-{nameSuffix}", ProbeMode.YarpReverseHttp2, null, workload)
        };
        if (includeHttp3)
        {
            arms.Add(new($"twp-reverse-http3-cleartext-{nameSuffix}", ProbeMode.ReverseHttp3Cleartext, null, workload));
            arms.Add(new($"yarp-reverse-http3-cleartext-{nameSuffix}", ProbeMode.YarpReverseHttp3Cleartext, null,
                workload));
        }

        if (nginxAvailable)
        {
            arms.Insert(1, new($"nginx-reverse-http1-tls-{nameSuffix}", ProbeMode.NginxReverseHttp1Tls, null, workload));
            // After insert: twp H1, nginx H1, yarp H1, twp H2, yarp H2 — put nginx H2 after twp H2.
            arms.Insert(4, new($"nginx-reverse-http2-{nameSuffix}", ProbeMode.NginxReverseHttp2, null, workload));
        }

        return arms;
    }

    private static IReadOnlyList<ArmSpec> ResolveArms(ProbeMode mode, bool nginxAvailable)
    {
        return mode switch
        {
            ProbeMode.ReverseHttp1 => [new("twp-reverse-http1", ProbeMode.ReverseHttp1, null)],
            ProbeMode.BareReverseHttp1 => [new("bare-reverse-http1", ProbeMode.BareReverseHttp1, null)],
            ProbeMode.NginxReverseHttp1 => nginxAvailable
                ? [new("nginx-reverse-http1", ProbeMode.NginxReverseHttp1, null)]
                : [],
            ProbeMode.YarpReverseHttp1 => [new("yarp-reverse-http1", ProbeMode.YarpReverseHttp1, null)],
            ProbeMode.HttpsMitm => [new("twp-https-mitm", ProbeMode.HttpsMitm, null)],
            ProbeMode.ReverseHttp1Tls => [new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null)],
            ProbeMode.BareReverseHttp1Tls => [new("bare-reverse-http1-tls", ProbeMode.BareReverseHttp1Tls, null)],
            ProbeMode.NginxReverseHttp1Tls => nginxAvailable
                ? [new("nginx-reverse-http1-tls", ProbeMode.NginxReverseHttp1Tls, null)]
                : [],
            ProbeMode.YarpReverseHttp1Tls => [new("yarp-reverse-http1-tls", ProbeMode.YarpReverseHttp1Tls, null)],
            ProbeMode.ReverseHttp2 => [new("twp-reverse-http2", ProbeMode.ReverseHttp2, null)],
            ProbeMode.ReverseHttp2Cleartext =>
                [new("twp-reverse-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null)],
            ProbeMode.ReverseHttp2ToH2c =>
                [new("twp-reverse-http2-to-h2c", ProbeMode.ReverseHttp2ToH2c, null)],
            ProbeMode.YarpReverseHttp2ToH2c =>
                [new("yarp-reverse-http2-to-h2c", ProbeMode.YarpReverseHttp2ToH2c, null)],
            ProbeMode.ReverseH2c => [new("twp-reverse-h2c", ProbeMode.ReverseH2c, null)],
            ProbeMode.YarpReverseH2c => [new("yarp-reverse-h2c", ProbeMode.YarpReverseH2c, null)],
            ProbeMode.ReverseH2cToH2c =>
                [new("twp-reverse-h2c-to-h2c", ProbeMode.ReverseH2cToH2c, null)],
            ProbeMode.YarpReverseH2cToH2c =>
                [new("yarp-reverse-h2c-to-h2c", ProbeMode.YarpReverseH2cToH2c, null)],
            ProbeMode.ReverseH2cToH1 =>
                [new("twp-reverse-h2c-to-h1", ProbeMode.ReverseH2cToH1, null)],
            ProbeMode.YarpReverseH2cToH1 =>
                [new("yarp-reverse-h2c-to-h1", ProbeMode.YarpReverseH2cToH1, null)],
            ProbeMode.ReverseH2cToH3 =>
                [new("twp-reverse-h2c-to-h3", ProbeMode.ReverseH2cToH3, null)],
            ProbeMode.YarpReverseH2cToH3 =>
                [new("yarp-reverse-h2c-to-h3", ProbeMode.YarpReverseH2cToH3, null)],
            ProbeMode.NginxReverseHttp2 => nginxAvailable
                ? [new("nginx-reverse-http2", ProbeMode.NginxReverseHttp2, null)]
                : [],
            ProbeMode.YarpReverseHttp2 => [new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null)],
            ProbeMode.ReverseHttp3 => [new("twp-reverse-http3", ProbeMode.ReverseHttp3, null)],
            ProbeMode.ReverseHttp3Cleartext =>
                [new("twp-reverse-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null)],
            ProbeMode.YarpReverseHttp3Cleartext =>
                [new("yarp-reverse-http3-cleartext", ProbeMode.YarpReverseHttp3Cleartext, null)],
            ProbeMode.ReverseHttp11ToHttp2 =>
                [new("twp-reverse-http11-to-http2", ProbeMode.ReverseHttp11ToHttp2, null)],
            ProbeMode.YarpReverseHttp11ToHttp2 =>
                [new("yarp-reverse-http11-to-http2", ProbeMode.YarpReverseHttp11ToHttp2, null)],
            ProbeMode.ReverseHttp1ToHttp3 =>
                [new("twp-reverse-http1-to-http3", ProbeMode.ReverseHttp1ToHttp3, null)],
            ProbeMode.YarpReverseHttp1ToHttp3 =>
                [new("yarp-reverse-http1-to-http3", ProbeMode.YarpReverseHttp1ToHttp3, null)],
            ProbeMode.ReverseHttp2ToHttp3 =>
                [new("twp-reverse-http2-to-http3", ProbeMode.ReverseHttp2ToHttp3, null)],
            ProbeMode.YarpReverseHttp2ToHttp3 =>
                [new("yarp-reverse-http2-to-http3", ProbeMode.YarpReverseHttp2ToHttp3, null)],
            ProbeMode.ReverseHttp3ToHttp2 =>
                [new("twp-reverse-http3-to-http2", ProbeMode.ReverseHttp3ToHttp2, null)],
            ProbeMode.YarpReverseHttp3ToHttp2 =>
                [new("yarp-reverse-http3-to-http2", ProbeMode.YarpReverseHttp3ToHttp2, null)],
            ProbeMode.YarpReverseHttp3ToHttp3 =>
                [new("yarp-reverse-http3-to-http3", ProbeMode.YarpReverseHttp3ToHttp3, null)],
            ProbeMode.ExplicitHttp1Multi =>
                [new("twp-explicit-http1-multi", ProbeMode.ExplicitHttp1Multi, null)],
            ProbeMode.ExplicitHttp2Multi =>
                [new("twp-explicit-http2-multi", ProbeMode.ExplicitHttp2Multi, null)],
            ProbeMode.Compare => nginxAvailable
                ?
                [
                    new("twp-reverse-http1", ProbeMode.ReverseHttp1, null),
                    new("nginx-reverse-http1", ProbeMode.NginxReverseHttp1, null),
                    new("yarp-reverse-http1", ProbeMode.YarpReverseHttp1, null),
                    new("twp-https-mitm", ProbeMode.HttpsMitm, null)
                ]
                :
                [
                    new("twp-reverse-http1", ProbeMode.ReverseHttp1, null),
                    new("yarp-reverse-http1", ProbeMode.YarpReverseHttp1, null),
                    new("twp-https-mitm", ProbeMode.HttpsMitm, null)
                ],
            ProbeMode.CompareHttp2 => nginxAvailable
                ?
                [
                    new("twp-reverse-http2", ProbeMode.ReverseHttp2, null),
                    new("nginx-reverse-http2", ProbeMode.NginxReverseHttp2, null),
                    new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null),
                    new("twp-reverse-http3", ProbeMode.ReverseHttp3, null)
                ]
                :
                [
                    new("twp-reverse-http2", ProbeMode.ReverseHttp2, null),
                    new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null),
                    new("twp-reverse-http3", ProbeMode.ReverseHttp3, null)
                ],
            ProbeMode.CompareTls => nginxAvailable
                ?
                [
                    new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null),
                    new("nginx-reverse-http1-tls", ProbeMode.NginxReverseHttp1Tls, null),
                    new("yarp-reverse-http1-tls", ProbeMode.YarpReverseHttp1Tls, null),
                    new("twp-reverse-http2", ProbeMode.ReverseHttp2, null),
                    new("nginx-reverse-http2", ProbeMode.NginxReverseHttp2, null),
                    new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null),
                    new("twp-reverse-http3", ProbeMode.ReverseHttp3, null)
                ]
                :
                [
                    new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null),
                    new("yarp-reverse-http1-tls", ProbeMode.YarpReverseHttp1Tls, null),
                    new("twp-reverse-http2", ProbeMode.ReverseHttp2, null),
                    new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null),
                    new("twp-reverse-http3", ProbeMode.ReverseHttp3, null)
                ],
            ProbeMode.CompareTerminate => nginxAvailable
                ?
                [
                    new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null),
                    new("nginx-reverse-http1-tls", ProbeMode.NginxReverseHttp1Tls, null),
                    new("yarp-reverse-http1-tls", ProbeMode.YarpReverseHttp1Tls, null),
                    new("twp-reverse-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null),
                    new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null),
                    new("twp-reverse-h2c-to-h1", ProbeMode.ReverseH2cToH1, null),
                    new("yarp-reverse-h2c-to-h1", ProbeMode.YarpReverseH2cToH1, null),
                    new("nginx-reverse-http2", ProbeMode.NginxReverseHttp2, null),
                    new("twp-reverse-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null),
                    new("yarp-reverse-http3-cleartext", ProbeMode.YarpReverseHttp3Cleartext, null)
                ]
                :
                [
                    new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null),
                    new("yarp-reverse-http1-tls", ProbeMode.YarpReverseHttp1Tls, null),
                    new("twp-reverse-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null),
                    new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null),
                    new("twp-reverse-h2c-to-h1", ProbeMode.ReverseH2cToH1, null),
                    new("yarp-reverse-h2c-to-h1", ProbeMode.YarpReverseH2cToH1, null),
                    new("twp-reverse-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null),
                    new("yarp-reverse-http3-cleartext", ProbeMode.YarpReverseHttp3Cleartext, null)
                ],
            ProbeMode.CompareSame => nginxAvailable
                ?
                [
                    new("twp-reverse-http1", ProbeMode.ReverseHttp1, null),
                    new("nginx-reverse-http1", ProbeMode.NginxReverseHttp1, null),
                    new("yarp-reverse-http1", ProbeMode.YarpReverseHttp1, null),
                    new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null),
                    new("nginx-reverse-http1-tls", ProbeMode.NginxReverseHttp1Tls, null),
                    new("yarp-reverse-http1-tls", ProbeMode.YarpReverseHttp1Tls, null),
                    new("twp-https-mitm", ProbeMode.HttpsMitm, null),
                    new("twp-reverse-http2", ProbeMode.ReverseHttp2, null),
                    new("twp-reverse-h2c-to-h2c", ProbeMode.ReverseH2cToH2c, null),
                    new("yarp-reverse-h2c-to-h2c", ProbeMode.YarpReverseH2cToH2c, null),
                    new("twp-reverse-h2c", ProbeMode.ReverseH2c, null),
                    new("yarp-reverse-h2c", ProbeMode.YarpReverseH2c, null),
                    new("nginx-reverse-http2", ProbeMode.NginxReverseHttp2, null),
                    new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null),
                    new("twp-reverse-http3", ProbeMode.ReverseHttp3, null)
                ]
                :
                [
                    new("twp-reverse-http1", ProbeMode.ReverseHttp1, null),
                    new("yarp-reverse-http1", ProbeMode.YarpReverseHttp1, null),
                    new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null),
                    new("yarp-reverse-http1-tls", ProbeMode.YarpReverseHttp1Tls, null),
                    new("twp-https-mitm", ProbeMode.HttpsMitm, null),
                    new("twp-reverse-http2", ProbeMode.ReverseHttp2, null),
                    new("twp-reverse-h2c-to-h2c", ProbeMode.ReverseH2cToH2c, null),
                    new("yarp-reverse-h2c-to-h2c", ProbeMode.YarpReverseH2cToH2c, null),
                    new("twp-reverse-h2c", ProbeMode.ReverseH2c, null),
                    new("yarp-reverse-h2c", ProbeMode.YarpReverseH2c, null),
                    new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null),
                    new("twp-reverse-http3", ProbeMode.ReverseHttp3, null)
                ],
            ProbeMode.CompareBridges =>
            [
                new("twp-reverse-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null),
                new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null),
                new("twp-reverse-http2-to-h2c", ProbeMode.ReverseHttp2ToH2c, null),
                new("yarp-reverse-http2-to-h2c", ProbeMode.YarpReverseHttp2ToH2c, null),
                new("twp-reverse-h2c-to-h1", ProbeMode.ReverseH2cToH1, null),
                new("yarp-reverse-h2c-to-h1", ProbeMode.YarpReverseH2cToH1, null),
                new("twp-reverse-h2c-to-h2c", ProbeMode.ReverseH2cToH2c, null),
                new("yarp-reverse-h2c-to-h2c", ProbeMode.YarpReverseH2cToH2c, null),
                new("twp-reverse-h2c-to-h3", ProbeMode.ReverseH2cToH3, null),
                new("yarp-reverse-h2c-to-h3", ProbeMode.YarpReverseH2cToH3, null),
                new("twp-reverse-http11-to-http2", ProbeMode.ReverseHttp11ToHttp2, null),
                new("yarp-reverse-http11-to-http2", ProbeMode.YarpReverseHttp11ToHttp2, null),
                new("twp-reverse-http1-to-http3", ProbeMode.ReverseHttp1ToHttp3, null),
                new("yarp-reverse-http1-to-http3", ProbeMode.YarpReverseHttp1ToHttp3, null),
                new("twp-reverse-http2-to-http3", ProbeMode.ReverseHttp2ToHttp3, null),
                new("yarp-reverse-http2-to-http3", ProbeMode.YarpReverseHttp2ToHttp3, null),
                new("twp-reverse-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null),
                new("yarp-reverse-http3-cleartext", ProbeMode.YarpReverseHttp3Cleartext, null),
                new("twp-reverse-http3-to-http2", ProbeMode.ReverseHttp3ToHttp2, null),
                new("yarp-reverse-http3-to-http2", ProbeMode.YarpReverseHttp3ToHttp2, null),
                new("yarp-reverse-http3-to-http3", ProbeMode.YarpReverseHttp3ToHttp3, null)
            ],
            ProbeMode.CompareMitm =>
            [
                new("twp-https-mitm", ProbeMode.HttpsMitm, null),
                new("twp-reverse-http2", ProbeMode.ReverseHttp2, null),
                new("twp-mitm-http2-to-http1", ProbeMode.MitmHttp2ToHttp1, null),
                new("twp-reverse-http3", ProbeMode.ReverseHttp3, null),
                new("twp-mitm-http3-to-http1", ProbeMode.MitmHttp3ToHttp1, null),
                new("twp-reverse-http11-to-http2", ProbeMode.ReverseHttp11ToHttp2, null),
                new("twp-reverse-http1-to-http3", ProbeMode.ReverseHttp1ToHttp3, null),
                new("twp-reverse-http2-to-http3", ProbeMode.ReverseHttp2ToHttp3, null),
                new("twp-reverse-http3-to-http2", ProbeMode.ReverseHttp3ToHttp2, null)
            ],
            ProbeMode.MitmHttp2ToHttp1 =>
                [new("twp-mitm-http2-to-http1", ProbeMode.MitmHttp2ToHttp1, null)],
            ProbeMode.MitmHttp3ToHttp1 =>
                [new("twp-mitm-http3-to-http1", ProbeMode.MitmHttp3ToHttp1, null)],
            ProbeMode.CompareCeiling => nginxAvailable
                ?
                [
                    new("twp-reverse-http1", ProbeMode.ReverseHttp1, null),
                    new("bare-reverse-http1", ProbeMode.BareReverseHttp1, null),
                    new("nginx-reverse-http1", ProbeMode.NginxReverseHttp1, null),
                    new("yarp-reverse-http1", ProbeMode.YarpReverseHttp1, null),
                    new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null),
                    new("bare-reverse-http1-tls", ProbeMode.BareReverseHttp1Tls, null),
                    new("nginx-reverse-http1-tls", ProbeMode.NginxReverseHttp1Tls, null),
                    new("yarp-reverse-http1-tls", ProbeMode.YarpReverseHttp1Tls, null),
                    new("twp-reverse-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null),
                    new("nginx-reverse-http2", ProbeMode.NginxReverseHttp2, null),
                    new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null)
                ]
                :
                [
                    new("twp-reverse-http1", ProbeMode.ReverseHttp1, null),
                    new("bare-reverse-http1", ProbeMode.BareReverseHttp1, null),
                    new("yarp-reverse-http1", ProbeMode.YarpReverseHttp1, null),
                    new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null),
                    new("bare-reverse-http1-tls", ProbeMode.BareReverseHttp1Tls, null),
                    new("yarp-reverse-http1-tls", ProbeMode.YarpReverseHttp1Tls, null),
                    new("twp-reverse-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null),
                    new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null)
                ],
            ProbeMode.CompareBodies =>
            [
                ..HeavierReverseArms(nginxAvailable, WorkloadOptions.ForBodyGet(64 * 1024), "body64k"),
                ..HeavierReverseArms(nginxAvailable, WorkloadOptions.ForBodyGet(256 * 1024), "body256k")
            ],
            ProbeMode.ComparePost =>
                HeavierReverseArms(nginxAvailable, WorkloadOptions.ForPost(64 * 1024, 64 * 1024), "post64k"),
            ProbeMode.CompareLossy =>
                // Userspace UDP shim + MsQuic under multi-connection load hangs; H1/H2 TCP tell the HOL story.
                HeavierReverseArms(nginxAvailable, WorkloadOptions.ForLossy(64 * 1024, 5, 1.0), "lossy",
                    includeHttp3: false),
            ProbeMode.CompareTlsCost => BuildTlsCostArms(nginxAvailable),
            ProbeMode.ExplicitPoolSweep =>
            [
                new("twp-explicit-http1-multi-c4", ProbeMode.ExplicitHttp1Multi, 4),
                new("twp-explicit-http1-multi-c32", ProbeMode.ExplicitHttp1Multi, 32),
                new("twp-explicit-http1-multi-c128", ProbeMode.ExplicitHttp1Multi, 128)
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    private static IReadOnlyList<ArmSpec> BuildTlsCostArms(bool nginxAvailable)
    {
        var tinyKa = WorkloadOptions.ForTlsKeepAlive(WorkloadOptions.TinyJsonBytes);
        var tinyNc = WorkloadOptions.ForTlsNewConnection();
        var largeKa = WorkloadOptions.ForTlsKeepAlive(256 * 1024);
        var arms = new List<ArmSpec>
        {
            new("twp-reverse-http1-tls-ka-tiny", ProbeMode.ReverseHttp1Tls, null, tinyKa),
            new("yarp-reverse-http1-tls-ka-tiny", ProbeMode.YarpReverseHttp1Tls, null, tinyKa),
            new("twp-reverse-http1-tls-nc-tiny", ProbeMode.ReverseHttp1Tls, null, tinyNc),
            new("yarp-reverse-http1-tls-nc-tiny", ProbeMode.YarpReverseHttp1Tls, null, tinyNc),
            new("twp-reverse-http1-tls-ka-256k", ProbeMode.ReverseHttp1Tls, null, largeKa),
            new("yarp-reverse-http1-tls-ka-256k", ProbeMode.YarpReverseHttp1Tls, null, largeKa)
        };
        if (nginxAvailable)
        {
            arms.Insert(1, new("nginx-reverse-http1-tls-ka-tiny", ProbeMode.NginxReverseHttp1Tls, null, tinyKa));
            arms.Insert(4, new("nginx-reverse-http1-tls-nc-tiny", ProbeMode.NginxReverseHttp1Tls, null, tinyNc));
            arms.Insert(7, new("nginx-reverse-http1-tls-ka-256k", ProbeMode.NginxReverseHttp1Tls, null, largeKa));
        }

        return arms;
    }

    private static async Task<double> RunArmAsync(ArmSpec arm, RampOptions options, StreamWriter csv,
        string? nginxVersionHint, CancellationToken cancellationToken)
    {
        var workload = arm.Workload ?? options.Workload;
        var maxCached = arm.MaxCachedConnections ?? options.MaxCachedConnections;
        await using var stack = await ChildProcessStack.StartAsync(arm.Mode, options.NginxPath, maxCached,
            cancellationToken, workload);
        var nginxVersion = stack.NginxVersion ?? nginxVersionHint;

        var p99Slo = workload.ResolveP99SloMs(options.Http1P99MsSlo, options.Http2P99MsSlo,
            options.Http3P99MsSlo, options.HttpsMitmP99MsSlo, arm.Mode);

        LossyTcpLink? tcpLink = null;
        LossyUdpLink? udpLink = null;
        Uri targetUri = stack.TargetUri;
        IReadOnlyList<Uri>? targetUris = stack.TargetUris.Count > 1 ? stack.TargetUris : null;
        int? quicPort = stack.QuicPort;

        try
        {
            if (workload.IsLossy)
            {
                var useQuicLink = string.Equals(stack.LoadGenerator, "quic-http3", StringComparison.OrdinalIgnoreCase)
                                  && stack.QuicPort is > 0;
                if (useQuicLink)
                {
                    udpLink = LossyUdpLink.Start(stack.QuicPort!.Value, workload.DelayMs, workload.LossPercent);
                    quicPort = udpLink.Port;
                    ProbeLog.Info(
                        $"  lossy-udp port={udpLink.Port} -> quic={stack.QuicPort} delay={workload.DelayMs}ms loss={workload.LossPercent}%");
                }
                else
                {
                    tcpLink = LossyTcpLink.Start(stack.TargetUri, workload.DelayMs, workload.LossPercent);
                    var scheme = stack.TargetUri.Scheme;
                    targetUri = new Uri(tcpLink.ListenUrlForScheme(scheme));
                    targetUris = null;
                    ProbeLog.Info(
                        $"  lossy-tcp port={tcpLink.Port} -> {stack.TargetUrl} delay={workload.DelayMs}ms loss={workload.LossPercent}%");
                }
            }

            LoadResult? lastGood = null;
            LoadResult? peak = null;
            var lastGoodConcurrency = 0;

            var useQuic = string.Equals(stack.LoadGenerator, "quic-http3", StringComparison.OrdinalIgnoreCase)
                          && quicPort is > 0;
            var loadOptions = new LoadRequestOptions
            {
                Target = targetUri,
                Targets = targetUris,
                ExplicitProxyUrl = stack.ExplicitProxyUrl,
                HttpVersion = stack.RequestHttpVersion,
                VersionPolicy = stack.VersionPolicy,
                Workload = workload
            };

            ProbeLog.Info(
                $"  target={targetUri} workload={workload.Suffix} proxy={(stack.ExplicitProxyUrl ?? "(direct-to-listen)")} http={stack.RequestHttpVersion} generator={(useQuic ? "quic-http3" : "dotnet-httpclient")} maxCached={(maxCached?.ToString() ?? "default")}");
            if (stack.IsCombinedServe)
            {
                ProbeLog.Info(
                    $"  attach: combined --serve pid={stack.ProxyProcessId} (origin+proxy same process; traces mix Kestrel origin with TWP)");
            }
            else
            {
                ProbeLog.Info(
                    $"  attach: split origin pid={stack.OriginProcessId} proxy pid={stack.ProxyProcessId}");
            }

            foreach (var concurrency in options.ConcurrencySteps)
            {
                ProbeLog.Info($"  warmup c={concurrency} for {options.Warmup.TotalSeconds:F0}s...");
                if (useQuic)
                {
                    var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, quicPort!.Value);
                    var authority = stack.OriginQuicPort is { } op
                        ? $"localhost:{op}"
                        : "localhost";
                    await QuicHttp3LoadGenerator.WarmupAsync(ep, "localhost", authority,
                        concurrency, options.Warmup, cancellationToken, workload);
                }
                else
                {
                    await EmbeddedLoadGenerator.WarmupAsync(loadOptions, concurrency, options.Warmup, cancellationToken);
                }

                ProbeLog.Info($"  measure c={concurrency} for {options.StepDuration.TotalSeconds:F0}s...");
                LoadResult result;
                if (useQuic)
                {
                    var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, quicPort!.Value);
                    var authority = stack.OriginQuicPort is { } op
                        ? $"localhost:{op}"
                        : "localhost";
                    result = await QuicHttp3LoadGenerator.RunAsync(ep, "localhost", authority,
                        concurrency, options.StepDuration, cancellationToken, workload);
                }
                else
                {
                    result = await EmbeddedLoadGenerator.RunAsync(loadOptions, concurrency, options.StepDuration,
                        cancellationToken);
                }

                var meetsSlo = result.ErrorRatePercent < options.MaxErrorRatePercent && result.P99Ms <= p99Slo;
                await CsvWriter.WriteRowAsync(csv, arm.Name, result, meetsSlo, nginxVersion, maxCached, workload,
                    stack.YarpVersion);
                await csv.FlushAsync(cancellationToken);

                ProbeLog.Info(string.Create(CultureInfo.InvariantCulture,
                    $"    rps={result.Rps:F0} err%={result.ErrorRatePercent:F3} p50={result.P50Ms:F1}ms p99={result.P99Ms:F1}ms max={result.MaxMs:F1}ms ver={result.NegotiatedVersionHint} slo={(meetsSlo ? "PASS" : "FAIL")}"));

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

            return peak?.Rps ?? 0;
        }
        finally
        {
            if (tcpLink != null)
                await tcpLink.DisposeAsync();
            if (udpLink != null)
                await udpLink.DisposeAsync();
        }
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
