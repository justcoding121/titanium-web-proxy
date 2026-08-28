using System.Net;
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
            if (File.Exists(certificates.CertificatePath) &&
                !string.IsNullOrEmpty(certificates.PrivateKeyPath) &&
                File.Exists(certificates.PrivateKeyPath))
            {
                Console.WriteLine("Certificate files present — load via CertificateManager as needed for TLS listeners.");
            }
        }

        if (string.IsNullOrEmpty(certificates.AcmeDomain))
        {
            return;
        }

        Console.WriteLine($"ACME domain configured: {certificates.AcmeDomain} (email={certificates.AcmeEmail ?? "(none)"})");
        Console.WriteLine("HTTP-01: place challenge tokens via env TITANIUM_ACME_TOKEN=token:keyAuth or file under .well-known.");

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

    public static void SetChallengeToken(string token, string keyAuthorization) =>
        AcmeTokens[token] = keyAuthorization;
}
