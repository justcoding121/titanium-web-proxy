using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Extension;

namespace Titanium.Web.Proxy.Network.Certificate;

/// <summary>
///     Subject / Authority Key Identifier helpers shared by the BouncyCastle certificate makers.
///     Several Titanium roots with the same common name can sit in a trust store at once (each CA regeneration
///     adds one); without key identifiers a chain builder has to guess which root signed a leaf by name alone.
///     Roots always carry an SKI. A leaf only carries an AKI when its issuer has an SKI, so leaves signed by a
///     legacy root that predates this change stay exactly as they were and keep validating against it.
/// </summary>
internal static class CertificateKeyIdentifiers
{
    private const string SubjectKeyIdentifierOid = "2.5.29.14";

    internal static void AddSubjectKeyIdentifier(X509V3CertificateGenerator generator, AsymmetricKeyParameter publicKey)
    {
        generator.AddExtension(X509Extensions.SubjectKeyIdentifier.Id, false,
            X509ExtensionUtilities.CreateSubjectKeyIdentifier(publicKey));
    }

    /// <summary>The issuer's SKI bytes, or <see langword="null" /> when it has none (legacy root).</summary>
    internal static byte[]? TryGetSubjectKeyIdentifier(X509Certificate2 issuer)
    {
        foreach (var extension in issuer.Extensions)
        {
            if (extension.Oid?.Value == SubjectKeyIdentifierOid && extension is X509SubjectKeyIdentifierExtension ski)
                return ski.SubjectKeyIdentifierBytes.ToArray();
        }

        return null;
    }

    internal static void AddAuthorityKeyIdentifier(X509V3CertificateGenerator generator, byte[]? issuerKeyId)
    {
        if (issuerKeyId == null)
            return;

        generator.AddExtension(X509Extensions.AuthorityKeyIdentifier.Id, false,
            new AuthorityKeyIdentifier(issuerKeyId));
    }
}
