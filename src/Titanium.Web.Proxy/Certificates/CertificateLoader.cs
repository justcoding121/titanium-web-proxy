using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.Network.Certificate;

internal static class CertificateLoader
{
    internal static X509Certificate2 LoadCertificate(byte[] data)
    {
        return X509CertificateLoader.LoadCertificate(data);
    }

    internal static X509Certificate2 LoadPkcs12(byte[] data, string? password,
        X509KeyStorageFlags storageFlags)
    {
        return X509CertificateLoader.LoadPkcs12(data, password, storageFlags);
    }
}
