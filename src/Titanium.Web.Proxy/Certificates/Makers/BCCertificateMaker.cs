using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.X509;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Shared;
using X509Certificate = Org.BouncyCastle.X509.X509Certificate;

namespace Titanium.Web.Proxy.Network.Certificate;

/// <summary>
///     Implements certificate generation operations.
/// </summary>
internal class BcCertificateMaker : ICertificateMaker
{
    // The FriendlyName value cannot be set on Unix.
    // Set this flag to true when exception detected to avoid further exceptions
    private static bool _doNotSetFriendlyName;
    private readonly int certificateValidDays;
    private readonly int certificateGraceDays;
    private readonly CertificateKeyAlgorithm leafKeyAlgorithm;

    internal BcCertificateMaker(int certificateValidDays, int certificateGraceDays,
        CertificateKeyAlgorithm leafKeyAlgorithm = CertificateKeyAlgorithm.Rsa2048)
    {
        this.certificateValidDays = certificateValidDays;
        this.certificateGraceDays = certificateGraceDays;
        this.leafKeyAlgorithm = leafKeyAlgorithm;
    }

    /// <summary>
    ///     Makes the certificate.
    /// </summary>
    /// <param name="sSubjectCn">The s subject cn.</param>
    /// <param name="signingCert">The signing cert.</param>
    /// <returns>X509Certificate2 instance.</returns>
    public X509Certificate2 MakeCertificate(string sSubjectCn, X509Certificate2? signingCert)
    {
        return MakeCertificateInternal(sSubjectCn, signingCert);
    }

    /// <summary>
    ///     Generates the certificate.
    /// </summary>
    /// <param name="subjectName">Name of the subject.</param>
    /// <param name="issuerDn">
    ///     The issuer distinguished name. Pass a value derived from the signing certificate's
    ///     <c>SubjectName.RawData</c> to preserve exact DER encoding, RDN order, and non-ASCII characters.
    /// </param>
    /// <param name="validFrom">The valid from.</param>
    /// <param name="validTo">The valid to.</param>
    /// <param name="keyStrength">The key strength.</param>
    /// <param name="signatureAlgorithm">The signature algorithm.</param>
    /// <param name="issuerPrivateKey">The issuer private key.</param>
    /// <param name="hostName">The host name</param>
    /// <returns>X509Certificate2 instance.</returns>
    /// <exception cref="PemException">Malformed sequence in RSA private key</exception>
    private static X509Certificate2 GenerateCertificate(string? hostName, // NOSONAR S107 -- Certificate fields map directly to the generated X.509 structure.
        string subjectName,
        X509Name issuerDn, DateTime validFrom,
        DateTime validTo, int keyStrength = 2048,
        string signatureAlgorithm = BcCertificateIssuer.Sha256WithRsa,
        AsymmetricKeyParameter? issuerPrivateKey = null,
        CertificateKeyAlgorithm keyAlgorithm = CertificateKeyAlgorithm.Rsa2048)
    {
        // Generating Random Numbers
        var randomGenerator = new CryptoApiRandomGenerator();
        var secureRandom = new SecureRandom(randomGenerator);

        // The Certificate Generator
        var certificateGenerator = new X509V3CertificateGenerator();

        // Serial Number
        var serialNumber =
            BigIntegers.CreateRandomInRange(BigInteger.One, BigInteger.ValueOf(long.MaxValue), secureRandom);
        certificateGenerator.SetSerialNumber(serialNumber);

        // Issuer and Subject Name — issuerDn is passed directly so that custom roots with C/O/L/CN
        // or escaped RDN values are reproduced exactly from their DER encoding rather than being
        // round-tripped through a display-string representation.
        var subjectDn = new X509Name(subjectName);
        certificateGenerator.SetIssuerDN(issuerDn);
        certificateGenerator.SetSubjectDN(subjectDn);

        certificateGenerator.SetNotBefore(validFrom);
        certificateGenerator.SetNotAfter(validTo);

        if (hostName != null)
        {
            // add subject alternative names
            var nameType = GeneralName.DnsName;
            if (IPAddress.TryParse(hostName, out _)) nameType = GeneralName.IPAddress;

            var subjectAlternativeNames = new Asn1Encodable[] { new GeneralName(nameType, hostName) };

            var subjectAlternativeNamesExtension = new DerSequence(subjectAlternativeNames);
            certificateGenerator.AddExtension(X509Extensions.SubjectAlternativeName.Id, false,
                subjectAlternativeNamesExtension);
        }

        // Subject public key, unique to this certificate. For RSA it usually comes from a buffer that
        // is refilled in the background, so its cost was paid before this certificate was asked for.
        var subjectKeyPair = LeafKeyPairSource.Rent(keyAlgorithm, keyStrength);

        certificateGenerator.SetPublicKey(subjectKeyPair.Public);

        // Set certificate intended purposes to only Server Authentication
        certificateGenerator.AddExtension(X509Extensions.ExtendedKeyUsage.Id, false,
            new ExtendedKeyUsage(KeyPurposeID.id_kp_serverAuth));
        if (issuerPrivateKey == null)
        {
            certificateGenerator.AddExtension(X509Extensions.BasicConstraints.Id, true, new BasicConstraints(true));
            certificateGenerator.AddExtension(X509Extensions.KeyUsage.Id, true,
                new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign));
        }
        else
        {
            // ECDSA leaves must not advertise keyEncipherment; RSA leaves need it for legacy TLS suites.
            var leafUsage = keyAlgorithm == CertificateKeyAlgorithm.EcdsaP256
                ? KeyUsage.DigitalSignature
                : KeyUsage.DigitalSignature | KeyUsage.KeyEncipherment;
            certificateGenerator.AddExtension(X509Extensions.KeyUsage.Id, false, new KeyUsage(leafUsage));
        }

