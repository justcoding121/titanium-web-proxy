namespace Titanium.Web.Proxy.Abstractions.Routing;

/// <summary>
///     Response compression (<c>server.responseCompression</c>). A null policy leaves every response
///     untouched and costs one null check. A route opts out with <c>compression: off</c>.
/// </summary>
public sealed class ResponseCompressionConfig
{
    /// <summary>Default <see cref="MinBytes" />: 1 KiB.</summary>
    public const int DefaultMinBytes = 1024;

    /// <summary>The default <see cref="MaxBytes" /> and the largest value a config may ask for: 1 MiB.</summary>
    public const int DefaultMaxBytes = 1_048_576;

    /// <summary><see cref="Mode" /> value that buffers a small known-length body and compresses it in one piece.</summary>
    public const string ModeBuffered = "buffered";

    /// <summary>
    ///     <see cref="Mode" /> value that compresses the body as it is relayed, flushing on every upstream read, so
    ///     large, chunked and close-delimited bodies are compressed without buffering them.
    /// </summary>
    public const string ModeStreaming = "streaming";

    /// <summary>Compression is applied only when true. Default: false.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    ///     <c>buffered</c> (default) or <c>streaming</c>. Buffered mode compresses only known-length bodies between
    ///     <see cref="MinBytes" /> and <see cref="MaxBytes" />. Streaming mode ignores <see cref="MaxBytes" />, also
    ///     handles chunked and close-delimited bodies, and keeps memory flat regardless of body size.
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>
    ///     Encodings in order of preference: <c>br</c> and <c>gzip</c>. Null means <c>br</c> then <c>gzip</c>,
    ///     so Brotli wins when the client accepts both with the same weight. A higher <c>q</c> weight in the client's
    ///     <c>Accept-Encoding</c> wins over this order.
    /// </summary>
    public IList<string>? Algorithms { get; init; }

    /// <summary>Smallest known Content-Length that is compressed. Default: 1024.</summary>
    public int MinBytes { get; init; } = DefaultMinBytes;

    /// <summary>
    ///     Largest known Content-Length that is compressed (and buffered) in buffered mode. Default: 1 MiB.
    ///     Not used in streaming mode.
    /// </summary>
    public int MaxBytes { get; init; } = DefaultMaxBytes;

    /// <summary><c>fastest</c> (default), <c>optimal</c>, or <c>smallestSize</c>.</summary>
    public string? Level { get; init; }

    /// <summary>
    ///     Media types that are compressed: an exact type such as <c>application/json</c> or a family such as
    ///     <c>text/*</c>. Null means <c>text/*</c>, <c>application/json</c>, <c>application/javascript</c>,
    ///     <c>application/xml</c> and <c>image/svg+xml</c>. Add <c>application/wasm</c> to compress WebAssembly.
    /// </summary>
    public IList<string>? MimeTypes { get; init; }
}
