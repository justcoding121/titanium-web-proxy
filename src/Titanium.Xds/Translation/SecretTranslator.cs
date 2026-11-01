using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Envoy.Extensions.TransportSockets.Tls.V3;

namespace Titanium.Xds.Translation;

public sealed class SecretMaterial
{
    public required string Name { get; init; }

    public string? CertificateChainPath { get; init; }

    public string? PrivateKeyPath { get; init; }

    public string? TrustedCaPath { get; init; }

    /// <summary>PKCS#12 export (no password) of the certificate and key, bound by SNI in the CLI.</summary>
    public string? PfxPath { get; init; }

    /// <summary>Server names the certificate answers for (DNS SANs, else the common name, else <c>*</c>).</summary>
    public IReadOnlyList<string> Hosts { get; init; } = [];
}

public static class SecretTranslator
{
    public static TranslationResult<SecretMaterial> Translate(Secret secret, string stateDirectory)
    {
        if (string.IsNullOrWhiteSpace(secret.Name))
        {
            return TranslationResult<SecretMaterial>.Fail(new TranslationError
            {
                ResourceName = "(unnamed)",
                FieldPath = "name",
                Message = "secret name is required",
            });
        }

        Directory.CreateDirectory(stateDirectory);
        switch (secret.TypeCase)
        {
            case Secret.TypeOneofCase.TlsCertificate:
            {
                var cert = secret.TlsCertificate;
                if (string.IsNullOrWhiteSpace(cert.CertificateChain) || string.IsNullOrWhiteSpace(cert.PrivateKey))
                {
                    return TranslationResult<SecretMaterial>.Fail(new TranslationError
                    {
                        ResourceName = secret.Name,
                        FieldPath = "tls_certificate",
                        Message = "certificate_chain and private_key are required",
                    });
                }

                var certPath = WriteSecretFile(stateDirectory, secret.Name, "cert.pem", cert.CertificateChain);
                var keyPath = WriteSecretFile(stateDirectory, secret.Name, "key.pem", cert.PrivateKey);

                // The CLI binds certificates by SNI from PKCS#12 files, so write one next to the PEM pair. A pair
                // that does not parse is a NACK: the control plane learns about it instead of serving a dead cert.
                string pfxPath;
                IReadOnlyList<string> hosts;
                try
                {
                    using var pem = X509Certificate2.CreateFromPem(cert.CertificateChain, cert.PrivateKey);
                    hosts = ReadHostNames(pem);
                    var pfxBytes = pem.Export(X509ContentType.Pfx);
                    pfxPath = WriteSecretBytes(stateDirectory, secret.Name, "pfx", pfxBytes);
                }
                catch (Exception ex) when (ex is CryptographicException or ArgumentException)
                {
                    return TranslationResult<SecretMaterial>.Fail(new TranslationError
                    {
                        ResourceName = secret.Name,
                        FieldPath = "tls_certificate",
                        Message = "certificate_chain and private_key do not form a valid pair: " + ex.Message,
                    });
                }

                return TranslationResult<SecretMaterial>.Ok(new SecretMaterial
                {
                    Name = secret.Name,
                    CertificateChainPath = certPath,
                    PrivateKeyPath = keyPath,
                    PfxPath = pfxPath,
                    Hosts = hosts,
                });
            }
            case Secret.TypeOneofCase.ValidationContext:
            {
                var ctx = secret.ValidationContext;
                if (string.IsNullOrWhiteSpace(ctx.TrustedCa))
                {
                    return TranslationResult<SecretMaterial>.Fail(new TranslationError
                    {
                        ResourceName = secret.Name,
                        FieldPath = "validation_context.trusted_ca",
                        Message = "trusted_ca is required",
                    });
                }

                var caPath = WriteSecretFile(stateDirectory, secret.Name, "ca.pem", ctx.TrustedCa);
                return TranslationResult<SecretMaterial>.Ok(new SecretMaterial
                {
                    Name = secret.Name,
                    TrustedCaPath = caPath,
                });
            }
            default:
                return TranslationResult<SecretMaterial>.Fail(new TranslationError
                {
                    ResourceName = secret.Name,
                    FieldPath = "type",
                    Message = "unsupported secret type",
                });
        }
    }

    /// <summary>DNS names from the subject alternative name extension, or the common name, or <c>*</c> (any server name).</summary>
    private static IReadOnlyList<string> ReadHostNames(X509Certificate2 certificate)
    {
        var names = new List<string>();
        foreach (var extension in certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>())
        {
            names.AddRange(extension.EnumerateDnsNames());
        }

        if (names.Count == 0 && certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false) is { Length: > 0 } cn)
        {
            names.Add(cn);
        }

        return names.Count == 0 ? ["*"] : names;
    }

    private static string WriteSecretFile(string stateDirectory, string secretName, string suffix, string pem) =>
        WriteSecretBytes(stateDirectory, secretName, suffix, System.Text.Encoding.UTF8.GetBytes(pem));

    private static string WriteSecretBytes(string stateDirectory, string secretName, string suffix, byte[] content)
    {
        var safeName = string.Concat(secretName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(stateDirectory, $"{safeName}.{suffix}");
        File.WriteAllBytes(path, content);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (IOException)
            {
                // best effort on platforms that support it
            }
        }

        return path;
    }
}
