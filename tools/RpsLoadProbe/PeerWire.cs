namespace Titanium.Web.Proxy.RpsLoadProbe;

internal enum PeerInboundProto
{
    H1c,
    H1Tls,
    H2c,
    H2Tls,
    H3
}

internal enum PeerOriginProto
{
    H1c,
    H1Tls,
    H2c,
    H2Tls,
    H3
}

internal enum PeerProduct
{
    Nginx,
    Haproxy,
    Envoy
}

internal readonly record struct NativePeerWire(
    ProbeMode Mode,
    string Name,
    PeerProduct Product,
    PeerInboundProto Inbound,
    PeerOriginProto Origin);

/// <summary>
/// Product-possible native reverse wires that were missing from the harness
/// (see product-arm-matrix.py). Existing terminate-to-H1 arms stay on their
/// original ProbeModes; this table is the new 5×5 remainder.
/// </summary>
internal static class PeerWire
{
    public static readonly NativePeerWire[] All =
    [
        new(ProbeMode.NginxReverseH2cToH1, "nginx-reverse-h2c-to-h1", PeerProduct.Nginx, PeerInboundProto.H2c, PeerOriginProto.H1c),
        new(ProbeMode.NginxReverseH2cToHttps, "nginx-reverse-h2c-to-https", PeerProduct.Nginx, PeerInboundProto.H2c, PeerOriginProto.H1Tls),
        new(ProbeMode.HaproxyReverseHttp1PlainToH2c, "haproxy-reverse-http1-plain-to-h2c", PeerProduct.Haproxy, PeerInboundProto.H1c, PeerOriginProto.H2c),
        new(ProbeMode.HaproxyReverseHttp1PlainToHttp2, "haproxy-reverse-http1-plain-to-http2", PeerProduct.Haproxy, PeerInboundProto.H1c, PeerOriginProto.H2Tls),
        new(ProbeMode.HaproxyReverseHttp1PlainToHttp3, "haproxy-reverse-http1-plain-to-http3", PeerProduct.Haproxy, PeerInboundProto.H1c, PeerOriginProto.H3),
        new(ProbeMode.HaproxyReverseHttp1ToH2c, "haproxy-reverse-http1-to-h2c", PeerProduct.Haproxy, PeerInboundProto.H1Tls, PeerOriginProto.H2c),
        new(ProbeMode.HaproxyReverseHttp11ToHttp2, "haproxy-reverse-http11-to-http2", PeerProduct.Haproxy, PeerInboundProto.H1Tls, PeerOriginProto.H2Tls),
        new(ProbeMode.HaproxyReverseHttp1ToHttp3, "haproxy-reverse-http1-to-http3", PeerProduct.Haproxy, PeerInboundProto.H1Tls, PeerOriginProto.H3),
        new(ProbeMode.HaproxyReverseH2cToH1, "haproxy-reverse-h2c-to-h1", PeerProduct.Haproxy, PeerInboundProto.H2c, PeerOriginProto.H1c),
        new(ProbeMode.HaproxyReverseH2cToHttps, "haproxy-reverse-h2c-to-https", PeerProduct.Haproxy, PeerInboundProto.H2c, PeerOriginProto.H1Tls),
        new(ProbeMode.HaproxyReverseH2cToH2c, "haproxy-reverse-h2c-to-h2c", PeerProduct.Haproxy, PeerInboundProto.H2c, PeerOriginProto.H2c),
        new(ProbeMode.HaproxyReverseH2c, "haproxy-reverse-h2c", PeerProduct.Haproxy, PeerInboundProto.H2c, PeerOriginProto.H2Tls),
        new(ProbeMode.HaproxyReverseH2cToH3, "haproxy-reverse-h2c-to-h3", PeerProduct.Haproxy, PeerInboundProto.H2c, PeerOriginProto.H3),
        new(ProbeMode.HaproxyReverseHttp2ToH2c, "haproxy-reverse-http2-to-h2c", PeerProduct.Haproxy, PeerInboundProto.H2Tls, PeerOriginProto.H2c),
        new(ProbeMode.HaproxyReverseHttp2ToHttps, "haproxy-reverse-http2-to-https", PeerProduct.Haproxy, PeerInboundProto.H2Tls, PeerOriginProto.H2Tls),
        new(ProbeMode.HaproxyReverseHttp2ToHttp3, "haproxy-reverse-http2-to-http3", PeerProduct.Haproxy, PeerInboundProto.H2Tls, PeerOriginProto.H3),
        new(ProbeMode.HaproxyReverseHttp3ToH2c, "haproxy-reverse-http3-to-h2c", PeerProduct.Haproxy, PeerInboundProto.H3, PeerOriginProto.H2c),
        new(ProbeMode.HaproxyReverseHttp3ToHttp2, "haproxy-reverse-http3-to-http2", PeerProduct.Haproxy, PeerInboundProto.H3, PeerOriginProto.H2Tls),
        new(ProbeMode.HaproxyReverseHttp3ToHttp3, "haproxy-reverse-http3-to-http3", PeerProduct.Haproxy, PeerInboundProto.H3, PeerOriginProto.H3),
        new(ProbeMode.EnvoyReverseHttp1PlainToH2c, "envoy-reverse-http1-plain-to-h2c", PeerProduct.Envoy, PeerInboundProto.H1c, PeerOriginProto.H2c),
        new(ProbeMode.EnvoyReverseHttp1PlainToHttp2, "envoy-reverse-http1-plain-to-http2", PeerProduct.Envoy, PeerInboundProto.H1c, PeerOriginProto.H2Tls),
        new(ProbeMode.EnvoyReverseHttp1PlainToHttp3, "envoy-reverse-http1-plain-to-http3", PeerProduct.Envoy, PeerInboundProto.H1c, PeerOriginProto.H3),
        new(ProbeMode.EnvoyReverseHttp1ToH2c, "envoy-reverse-http1-to-h2c", PeerProduct.Envoy, PeerInboundProto.H1Tls, PeerOriginProto.H2c),
        new(ProbeMode.EnvoyReverseHttp11ToHttp2, "envoy-reverse-http11-to-http2", PeerProduct.Envoy, PeerInboundProto.H1Tls, PeerOriginProto.H2Tls),
        new(ProbeMode.EnvoyReverseHttp1ToHttp3, "envoy-reverse-http1-to-http3", PeerProduct.Envoy, PeerInboundProto.H1Tls, PeerOriginProto.H3),
        new(ProbeMode.EnvoyReverseH2cToH1, "envoy-reverse-h2c-to-h1", PeerProduct.Envoy, PeerInboundProto.H2c, PeerOriginProto.H1c),
        new(ProbeMode.EnvoyReverseH2cToHttps, "envoy-reverse-h2c-to-https", PeerProduct.Envoy, PeerInboundProto.H2c, PeerOriginProto.H1Tls),
        new(ProbeMode.EnvoyReverseH2cToH2c, "envoy-reverse-h2c-to-h2c", PeerProduct.Envoy, PeerInboundProto.H2c, PeerOriginProto.H2c),
        new(ProbeMode.EnvoyReverseH2c, "envoy-reverse-h2c", PeerProduct.Envoy, PeerInboundProto.H2c, PeerOriginProto.H2Tls),
        new(ProbeMode.EnvoyReverseH2cToH3, "envoy-reverse-h2c-to-h3", PeerProduct.Envoy, PeerInboundProto.H2c, PeerOriginProto.H3),
        new(ProbeMode.EnvoyReverseHttp2ToH2c, "envoy-reverse-http2-to-h2c", PeerProduct.Envoy, PeerInboundProto.H2Tls, PeerOriginProto.H2c),
        new(ProbeMode.EnvoyReverseHttp2ToHttps, "envoy-reverse-http2-to-https", PeerProduct.Envoy, PeerInboundProto.H2Tls, PeerOriginProto.H2Tls),
        new(ProbeMode.EnvoyReverseHttp2ToHttp3, "envoy-reverse-http2-to-http3", PeerProduct.Envoy, PeerInboundProto.H2Tls, PeerOriginProto.H3),
        new(ProbeMode.EnvoyReverseHttp3ToH2c, "envoy-reverse-http3-to-h2c", PeerProduct.Envoy, PeerInboundProto.H3, PeerOriginProto.H2c),
        new(ProbeMode.EnvoyReverseHttp3ToHttp2, "envoy-reverse-http3-to-http2", PeerProduct.Envoy, PeerInboundProto.H3, PeerOriginProto.H2Tls),
        new(ProbeMode.EnvoyReverseHttp3ToHttp3, "envoy-reverse-http3-to-http3", PeerProduct.Envoy, PeerInboundProto.H3, PeerOriginProto.H3)
    ];

