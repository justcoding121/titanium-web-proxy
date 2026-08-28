using System.Net;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;

namespace Titanium.Cli.Certificates;

/// <summary>Applies certificate paths and ACME HTTP-01 challenge responses.</summary>
internal static class CertificateBootstrap
{
    // In-memory token store for HTTP-01 (operators/Plus can replace via ReplaceCertificate after issuance).
    private static readonly Dictionary<string, string> AcmeTokens = new(StringComparer.Ordinal);

    public static void Apply(ProxyServer proxy, CertificatesConfig? certificates)
    {
        if (certificates is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(certificates.CertificatePath))
        {
            Console.WriteLine($"Certificate path configured: {certificates.CertificatePath}");
            if (TryLoadLeaf(certificates.CertificatePath, certificates.PrivateKeyPath, out var leaf) && leaf is not null)
            {
                AssignGenericCertificate(proxy, leaf);
                Console.WriteLine("Loaded leaf certificate onto DecryptSsl endpoints.");
            }
            else if (File.Exists(certificates.CertificatePath))
            {
                Console.WriteLine(
                    "Certificate file present but could not load (need PEM+PrivateKeyPath or PFX).");
            }
        }

        if (string.IsNullOrEmpty(certificates.AcmeDomain))
        {
            return;
        }

        Console.WriteLine($"ACME domain configured: {certificates.AcmeDomain} (email={certificates.AcmeEmail ?? "(none)"})");
        Console.WriteLine("HTTP-01: place challenge tokens via env TITANIUM_ACME_TOKEN=token:keyAuth or file under .well-known.");

        if (!string.IsNullOrEmpty(certificates.CertificatePath) &&
            !File.Exists(certificates.CertificatePath))
        {
            Console.WriteLine(
                "Note: ACME domain set but certificate files are missing. " +
                "Serve HTTP-01, place PEM/PFX at CertificatePath (+ PrivateKeyPath), then call ReplaceCertificate " +
                "or IssueOrRenewAsync (also honors TITANIUM_ACME_CERT_PATH).");
        }

        // Optional single token from environment: TOKEN:KEYAUTH
        var env = Environment.GetEnvironmentVariable("TITANIUM_ACME_TOKEN");
        if (!string.IsNullOrEmpty(env))
        {
            var parts = env.Split(':', 2);
            if (parts.Length == 2)
            {
                AcmeTokens[parts[0]] = parts[1];
            }
        }

        proxy.BeforeRequest += (_, e) =>
        {
            var path = e.HttpClient.Request.RequestUri?.AbsolutePath ?? "";
            const string prefix = "/.well-known/acme-challenge/";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return Task.CompletedTask;
            }

            var token = path[prefix.Length..];
            if (AcmeTokens.TryGetValue(token, out var keyAuth))
            {
                e.Ok(keyAuth, [new HttpHeader("Content-Type", "text/plain")]);
                return Task.CompletedTask;
            }

            // Also try reading from a local challenge directory next to the process.
            var file = Path.Combine(AppContext.BaseDirectory, ".well-known", "acme-challenge", token);
            if (File.Exists(file))
            {
                e.Ok(File.ReadAllText(file), [new HttpHeader("Content-Type", "text/plain")]);
                return Task.CompletedTask;
            }

            e.GenericResponse("challenge not found", HttpStatusCode.NotFound);
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Reloads a leaf certificate from disk and assigns it to all <see cref="ProxyEndPoint.DecryptSsl"/> endpoints.
    /// Call after ACME issuance when cert files appear (or when <c>TITANIUM_ACME_CERT_PATH</c> is set).
    /// </summary>
    public static void ReplaceCertificate(ProxyServer proxy, string certPath, string? keyPath)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentException.ThrowIfNullOrWhiteSpace(certPath);

        if (!TryLoadLeaf(certPath, keyPath, out var leaf) || leaf is null)
        {
            throw new InvalidOperationException(
                $"Unable to load certificate from '{certPath}' (key='{keyPath ?? "(none)"}').");
        }

        AssignGenericCertificate(proxy, leaf);
        Console.WriteLine($"Replaced GenericCertificate from {certPath}.");
    }

