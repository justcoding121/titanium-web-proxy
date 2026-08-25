using System.Globalization;

namespace Titanium.Web.Proxy.RpsLoadProbe;

internal enum ProbeMode
{
    ReverseHttp1,
    BareReverseHttp1,
    NginxReverseHttp1,
    YarpReverseHttp1,
    HttpsMitm,
    /// <summary>Explicit intercepting proxy: cleartext client → cleartext HTTP/1 origin.</summary>
    HttpMitm,
    ReverseHttp1Mitm,
    ReverseHttp1Tls,
    /// <summary>Client HTTP/1 plain → HTTPS HTTP/1 origin (outbound TLS only).</summary>
    ReverseHttp1ToHttps,
    BareReverseHttp1Tls,
    NginxReverseHttp1Tls,
    YarpReverseHttp1Tls,
    YarpReverseHttp1ToHttps,
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
    /// <summary>Native reverse: client QUIC/h3 → cleartext HTTP/1. Requires nginx <c>http_v3_module</c>.</summary>
    NginxReverseHttp3Cleartext,
    /// <summary>Managed reverse peer client TLS+h2 → cleartext HTTP/1 origin (native reverse peer parity).</summary>
    YarpReverseHttp2,
    YarpReverseHttp2ToH2c,
    YarpReverseH2c,
    YarpReverseH2cToH2c,
    YarpReverseH2cToH1,
    YarpReverseH2cToH3,
    ReverseHttp3,
    /// <summary>TWP QUIC/h3 terminate → ForwardCleartext → cleartext HTTP/1 origin.</summary>
    ReverseHttp3Cleartext,
    /// <summary>Managed reverse peer HTTP/3 terminate → cleartext HTTP/1. Client uses quic-http3 (matched with TWP).</summary>
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
    /// <summary>Client H3 → H3→H2 bridge → cleartext HTTP/2 (h2c).</summary>
    ReverseHttp3ToH2c,
    YarpReverseHttp3ToH2c,
    /// <summary>Client H1 TLS → H1→H2 bridge → cleartext HTTP/2 (h2c).</summary>
    ReverseHttp1ToH2c,
    YarpReverseHttp1ToH2c,
    /// <summary>Client H1 plain → H1→H2 bridge → cleartext HTTP/2 (h2c).</summary>
    ReverseHttp1PlainToH2c,
    YarpReverseHttp1PlainToH2c,
    /// <summary>Client H1 plain → H1→H2 bridge → origin HTTPS h2.</summary>
    ReverseHttp1PlainToHttp2,
    YarpReverseHttp1PlainToHttp2,
    /// <summary>Client H1 plain → H1→H3 bridge → origin QUIC/h3.</summary>
    ReverseHttp1PlainToHttp3,
    YarpReverseHttp1PlainToHttp3,
    /// <summary>Client prior-knowledge h2c → H2→H1 bridge → origin HTTPS HTTP/1.</summary>
    ReverseH2cToHttps,
    YarpReverseH2cToHttps,
    /// <summary>Managed reverse peer H1 TLS → HTTPS HTTP/1 (dual-crypto peer of reverse-http1-mitm).</summary>
    YarpReverseHttp1TlsToHttps,
    /// <summary>Managed reverse peer H2 TLS → HTTPS HTTP/1 (dual-crypto peer of mitm-http2-to-http1).</summary>
    YarpReverseHttp2ToHttpsHttp1,
    /// <summary>Managed reverse peer H3 → HTTPS HTTP/1 (dual-crypto peer of mitm-http3-to-http1).</summary>
    YarpReverseHttp3ToHttpsHttp1,
    /// <summary>Managed reverse peer client H3 → origin HTTP/3.</summary>
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
    /// <summary>Fair TLS-terminate compare: H1 TLS, H2→H1 cleartext, H3→H1 cleartext vs native reverse peer where available.</summary>
    CompareTerminate,
    /// <summary>
    /// Same-protocol matrix: H1 cleartext, H1 TLS terminate, H2 MITM, H3 MITM (+ native reverse peer where comparable).
    /// </summary>
    CompareSame,
    /// <summary>All implemented cross-version bridges under load (no native reverse peer).</summary>
    CompareBridges,
    /// <summary>H3→H1 cleartext only: TWP + YARP (+ nginx when http_v3_module).</summary>
    CompareHttp3Cleartext,
    /// <summary>
    /// MITM / inspectable matrix: full 5×5 Client×Origin wire pairs (TWP) plus explicit https-mitm CONNECT.
    /// nginx/YARP cannot MITM — TWP-only for the MITM extras; reverse cells use TWP reverse arms.
    /// </summary>
    CompareMitm,
    /// <summary>
    /// Full 5×5 Client×Origin reverse matrix: all TWP + YARP pairs for
    /// {H1·plain, H1·TLS, H2·plain, H2·TLS, H3·QUIC}².
    /// </summary>
    CompareMatrix,
    /// <summary>TWP vs bare C# reverse vs native reverse peer on the three Linux native-winning reverse rows.</summary>
    CompareCeiling,
    /// <summary>Heavier reverse GET bodies (64 KiB / 256 KiB) vs native reverse peer where possible.</summary>
    CompareBodies,
    /// <summary>POST 64 KiB request+response reverse vs native reverse peer where possible.</summary>
    ComparePost,
    /// <summary>64 KiB GET under userspace delay/loss (H2/H3 conditions) vs native reverse peer where possible.</summary>
    CompareLossy,
    /// <summary>H1 TLS terminate cost: keep-alive tiny, new-connection tiny, keep-alive 256 KiB.</summary>
    CompareTlsCost,
    /// <summary>Architecture-sensitive reverse: slow consumer, early response, H2 duplex, WebSocket echo.</summary>
    CompareArch,
    /// <summary>
    /// Saturation control: origin-direct (+ optional bombardier) and H1 plain reverse peers in one session.
    /// </summary>
    CompareSaturation,
    /// <summary>Load generator → origin child only (no proxy); calibration ceiling for reverse peers.</summary>
    OriginDirect,
    /// <summary>Managed reverse peer H2 TLS → HTTPS HTTP/2 origin.</summary>
    YarpReverseHttp2ToHttps,
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
        if (options.Mode is ProbeMode.CompareSaturation or ProbeMode.OriginDirect)
        {
            ProbeLog.Info(
                "Process split: origin-direct arms are two OS processes (load generator + origin child); proxy arms remain three.");
        }
        else
        {
            ProbeLog.Info("Process split: every arm is three OS processes (load generator + origin child + proxy child).");
        }

        if (options.Repeats > 1)
            ProbeLog.Info($"Repeats={options.Repeats} (median peak RPS per arm — dampens GHA runner noise).");
        ProbeLog.Info(string.Empty);

