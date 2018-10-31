using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.Network
{
    internal sealed class DefaultCertificateDiskCache : ICertificateCache
    {
        private const string defaultCertificateDirectoryName = "crts";
        private const string defaultCertificateFileExtension = ".pfx";
        private const string defaultRootCertificateFileName = "rootCert" + defaultCertificateFileExtension;
        private string rootCertificatePath;
        private string certificatePath;

        public X509Certificate2 LoadRootCertificate(string name, string password, X509KeyStorageFlags storageFlags)
        {
            string filePath = getRootCertificatePath(name);
            return loadCertificate(filePath, password, storageFlags);
        }

        public void SaveRootCertificate(string name, string password, X509Certificate2 certificate)
        {
            string filePath = getRootCertificatePath(name);
            byte[] exported = certificate.Export(X509ContentType.Pkcs12, password);
            File.WriteAllBytes(filePath, exported);
        }

        /// <inheritdoc />
        public X509Certificate2 LoadCertificate(string subjectName, X509KeyStorageFlags storageFlags)
        {
            string filePath = Path.Combine(getCertificatePath(), subjectName + defaultCertificateFileExtension);
            return loadCertificate(filePath, string.Empty, storageFlags);
        }

        /// <inheritdoc />
        public void SaveCertificate(string subjectName, X509Certificate2 certificate)
        {
            string filePath = Path.Combine(getCertificatePath(), subjectName + defaultCertificateFileExtension);
            byte[] exported = certificate.Export(X509ContentType.Pkcs12);
            File.WriteAllBytes(filePath, exported);
        }

        public void Clear()
        {
            try
            {
                Directory.Delete(getCertificatePath(), true);
            }
            catch (DirectoryNotFoundException)
            {
                // do nothing
            }

            certificatePath = null;
        }

        private X509Certificate2 loadCertificate(string filePath, string password, X509KeyStorageFlags storageFlags)
        {
            byte[] exported;
            try
            {
                exported = File.ReadAllBytes(filePath);
            }
            catch (IOException)
            {
                // file or directory not found
                return null;
            }

            return new X509Certificate2(exported, password, storageFlags);
        }

        private string getRootCertificatePath(string filePath)
        {
            if (Path.IsPathRooted(filePath))
            {
                return filePath;
            }

            return Path.Combine(getRootCertificateDirectory(),
                string.IsNullOrEmpty(filePath) ? defaultRootCertificateFileName : filePath);
        }

        private string getCertificatePath()
        {
            if (certificatePath == null)
            {
                string path = getRootCertificateDirectory();

                string certPath = Path.Combine(path, defaultCertificateDirectoryName);
                if (!Directory.Exists(certPath))
                {
                    Directory.CreateDirectory(certPath);
                }

                certificatePath = certPath;
            }

            return certificatePath;
        }

        private string getRootCertificateDirectory()
        {
            if (rootCertificatePath == null)
            {
                string assemblyLocation = GetType().Assembly.Location;

                // dynamically loaded assemblies returns string.Empty location
                if (assemblyLocation == string.Empty)
                {
                    assemblyLocation = Assembly.GetEntryAssembly().Location;
                }

                string path = Path.GetDirectoryName(assemblyLocation);

                rootCertificatePath = path ?? throw new NullReferenceException();
            }

            return rootCertificatePath;
        }
    }
}