    /// <summary>
    /// Stub ACME issuance: when email+domain are set, documents the operator path and seeds challenge tokens from env.
    /// Does not write a self-signed leaf; after the challenge, place certs and call <see cref="ReplaceCertificate"/>.
    /// </summary>
    public static Task IssueAcmeCertificateAsync(ProxyServer proxy, CertificatesConfig certificates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(certificates);

        if (string.IsNullOrEmpty(certificates.AcmeEmail) || string.IsNullOrEmpty(certificates.AcmeDomain))
        {
            return Task.CompletedTask;
        }

        Console.WriteLine(
            $"ACME issue stub for {certificates.AcmeDomain} ({certificates.AcmeEmail}): " +
            "operators should complete HTTP-01 externally and place PEM/PFX at CertificatePath, " +
            "then call ReplaceCertificate / IssueOrRenewAsync.");

        var env = Environment.GetEnvironmentVariable("TITANIUM_ACME_TOKEN");
        if (!string.IsNullOrEmpty(env))
        {
            var parts = env.Split(':', 2);
            if (parts.Length == 2)
            {
                SetChallengeToken(parts[0], parts[1]);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// After HTTP-01 challenge serving is active, waits for cert files (config paths or
    /// <c>TITANIUM_ACME_CERT_PATH</c> / optional <c>TITANIUM_ACME_KEY_PATH</c>) then calls <see cref="ReplaceCertificate"/>.
    /// </summary>
    public static async Task IssueOrRenewAsync(ProxyServer proxy, CertificatesConfig certificates,
        TimeSpan? pollTimeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(certificates);

        await IssueAcmeCertificateAsync(proxy, certificates, cancellationToken).ConfigureAwait(false);

        var timeout = pollTimeout ?? TimeSpan.FromMinutes(5);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var envCert = Environment.GetEnvironmentVariable("TITANIUM_ACME_CERT_PATH");
            var envKey = Environment.GetEnvironmentVariable("TITANIUM_ACME_KEY_PATH");
            var certPath = !string.IsNullOrEmpty(envCert) ? envCert : certificates.CertificatePath;
            var keyPath = !string.IsNullOrEmpty(envKey) ? envKey : certificates.PrivateKeyPath;

            if (!string.IsNullOrEmpty(certPath) &&
                File.Exists(certPath) &&
                TryLoadLeaf(certPath, keyPath, out _))
            {
                ReplaceCertificate(proxy, certPath, keyPath);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine(
            "IssueOrRenewAsync timed out waiting for certificate files. " +
            "Place certs and call ReplaceCertificate when ready.");
    }

    public static void SetChallengeToken(string token, string keyAuthorization) =>
        AcmeTokens[token] = keyAuthorization;

    private static void AssignGenericCertificate(ProxyServer proxy, X509Certificate2 leaf)
    {
        foreach (var endPoint in proxy.ProxyEndPoints)
        {
            if (endPoint.DecryptSsl)
            {
                endPoint.GenericCertificate = leaf;
            }
        }
    }

    private static bool TryLoadLeaf(string certPath, string? keyPath, out X509Certificate2? leaf)
    {
        leaf = null;
        if (string.IsNullOrEmpty(certPath) || !File.Exists(certPath))
        {
            return false;
        }

        try
        {
            var ext = Path.GetExtension(certPath);
            if (ext.Equals(".pfx", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".p12", StringComparison.OrdinalIgnoreCase))
            {
                var password = Environment.GetEnvironmentVariable("TITANIUM_CERT_PASSWORD");
                leaf = X509CertificateLoader.LoadPkcs12(
                    File.ReadAllBytes(certPath),
                    password,
                    X509KeyStorageFlags.EphemeralKeySet);
                return true;
            }

            if (!string.IsNullOrEmpty(keyPath) && File.Exists(keyPath))
            {
                leaf = X509Certificate2.CreateFromPemFile(certPath, keyPath);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Certificate load failed: {ex.Message}");
            return false;
        }
    }
}
