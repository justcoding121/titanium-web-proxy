using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.IntegrationTests.Setup;

/// <summary>
///     Process-local certificate authority used by integration tests.
///     <para>
///         Intentionally uses a subject DN distinct from the library default
///         (<c>Titanium Root Certificate Authority</c>). The Basic/WPF examples call
///         <c>new ProxyServer()</c>, which trusts that product root into the current-user
///         Windows stores on <c>Start()</c>. Combined with <c>TestHelper</c> forcing
///         <c>UseProxy = false</c> on direct clients (so a concurrently running example that
///         owns the WinINET system proxy cannot MITM test traffic), tests stay green while
///         examples are installed and/or running via <c>dotnet run</c> in another console.
///     </para>
/// </summary>
internal static class TestCertificateAuthority
{
    /// <summary>
    ///     Subject CN for the test-only root. Must not equal
    ///     <c>CertificateManager</c>'s default product root name.
    /// </summary>
    public const string RootCertificateName = "Titanium Integration Test Root CA";

    private static readonly Lazy<X509Certificate2> rootCertificate = new(CreateRootCertificate);

    // Store the server cert as raw PKCS12 bytes so we can vend independent X509Certificate2 instances.
    // Each caller (TestServer) gets its own object with its own key handle, preventing any shared
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
            chain.ChainPolicy.DisableCertificateDownloads = true;
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
        proxy.CertificateManager.RootCertificateName = RootCertificateName;
        proxy.CertificateManager.RootCertificate = RootCertificate;
        var cert = proxy.CertificateManager.CreateServerCertificate("localhost").GetAwaiter().GetResult()
                   ?? throw new InvalidOperationException("Could not create the integration test server certificate.");
        return cert.Export(X509ContentType.Pkcs12);
    }

    private static X509Certificate2 CreateRootCertificate()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = RootCertificateName;
        if (!proxy.CertificateManager.CreateRootCertificate(false) ||
            proxy.CertificateManager.RootCertificate == null)
        {
            throw new InvalidOperationException("Could not create the integration test root certificate.");
        }

        // Clone via PKCS12 so the returned instance is independent of CertificateManager's
        // lifetime (the temporary proxy is disposed at the end of this method).
        var bytes = proxy.CertificateManager.RootCertificate.Export(X509ContentType.Pkcs12);
        return X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.Exportable);
    }
}
