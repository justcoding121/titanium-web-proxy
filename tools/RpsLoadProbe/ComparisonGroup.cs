namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Comparison-group (wiki-row) keys for arm sharding. One Client×Origin (+ workload suffix)
/// must stay on a single GHA job so TWP÷YARP and Lite÷Reverse ratios stay same-VM.
/// </summary>
internal static class ComparisonGroup
{
    private static readonly string[] WorkloadSuffixes =
    [
        "-body64k", "-body256k", "-post64k", "-lossy",
        "-slow256k", "-early64k", "-duplex-h2", "-duplex-ws",
        "-ka-tiny", "-nc-tiny", "-ka-256k"
    ];

    /// <summary>
    /// Map ProbeMode → canonical Client×Origin wire id. Peers that share a wiki cell share a key.
    /// MITM Lite/Full reuse the same ProbeMode as TWP reverse, so they group automatically.
    /// </summary>
    private static readonly Dictionary<ProbeMode, string> WireByMode = BuildWireMap();

    public static string Key(ProbeMode mode, string armName)
    {
        var suffix = ExtractWorkloadSuffix(armName);

        // Unary gRPC wiki row (all five products) — arm names are *-grpc-*.
        if (armName.Contains("-grpc-", StringComparison.OrdinalIgnoreCase)
            || armName.StartsWith("grpc-", StringComparison.OrdinalIgnoreCase))
            return suffix == null ? "grpc-unary" : "grpc-unary" + suffix;

        // CONNECT MITM pair: both Lite and Full use HttpsMitm.
        if (mode is ProbeMode.HttpsMitm)
            return suffix == null ? "https-connect" : "https-connect" + suffix;

        if (!WireByMode.TryGetValue(mode, out var wire))
            wire = mode.ToString();

        return suffix == null ? wire : wire + suffix;
    }

