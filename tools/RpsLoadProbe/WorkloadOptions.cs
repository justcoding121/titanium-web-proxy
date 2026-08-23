using System.Net.Http;
using System.Text;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Workload knobs for saturation arms. Defaults preserve the historical tiny-GET loopback matrix.
/// </summary>
internal sealed class WorkloadOptions
{
    public static readonly int TinyJsonBytes =
        Encoding.UTF8.GetByteCount(OriginServer.ResponseBody);

    public string Method { get; init; } = "GET";
    public int ResponseBytes { get; init; } = TinyJsonBytes;
    public int RequestBytes { get; init; }
    public bool KeepAlive { get; init; } = true;
    public int DelayMs { get; init; }
    public double LossPercent { get; init; }
    public bool CaptureTlsTiming { get; init; }
    public int ClientReadChunkBytes { get; init; }
    public int ClientReadSleepMs { get; init; }
    public int EarlyResponseAfterBytes { get; init; }
    public bool IsDuplexHttp { get; init; }
    public bool IsWebSocket { get; init; }

    public bool IsLossy => DelayMs > 0 || LossPercent > 0;
    public bool IsHeavyBody => ResponseBytes > TinyJsonBytes || RequestBytes > 0;
    public bool IsHandshake => !KeepAlive;
    public bool IsSlowConsumer => ClientReadChunkBytes > 0 && ClientReadSleepMs > 0;
    public bool IsEarlyResponse => EarlyResponseAfterBytes > 0;
    public bool IsArchitectureSensitive =>
        IsSlowConsumer || IsEarlyResponse || IsDuplexHttp || IsWebSocket;

    public HttpMethod HttpMethod =>
        string.Equals(Method, "POST", StringComparison.OrdinalIgnoreCase)
            ? HttpMethod.Post
            : HttpMethod.Get;

    public static WorkloadOptions TinyGet { get; } = new();

    public static WorkloadOptions ForBodyGet(int responseBytes) => new()
    {
        Method = "GET",
        ResponseBytes = responseBytes,
        KeepAlive = true
    };

    public static WorkloadOptions ForPost(int requestBytes, int responseBytes) => new()
    {
        Method = "POST",
        RequestBytes = requestBytes,
        ResponseBytes = responseBytes,
        KeepAlive = true
    };

    public static WorkloadOptions ForLossy(int responseBytes, int delayMs, double lossPercent) => new()
    {
        Method = "GET",
        ResponseBytes = responseBytes,
        KeepAlive = true,
        DelayMs = delayMs,
        LossPercent = lossPercent
    };

    public static WorkloadOptions ForTlsKeepAlive(int responseBytes) => new()
    {
        Method = "GET",
        ResponseBytes = responseBytes,
        KeepAlive = true,
        CaptureTlsTiming = false
    };

    public static WorkloadOptions ForTlsNewConnection() => new()
    {
        Method = "GET",
        ResponseBytes = TinyJsonBytes,
        KeepAlive = false,
        // RPS fairness: do not enable TWP-only timing capture on the NC arm (YARP has no equivalent).
        // Set TWP_RPS_CAPTURE_TLS=1 manually when collecting ClientTlsTiming.
        CaptureTlsTiming = false
    };

    public static WorkloadOptions ForSlowConsumer() => new()
    {
        Method = "GET",
        ResponseBytes = 256 * 1024,
        KeepAlive = true,
        ClientReadChunkBytes = 16 * 1024,
        ClientReadSleepMs = 8
    };

    public static WorkloadOptions ForEarlyResponse() => new()
    {
        Method = "POST",
        RequestBytes = 64 * 1024,
        ResponseBytes = 64 * 1024,
        KeepAlive = true,
        EarlyResponseAfterBytes = 8 * 1024
    };

    public static WorkloadOptions ForDuplexH2() => new()
    {
        Method = "POST",
        RequestBytes = 64 * 1024,
        ResponseBytes = 64 * 1024,
        KeepAlive = true,
        EarlyResponseAfterBytes = 8 * 1024,
        IsDuplexHttp = true
    };

    public static WorkloadOptions ForWebSocket() => new()
    {
        Method = "GET",
        KeepAlive = true,
        IsWebSocket = true
    };

    public WorkloadOptions WithCaptureTlsTiming(bool capture) => Copy(captureTlsTiming: capture);

