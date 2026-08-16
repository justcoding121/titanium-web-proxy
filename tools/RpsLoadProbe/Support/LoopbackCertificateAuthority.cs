using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy;

namespace Titanium.Web.Proxy.RpsLoadProbe.Support;

/// <summary>
/// Process-local CA for the HTTPS MITM arm. Nothing is persisted or trusted system-wide.
/// </summary>
internal static class LoopbackCertificateAuthority
{
    public const string RootCertificateName = "Titanium RPS LoadProbe Root CA";

    private static readonly Lazy<X509Certificate2> rootCertificate = new(CreateRootCertificate);
    private static readonly Lazy<byte[]> serverCertificateBytes = new(CreateServerCertificateBytes);

    public static X509Certificate2 RootCertificate => rootCertificate.Value;

    public static X509Certificate2 ServerCertificate =>
        X509CertificateLoader.LoadPkcs12(serverCertificateBytes.Value, null, X509KeyStorageFlags.Exportable);

    public static bool Validate(X509Certificate? certificate)
    {
        if (certificate is null) return false;

        var loaded = certificate as X509Certificate2
                     ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(RootCertificate);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            return chain.Build(loaded);
        }
        finally
        {
            if (!ReferenceEquals(loaded, certificate)) loaded.Dispose();
        }
    }

    private static byte[] CreateServerCertificateBytes()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = RootCertificateName;
        proxy.CertificateManager.RootCertificate = RootCertificate;
        var cert = proxy.CertificateManager.CreateServerCertificate("localhost").GetAwaiter().GetResult()
                   ?? throw new InvalidOperationException("Could not create the load-probe server certificate.");
        return cert.Export(X509ContentType.Pkcs12);
    }

    private static X509Certificate2 CreateRootCertificate()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = RootCertificateName;
        if (!proxy.CertificateManager.CreateRootCertificate(false) || proxy.CertificateManager.RootCertificate == null)
            throw new InvalidOperationException("Could not create the load-probe root certificate.");

        var bytes = proxy.CertificateManager.RootCertificate.Export(X509ContentType.Pkcs12);
        return X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.Exportable);
    }
}
