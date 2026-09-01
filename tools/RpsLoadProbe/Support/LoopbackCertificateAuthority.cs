using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy;

namespace Titanium.Web.Proxy.RpsLoadProbe.Support;

/// <summary>
/// Shared test CA for the load probe. When <see cref="CertDirEnvironmentVariable"/> is set
/// and the directory already contains PFX files, children load the parent-seeded root/leaf
/// so HTTPS/QUIC origin and proxy can trust each other across processes. Otherwise a
/// process-local root is minted (leftover in-proc <c>--serve</c> / unit use).
/// Nothing is persisted or trusted system-wide unless the parent writes a temp dir.
/// </summary>
internal static class LoopbackCertificateAuthority
{
    public const string RootCertificateName = "Titanium RPS LoadProbe Root CA";
    public const string CertDirEnvironmentVariable = "TWP_RPS_CERT_DIR";
    public const string RootPfxFileName = "root.pfx";
    public const string ServerPfxFileName = "server.pfx";

    private static readonly Lazy<X509Certificate2> rootCertificate = new(CreateRootCertificate);
    private static readonly Lazy<byte[]> serverCertificateBytes = new(CreateServerCertificateBytes);

    public static X509Certificate2 RootCertificate => rootCertificate.Value;

    public static X509Certificate2 ServerCertificate =>
        X509CertificateLoader.LoadPkcs12(serverCertificateBytes.Value, null, X509KeyStorageFlags.Exportable);

    /// <summary>
    /// Materialize root + server PFX into a directory and point <see cref="CertDirEnvironmentVariable"/> at it.
    /// Safe to call more than once in the same process; existing files are reused.
    /// </summary>
    public static string SeedDirectory(string? directory = null)
    {
        directory = string.IsNullOrWhiteSpace(directory)
            ? Environment.GetEnvironmentVariable(CertDirEnvironmentVariable)
            : directory;
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.Combine(Path.GetTempPath(), "twp-rps-certs-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable(CertDirEnvironmentVariable, directory);

        var rootPath = Path.Combine(directory, RootPfxFileName);
        var serverPath = Path.Combine(directory, ServerPfxFileName);
        if (!File.Exists(rootPath))
            File.WriteAllBytes(rootPath, RootCertificate.Export(X509ContentType.Pkcs12));
        if (!File.Exists(serverPath))
            File.WriteAllBytes(serverPath, serverCertificateBytes.Value);

        return directory;
    }

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

    private static bool TryReadSharedPfx(string fileName, out byte[] bytes)
    {
        bytes = [];
        var dir = Environment.GetEnvironmentVariable(CertDirEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(dir))
            return false;

        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path))
            return false;

        bytes = File.ReadAllBytes(path);
        return bytes.Length > 0;
    }

    private static byte[] CreateServerCertificateBytes()
    {
        if (TryReadSharedPfx(ServerPfxFileName, out var shared))
            return shared;

        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = RootCertificateName;
        proxy.CertificateManager.RootCertificate = RootCertificate;
        var cert = proxy.CertificateManager.CreateServerCertificate("localhost").GetAwaiter().GetResult()
                   ?? throw new InvalidOperationException("Could not create the load-probe server certificate.");
        return cert.Export(X509ContentType.Pkcs12);
    }

    private static X509Certificate2 CreateRootCertificate()
    {
        if (TryReadSharedPfx(RootPfxFileName, out var shared))
            return X509CertificateLoader.LoadPkcs12(shared, null, X509KeyStorageFlags.Exportable);

        using var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = RootCertificateName;
        if (!proxy.CertificateManager.CreateRootCertificate(false) || proxy.CertificateManager.RootCertificate == null)
            throw new InvalidOperationException("Could not create the load-probe root certificate.");

        var bytes = proxy.CertificateManager.RootCertificate.Export(X509ContentType.Pkcs12);
        return X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.Exportable);
    }
}
