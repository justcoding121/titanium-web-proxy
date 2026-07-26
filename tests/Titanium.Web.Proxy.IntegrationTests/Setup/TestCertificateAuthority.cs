using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.IntegrationTests.Setup;

internal static class TestCertificateAuthority
{
    private static readonly Lazy<X509Certificate2> rootCertificate = new(CreateRootCertificate);

    // Store the server cert as raw PKCS12 bytes so we can vend independent X509Certificate2 instances.
    // Each caller (TestServer) gets its own object with its own CAPI key handle, preventing any shared
    // handle from being invalidated when Kestrel disposes "its" copy of the certificate.
    private static readonly Lazy<byte[]> serverCertificateBytes = new(CreateServerCertificateBytes);

    public static X509Certificate2 RootCertificate => rootCertificate.Value;

    /// <summary>
    /// Creates a fresh, independent <see cref="X509Certificate2"/> from cached PKCS12 bytes.
    /// Each call returns a new object. Callers (e.g. <see cref="TestServer"/>) own the returned
    /// certificate and may let Kestrel dispose it without affecting any other caller's copy.
    /// </summary>
    public static X509Certificate2 ServerCertificate =>
        X509CertificateLoader.LoadPkcs12(serverCertificateBytes.Value, null,
            X509KeyStorageFlags.Exportable);

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

    private static byte[] CreateServerCertificateBytes()
    {
        // Use a temporary proxy just to drive the BouncyCastle cert generation pipeline.
        // Export the result to PKCS12 bytes immediately so the bytes are independent of
        // the proxy's CertificateManager cache (which may dispose the cert during cleanup).
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificate = RootCertificate;
        var cert = proxy.CertificateManager.CreateServerCertificate("localhost").GetAwaiter().GetResult()
                   ?? throw new InvalidOperationException("Could not create the integration test server certificate.");
        return cert.Export(X509ContentType.Pkcs12);
    }

    private static X509Certificate2 CreateRootCertificate()
    {
        using var proxy = new ProxyServer(false, false, false);
        // Distinct CN from the library default ("Titanium Root Certificate Authority") so a previously
        // user-trusted product root left in CurrentUser\Root cannot collide with CustomRootTrust
        // chain building or Schannel server-cert selection during HTTPS reverse-proxy tests.
        proxy.CertificateManager.RootCertificateName = "Titanium Integration Test Root CA";
        if (!proxy.CertificateManager.CreateRootCertificate(false) ||
            proxy.CertificateManager.RootCertificate == null)
        {
            throw new InvalidOperationException("Could not create the integration test root certificate.");
        }

        return proxy.CertificateManager.RootCertificate;
    }
}
