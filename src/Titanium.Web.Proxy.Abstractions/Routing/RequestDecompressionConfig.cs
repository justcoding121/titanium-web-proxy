namespace Titanium.Web.Proxy.Abstractions.Routing;

/// <summary>
///     Bounded request-body decompression (<c>server.requestDecompression</c>). A request carrying
///     <c>Content-Encoding: gzip</c> or <c>br</c> is decompressed before it is sent to the origin, so the origin
///     and any body-aware policy see plain bytes. The compressed body and the decompressed body are both held in
///     memory, so both limits are enforced while decompressing and a breach answers 413 before the origin sees a
///     byte. A null policy is off and costs one null check.
/// </summary>
public sealed class RequestDecompressionConfig
{
    /// <summary>Default <see cref="MaxDecompressedBytes" />: 10 MiB.</summary>
    public const int DefaultMaxDecompressedBytes = 10_485_760;

    /// <summary>The largest <see cref="MaxDecompressedBytes" /> a config may ask for: 64 MiB.</summary>
    public const int MaxAllowedDecompressedBytes = 67_108_864;

    /// <summary>Default <see cref="MaxRatio" />: 100.</summary>
    public const int DefaultMaxRatio = 100;

    /// <summary>The largest <see cref="MaxRatio" /> a config may ask for.</summary>
    public const int MaxAllowedRatio = 10_000;

    /// <summary>Decompression is applied only when true. Default: false.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    ///     Most bytes a decompressed request body may hold, and also the most compressed bytes that are read.
    ///     A larger body is answered with 413. Default: 10 MiB.
    /// </summary>
    public int MaxDecompressedBytes { get; init; } = DefaultMaxDecompressedBytes;

    /// <summary>
    ///     Largest decompressed-to-compressed size ratio. A body that expands further is answered with 413 (a
    ///     zip bomb). The ratio is not applied while the decompressed body is still under 4 KiB. Default: 100.
    /// </summary>
    public int MaxRatio { get; init; } = DefaultMaxRatio;
}
