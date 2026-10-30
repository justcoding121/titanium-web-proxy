using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Compression;

/// <summary>How a bounded request-body decompression ended.</summary>
internal enum RequestDecompressionOutcome
{
    /// <summary>The body was decompressed within both limits.</summary>
    Decompressed,

    /// <summary>The decompressed body outgrew <c>maxDecompressedBytes</c>.</summary>
    TooLarge,

    /// <summary>The body expanded further than <c>maxRatio</c> allows (a zip bomb).</summary>
    RatioExceeded,

    /// <summary>The bytes are not valid for the declared coding.</summary>
    Corrupt,
}

/// <summary>
///     Compiled <c>server.requestDecompression</c>: decides whether a request body is decompressed and does it
///     inside two limits, so a small compressed body cannot expand into an unbounded one. Built once per config.
/// </summary>
internal sealed class RequestDecompressionPolicy
{
    /// <summary>The ratio limit is not applied while the decompressed body is under this many bytes.</summary>
    internal const int RatioFloorBytes = 4096;

    private const int ReadBytes = 16 * 1024;

    private RequestDecompressionPolicy(RequestDecompressionConfig config)
    {
        MaxDecompressedBytes = config.MaxDecompressedBytes;
        MaxRatio = config.MaxRatio;
    }

    internal int MaxDecompressedBytes { get; }

    internal int MaxRatio { get; }

    /// <summary>Null (off) unless the config is present and enabled.</summary>
    internal static RequestDecompressionPolicy? Create(RequestDecompressionConfig? config) =>
        config is { Enabled: true } ? new RequestDecompressionPolicy(config) : null;

    /// <summary>
    ///     True when the request carries exactly one <c>Content-Encoding</c> that is gzip or Brotli. Stacked
    ///     encodings, deflate and anything else are left for the origin.
    /// </summary>
    internal static bool TryGetEncoding(Request request, out HttpCompression encoding)
    {
        encoding = HttpCompression.Unsupported;
        if (request.ContentEncoding is not { Length: > 0 } header)
            return false;

        var name = header.Trim().ToLowerInvariant();
        encoding = name switch
        {
            "gzip" or "x-gzip" => HttpCompression.Gzip,
            "br" => HttpCompression.Brotli,
            _ => HttpCompression.Unsupported,
        };
        return encoding != HttpCompression.Unsupported;
    }

    /// <summary>
    ///     Decompresses <paramref name="compressed" />, stopping as soon as the output passes
    ///     <see cref="MaxDecompressedBytes" /> or <c>max(compressed length x MaxRatio, 4 KiB)</c>, so memory never
    ///     exceeds the smaller bound plus one read.
    /// </summary>
    internal RequestDecompressionOutcome TryDecompress(HttpCompression encoding, byte[] compressed, // NOSONAR S3776 -- Brotli and stream decoders share one byte cap and corrupt handling.
        out byte[] decompressed)
    {
        decompressed = [];
        var ratioLimit = Math.Max((long)compressed.Length * MaxRatio, RatioFloorBytes);
        var limit = Math.Min(MaxDecompressedBytes, ratioLimit);

        using var output = new MemoryStream(Math.Min(compressed.Length * 4, (int)Math.Min(limit, 1 << 20)));
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBytes);

        if (encoding == HttpCompression.Brotli)
        {
            try
            {
                var outcome = DecodeBrotli(compressed, buffer, output, limit);
                if (outcome == RequestDecompressionOutcome.Decompressed)
                    decompressed = output.ToArray();
                return outcome;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        using var input = new MemoryStream(compressed, writable: false);
        using var decoder = DecompressionFactory.Create(encoding, input);

        try
        {
            long total = 0;
            while (true)
            {
                int read;
                try
                {
                    read = decoder.Read(buffer, 0, buffer.Length);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException)
                {
                    return RequestDecompressionOutcome.Corrupt;
                }

                if (read == 0)
                    break;

                total += read;
                if (total > limit)
                {
                    // Report whichever bound was crossed first; the absolute cap wins a tie.
                    return total > MaxDecompressedBytes
                        ? RequestDecompressionOutcome.TooLarge
                        : RequestDecompressionOutcome.RatioExceeded;
                }

                output.Write(buffer, 0, read);
            }

            // GZipStream ends silently on a stream cut short, so check the member's ISIZE field (the last four
            // bytes: the uncompressed length mod 2^32) to refuse a truncated body instead of forwarding it.
            if (compressed.Length > 0
                && (compressed.Length < 18
                    || BinaryPrimitives.ReadUInt32LittleEndian(compressed.AsSpan(compressed.Length - 4))
                    != (uint)total))
            {
                return RequestDecompressionOutcome.Corrupt;
            }

            decompressed = output.ToArray();
            return RequestDecompressionOutcome.Decompressed;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///     Brotli has no trailer, and <c>BrotliStream</c> ends silently on a truncated stream. The decoder's
    ///     status tells a finished stream (<c>Done</c>) from one that ran out of input (<c>NeedMoreData</c>).
    /// </summary>
    private RequestDecompressionOutcome DecodeBrotli(byte[] compressed, byte[] buffer, MemoryStream output, long limit)
    {
        var decoder = new BrotliDecoder();
        try
        {
            var position = 0;
            long total = 0;
            while (true)
            {
                var status = decoder.Decompress(compressed.AsSpan(position), buffer, out var consumed, out var written);
                position += consumed;
                total += written;
                if (total > limit)
                {
                    return total > MaxDecompressedBytes
                        ? RequestDecompressionOutcome.TooLarge
                        : RequestDecompressionOutcome.RatioExceeded;
                }

                output.Write(buffer, 0, written);
                switch (status)
                {
                    case OperationStatus.Done:
                        return RequestDecompressionOutcome.Decompressed;
                    case OperationStatus.DestinationTooSmall:
                        continue;
                    case OperationStatus.NeedMoreData when position < compressed.Length && (consumed > 0 || written > 0):
                        continue;
                    default:
                        // NeedMoreData with the input exhausted is a truncated stream; InvalidData is corrupt.
                        return RequestDecompressionOutcome.Corrupt;
                }
            }
        }
        finally
        {
            decoder.Dispose();
        }
    }
}
