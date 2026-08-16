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

    public bool IsLossy => DelayMs > 0 || LossPercent > 0;
    public bool IsHeavyBody => ResponseBytes > TinyJsonBytes || RequestBytes > 0;
    public bool IsHandshake => !KeepAlive;

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
        CaptureTlsTiming = true
    };

    public WorkloadOptions WithCaptureTlsTiming(bool capture) => new()
    {
        Method = Method,
        ResponseBytes = ResponseBytes,
        RequestBytes = RequestBytes,
        KeepAlive = KeepAlive,
        DelayMs = DelayMs,
        LossPercent = LossPercent,
        CaptureTlsTiming = capture
    };

    public double ResolveP99SloMs(double http1, double http2, double http3, double httpsMitm,
        ProbeMode mode)
    {
        if (IsLossy)
            return 2000;
        if (IsHandshake)
            return 200;
        if (IsHeavyBody)
            return 500;

        return mode switch
        {
            ProbeMode.HttpsMitm or ProbeMode.ExplicitHttp1Multi or ProbeMode.ExplicitHttp2Multi =>
                httpsMitm,
            ProbeMode.ReverseHttp2 or ProbeMode.ReverseHttp2Cleartext or ProbeMode.ReverseHttp2ToH2c
                or ProbeMode.ReverseH2c or ProbeMode.ReverseH2cToH2c or ProbeMode.ReverseH2cToH1
                or ProbeMode.NginxReverseHttp2 or ProbeMode.ReverseHttp2ToHttp3 or ProbeMode.ReverseH2cToH3
                or ProbeMode.MitmHttp2ToHttp1 =>
                http2,
            ProbeMode.ReverseHttp3 or ProbeMode.ReverseHttp3Cleartext or ProbeMode.ReverseHttp3ToHttp2
                or ProbeMode.ReverseHttp1ToHttp3 or ProbeMode.MitmHttp3ToHttp1 => http3,
            _ => http1
        };
    }

    public string Suffix =>
        $"{Method.ToLowerInvariant()}-r{ResponseBytes}-q{RequestBytes}-{(KeepAlive ? "ka" : "nc")}" +
        (IsLossy ? $"-d{DelayMs}-l{LossPercent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}" : "");
}