        await using var csv = new StreamWriter(csvPath);
        await CsvWriter.WriteHeaderAsync(csv);

        var nginxHttp3 = nginxExe != null && NginxHost.SupportsHttp3Module(NginxHost.ReadConfigureArguments(nginxExe));
        if (nginxExe != null && !nginxHttp3)
            ProbeLog.Info("nginx has no http_v3_module — skipping HTTP/3 native reverse arms (install nginx.org mainline on Linux).");

        var bombardierAvailable = BombardierLoadGenerator.IsAvailable();
        var arms = ResolveArms(options.Mode, nginxExe != null, nginxHttp3, bombardierAvailable).ToList();
        if (!System.Net.Quic.QuicListener.IsSupported)
        {
            var removed = arms.RemoveAll(a =>
                a.Mode is ProbeMode.ReverseHttp3 or ProbeMode.ReverseHttp3Cleartext
                    or ProbeMode.YarpReverseHttp3Cleartext or ProbeMode.NginxReverseHttp3Cleartext
                    or ProbeMode.ReverseHttp1ToHttp3 or ProbeMode.YarpReverseHttp1ToHttp3
                    or ProbeMode.ReverseHttp1PlainToHttp3 or ProbeMode.YarpReverseHttp1PlainToHttp3
                    or ProbeMode.ReverseHttp2ToHttp3 or ProbeMode.YarpReverseHttp2ToHttp3
                    or ProbeMode.ReverseHttp3ToHttp2 or ProbeMode.YarpReverseHttp3ToHttp2
                    or ProbeMode.ReverseHttp3ToH2c or ProbeMode.YarpReverseHttp3ToH2c
                    or ProbeMode.YarpReverseHttp3ToHttp3
                    or ProbeMode.YarpReverseHttp3ToHttpsHttp1
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
                or ProbeMode.NginxReverseHttp2 or ProbeMode.NginxReverseHttp3Cleartext
                or ProbeMode.Compare or ProbeMode.CompareHttp2
                or ProbeMode.CompareTls or ProbeMode.CompareTerminate or ProbeMode.CompareSame
                or ProbeMode.CompareBridges or ProbeMode.CompareHttp3Cleartext
                or ProbeMode.CompareBodies or ProbeMode.ComparePost or ProbeMode.CompareLossy
                or ProbeMode.CompareTlsCost or ProbeMode.CompareArch or ProbeMode.CompareSaturation)
            && nginxExe == null)
        {
            ProbeLog.Info(NginxHost.NginxMissingMessage());
            ProbeLog.Info(string.Empty);
        }

        var peakByArm = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var rssByArm = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var cpuByArm = new Dictionary<string, List<double>>(StringComparer.Ordinal);
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

                list.Add(peak.PeakRps);
                if (peak.RssPeakBytes is { } rss)
                {
                    if (!rssByArm.TryGetValue(arm.Name, out var rssList))
                    {
                        rssList = [];
                        rssByArm[arm.Name] = rssList;
                    }

                    rssList.Add(rss);
                }

                if (peak.CpuAvgPct is { } cpu)
                {
                    if (!cpuByArm.TryGetValue(arm.Name, out var cpuList))
                    {
                        cpuList = [];
                        cpuByArm[arm.Name] = cpuList;
                    }

                    cpuList.Add(cpu);
                }

