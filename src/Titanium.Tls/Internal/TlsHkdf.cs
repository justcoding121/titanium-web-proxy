using System;
using System.Security.Cryptography;

namespace Titanium.Tls;

/// <summary>
/// TLS 1.3 HKDF-Expand-Label and Derive-Secret (RFC 8446 section 7.1).
/// Calls are synchronous. The label is the short label, without the "tls13 " prefix.
/// </summary>
internal static class TlsHkdf
{
    private static ReadOnlySpan<byte> LabelPrefix => "tls13 "u8;

    internal static int HashLength(HashAlgorithmName hash)
    {
        if (hash == HashAlgorithmName.SHA256)
        {
            return 32;
        }

        if (hash == HashAlgorithmName.SHA384)
        {
            return 48;
        }

        throw new ArgumentException("TLS 1.3 uses SHA-256 or SHA-384.", nameof(hash));
    }

    internal static void Extract(HashAlgorithmName hash, ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, Span<byte> prk)
    {
        HKDF.Extract(hash, ikm, salt, prk);
    }

    internal static void ExpandLabel(
        HashAlgorithmName hash,
        ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> label,
        ReadOnlySpan<byte> context,
        Span<byte> output)
    {
        if (label.Length > 249)
        {
            throw new ArgumentException("TLS 1.3 labels are at most 249 bytes.", nameof(label));
        }

        if (context.Length > 255)
        {
            throw new ArgumentException("HKDF label context is at most 255 bytes.", nameof(context));
        }

        if (output.Length > ushort.MaxValue)
        {
            throw new ArgumentException("HKDF output is at most 65535 bytes.", nameof(output));
        }

        int infoLength = 2 + 1 + LabelPrefix.Length + label.Length + 1 + context.Length;
        Span<byte> info = infoLength <= 512 ? stackalloc byte[infoLength] : new byte[infoLength];
        WriteInfo(info, output.Length, label, context);
        HKDF.Expand(hash, secret, output, info);
    }

    internal static void Hash(HashAlgorithmName hash, ReadOnlySpan<byte> data, Span<byte> destination)
    {
        int hashLength = HashLength(hash);
        if (destination.Length < hashLength)
        {
            throw new ArgumentException("Destination is shorter than the hash.", nameof(destination));
        }

        if (hash == HashAlgorithmName.SHA256)
        {
            SHA256.HashData(data, destination[..hashLength]);
            return;
        }

        SHA384.HashData(data, destination[..hashLength]);
    }

    internal static void DeriveSecret(
        HashAlgorithmName hash,
        ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> label,
        ReadOnlySpan<byte> transcriptHash,
        Span<byte> output)
    {
        ExpandLabel(hash, secret, label, transcriptHash, output);
    }

    private static void WriteInfo(Span<byte> info, int length, ReadOnlySpan<byte> label, ReadOnlySpan<byte> context)
    {
        info[0] = (byte)(length >> 8);
        info[1] = (byte)length;
        int labelLength = LabelPrefix.Length + label.Length;
        info[2] = (byte)labelLength;
        LabelPrefix.CopyTo(info.Slice(3));
        label.CopyTo(info.Slice(3 + LabelPrefix.Length));
        int contextAt = 3 + labelLength;
        info[contextAt] = (byte)context.Length;
        context.CopyTo(info.Slice(contextAt + 1));
    }
}
