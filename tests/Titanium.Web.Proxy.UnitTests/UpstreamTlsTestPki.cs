using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace Titanium.Web.Proxy.TestSupport;

/// <summary>
///     Throwaway CA plus the certificates and PEM/PFX files that upstream TLS tests need. Shared by the unit
///     and integration test projects (linked file).
/// </summary>
internal sealed class UpstreamTlsTestPki : IDisposable
{
    private readonly X509Certificate2 ca;
    private int serial = 1;

    public UpstreamTlsTestPki(string commonName = "Upstream TLS Test CA")
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("twp-upstream-tls-").FullName;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        ca = Persist(selfSigned);
    }

    public string Directory { get; }

    public X509Certificate2 CaCertificate => ca;

    /// <summary>Writes the CA as PEM and returns the path.</summary>
    public string WriteCaPem(string name = "ca.pem")
    {
        var path = Path.Combine(Directory, name);
        File.WriteAllText(path, ca.ExportCertificatePem());
        return path;
    }

    public X509Certificate2 IssueServer(params string[] dnsNames) =>
        Issue("CN=" + (dnsNames.Length > 0 ? dnsNames[0] : "server"), dnsNames, "1.3.6.1.5.5.7.3.1");

    public X509Certificate2 IssueClient(string commonName = "twp-client") =>
        Issue("CN=" + commonName, [], "1.3.6.1.5.5.7.3.2");

    /// <summary>Writes a certificate and its key as two PEM files.</summary>
    public (string CertPath, string KeyPath) WritePemPair(X509Certificate2 certificate, string stem = "client")
    {
        var certPath = Path.Combine(Directory, stem + ".crt");
        var keyPath = Path.Combine(Directory, stem + ".key");
        File.WriteAllText(certPath, certificate.ExportCertificatePem());
        using var rsa = certificate.GetRSAPrivateKey()!;
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        return (certPath, keyPath);
    }

    /// <summary>Writes a certificate and its key as an encrypted PEM key plus certificate.</summary>
    public (string CertPath, string KeyPath) WriteEncryptedPemPair(X509Certificate2 certificate, string password,
        string stem = "client-enc")
    {
        var certPath = Path.Combine(Directory, stem + ".crt");
        var keyPath = Path.Combine(Directory, stem + ".key");
        File.WriteAllText(certPath, certificate.ExportCertificatePem());
        using var rsa = certificate.GetRSAPrivateKey()!;
        File.WriteAllText(keyPath, rsa.ExportEncryptedPkcs8PrivateKeyPem(password,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 2000)));
        return (certPath, keyPath);
    }

    public string WritePfx(X509Certificate2 certificate, string? password, string name = "client.pfx")
    {
        var path = Path.Combine(Directory, name);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, password));
        return path;
    }

    public void Dispose()
    {
        ca.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, true);
        }
        catch (IOException)
        {
            // Temp files; the OS cleans up eventually.
        }
        catch (UnauthorizedAccessException)
        {
            // A handle may still be open on Windows.
        }
    }

    private X509Certificate2 Issue(string subject, string[] dnsNames, string ekuOid)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid(ekuOid) }, false));
        if (dnsNames.Length > 0)
        {
            var san = new SubjectAlternativeNameBuilder();
            foreach (var name in dnsNames)
            {
                san.AddDnsName(name);
            }

            request.CertificateExtensions.Add(san.Build());
        }

        var serialNumber = BitConverter.GetBytes(Interlocked.Increment(ref serial));
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29),
            serialNumber);
        using var withKey = issued.CopyWithPrivateKey(key);
        return Persist(withKey);
    }

    /// <summary>SChannel needs a persisted key; an ephemeral one makes the TLS handshake fail on Windows.</summary>
    private static X509Certificate2 Persist(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null,
            X509KeyStorageFlags.Exportable);
}