                ProbeLog.Info(string.Empty);
            }
        }

        if (options.Mode is ProbeMode.CompareSaturation)
            WriteSaturationSummary(peakByArm, rssByArm, cpuByArm);
        else
            WriteMedianSummary(peakByArm, rssByArm, cpuByArm);

        await csv.FlushAsync(cancellationToken);
        ProbeLog.Info($"CSV: {Path.GetFullPath(csvPath)}");
        return 0;
    }

    private static void WriteMedianSummary(Dictionary<string, List<double>> peakByArm,
        Dictionary<string, List<long>> rssByArm, Dictionary<string, List<double>> cpuByArm)
    {
        ProbeLog.Info("=== median peaks across repeats ===");
        double? twpH1Tls = null, nginxH1Tls = null, yarpH1Tls = null;
        foreach (var (name, peaks) in peakByArm.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var median = Median(peaks);
            var line = string.Create(CultureInfo.InvariantCulture,
                $"  {name}: median_peak_rps={median:F1} (n={peaks.Count})");
            if (rssByArm.TryGetValue(name, out var rssList) && rssList.Count > 0)
            {
                var medianRss = MedianLong(rssList);
                line += string.Create(CultureInfo.InvariantCulture,
                    $" median_memory_rss_bytes={medianRss}");
            }

            if (cpuByArm.TryGetValue(name, out var cpuList) && cpuList.Count > 0)
            {
                var medianCpu = Median(cpuList);
                line += string.Create(CultureInfo.InvariantCulture,
                    $" median_cpu_avg_pct={medianCpu:F1}");
            }

            ProbeLog.Info(line);
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

    private static readonly HashSet<string> SaturationBlockA = new(StringComparer.Ordinal)
    {
        "origin-direct", "origin-direct-bombardier", "bare-reverse-http1", "nginx-reverse-http1",
        "yarp-reverse-http1", "twp-reverse-http1"
    };

    private static readonly HashSet<string> SaturationBlockB = new(StringComparer.Ordinal)
    {
        "nginx-reverse-http2", "yarp-reverse-http2", "twp-reverse-http2-cleartext"
    };

    private static readonly HashSet<string> SaturationBlockC = new(StringComparer.Ordinal)
    {
        "nginx-reverse-http3-cleartext", "yarp-reverse-http3-cleartext", "twp-reverse-http3-cleartext"
    };

    private static void WriteSaturationSummary(Dictionary<string, List<double>> peakByArm,
        Dictionary<string, List<long>> rssByArm, Dictionary<string, List<double>> cpuByArm)
    {
        ProbeLog.Info("=== saturation control (median peaks) ===");

        WriteSaturationBlockA(peakByArm, rssByArm, cpuByArm);
        WriteSaturationPeerBlock("Block B -- H2 TLS->H1", SaturationBlockB, "yarp-reverse-http2",
            "nginx-reverse-http2", peakByArm, rssByArm, cpuByArm);
        WriteSaturationPeerBlock("Block C -- H3->H1", SaturationBlockC, "yarp-reverse-http3-cleartext",
            "nginx-reverse-http3-cleartext", peakByArm, rssByArm, cpuByArm);
    }

    private static void WriteSaturationBlockA(Dictionary<string, List<double>> peakByArm,
        Dictionary<string, List<long>> rssByArm, Dictionary<string, List<double>> cpuByArm)
    {
        ProbeLog.Info("  --- Block A -- H1 plain ---");
        double? originDirect = null, originBombardier = null;
        var medians = new List<(string Name, double Median)>();
        foreach (var name in SaturationBlockA.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!peakByArm.TryGetValue(name, out var peaks) || peaks.Count == 0)
                continue;
            var median = Median(peaks);
            medians.Add((name, median));
            ProbeLog.Info(FormatSaturationArmLine(name, median, peaks.Count, rssByArm, cpuByArm));
            if (name.Equals("origin-direct", StringComparison.Ordinal))
                originDirect = median;
            if (name.Equals("origin-direct-bombardier", StringComparison.Ordinal))
                originBombardier = median;
        }

        foreach (var (name, median) in medians)
        {
            if (name.Equals("origin-direct", StringComparison.Ordinal) ||
                name.Equals("origin-direct-bombardier", StringComparison.Ordinal))
                continue;

            if (originDirect is > 0)
            {
                var pct = median * 100.0 / originDirect.Value;
                ProbeLog.Info(string.Create(CultureInfo.InvariantCulture,
                    $"  {name}: {pct:F1}% of origin-direct"));
            }

            if (originBombardier is > 0)
            {
                var pct = median * 100.0 / originBombardier.Value;
                ProbeLog.Info(string.Create(CultureInfo.InvariantCulture,
                    $"  {name}: {pct:F1}% of origin-direct-bombardier"));
            }
        }
    }

    private static void WriteSaturationPeerBlock(string title, HashSet<string> blockArms, string yarpArm,
        string nginxArm, Dictionary<string, List<double>> peakByArm, Dictionary<string, List<long>> rssByArm,
        Dictionary<string, List<double>> cpuByArm)
    {
        var present = blockArms
            .Where(n => peakByArm.TryGetValue(n, out var peaks) && peaks.Count > 0)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (present.Count == 0)
            return;

        ProbeLog.Info($"  --- {title} ---");
        double? yarpPeak = null, nginxPeak = null;
        var medians = new List<(string Name, double Median)>();
        foreach (var name in present)
        {
            var peaks = peakByArm[name];
            var median = Median(peaks);
            medians.Add((name, median));
            ProbeLog.Info(FormatSaturationArmLine(name, median, peaks.Count, rssByArm, cpuByArm));
            if (name.Equals(yarpArm, StringComparison.Ordinal))
                yarpPeak = median;
            if (name.Equals(nginxArm, StringComparison.Ordinal))
                nginxPeak = median;
        }

        foreach (var (name, median) in medians)
        {
            if (yarpPeak is > 0 && !name.Equals(yarpArm, StringComparison.Ordinal))
            {
                var ratio = median / yarpPeak.Value;
                ProbeLog.Info(string.Create(CultureInfo.InvariantCulture,
                    $"  {name}: {ratio:F3}× YARP"));
            }

            if (nginxPeak is > 0 && !name.Equals(nginxArm, StringComparison.Ordinal))
            {
                var ratio = median / nginxPeak.Value;
                ProbeLog.Info(string.Create(CultureInfo.InvariantCulture,
                    $"  {name}: {ratio:F3}× nginx"));
            }
        }
    }

    private static string FormatSaturationArmLine(string name, double medianPeak, int n,
        Dictionary<string, List<long>> rssByArm, Dictionary<string, List<double>> cpuByArm)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"  {name}: median_peak_rps={medianPeak:F1} (n={n})");
        if (rssByArm.TryGetValue(name, out var rssList) && rssList.Count > 0)
        {
            var medianRss = MedianLong(rssList);
            line += string.Create(CultureInfo.InvariantCulture, $" median_memory_rss_bytes={medianRss}");
        }

        if (cpuByArm.TryGetValue(name, out var cpuList) && cpuList.Count > 0)
        {
            var medianCpu = Median(cpuList);
            line += string.Create(CultureInfo.InvariantCulture, $" median_cpu_avg_pct={medianCpu:F1}");
        }

        return line;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }

    private static long MedianLong(List<long> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2
            : sorted[mid];
    }

    private sealed record ArmSpec(string Name, ProbeMode Mode, int? MaxCachedConnections,
        WorkloadOptions? Workload = null, string? PreferredGenerator = null);

    private static IReadOnlyList<ArmSpec> HeavierReverseArms(bool nginxAvailable, bool nginxHttp3Available,
        WorkloadOptions workload, string nameSuffix, bool includeHttp3 = true)
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
            if (nginxHttp3Available)
                arms.Add(new($"nginx-reverse-http3-cleartext-{nameSuffix}", ProbeMode.NginxReverseHttp3Cleartext, null,
                    workload));
            arms.Add(new($"yarp-reverse-http3-cleartext-{nameSuffix}", ProbeMode.YarpReverseHttp3Cleartext, null,
                workload));
        }

        if (nginxAvailable)
        {
            arms.Insert(1, new($"nginx-reverse-http1-tls-{nameSuffix}", ProbeMode.NginxReverseHttp1Tls, null, workload));
            // After insert: twp H1, native H1, managed H1, twp H2, managed H2 — put native H2 after twp H2.
            arms.Insert(4, new($"nginx-reverse-http2-{nameSuffix}", ProbeMode.NginxReverseHttp2, null, workload));
        }

        return arms;
    }

    private static IReadOnlyList<ArmSpec> ResolveArms(ProbeMode mode, bool nginxAvailable,
        bool nginxHttp3Available = false, bool bombardierAvailable = false)
    {
        return mode switch
        {
            ProbeMode.OriginDirect => [new("origin-direct", ProbeMode.OriginDirect, null)],
            ProbeMode.CompareSaturation => BuildSaturationArms(nginxAvailable, bombardierAvailable, nginxHttp3Available),
            ProbeMode.ReverseHttp1 => [new("twp-reverse-http1", ProbeMode.ReverseHttp1, null)],
            ProbeMode.BareReverseHttp1 => [new("bare-reverse-http1", ProbeMode.BareReverseHttp1, null)],
            ProbeMode.NginxReverseHttp1 => nginxAvailable
                ? [new("nginx-reverse-http1", ProbeMode.NginxReverseHttp1, null)]
                : [],
            ProbeMode.YarpReverseHttp1 => [new("yarp-reverse-http1", ProbeMode.YarpReverseHttp1, null)],
            ProbeMode.HttpsMitm => [new("twp-https-mitm", ProbeMode.HttpsMitm, null)],
            ProbeMode.HttpMitm => [new("twp-http-mitm", ProbeMode.HttpMitm, null)],
            ProbeMode.ReverseHttp1Mitm => [new("twp-reverse-http1-mitm", ProbeMode.ReverseHttp1Mitm, null)],
            ProbeMode.ReverseHttp1Tls => [new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null)],
            ProbeMode.ReverseHttp1ToHttps => [new("twp-reverse-http1-to-https", ProbeMode.ReverseHttp1ToHttps, null)],
            ProbeMode.BareReverseHttp1Tls => [new("bare-reverse-http1-tls", ProbeMode.BareReverseHttp1Tls, null)],
            ProbeMode.NginxReverseHttp1Tls => nginxAvailable
                ? [new("nginx-reverse-http1-tls", ProbeMode.NginxReverseHttp1Tls, null)]
                : [],
            ProbeMode.YarpReverseHttp1Tls => [new("yarp-reverse-http1-tls", ProbeMode.YarpReverseHttp1Tls, null)],
            ProbeMode.YarpReverseHttp1ToHttps => [new("yarp-reverse-http1-to-https", ProbeMode.YarpReverseHttp1ToHttps, null)],
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
            ProbeMode.NginxReverseHttp3Cleartext => nginxHttp3Available
                ? [new("nginx-reverse-http3-cleartext", ProbeMode.NginxReverseHttp3Cleartext, null)]
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
            ProbeMode.ReverseHttp3ToH2c =>
                [new("twp-reverse-http3-to-h2c", ProbeMode.ReverseHttp3ToH2c, null)],
            ProbeMode.YarpReverseHttp3ToH2c =>
                [new("yarp-reverse-http3-to-h2c", ProbeMode.YarpReverseHttp3ToH2c, null)],
            ProbeMode.ReverseHttp1ToH2c =>
                [new("twp-reverse-http1-to-h2c", ProbeMode.ReverseHttp1ToH2c, null)],
            ProbeMode.YarpReverseHttp1ToH2c =>
                [new("yarp-reverse-http1-to-h2c", ProbeMode.YarpReverseHttp1ToH2c, null)],
            ProbeMode.ReverseHttp1PlainToH2c =>
                [new("twp-reverse-http1-plain-to-h2c", ProbeMode.ReverseHttp1PlainToH2c, null)],
            ProbeMode.YarpReverseHttp1PlainToH2c =>
                [new("yarp-reverse-http1-plain-to-h2c", ProbeMode.YarpReverseHttp1PlainToH2c, null)],
            ProbeMode.ReverseHttp1PlainToHttp2 =>
                [new("twp-reverse-http1-plain-to-http2", ProbeMode.ReverseHttp1PlainToHttp2, null)],
            ProbeMode.YarpReverseHttp1PlainToHttp2 =>
                [new("yarp-reverse-http1-plain-to-http2", ProbeMode.YarpReverseHttp1PlainToHttp2, null)],
            ProbeMode.ReverseHttp1PlainToHttp3 =>
                [new("twp-reverse-http1-plain-to-http3", ProbeMode.ReverseHttp1PlainToHttp3, null)],
            ProbeMode.YarpReverseHttp1PlainToHttp3 =>
                [new("yarp-reverse-http1-plain-to-http3", ProbeMode.YarpReverseHttp1PlainToHttp3, null)],
            ProbeMode.ReverseH2cToHttps =>
                [new("twp-reverse-h2c-to-https", ProbeMode.ReverseH2cToHttps, null)],
            ProbeMode.YarpReverseH2cToHttps =>
                [new("yarp-reverse-h2c-to-https", ProbeMode.YarpReverseH2cToHttps, null)],
            ProbeMode.YarpReverseHttp1TlsToHttps =>
                [new("yarp-reverse-http1-tls-to-https", ProbeMode.YarpReverseHttp1TlsToHttps, null)],
            ProbeMode.YarpReverseHttp2ToHttpsHttp1 =>
                [new("yarp-reverse-http2-to-https-http1", ProbeMode.YarpReverseHttp2ToHttpsHttp1, null)],
            ProbeMode.YarpReverseHttp3ToHttpsHttp1 =>
                [new("yarp-reverse-http3-to-https-http1", ProbeMode.YarpReverseHttp3ToHttpsHttp1, null)],
            ProbeMode.YarpReverseHttp3ToHttp3 =>
                [new("yarp-reverse-http3-to-http3", ProbeMode.YarpReverseHttp3ToHttp3, null)],
            ProbeMode.ExplicitHttp1Multi =>
                [new("twp-explicit-http1-multi", ProbeMode.ExplicitHttp1Multi, null)],
            ProbeMode.ExplicitHttp2Multi =>
                [new("twp-explicit-http2-multi", ProbeMode.ExplicitHttp2Multi, null)],
            ProbeMode.MitmHttp2ToHttp1 =>
                [new("twp-mitm-http2-to-http1", ProbeMode.MitmHttp2ToHttp1, null)],
            ProbeMode.MitmHttp3ToHttp1 =>
                [new("twp-mitm-http3-to-http1", ProbeMode.MitmHttp3ToHttp1, null)],
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
                    ..(nginxHttp3Available
                        ? new ArmSpec[]
                        {
                            new("nginx-reverse-http3-cleartext", ProbeMode.NginxReverseHttp3Cleartext, null)
                        }
                        : []),
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
                    new("twp-reverse-http1-to-https", ProbeMode.ReverseHttp1ToHttps, null),
                    new("yarp-reverse-http1-to-https", ProbeMode.YarpReverseHttp1ToHttps, null),
                    new("twp-https-mitm", ProbeMode.HttpsMitm, null),
                    new("twp-reverse-http2", ProbeMode.ReverseHttp2, null),
                    new("twp-reverse-h2c-to-h2c", ProbeMode.ReverseH2cToH2c, null),
                    new("yarp-reverse-h2c-to-h2c", ProbeMode.YarpReverseH2cToH2c, null),
                    new("twp-reverse-h2c", ProbeMode.ReverseH2c, null),
                    new("yarp-reverse-h2c", ProbeMode.YarpReverseH2c, null),
                    new("nginx-reverse-http2", ProbeMode.NginxReverseHttp2, null),
                    new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null),
                    new("twp-reverse-http3", ProbeMode.ReverseHttp3, null),
                    new("yarp-reverse-http3-to-http3", ProbeMode.YarpReverseHttp3ToHttp3, null)
                ]
                :
                [
                    new("twp-reverse-http1", ProbeMode.ReverseHttp1, null),
                    new("yarp-reverse-http1", ProbeMode.YarpReverseHttp1, null),
                    new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null),
                    new("yarp-reverse-http1-tls", ProbeMode.YarpReverseHttp1Tls, null),
                    new("twp-reverse-http1-to-https", ProbeMode.ReverseHttp1ToHttps, null),
                    new("yarp-reverse-http1-to-https", ProbeMode.YarpReverseHttp1ToHttps, null),
                    new("twp-https-mitm", ProbeMode.HttpsMitm, null),
                    new("twp-reverse-http2", ProbeMode.ReverseHttp2, null),
                    new("twp-reverse-h2c-to-h2c", ProbeMode.ReverseH2cToH2c, null),
                    new("yarp-reverse-h2c-to-h2c", ProbeMode.YarpReverseH2cToH2c, null),
                    new("twp-reverse-h2c", ProbeMode.ReverseH2c, null),
                    new("yarp-reverse-h2c", ProbeMode.YarpReverseH2c, null),
                    new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null),
                    new("twp-reverse-http3", ProbeMode.ReverseHttp3, null),
                    new("yarp-reverse-http3-to-http3", ProbeMode.YarpReverseHttp3ToHttp3, null)
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
                new("twp-reverse-h2c-to-https", ProbeMode.ReverseH2cToHttps, null),
                new("yarp-reverse-h2c-to-https", ProbeMode.YarpReverseH2cToHttps, null),
                new("twp-reverse-h2c-to-h3", ProbeMode.ReverseH2cToH3, null),
                new("yarp-reverse-h2c-to-h3", ProbeMode.YarpReverseH2cToH3, null),
                new("twp-reverse-http11-to-http2", ProbeMode.ReverseHttp11ToHttp2, null),
                new("yarp-reverse-http11-to-http2", ProbeMode.YarpReverseHttp11ToHttp2, null),
                new("twp-reverse-http1-to-h2c", ProbeMode.ReverseHttp1ToH2c, null),
                new("yarp-reverse-http1-to-h2c", ProbeMode.YarpReverseHttp1ToH2c, null),
                new("twp-reverse-http1-plain-to-h2c", ProbeMode.ReverseHttp1PlainToH2c, null),
                new("yarp-reverse-http1-plain-to-h2c", ProbeMode.YarpReverseHttp1PlainToH2c, null),
                new("twp-reverse-http1-plain-to-http2", ProbeMode.ReverseHttp1PlainToHttp2, null),
                new("yarp-reverse-http1-plain-to-http2", ProbeMode.YarpReverseHttp1PlainToHttp2, null),
                new("twp-reverse-http1-plain-to-http3", ProbeMode.ReverseHttp1PlainToHttp3, null),
                new("yarp-reverse-http1-plain-to-http3", ProbeMode.YarpReverseHttp1PlainToHttp3, null),
                new("twp-reverse-http1-to-http3", ProbeMode.ReverseHttp1ToHttp3, null),
                new("yarp-reverse-http1-to-http3", ProbeMode.YarpReverseHttp1ToHttp3, null),
                new("twp-reverse-http2-to-http3", ProbeMode.ReverseHttp2ToHttp3, null),
                new("yarp-reverse-http2-to-http3", ProbeMode.YarpReverseHttp2ToHttp3, null),
                new("twp-reverse-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null),
                new("yarp-reverse-http3-cleartext", ProbeMode.YarpReverseHttp3Cleartext, null),
                ..(nginxHttp3Available
                    ? new ArmSpec[]
                    {
                        new("nginx-reverse-http3-cleartext", ProbeMode.NginxReverseHttp3Cleartext, null)
                    }
                    : []),
                new("twp-reverse-http3-to-h2c", ProbeMode.ReverseHttp3ToH2c, null),
                new("yarp-reverse-http3-to-h2c", ProbeMode.YarpReverseHttp3ToH2c, null),
                new("twp-reverse-http3-to-http2", ProbeMode.ReverseHttp3ToHttp2, null),
                new("yarp-reverse-http3-to-http2", ProbeMode.YarpReverseHttp3ToHttp2, null),
                new("yarp-reverse-http3-to-http3", ProbeMode.YarpReverseHttp3ToHttp3, null)
            ],
            ProbeMode.CompareHttp3Cleartext =>
            [
                new("twp-reverse-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null),
                new("yarp-reverse-http3-cleartext", ProbeMode.YarpReverseHttp3Cleartext, null),
                ..(nginxHttp3Available
                    ? new ArmSpec[]
                    {
                        new("nginx-reverse-http3-cleartext", ProbeMode.NginxReverseHttp3Cleartext, null)
                    }
                    : [])
            ],
            ProbeMode.CompareMitm =>
            [
                // Full 5×5 Client×Origin wire pairs (TWP reverse / inspectable) + explicit CONNECT.
                new("twp-reverse-http1", ProbeMode.ReverseHttp1, null), // H1 plain→H1 plain
                new("twp-reverse-http1-to-https", ProbeMode.ReverseHttp1ToHttps, null), // H1 plain→H1 TLS
                new("twp-reverse-http1-plain-to-h2c", ProbeMode.ReverseHttp1PlainToH2c, null), // H1 plain→H2 plain
                new("twp-reverse-http1-plain-to-http2", ProbeMode.ReverseHttp1PlainToHttp2, null), // H1 plain→H2 TLS
                new("twp-reverse-http1-plain-to-http3", ProbeMode.ReverseHttp1PlainToHttp3, null), // H1 plain→H3
                new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null), // H1 TLS→H1 plain
                new("twp-reverse-http1-mitm", ProbeMode.ReverseHttp1Mitm, null), // H1 TLS→H1 TLS
                new("twp-reverse-http1-to-h2c", ProbeMode.ReverseHttp1ToH2c, null), // H1 TLS→H2 plain
                new("twp-reverse-http11-to-http2", ProbeMode.ReverseHttp11ToHttp2, null), // H1 TLS→H2 TLS
                new("twp-reverse-http1-to-http3", ProbeMode.ReverseHttp1ToHttp3, null), // H1 TLS→H3
                new("twp-reverse-h2c-to-h1", ProbeMode.ReverseH2cToH1, null), // H2 plain→H1 plain
                new("twp-reverse-h2c-to-https", ProbeMode.ReverseH2cToHttps, null), // H2 plain→H1 TLS
                new("twp-reverse-h2c-to-h2c", ProbeMode.ReverseH2cToH2c, null), // H2 plain→H2 plain
                new("twp-reverse-h2c", ProbeMode.ReverseH2c, null), // H2 plain→H2 TLS
                new("twp-reverse-h2c-to-h3", ProbeMode.ReverseH2cToH3, null), // H2 plain→H3
                new("twp-reverse-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null), // H2 TLS→H1 plain
                new("twp-mitm-http2-to-http1", ProbeMode.MitmHttp2ToHttp1, null), // H2 TLS→H1 TLS
                new("twp-reverse-http2-to-h2c", ProbeMode.ReverseHttp2ToH2c, null), // H2 TLS→H2 plain
                new("twp-reverse-http2", ProbeMode.ReverseHttp2, null), // H2 TLS→H2 TLS
                new("twp-reverse-http2-to-http3", ProbeMode.ReverseHttp2ToHttp3, null), // H2 TLS→H3
                new("twp-reverse-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null), // H3→H1 plain
                new("twp-mitm-http3-to-http1", ProbeMode.MitmHttp3ToHttp1, null), // H3→H1 TLS
                new("twp-reverse-http3-to-h2c", ProbeMode.ReverseHttp3ToH2c, null), // H3→H2 plain
                new("twp-reverse-http3-to-http2", ProbeMode.ReverseHttp3ToHttp2, null), // H3→H2 TLS
                new("twp-reverse-http3", ProbeMode.ReverseHttp3, null), // H3→H3
                new("twp-https-mitm", ProbeMode.HttpsMitm, null) // explicit CONNECT → H1 TLS
            ],
            ProbeMode.CompareMatrix => BuildFullMatrixArms(nginxAvailable, nginxHttp3Available),
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
                ..HeavierReverseArms(nginxAvailable, nginxHttp3Available, WorkloadOptions.ForBodyGet(64 * 1024),
                    "body64k"),
                ..HeavierReverseArms(nginxAvailable, nginxHttp3Available, WorkloadOptions.ForBodyGet(256 * 1024),
                    "body256k")
            ],
            ProbeMode.ComparePost =>
                HeavierReverseArms(nginxAvailable, nginxHttp3Available,
                    WorkloadOptions.ForPost(64 * 1024, 64 * 1024), "post64k"),
            ProbeMode.CompareLossy =>
                // H1/H2: TCP delay + connection stall (HOL). H3: UDP delay + datagram drop (QUIC).
                HeavierReverseArms(nginxAvailable, nginxHttp3Available,
                    WorkloadOptions.ForLossy(64 * 1024, 5, 1.0), "lossy"),
            ProbeMode.CompareTlsCost => BuildTlsCostArms(nginxAvailable),
            ProbeMode.CompareArch => BuildArchArms(nginxAvailable, nginxHttp3Available),
            ProbeMode.YarpReverseHttp2ToHttps =>
                [new("yarp-reverse-http2-to-https", ProbeMode.YarpReverseHttp2ToHttps, null)],
            ProbeMode.ExplicitPoolSweep =>
            [
                new("twp-explicit-http1-multi-c4", ProbeMode.ExplicitHttp1Multi, 4),
                new("twp-explicit-http1-multi-c32", ProbeMode.ExplicitHttp1Multi, 32),
                new("twp-explicit-http1-multi-c128", ProbeMode.ExplicitHttp1Multi, 128)
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    private static IReadOnlyList<ArmSpec> BuildFullMatrixArms(bool nginxAvailable, bool nginxHttp3Available)
    {
        // Full 5×5 Client×Origin reverse cartesian: TWP + YARP for each cell.
        // nginxAvailable / nginxHttp3Available reserved for optional native peers (not in this matrix).
        _ = nginxAvailable;
        _ = nginxHttp3Available;
        return
        [
            // H1 plain client
            new("twp-reverse-http1", ProbeMode.ReverseHttp1, null),
            new("yarp-reverse-http1", ProbeMode.YarpReverseHttp1, null),
            new("twp-reverse-http1-to-https", ProbeMode.ReverseHttp1ToHttps, null),
            new("yarp-reverse-http1-to-https", ProbeMode.YarpReverseHttp1ToHttps, null),
            new("twp-reverse-http1-plain-to-h2c", ProbeMode.ReverseHttp1PlainToH2c, null),
            new("yarp-reverse-http1-plain-to-h2c", ProbeMode.YarpReverseHttp1PlainToH2c, null),
            new("twp-reverse-http1-plain-to-http2", ProbeMode.ReverseHttp1PlainToHttp2, null),
            new("yarp-reverse-http1-plain-to-http2", ProbeMode.YarpReverseHttp1PlainToHttp2, null),
            new("twp-reverse-http1-plain-to-http3", ProbeMode.ReverseHttp1PlainToHttp3, null),
            new("yarp-reverse-http1-plain-to-http3", ProbeMode.YarpReverseHttp1PlainToHttp3, null),
            // H1 TLS client
            new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null),
            new("yarp-reverse-http1-tls", ProbeMode.YarpReverseHttp1Tls, null),
            new("twp-reverse-http1-mitm", ProbeMode.ReverseHttp1Mitm, null),
            new("yarp-reverse-http1-tls-to-https", ProbeMode.YarpReverseHttp1TlsToHttps, null),
            new("twp-reverse-http1-to-h2c", ProbeMode.ReverseHttp1ToH2c, null),
            new("yarp-reverse-http1-to-h2c", ProbeMode.YarpReverseHttp1ToH2c, null),
            new("twp-reverse-http11-to-http2", ProbeMode.ReverseHttp11ToHttp2, null),
            new("yarp-reverse-http11-to-http2", ProbeMode.YarpReverseHttp11ToHttp2, null),
            new("twp-reverse-http1-to-http3", ProbeMode.ReverseHttp1ToHttp3, null),
            new("yarp-reverse-http1-to-http3", ProbeMode.YarpReverseHttp1ToHttp3, null),
            // H2 plain (h2c) client
            new("twp-reverse-h2c-to-h1", ProbeMode.ReverseH2cToH1, null),
            new("yarp-reverse-h2c-to-h1", ProbeMode.YarpReverseH2cToH1, null),
            new("twp-reverse-h2c-to-https", ProbeMode.ReverseH2cToHttps, null),
            new("yarp-reverse-h2c-to-https", ProbeMode.YarpReverseH2cToHttps, null),
            new("twp-reverse-h2c-to-h2c", ProbeMode.ReverseH2cToH2c, null),
            new("yarp-reverse-h2c-to-h2c", ProbeMode.YarpReverseH2cToH2c, null),
            new("twp-reverse-h2c", ProbeMode.ReverseH2c, null),
            new("yarp-reverse-h2c", ProbeMode.YarpReverseH2c, null),
            new("twp-reverse-h2c-to-h3", ProbeMode.ReverseH2cToH3, null),
            new("yarp-reverse-h2c-to-h3", ProbeMode.YarpReverseH2cToH3, null),
            // H2 TLS client
            new("twp-reverse-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null),
            new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null),
            new("twp-mitm-http2-to-http1", ProbeMode.MitmHttp2ToHttp1, null),
            new("yarp-reverse-http2-to-https-http1", ProbeMode.YarpReverseHttp2ToHttpsHttp1, null),
            new("twp-reverse-http2-to-h2c", ProbeMode.ReverseHttp2ToH2c, null),
            new("yarp-reverse-http2-to-h2c", ProbeMode.YarpReverseHttp2ToH2c, null),
            new("twp-reverse-http2", ProbeMode.ReverseHttp2, null),
            new("yarp-reverse-http2-to-https", ProbeMode.YarpReverseHttp2ToHttps, null),
            new("twp-reverse-http2-to-http3", ProbeMode.ReverseHttp2ToHttp3, null),
            new("yarp-reverse-http2-to-http3", ProbeMode.YarpReverseHttp2ToHttp3, null),
            // H3 QUIC client
            new("twp-reverse-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null),
            new("yarp-reverse-http3-cleartext", ProbeMode.YarpReverseHttp3Cleartext, null),
            new("twp-mitm-http3-to-http1", ProbeMode.MitmHttp3ToHttp1, null),
            new("yarp-reverse-http3-to-https-http1", ProbeMode.YarpReverseHttp3ToHttpsHttp1, null),
            new("twp-reverse-http3-to-h2c", ProbeMode.ReverseHttp3ToH2c, null),
            new("yarp-reverse-http3-to-h2c", ProbeMode.YarpReverseHttp3ToH2c, null),
            new("twp-reverse-http3-to-http2", ProbeMode.ReverseHttp3ToHttp2, null),
            new("yarp-reverse-http3-to-http2", ProbeMode.YarpReverseHttp3ToHttp2, null),
            new("twp-reverse-http3", ProbeMode.ReverseHttp3, null),
            new("yarp-reverse-http3-to-http3", ProbeMode.YarpReverseHttp3ToHttp3, null)
        ];
    }

    private static IReadOnlyList<ArmSpec> BuildSaturationArms(bool nginxAvailable, bool bombardierAvailable,
        bool nginxHttp3Available)
    {
        // Block A — H1 plain
        var arms = new List<ArmSpec>
        {
            new("origin-direct", ProbeMode.OriginDirect, null)
        };
        if (bombardierAvailable)
        {
            arms.Add(new("origin-direct-bombardier", ProbeMode.OriginDirect, null,
                PreferredGenerator: BombardierLoadGenerator.GeneratorName));
        }

        arms.Add(new("bare-reverse-http1", ProbeMode.BareReverseHttp1, null));
        if (nginxAvailable)
            arms.Add(new("nginx-reverse-http1", ProbeMode.NginxReverseHttp1, null));
        arms.Add(new("yarp-reverse-http1", ProbeMode.YarpReverseHttp1, null));
        arms.Add(new("twp-reverse-http1", ProbeMode.ReverseHttp1, null));

        // Block B — H2 TLS → H1 cleartext (peer ratios, not % of H1 origin-direct)
        if (nginxAvailable)
            arms.Add(new("nginx-reverse-http2", ProbeMode.NginxReverseHttp2, null));
        arms.Add(new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null));
        arms.Add(new("twp-reverse-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null));

        // Block C — H3 → H1 cleartext (QuicListener skip happens in RunAsync RemoveAll)
        if (nginxHttp3Available)
            arms.Add(new("nginx-reverse-http3-cleartext", ProbeMode.NginxReverseHttp3Cleartext, null));
        arms.Add(new("yarp-reverse-http3-cleartext", ProbeMode.YarpReverseHttp3Cleartext, null));
        arms.Add(new("twp-reverse-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null));

        return arms;
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

    private static IReadOnlyList<ArmSpec> BuildArchArms(bool nginxAvailable, bool nginxHttp3Available)
    {
        var slow = WorkloadOptions.ForSlowConsumer();
        var early = WorkloadOptions.ForEarlyResponse();
        var duplex = WorkloadOptions.ForDuplexH2();
        var ws = WorkloadOptions.ForWebSocket();
        var arms = new List<ArmSpec>();
        arms.AddRange(HeavierReverseArms(nginxAvailable, nginxHttp3Available, slow, "slow256k"));
        arms.AddRange(HeavierReverseArms(nginxAvailable, nginxHttp3Available, early, "early64k"));
        arms.Add(new("twp-reverse-http2-duplex-h2", ProbeMode.ReverseHttp2, null, duplex));
        arms.Add(new("yarp-reverse-http2-to-https-duplex-h2", ProbeMode.YarpReverseHttp2ToHttps, null, duplex));
        arms.Add(new("twp-reverse-http1-tls-duplex-ws", ProbeMode.ReverseHttp1Tls, null, ws));
        if (nginxAvailable)
            arms.Add(new("nginx-reverse-http1-tls-duplex-ws", ProbeMode.NginxReverseHttp1Tls, null, ws));
        arms.Add(new("yarp-reverse-http1-tls-duplex-ws", ProbeMode.YarpReverseHttp1Tls, null, ws));
        return arms;
    }

    private sealed record ArmPeakResult(double PeakRps, long? RssPeakBytes, double? CpuAvgPct);

    private static async Task<ArmPeakResult> RunArmAsync(ArmSpec arm, RampOptions options, StreamWriter csv,
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
        var stackUsesQuicGenerator = string.Equals(stack.LoadGenerator, "quic-http3",
            StringComparison.OrdinalIgnoreCase);
        var http3Client = stack.RequestHttpVersion.Major >= 3;
        // Lossy H3: force raw QuicConnection client. HttpClient+UDP-shim works on Linux/laptop
        // but collapses on windows-latest GHA (sustain ~1).
        var forceLossyQuicGenerator = workload.IsLossy && http3Client;

        try
        {
            if (workload.IsLossy)
            {
                var backendQuicPort = stack.QuicPort ?? (http3Client ? stack.TargetUri.Port : (int?)null);
                if ((stackUsesQuicGenerator || http3Client) && backendQuicPort is > 0)
                {
                    udpLink = LossyUdpLink.Start(backendQuicPort.Value, workload.DelayMs, workload.LossPercent);
                    quicPort = udpLink.Port;
                    // Log URI points at the shim; quic-http3 dials quicPort directly.
                    targetUri = new Uri(udpLink.ListenUrlHttps);
                    targetUris = null;

                    ProbeLog.Info(
                        $"  lossy-udp port={udpLink.Port} -> quic={backendQuicPort} delay={workload.DelayMs}ms loss={workload.LossPercent}%");
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
            ProcessResourceSample? peakResources = null;
            var lastGoodConcurrency = 0;

            var useQuic = (stackUsesQuicGenerator || forceLossyQuicGenerator) && quicPort is > 0;
            var useBombardier = string.Equals(arm.PreferredGenerator, BombardierLoadGenerator.GeneratorName,
                StringComparison.OrdinalIgnoreCase);
            var generatorLabel = useQuic
                ? "quic-http3"
                : useBombardier
                    ? BombardierLoadGenerator.GeneratorName
                    : "dotnet-httpclient";
            var loadOptions = new LoadRequestOptions
            {
                Target = targetUri,
                Targets = targetUris,
                ExplicitProxyUrl = stack.ExplicitProxyUrl,
                HttpVersion = stack.RequestHttpVersion,
                VersionPolicy = stack.VersionPolicy,
                Workload = workload
            };

            // Column names stay proxy_*; origin-direct samples the origin child PID.
            var samplePid = stack.IsOriginDirect ? stack.OriginProcessId : stack.ProxyProcessId;

            ProbeLog.Info(
                $"  target={targetUri} workload={workload.Suffix} proxy={(stack.ExplicitProxyUrl ?? "(direct-to-listen)")} http={stack.RequestHttpVersion} generator={generatorLabel} maxCached={(maxCached?.ToString() ?? "default")}");
            if (stack.IsOriginDirect)
            {
                ProbeLog.Info($"  attach: origin-only pid={stack.OriginProcessId}");
            }
            else if (stack.IsCombinedServe)
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
                LoadResult result;
                ProcessResourceSample? resources = null;
                try
                {
                    if (useQuic)
                    {
                        var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, quicPort!.Value);
                        // TWP TransparentQuic uses :authority as the upstream target. Managed reverse uses the listen host.
                        var authority = ResolveQuicAuthority(arm.Mode, stack);
                        await QuicHttp3LoadGenerator.WarmupAsync(ep, "localhost", authority,
                            concurrency, options.Warmup, cancellationToken, workload);
                    }
                    else if (useBombardier)
                    {
                        await BombardierLoadGenerator.WarmupAsync(targetUri, concurrency, options.Warmup, workload,
                            cancellationToken);
                    }
                    else
                    {
                        await EmbeddedLoadGenerator.WarmupAsync(loadOptions, concurrency, options.Warmup, cancellationToken);
                    }

                    ProbeLog.Info($"  measure c={concurrency} for {options.StepDuration.TotalSeconds:F0}s...");
                    Task<LoadResult> measureTask;
                    if (useQuic)
                    {
                        var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, quicPort!.Value);
                        var authority = ResolveQuicAuthority(arm.Mode, stack);
                        measureTask = QuicHttp3LoadGenerator.RunAsync(ep, "localhost", authority,
                            concurrency, options.StepDuration, cancellationToken, workload);
                    }
                    else if (useBombardier)
                    {
                        measureTask = BombardierLoadGenerator.RunAsync(targetUri, concurrency, options.StepDuration,
                            workload, cancellationToken);
                    }
                    else
                    {
                        measureTask = EmbeddedLoadGenerator.RunAsync(loadOptions, concurrency, options.StepDuration,
                            cancellationToken);
                    }

                    Task<ProcessResourceSample?>? sampleTask = samplePid is int pid
                        ? ProcessResourceSampler.SampleDuringAsync(pid, options.StepDuration, cancellationToken)
                        : null;

                    result = await measureTask;
                    if (sampleTask != null)
                        resources = await sampleTask;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // Lossy H3 / MsQuic can abort a step; record a hard fail and continue the ramp.
                    ProbeLog.Error($"  step c={concurrency} aborted: {ex.GetType().Name}: {ex.Message}");
                    result = new LoadResult(
                        Generator: generatorLabel,
                        Concurrency: concurrency,
                        DurationSeconds: options.StepDuration.TotalSeconds,
                        Ok: 0,
                        Errors: 1,
                        Rps: 0,
                        ErrorRatePercent: 100,
                        P50Ms: 0,
                        P99Ms: 0,
                        MaxMs: 0,
                        NegotiatedVersionHint: stack.RequestHttpVersion.ToString());
                }

                var meetsSlo = result.ErrorRatePercent < options.MaxErrorRatePercent && result.P99Ms <= p99Slo;
                await CsvWriter.WriteRowAsync(csv, arm.Name, result, meetsSlo, nginxVersion, maxCached, workload,
                    stack.YarpVersion, resources);
                await csv.FlushAsync(cancellationToken);

                var resourceHint = resources is { } r
                    ? string.Create(CultureInfo.InvariantCulture,
                        $" memory_rss={r.PeakRssBytes} cpu_avg={r.AvgCpuPercent:F1}%")
                    : "";
                ProbeLog.Info(string.Create(CultureInfo.InvariantCulture,
                    $"    rps={result.Rps:F0} err%={result.ErrorRatePercent:F3} p50={result.P50Ms:F1}ms p99={result.P99Ms:F1}ms max={result.MaxMs:F1}ms ver={result.NegotiatedVersionHint} slo={(meetsSlo ? "PASS" : "FAIL")}{resourceHint}"));

                if (peak == null || result.Rps > peak.Rps)
                {
                    peak = result;
                    peakResources = resources;
                }

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

            return new ArmPeakResult(peak?.Rps ?? 0, peakResources?.PeakRssBytes, peakResources?.AvgCpuPercent);
        }
        finally
        {
            if (tcpLink != null)
                await tcpLink.DisposeAsync();
            if (udpLink != null)
                await udpLink.DisposeAsync();
        }
    }

    /// <summary>
    ///     TWP <c>TransparentQuicProxyEndPoint</c> forwards using <c>:authority</c> as the upstream target
    ///     when an origin QUIC port is published. Managed reverse listens on the listen host directly.
    /// </summary>
    private static string ResolveQuicAuthority(ProbeMode mode, ChildProcessStack stack)
    {
        var yarpInboundH3 = mode is ProbeMode.YarpReverseHttp3Cleartext
            or ProbeMode.YarpReverseHttp3ToHttp2
            or ProbeMode.YarpReverseHttp3ToHttp3
            or ProbeMode.NginxReverseHttp3Cleartext;
        if (yarpInboundH3)
            return "localhost";

        return stack.OriginQuicPort is { } originPort
            ? $"localhost:{originPort}"
            : "localhost";
    }

    /// <summary>
    ///     Publishable TWP÷peer ratios require the same load generator on both sides.
    ///     Throws when CSV/generator labels would mix quic-http3 with HttpClient for an H3 pair.
    /// </summary>
    internal static void EnsureMatchedGenerators(string twpGenerator, string yarpGenerator, string armLabel)
    {
        var a = NormalizeGenerator(twpGenerator);
        var b = NormalizeGenerator(yarpGenerator);
        if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing TWP÷YARP ratio for '{armLabel}': generators differ ({a} vs {b}). Match clients first.");
        }
    }

    private static string NormalizeGenerator(string? generator) =>
        string.IsNullOrWhiteSpace(generator) ? "dotnet-httpclient" : generator.Trim();

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
