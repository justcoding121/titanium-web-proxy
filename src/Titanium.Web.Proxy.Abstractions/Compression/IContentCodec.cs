using System.IO;
using System.IO.Compression;

namespace Titanium.Web.Proxy.Abstractions.Compression;

/// <summary>
///     Optional content coding registered at runtime (for example <c>zstd</c> via the CLI). Built-in
///     <c>gzip</c> and <c>br</c> stay in Core; unknown names are resolved through <see cref="ContentCodecs" />.
/// </summary>
public interface IContentCodec
{
    /// <summary>Wire token for <c>Content-Encoding</c> and <c>Accept-Encoding</c> (for example <c>zstd</c>).</summary>
    string Name { get; }

    /// <summary>Compresses bytes written to this stream into <paramref name="sink" />.</summary>
    Stream CreateCompressor(Stream sink, CompressionLevel level, bool leaveOpen = true);

    /// <summary>Decompresses bytes read from <paramref name="source" />.</summary>
    Stream CreateDecompressor(Stream source, bool leaveOpen = true);
}
