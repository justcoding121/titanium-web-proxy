using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Titanium.Gateway.Kube;

/// <summary>
///     Converts TLS Secrets (PEM <c>tls.crt</c> and <c>tls.key</c>) into the password-less PFX files the data plane
///     loads for SNI. A Secret that does not convert is reported as having no usable certificate, so the Gateway
///     listener that references it is marked invalid instead of the data plane failing to load its config.
///     Results are cached per Secret resource version.
/// </summary>
internal sealed class CertificateStore
{
    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);

    /// <summary>Returns the PFX for the Secret, or null when the Secret holds no certificate and key pair that match.</summary>
    public byte[]? GetPfx(SecretData secret)
    {
        var key = secret.Namespace + "/" + secret.Name;
        if (_cache.TryGetValue(key, out var cached) && cached.ResourceVersion == secret.ResourceVersion)
        {
            return cached.Pfx;
        }

        var pfx = Convert(secret);
        _cache[key] = new Entry(secret.ResourceVersion, pfx);
        return pfx;
    }

    /// <summary>Forgets Secrets that are no longer watched.</summary>
    public void Retain(IEnumerable<SecretData> live)
    {
        var keep = live.Select(s => s.Namespace + "/" + s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _cache.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _cache.TryRemove(key, out _);
        }
    }

    private static byte[]? Convert(SecretData secret)
    {
        if (secret.Certificate is null || secret.PrivateKey is null)
        {
            return null;
        }

        try
        {
            var certificatePem = Encoding.UTF8.GetString(secret.Certificate);
            var keyPem = Encoding.UTF8.GetString(secret.PrivateKey);
            using var leaf = X509Certificate2.CreateFromPem(certificatePem, keyPem);
            var chain = new X509Certificate2Collection();
            chain.ImportFromPem(certificatePem);
            try
            {
                var all = new X509Certificate2Collection { leaf };
                for (var i = 1; i < chain.Count; i++)
                {
                    all.Add(chain[i]);
                }

                return all.Export(X509ContentType.Pfx, password: null);
            }
            finally
            {
                foreach (var certificate in chain)
                {
                    certificate.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    private sealed record Entry(string ResourceVersion, byte[]? Pfx);
}
