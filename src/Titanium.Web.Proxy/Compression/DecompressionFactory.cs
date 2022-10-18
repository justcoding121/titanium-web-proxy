using System;
using System.IO;
using System.IO.Compression;

namespace Titanium.Web.Proxy.Compression;

/// <summary>
///     A factory to generate the de-compression methods based on the type of compression
/// </summary>
internal class DecompressionFactory
{
    internal static Stream Create(HttpCompression type, Stream stream, bool leaveOpen = true)
    {
        return type switch
        {
            HttpCompression.Gzip => new GZipStream(stream, CompressionMode.Decompress, leaveOpen),
            HttpCompression.Deflate => new DeflateStream(stream, CompressionMode.Decompress, leaveOpen),
            HttpCompression.Brotli => new BrotliSharpLib.BrotliStream(stream, CompressionMode.Decompress, leaveOpen),
            _ => throw new Exception($"Unsupported decompression mode: {type}")
        };
    }
}