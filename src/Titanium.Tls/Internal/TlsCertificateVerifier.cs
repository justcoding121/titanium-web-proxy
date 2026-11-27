using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Tls;

/// <summary>
/// Checks a TLS 1.3 CertificateVerify signature. SHA-1 and PKCS#1 v1.5 schemes are rejected.
/// </summary>
internal static class TlsCertificateVerifier
{
    private const int MinimumRsaBits = 2048;
    private const string P256 = "1.2.840.10045.3.1.7";
    private const string P384 = "1.3.132.0.34";
    private const string RsaPss = "1.2.840.113549.1.1.10";

    internal static bool Verify(X509Certificate2 certificate, ushort scheme, ReadOnlySpan<byte> content, ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        try
        {
            return scheme switch
            {
                0x0403 => VerifyEcdsa(certificate, content, signature, HashAlgorithmName.SHA256, P256),
                0x0503 => VerifyEcdsa(certificate, content, signature, HashAlgorithmName.SHA384, P384),
                0x0804 => VerifyRsaPss(certificate, content, signature, HashAlgorithmName.SHA256, rsaPssKey: false),
                0x0805 => VerifyRsaPss(certificate, content, signature, HashAlgorithmName.SHA384, rsaPssKey: false),
                0x0806 => VerifyRsaPss(certificate, content, signature, HashAlgorithmName.SHA512, rsaPssKey: false),
                0x0809 => VerifyRsaPss(certificate, content, signature, HashAlgorithmName.SHA256, rsaPssKey: true),
                0x080A => VerifyRsaPss(certificate, content, signature, HashAlgorithmName.SHA384, rsaPssKey: true),
                0x080B => VerifyRsaPss(certificate, content, signature, HashAlgorithmName.SHA512, rsaPssKey: true),
                _ => false,
            };
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool VerifyEcdsa(X509Certificate2 certificate, ReadOnlySpan<byte> content, ReadOnlySpan<byte> signature, HashAlgorithmName hash, string curveOid)
    {
        using ECDsa? key = certificate.GetECDsaPublicKey();
        if (key is null || !string.Equals(key.ExportParameters(false).Curve.Oid.Value, curveOid, StringComparison.Ordinal))
        {
            return false;
        }

        return key.VerifyData(content, signature, hash, DSASignatureFormat.Rfc3279DerSequence);
    }

    private static bool VerifyRsaPss(X509Certificate2 certificate, ReadOnlySpan<byte> content, ReadOnlySpan<byte> signature, HashAlgorithmName hash, bool rsaPssKey)
    {
        bool pssKey = string.Equals(certificate.PublicKey.Oid.Value, RsaPss, StringComparison.Ordinal);
        if (pssKey != rsaPssKey)
        {
            return false;
        }

        using RSA? key = certificate.GetRSAPublicKey();
        if (key is null || key.KeySize < MinimumRsaBits)
        {
            return false;
        }

        return key.VerifyData(content, signature, hash, RSASignaturePadding.Pss);
    }
}
