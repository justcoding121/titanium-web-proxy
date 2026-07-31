using System;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.Benchmarks.Support;

/// <summary>
///     Process-local certificate authority for the HTTP/2 end-to-end benchmark, which needs a real
///     TLS handshake on both legs: the proxy must present a MITM leaf certificate to the client leg,
///     and must validate the Kestrel origin's certificate on the server leg. Deliberately minimal
///     compared to a production certificate store - a benchmark run is single-process and
///     short-lived, so there is nothing to persist or trust system-wide.
/// </summary>
internal static class LoopbackCertificateAuthority
{
    public const string RootCertificateName = "Titanium Benchmarks Root CA";

    private static readonly Lazy<X509Certificate2> rootCertificate = new(CreateRootCertificate);
    private static readonly Lazy<byte[]> serverCertificateBytes = new(CreateServerCertificateBytes);

    public static X509Certificate2 RootCertificate => rootCertificate.Value;

    /// <summary>
    ///     Vends an independent <see cref="X509Certificate2" /> instance each call so Kestrel can own
    ///     and dispose "its" copy without invalidating a shared key handle used elsewhere.
    /// </summary>
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
                   ?? throw new InvalidOperationException("Could not create the benchmark server certificate.");
        return cert.Export(X509ContentType.Pkcs12);
    }

    private static X509Certificate2 CreateRootCertificate()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = RootCertificateName;
        if (!proxy.CertificateManager.CreateRootCertificate(false) || proxy.CertificateManager.RootCertificate == null)
            throw new InvalidOperationException("Could not create the benchmark root certificate.");

        var bytes = proxy.CertificateManager.RootCertificate.Export(X509ContentType.Pkcs12);
        return X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.Exportable);
    }
}
