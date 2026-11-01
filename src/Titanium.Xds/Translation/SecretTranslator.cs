using Envoy.Extensions.TransportSockets.Tls.V3;

namespace Titanium.Xds.Translation;

public sealed class SecretMaterial
{
    public required string Name { get; init; }

    public string? CertificateChainPath { get; init; }

    public string? PrivateKeyPath { get; init; }

    public string? TrustedCaPath { get; init; }
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
                return TranslationResult<SecretMaterial>.Ok(new SecretMaterial
                {
                    Name = secret.Name,
                    CertificateChainPath = certPath,
                    PrivateKeyPath = keyPath,
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

    private static string WriteSecretFile(string stateDirectory, string secretName, string suffix, string pem)
    {
        var safeName = string.Concat(secretName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(stateDirectory, $"{safeName}.{suffix}");
        File.WriteAllText(path, pem);
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