    public WorkloadOptions Copy(
        string? method = null,
        int? responseBytes = null,
        int? requestBytes = null,
        bool? keepAlive = null,
        int? delayMs = null,
        double? lossPercent = null,
        bool? captureTlsTiming = null,
        int? clientReadChunkBytes = null,
        int? clientReadSleepMs = null,
        int? earlyResponseAfterBytes = null,
        bool? isDuplexHttp = null,
        bool? isWebSocket = null) => new()
    {
        Method = method ?? Method,
        ResponseBytes = responseBytes ?? ResponseBytes,
        RequestBytes = requestBytes ?? RequestBytes,
        KeepAlive = keepAlive ?? KeepAlive,
        DelayMs = delayMs ?? DelayMs,
        LossPercent = lossPercent ?? LossPercent,
        CaptureTlsTiming = captureTlsTiming ?? CaptureTlsTiming,
        ClientReadChunkBytes = clientReadChunkBytes ?? ClientReadChunkBytes,
        ClientReadSleepMs = clientReadSleepMs ?? ClientReadSleepMs,
        EarlyResponseAfterBytes = earlyResponseAfterBytes ?? EarlyResponseAfterBytes,
        IsDuplexHttp = isDuplexHttp ?? IsDuplexHttp,
        IsWebSocket = isWebSocket ?? IsWebSocket
    };

    public double ResolveP99SloMs(double http1, double http2, double http3, double httpsMitm,
        ProbeMode mode)
    {
        if (IsArchitectureSensitive)
            return 5000;
        if (IsLossy)
            return 2000;
        if (IsHandshake)
            return 200;
        if (IsHeavyBody)
            return 500;

        return mode switch
        {
            ProbeMode.HttpsMitm or ProbeMode.ReverseHttp1Mitm
                or ProbeMode.ExplicitHttp1Multi or ProbeMode.ExplicitHttp2Multi =>
                httpsMitm,
            ProbeMode.ReverseHttp2 or ProbeMode.ReverseHttp2Cleartext or ProbeMode.ReverseHttp2ToH2c
                or ProbeMode.YarpReverseHttp2 or ProbeMode.YarpReverseHttp2ToH2c
                or ProbeMode.YarpReverseHttp2ToHttps
                or ProbeMode.ReverseH2c or ProbeMode.ReverseH2cToH2c or ProbeMode.ReverseH2cToH1
                or ProbeMode.YarpReverseH2c or ProbeMode.YarpReverseH2cToH2c or ProbeMode.YarpReverseH2cToH1
                or ProbeMode.NginxReverseHttp2 or ProbeMode.ReverseHttp2ToHttp3 or ProbeMode.YarpReverseHttp2ToHttp3
                or ProbeMode.ReverseH2cToH3 or ProbeMode.YarpReverseH2cToH3
                or ProbeMode.MitmHttp2ToHttp1 =>
                http2,
            ProbeMode.ReverseHttp3 or ProbeMode.ReverseHttp3Cleartext or ProbeMode.YarpReverseHttp3Cleartext
                or ProbeMode.NginxReverseHttp3Cleartext
                or ProbeMode.ReverseHttp3ToHttp2 or ProbeMode.YarpReverseHttp3ToHttp2
                or ProbeMode.YarpReverseHttp3ToHttp3
                or ProbeMode.ReverseHttp1ToHttp3 or ProbeMode.YarpReverseHttp1ToHttp3
                or ProbeMode.MitmHttp3ToHttp1 => http3,
            _ => http1
        };
    }

    public string Suffix
    {
        get
        {
            var suffix =
                $"{Method.ToLowerInvariant()}-r{ResponseBytes}-q{RequestBytes}-{(KeepAlive ? "ka" : "nc")}";
            if (IsLossy)
                suffix += $"-d{DelayMs}-l{LossPercent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}";
            if (IsSlowConsumer)
                suffix += $"-slow{ClientReadChunkBytes}-{ClientReadSleepMs}";
            if (IsEarlyResponse)
                suffix += $"-early{EarlyResponseAfterBytes}";
            if (IsDuplexHttp)
                suffix += "-duplex";
            if (IsWebSocket)
                suffix += "-ws";
            return suffix;
        }
    }
}
