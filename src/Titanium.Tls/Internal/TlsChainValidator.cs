using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Tls;

/// <summary>
/// Chain and hostname result for one Certificate message. The stapled OCSP response and the timestamps are copied out and are not checked.
/// </summary>
internal sealed class TlsCertificateCheck : IDisposable
{
    private readonly X509Certificate2[] _certificates;

    internal TlsCertificateCheck(SslPolicyErrors errors, X509Certificate2[] certificates, X509Chain? chain, byte[]? stapledStatus, byte[]? signedTimestamps)
    {
        Errors = errors;
        _certificates = certificates;
        Chain = chain;
        StapledStatus = stapledStatus;
        SignedCertificateTimestamps = signedTimestamps;
    }

    internal SslPolicyErrors Errors { get; }

    internal X509Chain? Chain { get; }

    internal byte[]? StapledStatus { get; }

    internal byte[]? SignedCertificateTimestamps { get; }

    public void Dispose()
    {
        Chain?.Dispose();
        foreach (X509Certificate2 certificate in _certificates)
        {
            certificate.Dispose();
        }
    }
}

/// <summary>
/// Builds an <see cref="X509Chain"/> the way <c>SslStream</c> does and reports <see cref="SslPolicyErrors"/>.
/// A stapled status is not passed to the chain. <see cref="X509Chain"/> cannot consume one.
/// </summary>
internal static class TlsChainValidator
{
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuth = "1.3.6.1.5.5.7.3.2";

    internal static TlsCertificateCheck Evaluate(ReadOnlySpan<byte> certificateList, string? targetHost, X509Certificate2Collection? trustAnchors, bool serverCertificate)
    {
        if (!TryReadCertificates(certificateList, out X509Certificate2[] certificates, out byte[]? stapled, out byte[]? timestamps))
        {
            return new TlsCertificateCheck(SslPolicyErrors.RemoteCertificateChainErrors, certificates, null, stapled, timestamps);
        }

        if (certificates.Length == 0)
        {
            return new TlsCertificateCheck(SslPolicyErrors.RemoteCertificateNotAvailable, certificates, null, stapled, timestamps);
        }

        var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(serverCertificate ? ServerAuth : ClientAuth));
        if (trustAnchors is not null)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(trustAnchors);
        }

        for (int i = 1; i < certificates.Length; i++)
        {
            chain.ChainPolicy.ExtraStore.Add(certificates[i]);
        }

        bool built = false;
        CryptoExecutionPolicy.Run(CryptoExecutionPolicy.Revocation(false), () => built = chain.Build(certificates[0]));
        SslPolicyErrors errors = built ? SslPolicyErrors.None : SslPolicyErrors.RemoteCertificateChainErrors;
        if (!HostMatches(certificates[0], targetHost))
        {
            errors |= SslPolicyErrors.RemoteCertificateNameMismatch;
        }

        return new TlsCertificateCheck(errors, certificates, chain, stapled, timestamps);
    }

    private static bool HostMatches(X509Certificate2 certificate, string? targetHost)
    {
        if (string.IsNullOrEmpty(targetHost))
        {
            return true;
        }

        return certificate.MatchesHostname(targetHost, allowWildcards: true, allowCommonName: true);
    }

    private static bool TryReadCertificates(ReadOnlySpan<byte> list, out X509Certificate2[] certificates, out byte[]? stapledStatus, out byte[]? signedTimestamps)
    {
        var loaded = new List<X509Certificate2>();
        stapledStatus = null;
        signedTimestamps = null;
        TlsReader reader = new(list);
        bool first = true;
        while (reader.Remaining > 0)
        {
            if (!reader.TryReadVector24(out ReadOnlySpan<byte> encoded) || encoded.IsEmpty || !reader.TryReadVector16(out ReadOnlySpan<byte> extensions))
            {
                certificates = loaded.ToArray();
                return false;
            }

            try
            {
                loaded.Add(X509CertificateLoader.LoadCertificate(encoded));
            }
            catch (CryptographicException)
            {
                certificates = loaded.ToArray();
                return false;
            }

            if (first)
            {
                stapledStatus = CopyExtension(extensions, ExtensionType.StatusRequest);
                signedTimestamps = CopyExtension(extensions, ExtensionType.SignedCertificateTimestamp);
                first = false;
            }
        }

        certificates = loaded.ToArray();
        return true;
    }

    private static byte[]? CopyExtension(ReadOnlySpan<byte> extensions, ushort type)
    {
        TlsReader reader = new(extensions);
        while (reader.Remaining > 0)
        {
            if (!reader.TryReadUInt16(out ushort found) || !reader.TryReadVector16(out ReadOnlySpan<byte> body))
            {
                return null;
            }

            if (found == type)
            {
                return body.ToArray();
            }
        }

        return null;
    }
}
