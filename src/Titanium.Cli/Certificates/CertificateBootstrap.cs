using System.Net;
using System.Security.Cryptography.X509Certificates;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Titanium.Cli;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;

namespace Titanium.Cli.Certificates;

/// <summary>Applies certificate paths and ACME HTTP-01 challenge / issuance.</summary>
internal static class CertificateBootstrap
{
    private static readonly Dictionary<string, string> AcmeTokens = new(StringComparer.Ordinal);

    /// <summary>Optional test hook replacing Certes issuance.</summary>
    internal static Func<ProxyServer, CertificatesConfig, CancellationToken, Task>? IssueOverride { get; set; }

    public static void Apply(ProxyServer proxy, CertificatesConfig? certificates)
    {
        if (certificates is null)
        {
            return;
        }

        TryApplyLeafCertificate(proxy, certificates);
        if (string.IsNullOrEmpty(certificates.AcmeDomain))
        {
            return;
        }

        RegisterAcmeChallengeHandling(proxy, certificates);
    }

    private static void TryApplyLeafCertificate(ProxyServer proxy, CertificatesConfig certificates)
    {
        if (string.IsNullOrEmpty(certificates.CertificatePath))
        {
            return;
        }

        AsyncConsole.WriteLine($"Certificate path configured: {certificates.CertificatePath}");
        if (TryLoadLeaf(certificates.CertificatePath, certificates.PrivateKeyPath, out var leaf) && leaf is not null)
        {
            AssignGenericCertificate(proxy, leaf);
            AsyncConsole.WriteLine("Loaded leaf certificate onto DecryptSsl endpoints.");
        }
        else if (File.Exists(certificates.CertificatePath))
        {
            AsyncConsole.WriteLine(
                "Certificate file present but could not load (need PEM+PrivateKeyPath or PFX).");
        }
    }

    private static void RegisterAcmeChallengeHandling(ProxyServer proxy, CertificatesConfig certificates)
    {
        AsyncConsole.WriteLine($"ACME domain configured: {certificates.AcmeDomain} (email={certificates.AcmeEmail ?? "(none)"})");
        AsyncConsole.WriteLine("HTTP-01: place challenge tokens via env TITANIUM_ACME_TOKEN=token:keyAuth or file under .well-known.");

        if (!string.IsNullOrEmpty(certificates.CertificatePath) &&
            !File.Exists(certificates.CertificatePath))
        {
            AsyncConsole.WriteLine(
                "Note: ACME domain set but certificate files are missing. " +
                "Serve HTTP-01, place PEM/PFX at CertificatePath (+ PrivateKeyPath), then call ReplaceCertificate " +
                "or IssueOrRenewAsync (also honors TITANIUM_ACME_CERT_PATH).");
        }

        SeedTokenFromEnvironment();

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

    private static void SeedTokenFromEnvironment()
    {
        var env = Environment.GetEnvironmentVariable("TITANIUM_ACME_TOKEN");
        if (string.IsNullOrEmpty(env))
        {
            return;
        }

        var parts = env.Split(':', 2);
        if (parts.Length == 2)
        {
            AcmeTokens[parts[0]] = parts[1];
        }
    }

    /// <summary>
    /// Reloads a leaf certificate from disk and assigns it to all <see cref="ProxyEndPoint.DecryptSsl"/> endpoints.
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
        AsyncConsole.WriteLine($"Replaced GenericCertificate from {certPath}.");
    }