    public static string? ExtractWorkloadSuffix(string armName)
    {
        foreach (var suffix in WorkloadSuffixes)
        {
            if (armName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return suffix;
        }

        return null;
    }

    /// <summary>
    /// Keep arms whose comparison group maps to shard <paramref name="shardIndex"/> of
    /// <paramref name="shardCount"/> (1-based). Groups are assigned in first-seen order:
    /// group k → shard (k % n) + 1. Preserves original arm order inside the shard.
    /// </summary>
    public static List<T> ApplyShard<T>(IReadOnlyList<T> arms, Func<T, string> keySelector,
        int shardIndex, int shardCount)
    {
        if (shardCount < 1)
            throw new ArgumentOutOfRangeException(nameof(shardCount), "Shard count must be >= 1.");
        if (shardIndex < 1 || shardIndex > shardCount)
            throw new ArgumentOutOfRangeException(nameof(shardIndex),
                $"Shard index must be 1..{shardCount}.");
        if (shardCount == 1)
            return arms.ToList();

        var groupIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var kept = new List<T>(arms.Count / shardCount + 1);
        foreach (var arm in arms)
        {
            var key = keySelector(arm);
            if (!groupIndex.TryGetValue(key, out var gi))
            {
                gi = groupIndex.Count;
                groupIndex[key] = gi;
            }

            if ((gi % shardCount) + 1 == shardIndex)
                kept.Add(arm);
        }

        return kept;
    }

    private static Dictionary<ProbeMode, string> BuildWireMap()
    {
        var map = new Dictionary<ProbeMode, string>();

        void Wire(string id, params ProbeMode[] modes)
        {
            foreach (var m in modes)
                map[m] = id;
        }

        // H1 plain client
        Wire("h1c-h1c", ProbeMode.ReverseHttp1, ProbeMode.YarpReverseHttp1, ProbeMode.NginxReverseHttp1,
            ProbeMode.HaproxyReverseHttp1, ProbeMode.EnvoyReverseHttp1, ProbeMode.BareReverseHttp1);
        Wire("h1c-h1tls", ProbeMode.ReverseHttp1ToHttps, ProbeMode.YarpReverseHttp1ToHttps,
            ProbeMode.NginxReverseHttp1ToHttps, ProbeMode.HaproxyReverseHttp1ToHttps,
            ProbeMode.EnvoyReverseHttp1ToHttps);
        Wire("h1c-h2c", ProbeMode.ReverseHttp1PlainToH2c, ProbeMode.YarpReverseHttp1PlainToH2c,
            ProbeMode.HaproxyReverseHttp1PlainToH2c, ProbeMode.EnvoyReverseHttp1PlainToH2c);
        Wire("h1c-h2tls", ProbeMode.ReverseHttp1PlainToHttp2, ProbeMode.YarpReverseHttp1PlainToHttp2,
            ProbeMode.HaproxyReverseHttp1PlainToHttp2, ProbeMode.EnvoyReverseHttp1PlainToHttp2);
        Wire("h1c-h3", ProbeMode.ReverseHttp1PlainToHttp3, ProbeMode.YarpReverseHttp1PlainToHttp3,
            ProbeMode.HaproxyReverseHttp1PlainToHttp3, ProbeMode.EnvoyReverseHttp1PlainToHttp3);

        // H1 TLS client
        Wire("h1tls-h1c", ProbeMode.ReverseHttp1Tls, ProbeMode.YarpReverseHttp1Tls,
            ProbeMode.NginxReverseHttp1Tls, ProbeMode.HaproxyReverseHttp1Tls,
            ProbeMode.EnvoyReverseHttp1Tls, ProbeMode.BareReverseHttp1Tls);
        Wire("h1tls-h1tls", ProbeMode.ReverseHttp1Mitm, ProbeMode.YarpReverseHttp1TlsToHttps,
            ProbeMode.NginxReverseHttp1TlsToHttps, ProbeMode.HaproxyReverseHttp1TlsToHttps,
            ProbeMode.EnvoyReverseHttp1TlsToHttps);
        Wire("h1tls-h2c", ProbeMode.ReverseHttp1ToH2c, ProbeMode.YarpReverseHttp1ToH2c,
            ProbeMode.HaproxyReverseHttp1ToH2c, ProbeMode.EnvoyReverseHttp1ToH2c);
        Wire("h1tls-h2tls", ProbeMode.ReverseHttp11ToHttp2, ProbeMode.YarpReverseHttp11ToHttp2,
            ProbeMode.HaproxyReverseHttp11ToHttp2, ProbeMode.EnvoyReverseHttp11ToHttp2);
        Wire("h1tls-h3", ProbeMode.ReverseHttp1ToHttp3, ProbeMode.YarpReverseHttp1ToHttp3,
            ProbeMode.HaproxyReverseHttp1ToHttp3, ProbeMode.EnvoyReverseHttp1ToHttp3);

        // H2 plain (h2c) client
        Wire("h2c-h1c", ProbeMode.ReverseH2cToH1, ProbeMode.YarpReverseH2cToH1,
            ProbeMode.NginxReverseH2cToH1, ProbeMode.HaproxyReverseH2cToH1, ProbeMode.EnvoyReverseH2cToH1);
        Wire("h2c-h1tls", ProbeMode.ReverseH2cToHttps, ProbeMode.YarpReverseH2cToHttps,
            ProbeMode.NginxReverseH2cToHttps, ProbeMode.HaproxyReverseH2cToHttps,
            ProbeMode.EnvoyReverseH2cToHttps);
        Wire("h2c-h2c", ProbeMode.ReverseH2cToH2c, ProbeMode.YarpReverseH2cToH2c,
            ProbeMode.HaproxyReverseH2cToH2c, ProbeMode.EnvoyReverseH2cToH2c);
        Wire("h2c-h2tls", ProbeMode.ReverseH2c, ProbeMode.YarpReverseH2c, ProbeMode.HaproxyReverseH2c,
            ProbeMode.EnvoyReverseH2c);
        Wire("h2c-h3", ProbeMode.ReverseH2cToH3, ProbeMode.YarpReverseH2cToH3,
            ProbeMode.HaproxyReverseH2cToH3, ProbeMode.EnvoyReverseH2cToH3);

        // H2 TLS client — terminate-to-H1 vs same-protocol H2 are different wiki rows
        Wire("h2tls-h1c", ProbeMode.ReverseHttp2Cleartext, ProbeMode.YarpReverseHttp2,
            ProbeMode.NginxReverseHttp2, ProbeMode.HaproxyReverseHttp2, ProbeMode.EnvoyReverseHttp2);
        Wire("h2tls-h1tls", ProbeMode.MitmHttp2ToHttp1, ProbeMode.YarpReverseHttp2ToHttpsHttp1,
            ProbeMode.NginxReverseHttp2ToHttpsHttp1, ProbeMode.HaproxyReverseHttp2ToHttpsHttp1,
            ProbeMode.EnvoyReverseHttp2ToHttpsHttp1);
        Wire("h2tls-h2c", ProbeMode.ReverseHttp2ToH2c, ProbeMode.YarpReverseHttp2ToH2c,
            ProbeMode.HaproxyReverseHttp2ToH2c, ProbeMode.EnvoyReverseHttp2ToH2c);
        Wire("h2tls-h2tls", ProbeMode.ReverseHttp2, ProbeMode.YarpReverseHttp2ToHttps,
            ProbeMode.HaproxyReverseHttp2ToHttps, ProbeMode.EnvoyReverseHttp2ToHttps);
        Wire("h2tls-h3", ProbeMode.ReverseHttp2ToHttp3, ProbeMode.YarpReverseHttp2ToHttp3,
            ProbeMode.HaproxyReverseHttp2ToHttp3, ProbeMode.EnvoyReverseHttp2ToHttp3);

        // H3 client
        Wire("h3-h1c", ProbeMode.ReverseHttp3Cleartext, ProbeMode.YarpReverseHttp3Cleartext,
            ProbeMode.NginxReverseHttp3Cleartext, ProbeMode.HaproxyReverseHttp3Cleartext,
            ProbeMode.EnvoyReverseHttp3Cleartext);
        Wire("h3-h1tls", ProbeMode.MitmHttp3ToHttp1, ProbeMode.YarpReverseHttp3ToHttpsHttp1,
            ProbeMode.NginxReverseHttp3ToHttpsHttp1, ProbeMode.HaproxyReverseHttp3ToHttpsHttp1,
            ProbeMode.EnvoyReverseHttp3ToHttpsHttp1);
        Wire("h3-h2c", ProbeMode.ReverseHttp3ToH2c, ProbeMode.YarpReverseHttp3ToH2c,
            ProbeMode.HaproxyReverseHttp3ToH2c, ProbeMode.EnvoyReverseHttp3ToH2c);
        Wire("h3-h2tls", ProbeMode.ReverseHttp3ToHttp2, ProbeMode.YarpReverseHttp3ToHttp2,
            ProbeMode.HaproxyReverseHttp3ToHttp2, ProbeMode.EnvoyReverseHttp3ToHttp2);
        Wire("h3-h3", ProbeMode.ReverseHttp3, ProbeMode.YarpReverseHttp3ToHttp3,
            ProbeMode.HaproxyReverseHttp3ToHttp3, ProbeMode.EnvoyReverseHttp3ToHttp3);

        Wire("https-connect", ProbeMode.HttpsMitm);
        Wire("http-mitm", ProbeMode.HttpMitm);

        return map;
    }
}