    private static readonly Dictionary<ProbeMode, NativePeerWire> ByMode =
        All.ToDictionary(w => w.Mode);

    private static readonly Dictionary<string, NativePeerWire> ByName =
        All.ToDictionary(w => w.Name, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(ProbeMode mode, out NativePeerWire wire) => ByMode.TryGetValue(mode, out wire);

    public static bool TryParseName(string name, out ProbeMode mode)
    {
        if (ByName.TryGetValue(name, out var wire))
        {
            mode = wire.Mode;
            return true;
        }

        mode = default;
        return false;
    }

    public static bool IsHttp3ClientOrOrigin(ProbeMode mode)
    {
        if (!TryGet(mode, out var wire))
            return false;
        return wire.Inbound == PeerInboundProto.H3 || wire.Origin == PeerOriginProto.H3;
    }

    public static bool NeedsQuicBuild(NativePeerWire wire) =>
        wire.Inbound == PeerInboundProto.H3 || wire.Origin == PeerOriginProto.H3;

    public static bool NeedsHttp2On(NativePeerWire wire) =>
        wire.Product == PeerProduct.Nginx && wire.Inbound == PeerInboundProto.H2c;

    public static bool NeedsInboundCerts(NativePeerWire wire) =>
        wire.Inbound is PeerInboundProto.H1Tls or PeerInboundProto.H2Tls or PeerInboundProto.H3;

    public static string ClientHttpVersion(NativePeerWire wire) => wire.Inbound switch
    {
        PeerInboundProto.H2c or PeerInboundProto.H2Tls => "2.0",
        PeerInboundProto.H3 => "3.0",
        _ => "1.1"
    };

    public static int OriginPort(NativePeerWire wire, int originHttpPort, int originHttpsPort, int originQuicPort) =>
        wire.Origin switch
        {
            PeerOriginProto.H1c or PeerOriginProto.H2c => originHttpPort,
            PeerOriginProto.H1Tls or PeerOriginProto.H2Tls => originHttpsPort,
            PeerOriginProto.H3 => originQuicPort,
            _ => throw new ArgumentOutOfRangeException(nameof(wire))
        };

    public static bool IsProduct(ProbeMode mode, PeerProduct product) =>
        TryGet(mode, out var wire) && wire.Product == product;
}
