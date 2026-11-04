using System.IO;
using System.IO.Compression;
using Titanium.Web.Proxy.Abstractions.Compression;
using ZstdSharp;

namespace Titanium.Cli.Compression;

/// <summary><c>zstd</c> codec registered by the CLI when configured (ZstdSharp.Port, MIT).</summary>
internal sealed class ZstdContentCodec : IContentCodec
{
    public string Name => "zstd";

    public Stream CreateCompressor(Stream sink, CompressionLevel level, bool leaveOpen = true) =>
        new CompressionStream(sink, MapLevel(level));

    public Stream CreateDecompressor(Stream source, bool leaveOpen = true) =>
        new DecompressionStream(source);

    internal static int MapLevel(CompressionLevel level) => level switch
    {
        CompressionLevel.SmallestSize => 12,
        CompressionLevel.Optimal => 3,
        _ => 1
    };
}
