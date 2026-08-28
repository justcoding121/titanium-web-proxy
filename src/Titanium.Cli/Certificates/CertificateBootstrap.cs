using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Certificates;

/// <summary>Applies certificate paths / ACME placeholders from config.</summary>
internal static class CertificateBootstrap
{
    public static void Apply(ProxyServer proxy, CertificatesConfig? certificates)
    {
        if (certificates is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(certificates.CertificatePath))
        {
            Console.WriteLine($"Certificate path configured: {certificates.CertificatePath}");
        }

        if (!string.IsNullOrEmpty(certificates.AcmeDomain))
        {
            // ACME HTTP-01 is prefix-gated in BeforeRequest when wired; skeleton only records intent.
            Console.WriteLine($"ACME domain configured: {certificates.AcmeDomain}");
        }

        _ = proxy;
    }
}
