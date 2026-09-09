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
    /// <summary>Native reverse: cleartext HTTP/1 → HTTPS HTTP/1 (<c>proxy_ssl</c>).</summary>
    NginxReverseHttp1ToHttps,
    /// <summary>Native reverse: TLS HTTP/1 → HTTPS HTTP/1 (dual TLS; <c>proxy_ssl</c>).</summary>
    NginxReverseHttp1TlsToHttps,
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
    /// <summary>Native reverse: client TLS+h2 → HTTPS HTTP/1 (<c>proxy_ssl</c>).</summary>
    NginxReverseHttp2ToHttpsHttp1,
    /// <summary>Native reverse: client QUIC/h3 → cleartext HTTP/1. Requires nginx <c>http_v3_module</c>.</summary>
    NginxReverseHttp3Cleartext,
    /// <summary>Native reverse: client QUIC/h3 → HTTPS HTTP/1 (<c>proxy_ssl</c>). Requires <c>http_v3_module</c>.</summary>
    NginxReverseHttp3ToHttpsHttp1,
    /// <summary>HAProxy Community/GPL reverse: cleartext HTTP/1 → cleartext HTTP/1. Linux/macOS only.</summary>
    HaproxyReverseHttp1,
    /// <summary>HAProxy: TLS HTTP/1 → cleartext HTTP/1.</summary>
    HaproxyReverseHttp1Tls,
    /// <summary>HAProxy: cleartext HTTP/1 → HTTPS HTTP/1.</summary>
    HaproxyReverseHttp1ToHttps,
    /// <summary>HAProxy: TLS HTTP/1 → HTTPS HTTP/1 (dual TLS).</summary>
    HaproxyReverseHttp1TlsToHttps,
    /// <summary>HAProxy: client TLS+h2 → cleartext HTTP/1.</summary>
    HaproxyReverseHttp2,
    /// <summary>HAProxy: client TLS+h2 → HTTPS HTTP/1.</summary>
    HaproxyReverseHttp2ToHttpsHttp1,
    /// <summary>HAProxy: client QUIC/h3 → cleartext HTTP/1. Requires <c>USE_QUIC</c>.</summary>
    HaproxyReverseHttp3Cleartext,
    /// <summary>HAProxy: client QUIC/h3 → HTTPS HTTP/1. Requires <c>USE_QUIC</c>.</summary>
    HaproxyReverseHttp3ToHttpsHttp1,
    /// <summary>Envoy reverse: cleartext HTTP/1 → cleartext HTTP/1. Linux/macOS only.</summary>
    EnvoyReverseHttp1,
    /// <summary>Envoy: TLS HTTP/1 → cleartext HTTP/1.</summary>
    EnvoyReverseHttp1Tls,
    /// <summary>Envoy: cleartext HTTP/1 → HTTPS HTTP/1.</summary>
    EnvoyReverseHttp1ToHttps,
    /// <summary>Envoy: TLS HTTP/1 → HTTPS HTTP/1 (dual TLS).</summary>
    EnvoyReverseHttp1TlsToHttps,
    /// <summary>Envoy: client TLS+h2 → cleartext HTTP/1.</summary>
    EnvoyReverseHttp2,
    /// <summary>Envoy: client TLS+h2 → HTTPS HTTP/1.</summary>
    EnvoyReverseHttp2ToHttpsHttp1,
    /// <summary>Envoy: client QUIC/h3 → cleartext HTTP/1.</summary>
    EnvoyReverseHttp3Cleartext,
    /// <summary>Envoy: client QUIC/h3 → HTTPS HTTP/1.</summary>
    EnvoyReverseHttp3ToHttpsHttp1,
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
    /// Quick smoke for nginx <c>proxy_ssl</c> HTTPS-origin peers (H1 plain/TLS, H2 TLS, H3→HTTPS H1).
    /// </summary>
    CompareNginxHttps,
    /// <summary>
    /// HAProxy-only smoke: every terminate arm that can run on this OS (repeats=1). Windows skips (no port).
    /// </summary>
    CompareHaproxySmoke,
    /// <summary>
    /// Envoy-only smoke: every terminate arm that can run on this OS (repeats=1). Windows skips (no port).
    /// </summary>
    CompareEnvoySmoke,
    /// <summary>
    /// True MITM 5×5: same Client×Origin wires as reverse, but TWP_RPS_HTTP_INTERCEPTION=1
    /// (no-op BeforeRequest/BeforeResponse = lite finish possible). TWP-only; plus explicit CONNECT.
    /// compare-mitm / compare-product also append mutate (full-session) twins.
    /// </summary>
    CompareMitm,
    /// <summary>
    /// Full 5×5 Client×Origin reverse matrix: all TWP + YARP pairs for
    /// {H1·plain, H1·TLS, H2·plain, H2·TLS, H3·QUIC}².
    /// </summary>
    CompareMatrix,
    /// <summary>
    /// Same-job product refresh: <see cref="CompareMatrix"/> reverse peers + <see cref="CompareMitm"/> TWP.
    /// </summary>
    CompareProduct,
    /// <summary>
    /// Fast smoke of arms required by <c>validate-compare-product-gates.ps1</c> (Lite+Full+Reverse
    /// gate pairs + YARP H3 peers). Use on a single OS to verify CSV + gate step before a long
    /// <see cref="CompareProduct"/> run finishes.
    /// </summary>
    CompareProductSmoke,
    /// <summary>PR2 local spot gate: MITM Full÷Reverse + reverse TWP÷YARP pairs @ c=64.</summary>
    CompareSpot,
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
    /// <summary>
    /// Product editions: library H1 baselines + CLI daemon / CLI+Plus / CLI+Intercept arms.
    /// </summary>
    CompareEditions,
    /// <summary>
    /// Alias for <see cref="CompareMatrix"/> used by Gate 2 cross-version validation (routes unset).
    /// </summary>
    CompareCrossVersion,
    /// <summary>Shipped CLI daemon: H1 plain forwardHost (product defaults).</summary>
    TwpCliReverseHttp1,
    /// <summary>Shipped CLI daemon: H1 TLS terminate → cleartext origin.</summary>
    TwpCliReverseHttp1Tls,
    /// <summary>Shipped CLI daemon: single route table ≡ ForwardHost.</summary>
    TwpCliReverseHttp1Route,
    /// <summary>CLI + Plus enabled (no options) — control-plane only.</summary>
    TwpCliPlusBaseHttp1,
    /// <summary>CLI + Plus + cache.enable.</summary>
    TwpCliPlusCacheHttp1,
    /// <summary>CLI + route RequestHeaderSet transform (forces session / intercept path).</summary>
    TwpCliInterceptHttp1,
    /// <summary>CLI + Plus WAF denyPaths that do not match probe traffic.</summary>
    TwpCliPlusWafHttp1,
    /// <summary>CLI + Plus CIDR allow 127.0.0.0/8.</summary>
    TwpCliPlusCidrHttp1,
    /// <summary>CLI + Plus JWT (RS256 JWKS) with Authorization Bearer on every request.</summary>
    TwpCliPlusJwtHttp1,
    /// <summary>CLI + Plus in-memory rate limit with a very high ceiling.</summary>
    TwpCliPlusRateLimitHttp1,
    /// <summary>CLI + Plus active health against ForwardHost+cluster destinations.</summary>
    TwpCliPlusResilienceHttp1,
    /// <summary>CLI + Plus file discovery with mid-ramp rewrite.</summary>
    TwpCliPlusDiscoveryFileHttp1,
    /// <summary>CLI + Plus-base with background control-plane /metrics + /v1/snapshot scrape.</summary>
    TwpCliPlusMetricsScrapeHttp1,
    /// <summary>CLI + Plus cache.enable with Cache-Control origin; warm then measure.</summary>
    TwpCliPlusCacheHitHttp1,
    /// <summary>CLI staticFiles.root tiny file.</summary>
    TwpCliStaticHttp1,
    /// <summary>CLI logging.enabled + Info file sink.</summary>
    TwpCliLoggingHttp1,
    /// <summary>CLI LeastTime LB across two healthy origins.</summary>
    TwpCliLbLeastTimeHttp1,
    /// <summary>CLI .twp site-file listen/forward dialect.</summary>
    TwpCliDialectTwpHttp1,
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
    public string? HaproxyPath { get; init; }
    public string? EnvoyPath { get; init; }
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
    /// <summary>
    /// After the first SLO fail on an arm that previously passed, run one more concurrency step
    /// (peak confirmation) then stop the arm. Default on — matches industry load-tool behavior.
    /// </summary>
    public bool StopOnSloFail { get; init; } = true;
    /// <summary>
    /// Wall-clock cap per arm. Null = derive from concurrency steps × (warmup+measure) + overhead.
    /// Prevents a single stuck measure (MsQuic/bombardier/child READY) from burning the 400m GHA step.
    /// </summary>
    public TimeSpan? ArmTimeout { get; init; }
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

        string? haproxyVersion = null;
        var haproxyExe = HaproxyHost.ResolveHaproxyExecutable(options.HaproxyPath);
        if (haproxyExe != null)
            haproxyVersion = ProbeHaproxyVersion(haproxyExe);

        string? envoyVersion = null;
        var envoyExe = EnvoyHost.ResolveEnvoyExecutable(options.EnvoyPath);
        if (envoyExe != null)
            envoyVersion = ProbeEnvoyVersion(envoyExe);

        ProbeLog.Info(MachineInfo.FormatReport(nginxVersion, haproxyVersion, envoyVersion));
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

        var haproxyQuic = haproxyExe != null && HaproxyHost.SupportsQuic(haproxyExe);
        if (haproxyExe != null && !haproxyQuic)
            ProbeLog.Info("haproxy has no USE_QUIC — skipping HTTP/3 HAProxy reverse arms (GHA Linux builds 3.2 with QUIC).");

        var envoyHttp3 = envoyExe != null && EnvoyHost.SupportsHttp3(envoyExe);
        if (envoyExe != null && !envoyHttp3)
            ProbeLog.Info("envoy has no HTTP/3 — skipping HTTP/3 Envoy reverse arms.");

        var bombardierAvailable = BombardierLoadGenerator.IsAvailable();
        var arms = ResolveArms(options.Mode, nginxExe != null, nginxHttp3, bombardierAvailable,
            haproxyExe != null, haproxyQuic, envoyExe != null, envoyHttp3).ToList();
        if (!System.Net.Quic.QuicListener.IsSupported)
        {
            var removed = arms.RemoveAll(a =>
                a.Mode is ProbeMode.ReverseHttp3 or ProbeMode.ReverseHttp3Cleartext
                    or ProbeMode.YarpReverseHttp3Cleartext or ProbeMode.NginxReverseHttp3Cleartext
                    or ProbeMode.NginxReverseHttp3ToHttpsHttp1
                    or ProbeMode.HaproxyReverseHttp3Cleartext or ProbeMode.HaproxyReverseHttp3ToHttpsHttp1
                    or ProbeMode.EnvoyReverseHttp3Cleartext or ProbeMode.EnvoyReverseHttp3ToHttpsHttp1
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
            // CompareHaproxySmoke on Windows: HAProxy unavailable is the expected outcome (skip).
            if (options.Mode is ProbeMode.CompareHaproxySmoke && haproxyExe == null)
            {
                ProbeLog.Info("compare-haproxy-smoke: HAProxy not available on this OS (expected on Windows) — skip OK.");
                return 0;
            }

            if (options.Mode is ProbeMode.CompareEnvoySmoke && envoyExe == null)
            {
                ProbeLog.Info("compare-envoy-smoke: Envoy not available on this OS (expected on Windows) — skip OK.");
                return 0;
            }

            ProbeLog.Error("No arms to run for this mode/host combination.");
            return 2;
        }

        if ((options.Mode is ProbeMode.NginxReverseHttp1 or ProbeMode.NginxReverseHttp1Tls
                or ProbeMode.NginxReverseHttp1ToHttps or ProbeMode.NginxReverseHttp1TlsToHttps
                or ProbeMode.NginxReverseHttp2 or ProbeMode.NginxReverseHttp2ToHttpsHttp1
                or ProbeMode.NginxReverseHttp3Cleartext or ProbeMode.NginxReverseHttp3ToHttpsHttp1
                or ProbeMode.Compare or ProbeMode.CompareHttp2
                or ProbeMode.CompareTls or ProbeMode.CompareTerminate or ProbeMode.CompareSame
                or ProbeMode.CompareBridges or ProbeMode.CompareHttp3Cleartext
                or ProbeMode.CompareBodies or ProbeMode.ComparePost or ProbeMode.CompareLossy
                or ProbeMode.CompareTlsCost or ProbeMode.CompareArch or ProbeMode.CompareSaturation
                or ProbeMode.CompareNginxHttps)
            && nginxExe == null)
        {
            ProbeLog.Info(NginxHost.NginxMissingMessage());
            ProbeLog.Info(string.Empty);
        }

        if ((options.Mode is ProbeMode.HaproxyReverseHttp1 or ProbeMode.HaproxyReverseHttp1Tls
                or ProbeMode.HaproxyReverseHttp1ToHttps or ProbeMode.HaproxyReverseHttp1TlsToHttps
                or ProbeMode.HaproxyReverseHttp2 or ProbeMode.HaproxyReverseHttp2ToHttpsHttp1
                or ProbeMode.HaproxyReverseHttp3Cleartext or ProbeMode.HaproxyReverseHttp3ToHttpsHttp1
                or ProbeMode.Compare or ProbeMode.CompareHttp2
                or ProbeMode.CompareTls or ProbeMode.CompareTerminate or ProbeMode.CompareSame
                or ProbeMode.CompareBridges or ProbeMode.CompareHttp3Cleartext
                or ProbeMode.CompareBodies or ProbeMode.ComparePost or ProbeMode.CompareLossy
                or ProbeMode.CompareTlsCost or ProbeMode.CompareArch or ProbeMode.CompareSaturation
                or ProbeMode.CompareCeiling or ProbeMode.CompareHaproxySmoke)
            && haproxyExe == null)
        {
            ProbeLog.Info(HaproxyHost.HaproxyMissingMessage());
            ProbeLog.Info(string.Empty);
        }

        if ((options.Mode is ProbeMode.EnvoyReverseHttp1 or ProbeMode.EnvoyReverseHttp1Tls
                or ProbeMode.EnvoyReverseHttp1ToHttps or ProbeMode.EnvoyReverseHttp1TlsToHttps
                or ProbeMode.EnvoyReverseHttp2 or ProbeMode.EnvoyReverseHttp2ToHttpsHttp1
                or ProbeMode.EnvoyReverseHttp3Cleartext or ProbeMode.EnvoyReverseHttp3ToHttpsHttp1
                or ProbeMode.Compare or ProbeMode.CompareHttp2
                or ProbeMode.CompareTls or ProbeMode.CompareTerminate or ProbeMode.CompareSame
                or ProbeMode.CompareBridges or ProbeMode.CompareHttp3Cleartext
                or ProbeMode.CompareBodies or ProbeMode.ComparePost or ProbeMode.CompareLossy
                or ProbeMode.CompareTlsCost or ProbeMode.CompareArch or ProbeMode.CompareSaturation
                or ProbeMode.CompareCeiling or ProbeMode.CompareEnvoySmoke)
            && envoyExe == null)
        {
            ProbeLog.Info(EnvoyHost.EnvoyMissingMessage());
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

            var armTimeout = options.ArmTimeout ?? ComputeDefaultArmTimeout(options);
            if (rep == 1)
            {
                ProbeLog.Info(string.Create(CultureInfo.InvariantCulture,
                    $"ArmTimeout={armTimeout.TotalMinutes:F1}m (stuck measure/READY/MsQuic cannot burn the full job)."));
            }

            foreach (var arm in arms)
            {
                ProbeLog.Info($"--- arm {arm.Name} ---");
                ArmPeakResult peak;
                using (var armCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    armCts.CancelAfter(armTimeout);
                    try
                    {
                        peak = await RunArmAsync(arm, options, csv, nginxVersion, haproxyVersion, envoyVersion,
                            armCts.Token)
                            .WaitAsync(armTimeout + TimeSpan.FromSeconds(30), cancellationToken);
                    }
                    catch (Exception ex) when (ex is TimeoutException
                                              || (ex is OperationCanceledException
                                                  && !cancellationToken.IsCancellationRequested))
                    {
                        // Not a cert/OS prompt — usually MsQuic/bombardier/child stdout stall.
                        ProbeLog.Error(
                            $"Arm {arm.Name} timed out after {armTimeout.TotalMinutes:F1}m — skipping remaining steps for this arm.");
                        peak = new ArmPeakResult(0, null, null);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException
                                              || !cancellationToken.IsCancellationRequested)
                    {
                        ProbeLog.Error($"Arm {arm.Name} failed: {ex.GetType().Name}: {ex.Message}");
                        peak = new ArmPeakResult(0, null, null);
                    }
                }

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

        if (options.Mode is ProbeMode.CompareHaproxySmoke or ProbeMode.CompareEnvoySmoke)
        {
            var peerPrefix = options.Mode is ProbeMode.CompareHaproxySmoke ? "haproxy-reverse-" : "envoy-reverse-";
            var peerPeaks = peakByArm.Where(kv => kv.Key.StartsWith(peerPrefix, StringComparison.Ordinal)).ToList();
            if (peerPeaks.Count > 0 && peerPeaks.All(kv => Median(kv.Value) <= 0))
            {
                ProbeLog.Error(
                    $"All {peerPrefix}* arms reported median_peak_rps=0 — peer likely failed to start or serve traffic.");
                return 1;
            }
        }

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
        WorkloadOptions? Workload = null, string? PreferredGenerator = null,
        bool EnableHttpInterception = false, bool MutateHttpInterception = false,
        bool BackgroundControlPlaneScrape = false, bool RewriteDiscoveryMidRamp = false,
        bool WarmCacheFirst = false);

    private static IReadOnlyList<ArmSpec> HeavierReverseArms(bool nginxAvailable, bool nginxHttp3Available,
        bool haproxyAvailable, bool haproxyHttp3Available, bool envoyAvailable, bool envoyHttp3Available,
        WorkloadOptions workload, string nameSuffix, bool includeHttp3 = true)
    {
        var arms = new List<ArmSpec>
        {
            new($"twp-reverse-http1-tls-{nameSuffix}", ProbeMode.ReverseHttp1Tls, null, workload)
        };
        if (nginxAvailable)
            arms.Add(new($"nginx-reverse-http1-tls-{nameSuffix}", ProbeMode.NginxReverseHttp1Tls, null, workload));
        if (haproxyAvailable)
            arms.Add(new($"haproxy-reverse-http1-tls-{nameSuffix}", ProbeMode.HaproxyReverseHttp1Tls, null, workload));
        if (envoyAvailable)
            arms.Add(new($"envoy-reverse-http1-tls-{nameSuffix}", ProbeMode.EnvoyReverseHttp1Tls, null, workload));
        arms.Add(new($"yarp-reverse-http1-tls-{nameSuffix}", ProbeMode.YarpReverseHttp1Tls, null, workload));
        arms.Add(new($"twp-reverse-http2-cleartext-{nameSuffix}", ProbeMode.ReverseHttp2Cleartext, null, workload));
        if (nginxAvailable)
            arms.Add(new($"nginx-reverse-http2-{nameSuffix}", ProbeMode.NginxReverseHttp2, null, workload));
        if (haproxyAvailable)
            arms.Add(new($"haproxy-reverse-http2-{nameSuffix}", ProbeMode.HaproxyReverseHttp2, null, workload));
        if (envoyAvailable)
            arms.Add(new($"envoy-reverse-http2-{nameSuffix}", ProbeMode.EnvoyReverseHttp2, null, workload));
        arms.Add(new($"yarp-reverse-http2-{nameSuffix}", ProbeMode.YarpReverseHttp2, null, workload));
        if (includeHttp3)
        {
            arms.Add(new($"twp-reverse-http3-cleartext-{nameSuffix}", ProbeMode.ReverseHttp3Cleartext, null, workload));
            if (nginxHttp3Available)
                arms.Add(new($"nginx-reverse-http3-cleartext-{nameSuffix}", ProbeMode.NginxReverseHttp3Cleartext, null,
                    workload));
            if (haproxyHttp3Available)
                arms.Add(new($"haproxy-reverse-http3-cleartext-{nameSuffix}", ProbeMode.HaproxyReverseHttp3Cleartext,
                    null, workload));
            if (envoyHttp3Available)
                arms.Add(new($"envoy-reverse-http3-cleartext-{nameSuffix}", ProbeMode.EnvoyReverseHttp3Cleartext, null,
                    workload));
            arms.Add(new($"yarp-reverse-http3-cleartext-{nameSuffix}", ProbeMode.YarpReverseHttp3Cleartext, null,
                workload));
        }

        return arms;
    }

    private static void AppendComparePeers(List<ArmSpec> arms, string baseName, ProbeMode twpMode, ProbeMode yarpMode,
        ProbeMode? nginxMode, ProbeMode? haproxyMode, ProbeMode? envoyMode,
        bool nginxAvailable, bool haproxyAvailable, bool envoyAvailable,
        int? maxCached = null, WorkloadOptions? workload = null)
    {
        arms.Add(new($"twp-{baseName}", twpMode, maxCached, workload));
        if (nginxAvailable && nginxMode is { } nm)
            arms.Add(new($"nginx-{baseName}", nm, maxCached, workload));
        if (haproxyAvailable && haproxyMode is { } hm)
            arms.Add(new($"haproxy-{baseName}", hm, maxCached, workload));
        if (envoyAvailable && envoyMode is { } em)
            arms.Add(new($"envoy-{baseName}", em, maxCached, workload));
        arms.Add(new($"yarp-{baseName}", yarpMode, maxCached, workload));
    }

    private static void InsertTerminatePeersAfterYarp(List<ArmSpec> arms, ProbeMode yarpMode,
        bool nginxAvailable, string? nginxName, ProbeMode? nginxMode,
        bool haproxyAvailable, string haproxyName, ProbeMode haproxyMode,
        bool envoyAvailable, string envoyName, ProbeMode envoyMode,
        WorkloadOptions? workload = null)
    {
        var i = arms.FindIndex(a => a.Mode == yarpMode);
        if (i < 0)
            return;
        var insertAt = i + 1;
        if (nginxAvailable && nginxMode is { } nm && nginxName != null)
            arms.Insert(insertAt++, new(nginxName, nm, null, workload));
        if (haproxyAvailable)
            arms.Insert(insertAt++, new(haproxyName, haproxyMode, null, workload));
        if (envoyAvailable)
            arms.Insert(insertAt, new(envoyName, envoyMode, null, workload));
    }

    private static IReadOnlyList<ArmSpec> ResolveArms(ProbeMode mode, bool nginxAvailable,
        bool nginxHttp3Available = false, bool bombardierAvailable = false,
        bool haproxyAvailable = false, bool haproxyQuicAvailable = false,
        bool envoyAvailable = false, bool envoyHttp3Available = false)
    {
        return mode switch
        {
            ProbeMode.OriginDirect => [new("origin-direct", ProbeMode.OriginDirect, null)],
            ProbeMode.CompareSaturation => BuildSaturationArms(nginxAvailable, bombardierAvailable,
                nginxHttp3Available, haproxyAvailable, haproxyQuicAvailable, envoyAvailable, envoyHttp3Available),
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
            ProbeMode.NginxReverseHttp1ToHttps => nginxAvailable
                ? [new("nginx-reverse-http1-to-https", ProbeMode.NginxReverseHttp1ToHttps, null)]
                : [],
            ProbeMode.NginxReverseHttp1TlsToHttps => nginxAvailable
                ? [new("nginx-reverse-http1-tls-to-https", ProbeMode.NginxReverseHttp1TlsToHttps, null)]
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
            ProbeMode.NginxReverseHttp2ToHttpsHttp1 => nginxAvailable
                ? [new("nginx-reverse-http2-to-https-http1", ProbeMode.NginxReverseHttp2ToHttpsHttp1, null)]
                : [],
            ProbeMode.NginxReverseHttp3Cleartext => nginxHttp3Available
                ? [new("nginx-reverse-http3-cleartext", ProbeMode.NginxReverseHttp3Cleartext, null)]
                : [],
            ProbeMode.NginxReverseHttp3ToHttpsHttp1 => nginxHttp3Available
                ? [new("nginx-reverse-http3-to-https-http1", ProbeMode.NginxReverseHttp3ToHttpsHttp1, null)]
                : [],
            ProbeMode.HaproxyReverseHttp1 => haproxyAvailable
                ? [new("haproxy-reverse-http1", ProbeMode.HaproxyReverseHttp1, null)]
                : [],
            ProbeMode.HaproxyReverseHttp1Tls => haproxyAvailable
                ? [new("haproxy-reverse-http1-tls", ProbeMode.HaproxyReverseHttp1Tls, null)]
                : [],
            ProbeMode.HaproxyReverseHttp1ToHttps => haproxyAvailable
                ? [new("haproxy-reverse-http1-to-https", ProbeMode.HaproxyReverseHttp1ToHttps, null)]
                : [],
            ProbeMode.HaproxyReverseHttp1TlsToHttps => haproxyAvailable
                ? [new("haproxy-reverse-http1-tls-to-https", ProbeMode.HaproxyReverseHttp1TlsToHttps, null)]
                : [],
            ProbeMode.HaproxyReverseHttp2 => haproxyAvailable
                ? [new("haproxy-reverse-http2", ProbeMode.HaproxyReverseHttp2, null)]
                : [],
            ProbeMode.HaproxyReverseHttp2ToHttpsHttp1 => haproxyAvailable
                ? [new("haproxy-reverse-http2-to-https-http1", ProbeMode.HaproxyReverseHttp2ToHttpsHttp1, null)]
                : [],
            ProbeMode.HaproxyReverseHttp3Cleartext => haproxyQuicAvailable
                ? [new("haproxy-reverse-http3-cleartext", ProbeMode.HaproxyReverseHttp3Cleartext, null)]
                : [],
            ProbeMode.HaproxyReverseHttp3ToHttpsHttp1 => haproxyQuicAvailable
                ? [new("haproxy-reverse-http3-to-https-http1", ProbeMode.HaproxyReverseHttp3ToHttpsHttp1, null)]
                : [],
            ProbeMode.EnvoyReverseHttp1 => envoyAvailable
                ? [new("envoy-reverse-http1", ProbeMode.EnvoyReverseHttp1, null)]
                : [],
            ProbeMode.EnvoyReverseHttp1Tls => envoyAvailable
                ? [new("envoy-reverse-http1-tls", ProbeMode.EnvoyReverseHttp1Tls, null)]
                : [],
            ProbeMode.EnvoyReverseHttp1ToHttps => envoyAvailable
                ? [new("envoy-reverse-http1-to-https", ProbeMode.EnvoyReverseHttp1ToHttps, null)]
                : [],
            ProbeMode.EnvoyReverseHttp1TlsToHttps => envoyAvailable
                ? [new("envoy-reverse-http1-tls-to-https", ProbeMode.EnvoyReverseHttp1TlsToHttps, null)]
                : [],
            ProbeMode.EnvoyReverseHttp2 => envoyAvailable
                ? [new("envoy-reverse-http2", ProbeMode.EnvoyReverseHttp2, null)]
                : [],
            ProbeMode.EnvoyReverseHttp2ToHttpsHttp1 => envoyAvailable
                ? [new("envoy-reverse-http2-to-https-http1", ProbeMode.EnvoyReverseHttp2ToHttpsHttp1, null)]
                : [],
            ProbeMode.EnvoyReverseHttp3Cleartext => envoyHttp3Available
                ? [new("envoy-reverse-http3-cleartext", ProbeMode.EnvoyReverseHttp3Cleartext, null)]
                : [],
            ProbeMode.EnvoyReverseHttp3ToHttpsHttp1 => envoyHttp3Available
                ? [new("envoy-reverse-http3-to-https-http1", ProbeMode.EnvoyReverseHttp3ToHttpsHttp1, null)]
                : [],
            ProbeMode.CompareNginxHttps => BuildNginxHttpsArms(nginxAvailable, nginxHttp3Available),
            ProbeMode.CompareHaproxySmoke => BuildHaproxySmokeArms(haproxyAvailable, haproxyQuicAvailable),
            ProbeMode.CompareEnvoySmoke => BuildEnvoySmokeArms(envoyAvailable, envoyHttp3Available),
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
            ProbeMode.Compare => BuildCompareArms(nginxAvailable, haproxyAvailable, envoyAvailable),
            ProbeMode.CompareHttp2 => BuildCompareHttp2Arms(nginxAvailable, haproxyAvailable, envoyAvailable),
            ProbeMode.CompareTls => BuildCompareTlsArms(nginxAvailable, haproxyAvailable, envoyAvailable),
            ProbeMode.CompareTerminate => BuildCompareTerminateArms(nginxAvailable, nginxHttp3Available,
                haproxyAvailable, haproxyQuicAvailable, envoyAvailable, envoyHttp3Available),
            ProbeMode.CompareSame => BuildCompareSameArms(nginxAvailable, haproxyAvailable, envoyAvailable),
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
                ..(haproxyQuicAvailable
                    ? new ArmSpec[]
                    {
                        new("haproxy-reverse-http3-cleartext", ProbeMode.HaproxyReverseHttp3Cleartext, null)
                    }
                    : []),
                ..(envoyHttp3Available
                    ? new ArmSpec[]
                    {
                        new("envoy-reverse-http3-cleartext", ProbeMode.EnvoyReverseHttp3Cleartext, null)
                    }
                    : []),
                new("twp-reverse-http3-to-h2c", ProbeMode.ReverseHttp3ToH2c, null),
                new("yarp-reverse-http3-to-h2c", ProbeMode.YarpReverseHttp3ToH2c, null),
                new("twp-reverse-http3-to-http2", ProbeMode.ReverseHttp3ToHttp2, null),
                new("yarp-reverse-http3-to-http2", ProbeMode.YarpReverseHttp3ToHttp2, null),
                new("yarp-reverse-http3-to-http3", ProbeMode.YarpReverseHttp3ToHttp3, null)
            ],
            ProbeMode.CompareHttp3Cleartext => BuildCompareHttp3CleartextArms(nginxAvailable, nginxHttp3Available,
                haproxyAvailable, haproxyQuicAvailable, envoyAvailable, envoyHttp3Available),
            ProbeMode.CompareMitm =>
            [
                ..BuildMitmArms(),
                ..BuildMitmFullArms()
            ],
            ProbeMode.CompareMatrix => BuildFullMatrixArms(nginxAvailable, nginxHttp3Available, haproxyAvailable,
                haproxyQuicAvailable, envoyAvailable, envoyHttp3Available),
            ProbeMode.CompareCrossVersion => BuildFullMatrixArms(nginxAvailable, nginxHttp3Available,
                haproxyAvailable, haproxyQuicAvailable, envoyAvailable, envoyHttp3Available),
            ProbeMode.CompareEditions => BuildEditionArms(),
            ProbeMode.TwpCliReverseHttp1 => [new("twp-cli-reverse-http1", ProbeMode.TwpCliReverseHttp1, null)],
            ProbeMode.TwpCliReverseHttp1Tls =>
                [new("twp-cli-reverse-http1-tls", ProbeMode.TwpCliReverseHttp1Tls, null)],
            ProbeMode.TwpCliReverseHttp1Route =>
                [new("twp-cli-reverse-http1-route", ProbeMode.TwpCliReverseHttp1Route, null)],
            ProbeMode.TwpCliPlusBaseHttp1 =>
                [new("twp-cli-plus-base-http1", ProbeMode.TwpCliPlusBaseHttp1, null)],
            ProbeMode.TwpCliPlusCacheHttp1 =>
                [new("twp-cli-plus-cache-http1", ProbeMode.TwpCliPlusCacheHttp1, null)],
            ProbeMode.TwpCliInterceptHttp1 =>
                [new("twp-cli-intercept-http1", ProbeMode.TwpCliInterceptHttp1, null)],
            ProbeMode.TwpCliPlusWafHttp1 =>
                [new("twp-cli-plus-waf-http1", ProbeMode.TwpCliPlusWafHttp1, null)],
            ProbeMode.TwpCliPlusCidrHttp1 =>
                [new("twp-cli-plus-cidr-http1", ProbeMode.TwpCliPlusCidrHttp1, null)],
            ProbeMode.TwpCliPlusJwtHttp1 =>
                [new("twp-cli-plus-jwt-http1", ProbeMode.TwpCliPlusJwtHttp1, null)],
            ProbeMode.TwpCliPlusRateLimitHttp1 =>
                [new("twp-cli-plus-ratelimit-http1", ProbeMode.TwpCliPlusRateLimitHttp1, null)],
            ProbeMode.TwpCliPlusResilienceHttp1 =>
                [new("twp-cli-plus-resilience-http1", ProbeMode.TwpCliPlusResilienceHttp1, null)],
            ProbeMode.TwpCliPlusDiscoveryFileHttp1 =>
                [new("twp-cli-plus-discovery-file-http1", ProbeMode.TwpCliPlusDiscoveryFileHttp1, null,
                    RewriteDiscoveryMidRamp: true)],
            ProbeMode.TwpCliPlusMetricsScrapeHttp1 =>
                [new("twp-cli-plus-metrics-scrape-http1", ProbeMode.TwpCliPlusMetricsScrapeHttp1, null,
                    BackgroundControlPlaneScrape: true)],
            ProbeMode.TwpCliPlusCacheHitHttp1 =>
                [new("twp-cli-plus-cache-hit-http1", ProbeMode.TwpCliPlusCacheHitHttp1, null,
                    WarmCacheFirst: true)],
            ProbeMode.TwpCliStaticHttp1 =>
                [new("twp-cli-static-http1", ProbeMode.TwpCliStaticHttp1, null)],
            ProbeMode.TwpCliLoggingHttp1 =>
                [new("twp-cli-logging-http1", ProbeMode.TwpCliLoggingHttp1, null)],
            ProbeMode.TwpCliLbLeastTimeHttp1 =>
                [new("twp-cli-lb-leasttime-http1", ProbeMode.TwpCliLbLeastTimeHttp1, null)],
            ProbeMode.TwpCliDialectTwpHttp1 =>
                [new("twp-cli-dialect-twp-http1", ProbeMode.TwpCliDialectTwpHttp1, null)],
            ProbeMode.CompareProduct =>
            [
                ..BuildFullMatrixArms(nginxAvailable, nginxHttp3Available, haproxyAvailable, haproxyQuicAvailable,
                    envoyAvailable, envoyHttp3Available),
                ..BuildMitmArms(),
                ..BuildMitmFullArms()
            ],
            ProbeMode.CompareProductSmoke => BuildProductSmokeArms(),
            ProbeMode.CompareSpot => BuildSpotArms(),
            ProbeMode.CompareCeiling => BuildCompareCeilingArms(nginxAvailable, haproxyAvailable, envoyAvailable),
            ProbeMode.CompareBodies =>
            [
                ..HeavierReverseArms(nginxAvailable, nginxHttp3Available, haproxyAvailable, haproxyQuicAvailable,
                    envoyAvailable, envoyHttp3Available, WorkloadOptions.ForBodyGet(64 * 1024), "body64k"),
                ..HeavierReverseArms(nginxAvailable, nginxHttp3Available, haproxyAvailable, haproxyQuicAvailable,
                    envoyAvailable, envoyHttp3Available, WorkloadOptions.ForBodyGet(256 * 1024), "body256k")
            ],
            ProbeMode.ComparePost =>
                HeavierReverseArms(nginxAvailable, nginxHttp3Available, haproxyAvailable, haproxyQuicAvailable,
                    envoyAvailable, envoyHttp3Available, WorkloadOptions.ForPost(64 * 1024, 64 * 1024), "post64k"),
            ProbeMode.CompareLossy =>
                HeavierReverseArms(nginxAvailable, nginxHttp3Available, haproxyAvailable, haproxyQuicAvailable,
                    envoyAvailable, envoyHttp3Available,
                    WorkloadOptions.ForLossy(64 * 1024, 5, 1.0), "lossy"),
            ProbeMode.CompareTlsCost => BuildTlsCostArms(nginxAvailable, haproxyAvailable, envoyAvailable),
            ProbeMode.CompareArch => BuildArchArms(nginxAvailable, nginxHttp3Available, haproxyAvailable,
                haproxyQuicAvailable, envoyAvailable, envoyHttp3Available),
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

    /// <summary>
    /// TWP-only true MITM 5×5 + CONNECT. Same ProbeModes/wires as reverse twins, but
    /// <see cref="ArmSpec.EnableHttpInterception"/> so noop session handlers run (lite finish OK).
    /// </summary>
    private static IReadOnlyList<ArmSpec> BuildMitmArms()
    {
        const bool intercept = true;
        return
        [
            new("twp-mitm-http1", ProbeMode.ReverseHttp1, null, EnableHttpInterception: intercept),
            new("twp-mitm-http1-to-https", ProbeMode.ReverseHttp1ToHttps, null, EnableHttpInterception: intercept),
            new("twp-mitm-http1-plain-to-h2c", ProbeMode.ReverseHttp1PlainToH2c, null, EnableHttpInterception: intercept),
            new("twp-mitm-http1-plain-to-http2", ProbeMode.ReverseHttp1PlainToHttp2, null, EnableHttpInterception: intercept),
            new("twp-mitm-http1-plain-to-http3", ProbeMode.ReverseHttp1PlainToHttp3, null, EnableHttpInterception: intercept),
            new("twp-mitm-http1-tls", ProbeMode.ReverseHttp1Tls, null, EnableHttpInterception: intercept),
            new("twp-mitm-http1-tls-to-https", ProbeMode.ReverseHttp1Mitm, null, EnableHttpInterception: intercept),
            new("twp-mitm-http1-to-h2c", ProbeMode.ReverseHttp1ToH2c, null, EnableHttpInterception: intercept),
            new("twp-mitm-http11-to-http2", ProbeMode.ReverseHttp11ToHttp2, null, EnableHttpInterception: intercept),
            new("twp-mitm-http1-to-http3", ProbeMode.ReverseHttp1ToHttp3, null, EnableHttpInterception: intercept),
            new("twp-mitm-h2c-to-h1", ProbeMode.ReverseH2cToH1, null, EnableHttpInterception: intercept),
            new("twp-mitm-h2c-to-https", ProbeMode.ReverseH2cToHttps, null, EnableHttpInterception: intercept),
            new("twp-mitm-h2c-to-h2c", ProbeMode.ReverseH2cToH2c, null, EnableHttpInterception: intercept),
            new("twp-mitm-h2c", ProbeMode.ReverseH2c, null, EnableHttpInterception: intercept),
            new("twp-mitm-h2c-to-h3", ProbeMode.ReverseH2cToH3, null, EnableHttpInterception: intercept),
            new("twp-mitm-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null, EnableHttpInterception: intercept),
            new("twp-mitm-http2-to-http1", ProbeMode.MitmHttp2ToHttp1, null, EnableHttpInterception: intercept),
            new("twp-mitm-http2-to-h2c", ProbeMode.ReverseHttp2ToH2c, null, EnableHttpInterception: intercept),
            new("twp-mitm-http2", ProbeMode.ReverseHttp2, null, EnableHttpInterception: intercept),
            new("twp-mitm-http2-to-http3", ProbeMode.ReverseHttp2ToHttp3, null, EnableHttpInterception: intercept),
            new("twp-mitm-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null, EnableHttpInterception: intercept),
            new("twp-mitm-http3-to-http1", ProbeMode.MitmHttp3ToHttp1, null, EnableHttpInterception: intercept),
            new("twp-mitm-http3-to-h2c", ProbeMode.ReverseHttp3ToH2c, null, EnableHttpInterception: intercept),
            new("twp-mitm-http3-to-http2", ProbeMode.ReverseHttp3ToHttp2, null, EnableHttpInterception: intercept),
            new("twp-mitm-http3", ProbeMode.ReverseHttp3, null, EnableHttpInterception: intercept),
            new("twp-mitm-https-connect", ProbeMode.HttpsMitm, null, EnableHttpInterception: intercept)
        ];
    }

    /// <summary>
    /// Same wires as <see cref="BuildMitmArms"/> but handlers mutate headers so unchanged-lite /
    /// compressed-relay is refused (full session re-encode). Names are <c>twp-mitm-full-*</c>.
    /// </summary>
    private static IReadOnlyList<ArmSpec> BuildMitmFullArms()
    {
        const bool intercept = true;
        const bool mutate = true;
        return
        [
            new("twp-mitm-full-http1", ProbeMode.ReverseHttp1, null, EnableHttpInterception: intercept,
                MutateHttpInterception: mutate),
            new("twp-mitm-full-http1-to-https", ProbeMode.ReverseHttp1ToHttps, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-mitm-full-http1-plain-to-h2c", ProbeMode.ReverseHttp1PlainToH2c, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-mitm-full-http1-plain-to-http2", ProbeMode.ReverseHttp1PlainToHttp2, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-mitm-full-http1-plain-to-http3", ProbeMode.ReverseHttp1PlainToHttp3, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-mitm-full-http1-tls", ProbeMode.ReverseHttp1Tls, null, EnableHttpInterception: intercept,
                MutateHttpInterception: mutate),
            new("twp-mitm-full-http1-tls-to-https", ProbeMode.ReverseHttp1Mitm, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-mitm-full-http1-to-h2c", ProbeMode.ReverseHttp1ToH2c, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-mitm-full-http11-to-http2", ProbeMode.ReverseHttp11ToHttp2, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-mitm-full-http1-to-http3", ProbeMode.ReverseHttp1ToHttp3, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-mitm-full-h2c-to-h1", ProbeMode.ReverseH2cToH1, null, EnableHttpInterception: intercept,
                MutateHttpInterception: mutate),
            new("twp-mitm-full-h2c-to-https", ProbeMode.ReverseH2cToHttps, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-mitm-full-h2c-to-h2c", ProbeMode.ReverseH2cToH2c, null, EnableHttpInterception: intercept,
                MutateHttpInterception: mutate),
            new("twp-mitm-full-h2c", ProbeMode.ReverseH2c, null, EnableHttpInterception: intercept,
                MutateHttpInterception: mutate),
            new("twp-mitm-full-h2c-to-h3", ProbeMode.ReverseH2cToH3, null, EnableHttpInterception: intercept,
                MutateHttpInterception: mutate),
            new("twp-mitm-full-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-mitm-full-http2-to-http1", ProbeMode.MitmHttp2ToHttp1, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-mitm-full-http2-to-h2c", ProbeMode.ReverseHttp2ToH2c, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-mitm-full-http2", ProbeMode.ReverseHttp2, null, EnableHttpInterception: intercept,
                MutateHttpInterception: mutate),
            new("twp-mitm-full-http2-to-http3", ProbeMode.ReverseHttp2ToHttp3, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-mitm-full-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-mitm-full-http3-to-http1", ProbeMode.MitmHttp3ToHttp1, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-mitm-full-http3-to-h2c", ProbeMode.ReverseHttp3ToH2c, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-mitm-full-http3-to-http2", ProbeMode.ReverseHttp3ToHttp2, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-mitm-full-http3", ProbeMode.ReverseHttp3, null, EnableHttpInterception: intercept,
                MutateHttpInterception: mutate),
            new("twp-mitm-full-https-connect", ProbeMode.HttpsMitm, null, EnableHttpInterception: intercept,
                MutateHttpInterception: mutate)
        ];
    }

    /// <summary>Local PR2 spot matrix: reverse + Full MITM pairs and YARP reverse peers.</summary>
    private static IReadOnlyList<ArmSpec> BuildSpotArms()
    {
        const bool intercept = true;
        const bool mutate = true;
        return
        [
            new("twp-reverse-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null),
            new("twp-mitm-full-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-reverse-http3-to-https-http1", ProbeMode.MitmHttp3ToHttp1, null),
            new("twp-mitm-full-http3-to-http1", ProbeMode.MitmHttp3ToHttp1, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-reverse-http3", ProbeMode.ReverseHttp3, null),
            new("twp-mitm-full-http3", ProbeMode.ReverseHttp3, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-reverse-http1", ProbeMode.ReverseHttp1, null),
            new("twp-mitm-full-http1", ProbeMode.ReverseHttp1, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-reverse-h2c-to-h2c", ProbeMode.ReverseH2cToH2c, null),
            new("twp-mitm-full-h2c-to-h2c", ProbeMode.ReverseH2cToH2c, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-reverse-http2-to-h2c", ProbeMode.ReverseHttp2ToH2c, null),
            new("twp-mitm-full-http2-to-h2c", ProbeMode.ReverseHttp2ToH2c, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-reverse-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null),
            new("twp-mitm-full-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("twp-reverse-http2", ProbeMode.ReverseHttp2, null),
            new("twp-mitm-full-http2", ProbeMode.ReverseHttp2, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("yarp-reverse-http3-to-https-http1", ProbeMode.YarpReverseHttp3ToHttpsHttp1, null),
            new("yarp-reverse-http3-to-http3", ProbeMode.YarpReverseHttp3ToHttp3, null)
        ];
    }

    /// <summary>
    /// Minimal arm set for <c>validate-compare-product-gates.ps1</c> (every Lite/Full/Reverse
    /// pair the script scores, plus YARP H3 reverse peers). Intended for Mac-only GHA smoke.
    /// </summary>
    private static IReadOnlyList<ArmSpec> BuildProductSmokeArms()
    {
        const bool intercept = true;
        const bool mutate = true;
        return
        [
            // H3→H1 plain
            new("twp-reverse-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null),
            new("twp-mitm-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null,
                EnableHttpInterception: intercept),
            new("twp-mitm-full-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            // H3→H1 TLS (previous Mac gate failure class)
            new("twp-reverse-http3-to-https-http1", ProbeMode.MitmHttp3ToHttp1, null),
            new("twp-mitm-http3-to-http1", ProbeMode.MitmHttp3ToHttp1, null,
                EnableHttpInterception: intercept),
            new("twp-mitm-full-http3-to-http1", ProbeMode.MitmHttp3ToHttp1, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("yarp-reverse-http3-to-https-http1", ProbeMode.YarpReverseHttp3ToHttpsHttp1, null),
            // H3→H3
            new("twp-reverse-http3", ProbeMode.ReverseHttp3, null),
            new("twp-mitm-http3", ProbeMode.ReverseHttp3, null, EnableHttpInterception: intercept),
            new("twp-mitm-full-http3", ProbeMode.ReverseHttp3, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            new("yarp-reverse-http3-to-http3", ProbeMode.YarpReverseHttp3ToHttp3, null),
            // H1 plain
            new("twp-reverse-http1", ProbeMode.ReverseHttp1, null),
            new("twp-mitm-http1", ProbeMode.ReverseHttp1, null, EnableHttpInterception: intercept),
            new("twp-mitm-full-http1", ProbeMode.ReverseHttp1, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            // H2 h2c→h2c
            new("twp-reverse-h2c-to-h2c", ProbeMode.ReverseH2cToH2c, null),
            new("twp-mitm-h2c-to-h2c", ProbeMode.ReverseH2cToH2c, null, EnableHttpInterception: intercept),
            new("twp-mitm-full-h2c-to-h2c", ProbeMode.ReverseH2cToH2c, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            // H2 TLS→h2c
            new("twp-reverse-http2-to-h2c", ProbeMode.ReverseHttp2ToH2c, null),
            new("twp-mitm-http2-to-h2c", ProbeMode.ReverseHttp2ToH2c, null, EnableHttpInterception: intercept),
            new("twp-mitm-full-http2-to-h2c", ProbeMode.ReverseHttp2ToH2c, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            // H2 plain
            new("twp-reverse-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null),
            new("twp-mitm-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null,
                EnableHttpInterception: intercept),
            new("twp-mitm-full-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate),
            // H2 TLS
            new("twp-reverse-http2", ProbeMode.ReverseHttp2, null),
            new("twp-mitm-http2", ProbeMode.ReverseHttp2, null, EnableHttpInterception: intercept),
            new("twp-mitm-full-http2", ProbeMode.ReverseHttp2, null,
                EnableHttpInterception: intercept, MutateHttpInterception: mutate)
        ];
    }

    /// <summary>
    /// Library H1 baselines plus shipped CLI / Plus / Intercept edition arms.
    /// </summary>
    private static IReadOnlyList<ArmSpec> BuildEditionArms() =>
    [
        new("twp-reverse-http1", ProbeMode.ReverseHttp1, null),
        new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null),
        new("twp-cli-reverse-http1", ProbeMode.TwpCliReverseHttp1, null),
        new("twp-cli-reverse-http1-tls", ProbeMode.TwpCliReverseHttp1Tls, null),
        new("twp-cli-reverse-http1-route", ProbeMode.TwpCliReverseHttp1Route, null),
        new("twp-cli-plus-base-http1", ProbeMode.TwpCliPlusBaseHttp1, null),
        new("twp-cli-plus-cache-http1", ProbeMode.TwpCliPlusCacheHttp1, null),
        new("twp-cli-intercept-http1", ProbeMode.TwpCliInterceptHttp1, null),
        new("twp-cli-plus-waf-http1", ProbeMode.TwpCliPlusWafHttp1, null),
        new("twp-cli-plus-cidr-http1", ProbeMode.TwpCliPlusCidrHttp1, null),
        new("twp-cli-plus-jwt-http1", ProbeMode.TwpCliPlusJwtHttp1, null),
        new("twp-cli-plus-ratelimit-http1", ProbeMode.TwpCliPlusRateLimitHttp1, null),
        new("twp-cli-plus-resilience-http1", ProbeMode.TwpCliPlusResilienceHttp1, null),
        new("twp-cli-plus-discovery-file-http1", ProbeMode.TwpCliPlusDiscoveryFileHttp1, null,
            RewriteDiscoveryMidRamp: true),
        new("twp-cli-plus-metrics-scrape-http1", ProbeMode.TwpCliPlusMetricsScrapeHttp1, null,
            BackgroundControlPlaneScrape: true),
        new("twp-cli-plus-cache-hit-http1", ProbeMode.TwpCliPlusCacheHitHttp1, null, WarmCacheFirst: true),
        new("twp-cli-static-http1", ProbeMode.TwpCliStaticHttp1, null),
        new("twp-cli-logging-http1", ProbeMode.TwpCliLoggingHttp1, null),
        new("twp-cli-lb-leasttime-http1", ProbeMode.TwpCliLbLeastTimeHttp1, null),
        new("twp-cli-dialect-twp-http1", ProbeMode.TwpCliDialectTwpHttp1, null)
    ];

    private static IReadOnlyList<ArmSpec> BuildFullMatrixArms(bool nginxAvailable, bool nginxHttp3Available,
        bool haproxyAvailable, bool haproxyHttp3Available, bool envoyAvailable, bool envoyHttp3Available)
    {
        // Full 5×5 Client×Origin reverse cartesian: TWP + YARP for each cell; native peers after YARP.
        var arms = new List<ArmSpec>
        {
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
            new("twp-reverse-http2-to-https-http1", ProbeMode.MitmHttp2ToHttp1, null),
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
            new("twp-reverse-http3-to-https-http1", ProbeMode.MitmHttp3ToHttp1, null),
            new("yarp-reverse-http3-to-https-http1", ProbeMode.YarpReverseHttp3ToHttpsHttp1, null),
            new("twp-reverse-http3-to-h2c", ProbeMode.ReverseHttp3ToH2c, null),
            new("yarp-reverse-http3-to-h2c", ProbeMode.YarpReverseHttp3ToH2c, null),
            new("twp-reverse-http3-to-http2", ProbeMode.ReverseHttp3ToHttp2, null),
            new("yarp-reverse-http3-to-http2", ProbeMode.YarpReverseHttp3ToHttp2, null),
            new("twp-reverse-http3", ProbeMode.ReverseHttp3, null),
            new("yarp-reverse-http3-to-http3", ProbeMode.YarpReverseHttp3ToHttp3, null)
        };

        InsertTerminatePeersAfterYarp(arms, ProbeMode.YarpReverseHttp1, nginxAvailable,
            "nginx-reverse-http1", ProbeMode.NginxReverseHttp1, haproxyAvailable,
            "haproxy-reverse-http1", ProbeMode.HaproxyReverseHttp1, envoyAvailable,
            "envoy-reverse-http1", ProbeMode.EnvoyReverseHttp1);
        InsertTerminatePeersAfterYarp(arms, ProbeMode.YarpReverseHttp1ToHttps, nginxAvailable,
            "nginx-reverse-http1-to-https", ProbeMode.NginxReverseHttp1ToHttps, haproxyAvailable,
            "haproxy-reverse-http1-to-https", ProbeMode.HaproxyReverseHttp1ToHttps, envoyAvailable,
            "envoy-reverse-http1-to-https", ProbeMode.EnvoyReverseHttp1ToHttps);
        InsertTerminatePeersAfterYarp(arms, ProbeMode.YarpReverseHttp1Tls, nginxAvailable,
            "nginx-reverse-http1-tls", ProbeMode.NginxReverseHttp1Tls, haproxyAvailable,
            "haproxy-reverse-http1-tls", ProbeMode.HaproxyReverseHttp1Tls, envoyAvailable,
            "envoy-reverse-http1-tls", ProbeMode.EnvoyReverseHttp1Tls);
        InsertTerminatePeersAfterYarp(arms, ProbeMode.YarpReverseHttp1TlsToHttps, nginxAvailable,
            "nginx-reverse-http1-tls-to-https", ProbeMode.NginxReverseHttp1TlsToHttps, haproxyAvailable,
            "haproxy-reverse-http1-tls-to-https", ProbeMode.HaproxyReverseHttp1TlsToHttps, envoyAvailable,
            "envoy-reverse-http1-tls-to-https", ProbeMode.EnvoyReverseHttp1TlsToHttps);
        InsertTerminatePeersAfterYarp(arms, ProbeMode.YarpReverseHttp2, nginxAvailable,
            "nginx-reverse-http2", ProbeMode.NginxReverseHttp2, haproxyAvailable,
            "haproxy-reverse-http2", ProbeMode.HaproxyReverseHttp2, envoyAvailable,
            "envoy-reverse-http2", ProbeMode.EnvoyReverseHttp2);
        InsertTerminatePeersAfterYarp(arms, ProbeMode.YarpReverseHttp2ToHttpsHttp1, nginxAvailable,
            "nginx-reverse-http2-to-https-http1", ProbeMode.NginxReverseHttp2ToHttpsHttp1, haproxyAvailable,
            "haproxy-reverse-http2-to-https-http1", ProbeMode.HaproxyReverseHttp2ToHttpsHttp1, envoyAvailable,
            "envoy-reverse-http2-to-https-http1", ProbeMode.EnvoyReverseHttp2ToHttpsHttp1);
        if (nginxHttp3Available || haproxyHttp3Available || envoyHttp3Available)
        {
            var i = arms.FindIndex(a => a.Mode == ProbeMode.YarpReverseHttp3Cleartext);
            if (i >= 0)
            {
                var insertAt = i + 1;
                if (nginxHttp3Available)
                    arms.Insert(insertAt++,
                        new("nginx-reverse-http3-cleartext", ProbeMode.NginxReverseHttp3Cleartext, null));
                if (haproxyHttp3Available)
                    arms.Insert(insertAt++,
                        new("haproxy-reverse-http3-cleartext", ProbeMode.HaproxyReverseHttp3Cleartext, null));
                if (envoyHttp3Available)
                    arms.Insert(insertAt,
                        new("envoy-reverse-http3-cleartext", ProbeMode.EnvoyReverseHttp3Cleartext, null));
            }

            i = arms.FindIndex(a => a.Mode == ProbeMode.YarpReverseHttp3ToHttpsHttp1);
            if (i >= 0)
            {
                var insertAt = i + 1;
                if (nginxHttp3Available)
                    arms.Insert(insertAt++,
                        new("nginx-reverse-http3-to-https-http1", ProbeMode.NginxReverseHttp3ToHttpsHttp1, null));
                if (haproxyHttp3Available)
                    arms.Insert(insertAt++,
                        new("haproxy-reverse-http3-to-https-http1", ProbeMode.HaproxyReverseHttp3ToHttpsHttp1, null));
                if (envoyHttp3Available)
                    arms.Insert(insertAt,
                        new("envoy-reverse-http3-to-https-http1", ProbeMode.EnvoyReverseHttp3ToHttpsHttp1, null));
            }
        }

        return arms;
    }

    private static IReadOnlyList<ArmSpec> BuildNginxHttpsArms(bool nginxAvailable, bool nginxHttp3Available)
    {
        // Peer TWP+YARP on the same wires so local/GHA smoke can confirm ÷nginx ratios.
        var arms = new List<ArmSpec>
        {
            new("twp-reverse-http1-to-https", ProbeMode.ReverseHttp1ToHttps, null),
            new("yarp-reverse-http1-to-https", ProbeMode.YarpReverseHttp1ToHttps, null)
        };
        if (nginxAvailable)
            arms.Add(new("nginx-reverse-http1-to-https", ProbeMode.NginxReverseHttp1ToHttps, null));

        arms.Add(new("twp-reverse-http1-mitm", ProbeMode.ReverseHttp1Mitm, null));
        arms.Add(new("yarp-reverse-http1-tls-to-https", ProbeMode.YarpReverseHttp1TlsToHttps, null));
        if (nginxAvailable)
            arms.Add(new("nginx-reverse-http1-tls-to-https", ProbeMode.NginxReverseHttp1TlsToHttps, null));

        arms.Add(new("twp-reverse-http2-to-https-http1", ProbeMode.MitmHttp2ToHttp1, null));
        arms.Add(new("yarp-reverse-http2-to-https-http1", ProbeMode.YarpReverseHttp2ToHttpsHttp1, null));
        if (nginxAvailable)
            arms.Add(new("nginx-reverse-http2-to-https-http1", ProbeMode.NginxReverseHttp2ToHttpsHttp1, null));

        arms.Add(new("twp-reverse-http3-to-https-http1", ProbeMode.MitmHttp3ToHttp1, null));
        arms.Add(new("yarp-reverse-http3-to-https-http1", ProbeMode.YarpReverseHttp3ToHttpsHttp1, null));
        if (nginxHttp3Available)
            arms.Add(new("nginx-reverse-http3-to-https-http1", ProbeMode.NginxReverseHttp3ToHttpsHttp1, null));

        return arms;
    }

    private static IReadOnlyList<ArmSpec> BuildHaproxySmokeArms(bool haproxyAvailable, bool haproxyHttp3Available)
    {
        if (!haproxyAvailable)
            return [];
        var arms = new List<ArmSpec>
        {
            new("twp-reverse-http1", ProbeMode.ReverseHttp1, null),
            new("haproxy-reverse-http1", ProbeMode.HaproxyReverseHttp1, null),
            new("yarp-reverse-http1", ProbeMode.YarpReverseHttp1, null),
            new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null),
            new("haproxy-reverse-http1-tls", ProbeMode.HaproxyReverseHttp1Tls, null),
            new("yarp-reverse-http1-tls", ProbeMode.YarpReverseHttp1Tls, null),
            new("twp-reverse-http1-to-https", ProbeMode.ReverseHttp1ToHttps, null),
            new("haproxy-reverse-http1-to-https", ProbeMode.HaproxyReverseHttp1ToHttps, null),
            new("yarp-reverse-http1-to-https", ProbeMode.YarpReverseHttp1ToHttps, null),
            new("twp-reverse-http1-mitm", ProbeMode.ReverseHttp1Mitm, null),
            new("haproxy-reverse-http1-tls-to-https", ProbeMode.HaproxyReverseHttp1TlsToHttps, null),
            new("yarp-reverse-http1-tls-to-https", ProbeMode.YarpReverseHttp1TlsToHttps, null),
            new("twp-reverse-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null),
            new("haproxy-reverse-http2", ProbeMode.HaproxyReverseHttp2, null),
            new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null),
            new("twp-reverse-http2-to-https-http1", ProbeMode.MitmHttp2ToHttp1, null),
            new("haproxy-reverse-http2-to-https-http1", ProbeMode.HaproxyReverseHttp2ToHttpsHttp1, null),
            new("yarp-reverse-http2-to-https-http1", ProbeMode.YarpReverseHttp2ToHttpsHttp1, null)
        };
        if (haproxyHttp3Available)
        {
            arms.Add(new("twp-reverse-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null));
            arms.Add(new("haproxy-reverse-http3-cleartext", ProbeMode.HaproxyReverseHttp3Cleartext, null));
            arms.Add(new("yarp-reverse-http3-cleartext", ProbeMode.YarpReverseHttp3Cleartext, null));
            arms.Add(new("twp-reverse-http3-to-https-http1", ProbeMode.MitmHttp3ToHttp1, null));
            arms.Add(new("haproxy-reverse-http3-to-https-http1", ProbeMode.HaproxyReverseHttp3ToHttpsHttp1, null));
            arms.Add(new("yarp-reverse-http3-to-https-http1", ProbeMode.YarpReverseHttp3ToHttpsHttp1, null));
        }

        return arms;
    }

    private static IReadOnlyList<ArmSpec> BuildEnvoySmokeArms(bool envoyAvailable, bool envoyHttp3Available)
    {
        if (!envoyAvailable)
            return [];
        var arms = new List<ArmSpec>
        {
            new("twp-reverse-http1", ProbeMode.ReverseHttp1, null),
            new("envoy-reverse-http1", ProbeMode.EnvoyReverseHttp1, null),
            new("yarp-reverse-http1", ProbeMode.YarpReverseHttp1, null),
            new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null),
            new("envoy-reverse-http1-tls", ProbeMode.EnvoyReverseHttp1Tls, null),
            new("yarp-reverse-http1-tls", ProbeMode.YarpReverseHttp1Tls, null),
            new("twp-reverse-http1-to-https", ProbeMode.ReverseHttp1ToHttps, null),
            new("envoy-reverse-http1-to-https", ProbeMode.EnvoyReverseHttp1ToHttps, null),
            new("yarp-reverse-http1-to-https", ProbeMode.YarpReverseHttp1ToHttps, null),
            new("twp-reverse-http1-mitm", ProbeMode.ReverseHttp1Mitm, null),
            new("envoy-reverse-http1-tls-to-https", ProbeMode.EnvoyReverseHttp1TlsToHttps, null),
            new("yarp-reverse-http1-tls-to-https", ProbeMode.YarpReverseHttp1TlsToHttps, null),
            new("twp-reverse-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null),
            new("envoy-reverse-http2", ProbeMode.EnvoyReverseHttp2, null),
            new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null),
            new("twp-reverse-http2-to-https-http1", ProbeMode.MitmHttp2ToHttp1, null),
            new("envoy-reverse-http2-to-https-http1", ProbeMode.EnvoyReverseHttp2ToHttpsHttp1, null),
            new("yarp-reverse-http2-to-https-http1", ProbeMode.YarpReverseHttp2ToHttpsHttp1, null)
        };
        if (envoyHttp3Available)
        {
            arms.Add(new("twp-reverse-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null));
            arms.Add(new("envoy-reverse-http3-cleartext", ProbeMode.EnvoyReverseHttp3Cleartext, null));
            arms.Add(new("yarp-reverse-http3-cleartext", ProbeMode.YarpReverseHttp3Cleartext, null));
            arms.Add(new("twp-reverse-http3-to-https-http1", ProbeMode.MitmHttp3ToHttp1, null));
            arms.Add(new("envoy-reverse-http3-to-https-http1", ProbeMode.EnvoyReverseHttp3ToHttpsHttp1, null));
            arms.Add(new("yarp-reverse-http3-to-https-http1", ProbeMode.YarpReverseHttp3ToHttpsHttp1, null));
        }

        return arms;
    }

    private static IReadOnlyList<ArmSpec> BuildCompareArms(bool nginxAvailable, bool haproxyAvailable,
        bool envoyAvailable)
    {
        var arms = new List<ArmSpec>();
        AppendComparePeers(arms, "reverse-http1", ProbeMode.ReverseHttp1, ProbeMode.YarpReverseHttp1,
            ProbeMode.NginxReverseHttp1, ProbeMode.HaproxyReverseHttp1, ProbeMode.EnvoyReverseHttp1,
            nginxAvailable, haproxyAvailable, envoyAvailable);
        arms.Add(new("twp-https-mitm", ProbeMode.HttpsMitm, null));
        return arms;
    }

    private static IReadOnlyList<ArmSpec> BuildCompareHttp2Arms(bool nginxAvailable, bool haproxyAvailable,
        bool envoyAvailable)
    {
        var arms = new List<ArmSpec>();
        AppendComparePeers(arms, "reverse-http2", ProbeMode.ReverseHttp2, ProbeMode.YarpReverseHttp2,
            ProbeMode.NginxReverseHttp2, ProbeMode.HaproxyReverseHttp2, ProbeMode.EnvoyReverseHttp2,
            nginxAvailable, haproxyAvailable, envoyAvailable);
        arms.Add(new("twp-reverse-http3", ProbeMode.ReverseHttp3, null));
        return arms;
    }

    private static IReadOnlyList<ArmSpec> BuildCompareTlsArms(bool nginxAvailable, bool haproxyAvailable,
        bool envoyAvailable)
    {
        var arms = new List<ArmSpec>();
        AppendComparePeers(arms, "reverse-http1-tls", ProbeMode.ReverseHttp1Tls, ProbeMode.YarpReverseHttp1Tls,
            ProbeMode.NginxReverseHttp1Tls, ProbeMode.HaproxyReverseHttp1Tls, ProbeMode.EnvoyReverseHttp1Tls,
            nginxAvailable, haproxyAvailable, envoyAvailable);
        AppendComparePeers(arms, "reverse-http2", ProbeMode.ReverseHttp2, ProbeMode.YarpReverseHttp2,
            ProbeMode.NginxReverseHttp2, ProbeMode.HaproxyReverseHttp2, ProbeMode.EnvoyReverseHttp2,
            nginxAvailable, haproxyAvailable, envoyAvailable);
        arms.Add(new("twp-reverse-http3", ProbeMode.ReverseHttp3, null));
        return arms;
    }

    private static IReadOnlyList<ArmSpec> BuildCompareTerminateArms(bool nginxAvailable, bool nginxHttp3Available,
        bool haproxyAvailable, bool haproxyHttp3Available, bool envoyAvailable, bool envoyHttp3Available)
    {
        var arms = new List<ArmSpec>();
        AppendComparePeers(arms, "reverse-http1-tls", ProbeMode.ReverseHttp1Tls, ProbeMode.YarpReverseHttp1Tls,
            ProbeMode.NginxReverseHttp1Tls, ProbeMode.HaproxyReverseHttp1Tls, ProbeMode.EnvoyReverseHttp1Tls,
            nginxAvailable, haproxyAvailable, envoyAvailable);
        arms.Add(new("twp-reverse-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null));
        arms.Add(new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null));
        arms.Add(new("twp-reverse-h2c-to-h1", ProbeMode.ReverseH2cToH1, null));
        arms.Add(new("yarp-reverse-h2c-to-h1", ProbeMode.YarpReverseH2cToH1, null));
        if (nginxAvailable)
            arms.Add(new("nginx-reverse-http2", ProbeMode.NginxReverseHttp2, null));
        if (haproxyAvailable)
            arms.Add(new("haproxy-reverse-http2", ProbeMode.HaproxyReverseHttp2, null));
        if (envoyAvailable)
            arms.Add(new("envoy-reverse-http2", ProbeMode.EnvoyReverseHttp2, null));
        arms.Add(new("twp-reverse-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null));
        if (nginxHttp3Available)
            arms.Add(new("nginx-reverse-http3-cleartext", ProbeMode.NginxReverseHttp3Cleartext, null));
        if (haproxyHttp3Available)
            arms.Add(new("haproxy-reverse-http3-cleartext", ProbeMode.HaproxyReverseHttp3Cleartext, null));
        if (envoyHttp3Available)
            arms.Add(new("envoy-reverse-http3-cleartext", ProbeMode.EnvoyReverseHttp3Cleartext, null));
        arms.Add(new("yarp-reverse-http3-cleartext", ProbeMode.YarpReverseHttp3Cleartext, null));
        return arms;
    }

    private static IReadOnlyList<ArmSpec> BuildCompareSameArms(bool nginxAvailable, bool haproxyAvailable,
        bool envoyAvailable)
    {
        var arms = new List<ArmSpec>();
        AppendComparePeers(arms, "reverse-http1", ProbeMode.ReverseHttp1, ProbeMode.YarpReverseHttp1,
            ProbeMode.NginxReverseHttp1, ProbeMode.HaproxyReverseHttp1, ProbeMode.EnvoyReverseHttp1,
            nginxAvailable, haproxyAvailable, envoyAvailable);
        AppendComparePeers(arms, "reverse-http1-tls", ProbeMode.ReverseHttp1Tls, ProbeMode.YarpReverseHttp1Tls,
            ProbeMode.NginxReverseHttp1Tls, ProbeMode.HaproxyReverseHttp1Tls, ProbeMode.EnvoyReverseHttp1Tls,
            nginxAvailable, haproxyAvailable, envoyAvailable);
        arms.Add(new("twp-reverse-http1-to-https", ProbeMode.ReverseHttp1ToHttps, null));
        arms.Add(new("yarp-reverse-http1-to-https", ProbeMode.YarpReverseHttp1ToHttps, null));
        arms.Add(new("twp-https-mitm", ProbeMode.HttpsMitm, null));
        arms.Add(new("twp-reverse-http2", ProbeMode.ReverseHttp2, null));
        arms.Add(new("twp-reverse-h2c-to-h2c", ProbeMode.ReverseH2cToH2c, null));
        arms.Add(new("yarp-reverse-h2c-to-h2c", ProbeMode.YarpReverseH2cToH2c, null));
        arms.Add(new("twp-reverse-h2c", ProbeMode.ReverseH2c, null));
        arms.Add(new("yarp-reverse-h2c", ProbeMode.YarpReverseH2c, null));
        if (nginxAvailable)
            arms.Add(new("nginx-reverse-http2", ProbeMode.NginxReverseHttp2, null));
        if (haproxyAvailable)
            arms.Add(new("haproxy-reverse-http2", ProbeMode.HaproxyReverseHttp2, null));
        if (envoyAvailable)
            arms.Add(new("envoy-reverse-http2", ProbeMode.EnvoyReverseHttp2, null));
        arms.Add(new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null));
        arms.Add(new("twp-reverse-http3", ProbeMode.ReverseHttp3, null));
        arms.Add(new("yarp-reverse-http3-to-http3", ProbeMode.YarpReverseHttp3ToHttp3, null));
        return arms;
    }

    private static IReadOnlyList<ArmSpec> BuildCompareHttp3CleartextArms(bool nginxAvailable,
        bool nginxHttp3Available, bool haproxyAvailable, bool haproxyHttp3Available, bool envoyAvailable,
        bool envoyHttp3Available)
    {
        var arms = new List<ArmSpec>
        {
            new("twp-reverse-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null),
            new("yarp-reverse-http3-cleartext", ProbeMode.YarpReverseHttp3Cleartext, null)
        };
        if (nginxHttp3Available)
            arms.Add(new("nginx-reverse-http3-cleartext", ProbeMode.NginxReverseHttp3Cleartext, null));
        if (haproxyHttp3Available)
            arms.Add(new("haproxy-reverse-http3-cleartext", ProbeMode.HaproxyReverseHttp3Cleartext, null));
        if (envoyHttp3Available)
            arms.Add(new("envoy-reverse-http3-cleartext", ProbeMode.EnvoyReverseHttp3Cleartext, null));
        return arms;
    }

    private static IReadOnlyList<ArmSpec> BuildCompareCeilingArms(bool nginxAvailable, bool haproxyAvailable,
        bool envoyAvailable)
    {
        var arms = new List<ArmSpec>
        {
            new("twp-reverse-http1", ProbeMode.ReverseHttp1, null),
            new("bare-reverse-http1", ProbeMode.BareReverseHttp1, null),
            new("yarp-reverse-http1", ProbeMode.YarpReverseHttp1, null),
            new("twp-reverse-http1-tls", ProbeMode.ReverseHttp1Tls, null),
            new("bare-reverse-http1-tls", ProbeMode.BareReverseHttp1Tls, null),
            new("yarp-reverse-http1-tls", ProbeMode.YarpReverseHttp1Tls, null),
            new("twp-reverse-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null),
            new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null)
        };
        if (nginxAvailable)
        {
            arms.Insert(2, new("nginx-reverse-http1", ProbeMode.NginxReverseHttp1, null));
            arms.Insert(6, new("nginx-reverse-http1-tls", ProbeMode.NginxReverseHttp1Tls, null));
            arms.Insert(10, new("nginx-reverse-http2", ProbeMode.NginxReverseHttp2, null));
        }

        if (haproxyAvailable)
        {
            var i = arms.FindIndex(a => a.Mode == ProbeMode.YarpReverseHttp1);
            if (i >= 0)
                arms.Insert(i + 1, new("haproxy-reverse-http1", ProbeMode.HaproxyReverseHttp1, null));
            i = arms.FindIndex(a => a.Mode == ProbeMode.YarpReverseHttp1Tls);
            if (i >= 0)
                arms.Insert(i + 1, new("haproxy-reverse-http1-tls", ProbeMode.HaproxyReverseHttp1Tls, null));
            i = arms.FindIndex(a => a.Mode == ProbeMode.YarpReverseHttp2);
            if (i >= 0)
                arms.Insert(i + 1, new("haproxy-reverse-http2", ProbeMode.HaproxyReverseHttp2, null));
        }

        if (envoyAvailable)
        {
            var i = arms.FindIndex(a => a.Mode == ProbeMode.YarpReverseHttp1);
            if (i >= 0)
                arms.Insert(i + 1, new("envoy-reverse-http1", ProbeMode.EnvoyReverseHttp1, null));
            i = arms.FindIndex(a => a.Mode == ProbeMode.YarpReverseHttp1Tls);
            if (i >= 0)
                arms.Insert(i + 1, new("envoy-reverse-http1-tls", ProbeMode.EnvoyReverseHttp1Tls, null));
            i = arms.FindIndex(a => a.Mode == ProbeMode.YarpReverseHttp2);
            if (i >= 0)
                arms.Insert(i + 1, new("envoy-reverse-http2", ProbeMode.EnvoyReverseHttp2, null));
        }

        return arms;
    }

    private static IReadOnlyList<ArmSpec> BuildSaturationArms(bool nginxAvailable, bool bombardierAvailable,
        bool nginxHttp3Available, bool haproxyAvailable, bool haproxyHttp3Available, bool envoyAvailable,
        bool envoyHttp3Available)
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
        if (haproxyAvailable)
            arms.Add(new("haproxy-reverse-http1", ProbeMode.HaproxyReverseHttp1, null));
        if (envoyAvailable)
            arms.Add(new("envoy-reverse-http1", ProbeMode.EnvoyReverseHttp1, null));
        arms.Add(new("yarp-reverse-http1", ProbeMode.YarpReverseHttp1, null));
        arms.Add(new("twp-reverse-http1", ProbeMode.ReverseHttp1, null));

        // Block B — H2 TLS → H1 cleartext (peer ratios, not % of H1 origin-direct)
        if (nginxAvailable)
            arms.Add(new("nginx-reverse-http2", ProbeMode.NginxReverseHttp2, null));
        if (haproxyAvailable)
            arms.Add(new("haproxy-reverse-http2", ProbeMode.HaproxyReverseHttp2, null));
        if (envoyAvailable)
            arms.Add(new("envoy-reverse-http2", ProbeMode.EnvoyReverseHttp2, null));
        arms.Add(new("yarp-reverse-http2", ProbeMode.YarpReverseHttp2, null));
        arms.Add(new("twp-reverse-http2-cleartext", ProbeMode.ReverseHttp2Cleartext, null));

        // Block C — H3 → H1 cleartext (QuicListener skip happens in RunAsync RemoveAll)
        if (nginxHttp3Available)
            arms.Add(new("nginx-reverse-http3-cleartext", ProbeMode.NginxReverseHttp3Cleartext, null));
        if (haproxyHttp3Available)
            arms.Add(new("haproxy-reverse-http3-cleartext", ProbeMode.HaproxyReverseHttp3Cleartext, null));
        if (envoyHttp3Available)
            arms.Add(new("envoy-reverse-http3-cleartext", ProbeMode.EnvoyReverseHttp3Cleartext, null));
        arms.Add(new("yarp-reverse-http3-cleartext", ProbeMode.YarpReverseHttp3Cleartext, null));
        arms.Add(new("twp-reverse-http3-cleartext", ProbeMode.ReverseHttp3Cleartext, null));

        return arms;
    }

    private static IReadOnlyList<ArmSpec> BuildTlsCostArms(bool nginxAvailable, bool haproxyAvailable,
        bool envoyAvailable)
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

        if (haproxyAvailable)
        {
            var i = arms.FindIndex(a => a.Name == "yarp-reverse-http1-tls-ka-tiny");
            if (i >= 0)
                arms.Insert(i, new("haproxy-reverse-http1-tls-ka-tiny", ProbeMode.HaproxyReverseHttp1Tls, null, tinyKa));
            i = arms.FindIndex(a => a.Name == "yarp-reverse-http1-tls-nc-tiny");
            if (i >= 0)
                arms.Insert(i, new("haproxy-reverse-http1-tls-nc-tiny", ProbeMode.HaproxyReverseHttp1Tls, null, tinyNc));
            i = arms.FindIndex(a => a.Name == "yarp-reverse-http1-tls-ka-256k");
            if (i >= 0)
                arms.Insert(i, new("haproxy-reverse-http1-tls-ka-256k", ProbeMode.HaproxyReverseHttp1Tls, null, largeKa));
        }

        if (envoyAvailable)
        {
            var i = arms.FindIndex(a => a.Name == "yarp-reverse-http1-tls-ka-tiny");
            if (i >= 0)
                arms.Insert(i, new("envoy-reverse-http1-tls-ka-tiny", ProbeMode.EnvoyReverseHttp1Tls, null, tinyKa));
            i = arms.FindIndex(a => a.Name == "yarp-reverse-http1-tls-nc-tiny");
            if (i >= 0)
                arms.Insert(i, new("envoy-reverse-http1-tls-nc-tiny", ProbeMode.EnvoyReverseHttp1Tls, null, tinyNc));
            i = arms.FindIndex(a => a.Name == "yarp-reverse-http1-tls-ka-256k");
            if (i >= 0)
                arms.Insert(i, new("envoy-reverse-http1-tls-ka-256k", ProbeMode.EnvoyReverseHttp1Tls, null, largeKa));
        }

        return arms;
    }

    private static IReadOnlyList<ArmSpec> BuildArchArms(bool nginxAvailable, bool nginxHttp3Available,
        bool haproxyAvailable, bool haproxyHttp3Available, bool envoyAvailable, bool envoyHttp3Available)
    {
        var slow = WorkloadOptions.ForSlowConsumer();
        var early = WorkloadOptions.ForEarlyResponse();
        var duplex = WorkloadOptions.ForDuplexH2();
        var ws = WorkloadOptions.ForWebSocket();
        var arms = new List<ArmSpec>();
        arms.AddRange(HeavierReverseArms(nginxAvailable, nginxHttp3Available, haproxyAvailable, haproxyHttp3Available,
            envoyAvailable, envoyHttp3Available, slow, "slow256k"));
        arms.AddRange(HeavierReverseArms(nginxAvailable, nginxHttp3Available, haproxyAvailable, haproxyHttp3Available,
            envoyAvailable, envoyHttp3Available, early, "early64k"));
        arms.Add(new("twp-reverse-http2-duplex-h2", ProbeMode.ReverseHttp2, null, duplex));
        arms.Add(new("yarp-reverse-http2-to-https-duplex-h2", ProbeMode.YarpReverseHttp2ToHttps, null, duplex));
        arms.Add(new("twp-reverse-http1-tls-duplex-ws", ProbeMode.ReverseHttp1Tls, null, ws));
        if (nginxAvailable)
            arms.Add(new("nginx-reverse-http1-tls-duplex-ws", ProbeMode.NginxReverseHttp1Tls, null, ws));
        if (haproxyAvailable)
            arms.Add(new("haproxy-reverse-http1-tls-duplex-ws", ProbeMode.HaproxyReverseHttp1Tls, null, ws));
        if (envoyAvailable)
            arms.Add(new("envoy-reverse-http1-tls-duplex-ws", ProbeMode.EnvoyReverseHttp1Tls, null, ws));
        arms.Add(new("yarp-reverse-http1-tls-duplex-ws", ProbeMode.YarpReverseHttp1Tls, null, ws));
        return arms;
    }

    private sealed record ArmPeakResult(double PeakRps, long? RssPeakBytes, double? CpuAvgPct);

    /// <summary>
    /// Budget: child start + one full concurrency ramp (warmup+measure+overhead per step) + margin.
    /// Floor 8m so short local smokes still tolerate cold starts; cap 25m so GHA cannot stall for hours.
    /// </summary>
    private static TimeSpan ComputeDefaultArmTimeout(RampOptions options)
    {
        var steps = Math.Max(1, options.ConcurrencySteps.Length);
        var perStep = options.Warmup + options.StepDuration + TimeSpan.FromSeconds(45);
        var budget = TimeSpan.FromSeconds(90) + TimeSpan.FromTicks(perStep.Ticks * steps) + TimeSpan.FromMinutes(2);
        if (budget < TimeSpan.FromMinutes(8))
            budget = TimeSpan.FromMinutes(8);
        if (budget > TimeSpan.FromMinutes(25))
            budget = TimeSpan.FromMinutes(25);
        return budget;
    }

    private static async Task<ArmPeakResult> RunArmAsync(ArmSpec arm, RampOptions options, StreamWriter csv,
        string? nginxVersionHint, string? haproxyVersionHint, string? envoyVersionHint,
        CancellationToken cancellationToken)
    {
        var workload = arm.Workload ?? options.Workload;
        var maxCached = arm.MaxCachedConnections ?? options.MaxCachedConnections;
        await using var stack = await ChildProcessStack.StartAsync(arm.Mode, options.NginxPath, options.HaproxyPath,
            options.EnvoyPath, maxCached, cancellationToken, workload, arm.EnableHttpInterception,
            arm.MutateHttpInterception);
        var nginxVersion = stack.NginxVersion ?? nginxVersionHint;
        var haproxyVersion = stack.HaproxyVersion ?? haproxyVersionHint;
        var envoyVersion = stack.EnvoyVersion ?? envoyVersionHint;

        if (!string.IsNullOrWhiteSpace(stack.AuthorizationBearer))
        {
            workload = workload.WithExtraHeaders(new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + stack.AuthorizationBearer
            });
        }

        using var scrapeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? scrapeTask = null;
        if (arm.BackgroundControlPlaneScrape && !string.IsNullOrWhiteSpace(stack.ControlPlaneUrl))
        {
            scrapeTask = RunControlPlaneScrapeLoopAsync(stack.ControlPlaneUrl!,
                stack.ControlPlaneSecret ?? TitaniumCliHost.ControlPlaneSharedSecret, scrapeCts.Token,
                stack.DashboardUrl);
            ProbeLog.Info(string.IsNullOrWhiteSpace(stack.DashboardUrl)
                ? "  background control-plane scrape every 10s (/v1/snapshot)"
                : "  background control-plane scrape every 10s (/v1/snapshot + dashboard /metrics)");
        }

        if (arm.WarmCacheFirst)
        {
            ProbeLog.Info("  warming response cache before ramp...");
            var warmOpts = new LoadRequestOptions
            {
                Target = stack.TargetUri,
                HttpVersion = stack.RequestHttpVersion,
                VersionPolicy = stack.VersionPolicy,
                Workload = workload
            };
            await EmbeddedLoadGenerator.WarmupAsync(warmOpts, concurrency: 4, TimeSpan.FromSeconds(3),
                cancellationToken);
        }

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
            // -1 = inactive; after first SLO fail with a prior pass, set to 1 (one more step) then 0 (stop).
            var stopOnSloFailStepsRemaining = -1;
            var discoveryRewritten = false;

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
                if (arm.RewriteDiscoveryMidRamp && !discoveryRewritten &&
                    options.ConcurrencySteps.Length > 0 &&
                    concurrency == options.ConcurrencySteps[options.ConcurrencySteps.Length / 2] &&
                    !string.IsNullOrWhiteSpace(stack.DiscoveryFilePath) &&
                    stack.OriginHttpPort is int originPort)
                {
                    TitaniumCliHost.RewriteDiscoveryFile(stack.DiscoveryFilePath!, originPort);
                    discoveryRewritten = true;
                    ProbeLog.Info($"  mid-ramp discovery rewrite → {stack.DiscoveryFilePath}");
                    await Task.Delay(300, cancellationToken);
                }

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

                    // Bound the measure even if the generator ignores CancellationToken (MsQuic/bombardier stalls).
                    var measureBudget = options.StepDuration + TimeSpan.FromSeconds(45);
                    result = await measureTask.WaitAsync(measureBudget, cancellationToken);
                    if (sampleTask != null)
                        resources = await sampleTask.WaitAsync(measureBudget, cancellationToken);
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
                    stack.YarpVersion, resources, haproxyVersion, envoyVersion);
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
                    if (options.StopOnSloFail && stopOnSloFailStepsRemaining < 0)
                    {
                        stopOnSloFailStepsRemaining = 1;
                        ProbeLog.Info("    (stop-on-slo-fail: one more step for peak confirmation)");
                    }
                }

                if (stopOnSloFailStepsRemaining == 0)
                {
                    ProbeLog.Info("    (stop-on-slo-fail: peak confirmation done; ending arm)");
                    break;
                }

                if (stopOnSloFailStepsRemaining > 0)
                    stopOnSloFailStepsRemaining--;
            }

            ProbeLog.Info(string.Create(CultureInfo.InvariantCulture,
                $"  summary arm={arm.Name} sustainable_rps={(lastGood?.Rps ?? 0):F0} @ c={lastGoodConcurrency} peak_rps={(peak?.Rps ?? 0):F0} @ c={peak?.Concurrency ?? 0} p99_slo_ms={p99Slo:F0}"));

            return new ArmPeakResult(peak?.Rps ?? 0, peakResources?.PeakRssBytes, peakResources?.AvgCpuPercent);
        }
        finally
        {
            scrapeCts.Cancel();
            if (scrapeTask != null)
            {
                try { await scrapeTask; }
                catch (OperationCanceledException) { /* expected */ }
            }

            if (tcpLink != null)
                await tcpLink.DisposeAsync();
            if (udpLink != null)
                await udpLink.DisposeAsync();
        }
    }

    private static async Task RunControlPlaneScrapeLoopAsync(string controlPlaneUrl, string sharedSecret,
        CancellationToken cancellationToken, string? dashboardUrl = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-Titanium-Control-Secret", sharedSecret);

        var baseUri = new Uri(controlPlaneUrl);
        var snapshotUrl = new Uri(baseUri, "/v1/snapshot").AbsoluteUri;
        string? metricsUrl = null;
        if (!string.IsNullOrWhiteSpace(dashboardUrl))
            metricsUrl = new Uri(new Uri(dashboardUrl), "/metrics").AbsoluteUri;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _ = await http.GetAsync(snapshotUrl, cancellationToken);
                // When a dashboard URL is provided (PlusMetricsScrape), also scrape Prometheus
                // /metrics on the dashboard port. Snapshot-only otherwise.
                if (metricsUrl != null)
                    _ = await http.GetAsync(metricsUrl, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // best-effort scrape; do not fail the arm
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
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
            or ProbeMode.YarpReverseHttp3ToHttpsHttp1
            or ProbeMode.NginxReverseHttp3Cleartext or ProbeMode.NginxReverseHttp3ToHttpsHttp1
            or ProbeMode.HaproxyReverseHttp3Cleartext or ProbeMode.HaproxyReverseHttp3ToHttpsHttp1
            or ProbeMode.EnvoyReverseHttp3Cleartext or ProbeMode.EnvoyReverseHttp3ToHttpsHttp1;
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

    private static string ProbeHaproxyVersion(string exe)
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
            var stdout = p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            var text = string.IsNullOrWhiteSpace(stdout) ? err : stdout;
            return text.Trim().Replace('\n', ' ');
        }
        catch
        {
            return "unknown";
        }
    }

    private static string ProbeEnvoyVersion(string exe)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (p == null) return "unknown";
            var stdout = p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            var text = string.IsNullOrWhiteSpace(stdout) ? err : stdout;
            return text.Trim().Replace('\n', ' ');
        }
        catch
        {
            return "unknown";
        }
    }
}