        var signatureFactory = new Asn1SignatureFactory(signatureAlgorithm,
            issuerPrivateKey ?? subjectKeyPair.Private, secureRandom);

        // Self-sign the certificate
        var certificate = certificateGenerator.Generate(signatureFactory);

        // Corresponding private key
        var privateKey = subjectKeyPair.Private;

        if (privateKey is RsaKeyParameters)
        {
            var privateKeyInfo = PrivateKeyInfoFactory.CreatePrivateKeyInfo(privateKey);

            var seq = (Asn1Sequence)Asn1Object.FromByteArray(privateKeyInfo.ParsePrivateKey().GetDerEncoded());

            if (seq.Count != 9) throw new PemException("Malformed sequence in RSA private key");

            var rsa = RsaPrivateKeyStructure.GetInstance(seq);
            privateKey = new RsaPrivateCrtKeyParameters(rsa.Modulus, rsa.PublicExponent, rsa.PrivateExponent,
                rsa.Prime1, rsa.Prime2, rsa.Exponent1,
                rsa.Exponent2, rsa.Coefficient);
        }

        // Set private key onto certificate instance
        var x509Certificate = WithPrivateKey(certificate, privateKey);

        if (!_doNotSetFriendlyName && RunTime.IsWindows)
            try
            {
                x509Certificate.FriendlyName = ProxyConstants.CnRemoverRegex.Replace(subjectName, string.Empty);
            }
            catch (PlatformNotSupportedException)
            {
                _doNotSetFriendlyName = true;
            }

