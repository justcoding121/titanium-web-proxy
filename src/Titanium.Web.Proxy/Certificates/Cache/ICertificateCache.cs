using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.Network;

public interface ICertificateCache
{
    /// <summary>
    ///     Loads the root certificate from the storage.
    /// </summary>
    X509Certificate2? LoadRootCertificate(string pathOrName, string password, X509KeyStorageFlags storageFlags);

    /// <summary>
    ///     Saves the root certificate to the storage.
    /// </summary>
    void SaveRootCertificate(string pathOrName, string password, X509Certificate2 certificate);

    /// <summary>
    ///     Loads a leaf certificate by subject name. Returns null if the certificate is missing or cannot be loaded.
    /// </summary>
    X509Certificate2? LoadCertificate(string subjectName, X509KeyStorageFlags storageFlags);

    /// <summary>
    ///     Stores certificate into the storage.
    /// </summary>
    void SaveCertificate(string subjectName, X509Certificate2 certificate);

    /// <summary>
    ///     Clears the storage.
    /// </summary>
    void Clear();
}