using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.Network.Certificate;

internal static class CertificateLoader
{
    internal static X509Certificate2 LoadPkcs12(byte[] data, string? password,
        X509KeyStorageFlags storageFlags)
    {
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(data, password, storageFlags);
#else
        return new X509Certificate2(data, password, storageFlags);
#endif
    }
}
