using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.IntegrationTests.Setup;

internal static class TestCertificateAuthority
{
    private static readonly Lazy<X509Certificate2> rootCertificate = new(CreateRootCertificate);
    private static readonly Lazy<X509Certificate2> serverCertificate = new(CreateServerCertificate);

    public static X509Certificate2 RootCertificate => rootCertificate.Value;
    public static X509Certificate2 ServerCertificate => serverCertificate.Value;

    public static bool Validate(X509Certificate certificate, SslPolicyErrors sslPolicyErrors)
    {
        const SslPolicyErrors fatalErrors =
            SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable;

        if (certificate == null || (sslPolicyErrors & fatalErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        var loadedCertificate = certificate as X509Certificate2;
        var disposeCertificate = loadedCertificate == null;
        loadedCertificate ??= X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());

        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(RootCertificate);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(loadedCertificate);
        }
        finally
        {
            if (disposeCertificate)
            {
                loadedCertificate.Dispose();
            }
        }
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificate = RootCertificate;
        return proxy.CertificateManager.CreateServerCertificate("localhost").GetAwaiter().GetResult();
    }

    private static X509Certificate2 CreateRootCertificate()
    {
        using var proxy = new ProxyServer(false, false, false);
        if (!proxy.CertificateManager.CreateRootCertificate(false) ||
            proxy.CertificateManager.RootCertificate == null)
        {
            throw new InvalidOperationException("Could not create the integration test root certificate.");
        }

        return proxy.CertificateManager.RootCertificate;
    }
}
