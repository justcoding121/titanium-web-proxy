using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.Configuration;

/// <summary>Loads one or more trust anchors from a PEM or certificate file.</summary>
public static class ClientCertificateAuthorityFiles
{
    public static X509Certificate2Collection Load(string path)
    {
        var collection = new X509Certificate2Collection();
        var text = File.ReadAllText(path);
        if (text.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
        {
            collection.ImportFromPem(text);
        }
        else
        {
            collection.Add(X509CertificateLoader.LoadCertificateFromFile(path));
        }

        return collection;
    }
}
