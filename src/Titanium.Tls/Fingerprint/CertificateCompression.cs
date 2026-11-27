using System;
using System.Buffers;
using System.IO.Compression;

namespace Titanium.Tls;

/// <summary>RFC 8879 certificate decompression. Brotli is algorithm 2.</summary>
internal static class CertificateCompression
{
    internal const ushort Brotli = 2;

    internal static bool TryDecompress(
        ushort algorithm,
        ReadOnlySpan<byte> compressed,
        int uncompressedLength,
        int maxOutput,
        Span<byte> destination,
        out int written,
        out HandshakeAlert alert)
    {
        written = 0;
        alert = HandshakeAlert.DecodeError;
        if (algorithm != Brotli || compressed.IsEmpty || uncompressedLength <= 0 || uncompressedLength > maxOutput || destination.Length < uncompressedLength)
        {
            return false;
        }

        var decoder = new BrotliDecoder();
        try
        {
            OperationStatus status = decoder.Decompress(compressed, destination[..uncompressedLength], out int consumed, out written);
            if (status != OperationStatus.Done || written != uncompressedLength || consumed != compressed.Length)
            {
                written = 0;
                return false;
            }
        }
        finally
        {
            decoder.Dispose();
        }

        alert = default;
        return true;
    }
}
