using System;
using System.IO;
using System.IO.Compression;

namespace Titanium.Web.Proxy.Compression;

/// <summary>
///     A factory to generate the de-compression methods based on the type of compression
/// </summary>
internal static class DecompressionFactory
{
    internal static Stream Create(HttpCompression type, Stream stream, bool leaveOpen = true)
    {
        return type switch
        {
            HttpCompression.Gzip => new GZipStream(stream, CompressionMode.Decompress, leaveOpen),
            HttpCompression.Deflate => new DeflateStream(stream, CompressionMode.Decompress, leaveOpen),
            // System.IO.Compression.BrotliStream (not BrotliSharpLib) is required here: its
            // ReadAsync is genuinely async (delegates to the underlying stream's ReadAsync).
            // BrotliSharpLib.BrotliStream has no real async support and falls back to the base
            // Stream.ReadAsync, which invokes the synchronous Read() on the wrapped stream. When
            // that wrapped stream is a LimitedStream (async-only by design - see LimitedStream.Read),
            // this throws NotSupportedException instead of decompressing.
            HttpCompression.Brotli => new BrotliStream(stream, CompressionMode.Decompress, leaveOpen),
            _ => throw new NotSupportedException($"Unsupported decompression mode: {type}")
        };
    }
}