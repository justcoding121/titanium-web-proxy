using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;

namespace Titanium.Web.Proxy.Network.Certificate;

/// <summary>
///     Resolves the BouncyCastle issuer private key and matching signature algorithm from a
///     .NET signing certificate so both BC makers stay aligned (RSA roots, custom ECDSA roots).
/// </summary>
internal static class BcCertificateIssuer
{
    internal const string Sha256WithRsa = "SHA256WithRSA";
    internal const string Sha256WithEcdsa = "SHA256WithECDSA";

    /// <summary>
    ///     Extracts the issuer private key for certificate signing. The returned disposable owns the
    ///     .NET key handle obtained from the certificate and must be disposed by the caller.
    /// </summary>
    internal static (AsymmetricKeyParameter PrivateKey, string SignatureAlgorithm, IDisposable Disposable)
        FromSigningCertificate(X509Certificate2 signingCertificate)
    {
        var rsa = signingCertificate.GetRSAPrivateKey();
        if (rsa != null)
        {
            var kp = DotNetUtilities.GetKeyPair(rsa);
            return (kp.Private, Sha256WithRsa, rsa);
        }

        var ecdsa = signingCertificate.GetECDsaPrivateKey();
        if (ecdsa != null)
        {
            var kp = DotNetUtilities.GetKeyPair(ecdsa);
            return (kp.Private, Sha256WithEcdsa, ecdsa);
        }

        throw new InvalidOperationException(
            "The signing certificate has neither an RSA nor an ECDSA private key.");
    }
}