    /// <summary>
    /// Issues a certificate via ACME HTTP-01 when <see cref="CertificatesConfig.AcmeDirectory"/> is set;
    /// otherwise documents the operator path and seeds challenge tokens from env.
    /// </summary>
    public static async Task IssueAcmeCertificateAsync(ProxyServer proxy, CertificatesConfig certificates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(certificates);

        if (string.IsNullOrEmpty(certificates.AcmeEmail) || string.IsNullOrEmpty(certificates.AcmeDomain))
        {
            return;
        }

        if (IssueOverride is not null)
        {
            await IssueOverride(proxy, certificates, cancellationToken).ConfigureAwait(false);
            return;
        }

        SeedTokenFromEnvironment();

        var directory = certificates.AcmeDirectory
                        ?? Environment.GetEnvironmentVariable("TITANIUM_ACME_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory))
        {
            AsyncConsole.WriteLine(
                $"ACME: no AcmeDirectory for {certificates.AcmeDomain} — " +
                "set certificates.acmeDirectory or TITANIUM_ACME_DIRECTORY for automated issue, " +
                "or place PEM/PFX and call ReplaceCertificate / IssueOrRenewAsync.");
            return;
        }

        AsyncConsole.WriteLine($"ACME: issuing for {certificates.AcmeDomain} via {directory}");
        await IssueWithCertesAsync(proxy, certificates, directory, cancellationToken).ConfigureAwait(false);
    }

    private static async Task IssueWithCertesAsync(
        ProxyServer proxy,
        CertificatesConfig certificates,
        string directoryUrl,
        CancellationToken cancellationToken)
    {
        var directoryUri = new Uri(directoryUrl);
        var acme = new AcmeContext(directoryUri);
        await acme.NewAccount(certificates.AcmeEmail!, true).ConfigureAwait(false);

        var order = await acme.NewOrder([certificates.AcmeDomain!]).ConfigureAwait(false);
        var authz = (await order.Authorizations().ConfigureAwait(false)).First();
        var httpChallenge = await authz.Http().ConfigureAwait(false);
        SetChallengeToken(httpChallenge.Token, httpChallenge.KeyAuthz);
        await httpChallenge.Validate().ConfigureAwait(false);

        // Poll authorization
        for (var i = 0; i < 30; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resource = await authz.Resource().ConfigureAwait(false);
            if (resource.Status == AuthorizationStatus.Valid)
            {
                break;
            }

            if (resource.Status == AuthorizationStatus.Invalid)
            {
                throw new InvalidOperationException("ACME authorization invalid.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        var privateKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var cert = await order.Generate(new CsrInfo
        {
            CommonName = certificates.AcmeDomain,
        }, privateKey).ConfigureAwait(false);

        var certPem = cert.ToPem();
        var keyPem = privateKey.ToPem();
        var certPath = certificates.CertificatePath
                       ?? Path.Combine(AppContext.BaseDirectory, "acme-cert.pem");
        var keyPath = certificates.PrivateKeyPath
                      ?? Path.Combine(AppContext.BaseDirectory, "acme-key.pem");
        await File.WriteAllTextAsync(certPath, certPem, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(keyPath, keyPem, cancellationToken).ConfigureAwait(false);
        ReplaceCertificate(proxy, certPath, keyPath);
        AsyncConsole.WriteLine($"ACME: certificate written to {certPath}");
    }

    /// <summary>
    /// Runs ACME issue (when directory configured), then polls for cert files and calls <see cref="ReplaceCertificate"/>.
    /// </summary>
    public static async Task IssueOrRenewAsync(ProxyServer proxy, CertificatesConfig certificates,
        TimeSpan? pollTimeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(certificates);

        await IssueAcmeCertificateAsync(proxy, certificates, cancellationToken).ConfigureAwait(false);

        var timeout = pollTimeout ?? TimeSpan.FromMinutes(5);
        if (int.TryParse(Environment.GetEnvironmentVariable("TITANIUM_ACME_POLL_SECONDS"), out var secs) && secs > 0)
        {
            timeout = TimeSpan.FromSeconds(secs);
        }

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

        AsyncConsole.WriteLine(
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
                // Exportable (not EphemeralKeySet): Windows Schannel needs a usable private key
                // for SslStream.AuthenticateAsServer on TLS-terminate listeners.
                leaf = X509CertificateLoader.LoadPkcs12(
                    File.ReadAllBytes(certPath),
                    password,
                    X509KeyStorageFlags.Exportable);
                return true;
            }

            if (!string.IsNullOrEmpty(keyPath) && File.Exists(keyPath))
            {
                using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
                // Re-wrap as PKCS#12 with Exportable so Schannel can present the leaf.
                leaf = X509CertificateLoader.LoadPkcs12(
                    pem.Export(X509ContentType.Pfx),
                    password: null,
                    X509KeyStorageFlags.Exportable);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            AsyncConsole.WriteError($"Certificate load failed: {ex.Message}");
            return false;
        }
    }
}
