using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Titanium.Web.Proxy.Abstractions.Network;

/// <summary>Loads one or more trust anchors from a PEM or certificate file.</summary>
public static class ClientCertificateAuthorityFiles
{
    public static X509Certificate2Collection Load(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Parses PEM text (one or more certificates) or a single DER certificate.</summary>
    public static X509Certificate2Collection Parse(byte[] content)
    {
        var collection = new X509Certificate2Collection();
        var text = Encoding.UTF8.GetString(content);
        if (text.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
        {
            collection.ImportFromPem(text);
        }
        else
        {
            collection.Add(X509CertificateLoader.LoadCertificate(content));
        }

        return collection;
    }
}