        return x509Certificate;
    }

    private static X509Certificate2 WithPrivateKey(X509Certificate certificate, AsymmetricKeyParameter privateKey)
    {
        // On non-Windows (notably macOS), importing a PKCS#12 blob with X509KeyStorageFlags.Exportable
        // throws PlatformNotSupportedException. Attach the private key in-memory instead.
        // Use ToRSAParameters + RSA.Create (not DotNetUtilities.ToRSA) — ToRSA is Windows-only via CAPI.
        if (!RunTime.IsWindows)
        {
            // CopyWithPrivateKey returns a brand-new X509Certificate2 combining the two; both
            // intermediates below hold unmanaged crypto handles (a native cert context and a key
            // handle respectively) that would otherwise sit unreleased until the next GC/finalizer pass.
            using var publicOnly = CertificateLoader.LoadCertificate(certificate.GetEncoded());

            if (privateKey is ECPrivateKeyParameters)
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportPkcs8PrivateKey(
                    PrivateKeyInfoFactory.CreatePrivateKeyInfo(privateKey).GetDerEncoded(), out _);
                return publicOnly.CopyWithPrivateKey(ecdsa);
            }

            using var rsa = RSA.Create();
            rsa.ImportParameters(DotNetUtilities.ToRSAParameters((RsaPrivateCrtKeyParameters)privateKey));
            return publicOnly.CopyWithPrivateKey(rsa);
        }

        const string password = "password";

        var builder = new Pkcs12StoreBuilder();
        if (RunTime.IsRunningOnMono)
        {
            builder.SetUseDerEncoding(true);
        }

        var store = builder.Build();
        var entry = new X509CertificateEntry(certificate);
        store.SetCertificateEntry(certificate.SubjectDN.ToString(), entry);

        store.SetKeyEntry(certificate.SubjectDN.ToString(), new AsymmetricKeyEntry(privateKey), new[] { entry });
        using (var ms = new MemoryStream())
        {
            store.Save(ms, password.ToCharArray(), new SecureRandom(new CryptoApiRandomGenerator()));

            return CertificateLoader.LoadPkcs12(ms.ToArray(), password, X509KeyStorageFlags.Exportable);
        }
    }

    /// <summary>
    ///     Makes the certificate internal.
    /// </summary>
    /// <param name="hostName">hostname for certificate</param>
    /// <param name="subjectName">The full subject.</param>
    /// <param name="validFrom">The valid from.</param>
    /// <param name="validTo">The valid to.</param>
    /// <param name="signingCertificate">The signing certificate.</param>
    /// <returns>X509Certificate2 instance.</returns>
    /// <exception cref="System.ArgumentException">
    ///     You must specify a Signing Certificate if and only if you are not creating a
    ///     root.
    /// </exception>
    private X509Certificate2 MakeCertificateInternal(string hostName, string subjectName,
        DateTime validFrom, DateTime validTo, X509Certificate2? signingCertificate)
    {
        // A self-signed certificate here is the root itself, which stays RSA whatever leaves use: it is
        // the certificate the user installs into a trust store, and it signs every leaf below.
        if (signingCertificate == null)
            return GenerateCertificate(null, subjectName, new X509Name(subjectName), validFrom, validTo);

        // Derive the issuer DN directly from the signing certificate's raw DER-encoded subject so that
        // RDN order, multi-valued RDNs, escaped characters, and non-ASCII values are preserved exactly.
        var issuerDn = X509Name.GetInstance(Asn1Object.FromByteArray(signingCertificate.SubjectName.RawData));

        var (issuerPrivateKey, signatureAlgorithm, disposable) =
            BcCertificateIssuer.FromSigningCertificate(signingCertificate);
        using (disposable)
        {
            return GenerateCertificate(hostName, subjectName, issuerDn, validFrom, validTo,
                signatureAlgorithm: signatureAlgorithm, issuerPrivateKey: issuerPrivateKey,
                keyAlgorithm: leafKeyAlgorithm);
        }
    }

    /// <summary>
    ///     Makes the certificate internal.
    /// </summary>
    /// <param name="subject">The s subject cn.</param>
    /// <param name="signingCert">The signing cert.</param>
    /// <returns>X509Certificate2.</returns>
    private X509Certificate2 MakeCertificateInternal(string subject,
        X509Certificate2? signingCert = null)
    {
        return MakeCertificateInternal(subject, $"CN={subject}",
            DateTime.UtcNow.AddDays(-certificateGraceDays), DateTime.UtcNow.AddDays(certificateValidDays),
            signingCert);
    }
}