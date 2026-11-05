using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Titanium.Gateway.Translation;

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
    public byte[]? GetPfx(SecretData secret) => Lookup(secret).Pfx;

    /// <summary>True when the Secret holds a <c>ca.crt</c> with at least one parseable certificate.</summary>
    public bool HasCa(SecretData secret) => Lookup(secret).HasCa;

    /// <summary>
    ///     The bytes of one file the data plane reads as PEM (an upstream client certificate, its key, or a trust
    ///     bundle). Null when the Secret does not hold a usable certificate and key pair (or CA bundle).
    /// </summary>
    public byte[]? GetPem(SecretData secret, TlsFileKind kind)
    {
        var entry = Lookup(secret);
        return kind switch
        {
            TlsFileKind.CertificatePem => entry.Pfx is null ? null : secret.Certificate,
            TlsFileKind.PrivateKeyPem => entry.Pfx is null ? null : secret.PrivateKey,
            TlsFileKind.CaPem => entry.HasCa ? secret.CaCertificate : null,
            _ => entry.Pfx,
        };
    }

    private Entry Lookup(SecretData secret)
    {
        var key = secret.Namespace + "/" + secret.Name;
        if (_cache.TryGetValue(key, out var cached) && cached.ResourceVersion == secret.ResourceVersion)
        {
            return cached;
        }

        var entry = new Entry(secret.ResourceVersion, Convert(secret), ParsesAsCa(secret.CaCertificate));
        _cache[key] = entry;
        return entry;
    }

    private static bool ParsesAsCa(byte[]? pem)
    {
        if (pem is null)
        {
            return false;
        }

        try
        {
            var collection = new X509Certificate2Collection();
            collection.ImportFromPem(Encoding.UTF8.GetString(pem));
            var found = collection.Count > 0;
            foreach (var certificate in collection)
            {
                certificate.Dispose();
            }

            return found;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            return false;
        }
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

    private sealed record Entry(string ResourceVersion, byte[]? Pfx, bool HasCa);
}
