using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Network
{
    public sealed class DefaultCertificateDiskCache : ICertificateCache
    {
        private const string DefaultCertificateDirectoryName = "crts";
        private const string DefaultCertificateFileExtension = ".pfx";
        private const string DefaultRootCertificateFileName = "rootCert" + DefaultCertificateFileExtension;
        private string? rootCertificatePath;

        public X509Certificate2? LoadRootCertificate(string pathOrName, string password, X509KeyStorageFlags storageFlags)
        {
            string path = GetRootCertificatePath(pathOrName);
            return LoadCertificate(path, password, storageFlags);
        }

        public void SaveRootCertificate(string pathOrName, string password, X509Certificate2 certificate)
        {
            string path = GetRootCertificatePath(pathOrName);
            byte[] exported = certificate.Export(X509ContentType.Pkcs12, password);
            File.WriteAllBytes(path, exported);
        }

        /// <inheritdoc />
        public X509Certificate2? LoadCertificate(string subjectName, X509KeyStorageFlags storageFlags)
        {
            string filePath = Path.Combine(GetCertificatePath(false), subjectName + DefaultCertificateFileExtension);
            return LoadCertificate(filePath, string.Empty, storageFlags);
        }

        /// <inheritdoc />
        public void SaveCertificate(string subjectName, X509Certificate2 certificate)
        {
            string filePath = Path.Combine(GetCertificatePath(true), subjectName + DefaultCertificateFileExtension);
            byte[] exported = certificate.Export(X509ContentType.Pkcs12);
            File.WriteAllBytes(filePath, exported);
        }

        public void Clear()
        {
            try
            {
                string path = GetCertificatePath(false);
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (Exception)
            {
                // do nothing
            }
        }

        private X509Certificate2? LoadCertificate(string path, string password, X509KeyStorageFlags storageFlags)
        {
            byte[] exported;

            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                exported = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                // file or directory not found
                return null;
            }

            return new X509Certificate2(exported, password, storageFlags);
        }

        private string GetRootCertificatePath(string pathOrName)
        {
            if (Path.IsPathRooted(pathOrName))
            {
                return pathOrName;
            }

            return Path.Combine(GetRootCertificateDirectory(),
                string.IsNullOrEmpty(pathOrName) ? DefaultRootCertificateFileName : pathOrName);
        }

        private string GetCertificatePath(bool create)
        {
            string path = GetRootCertificateDirectory();

            string certPath = Path.Combine(path, DefaultCertificateDirectoryName);
            if (create && !Directory.Exists(certPath))
            {
                Directory.CreateDirectory(certPath);
            }

            return certPath;
        }

        private string GetRootCertificateDirectory()
        {
            if (rootCertificatePath == null)
            {
                if (RunTime.IsUwpOnWindows)
                {
                    rootCertificatePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                }
                else if (RunTime.IsLinux)
                {
                    rootCertificatePath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                }
                else if (RunTime.IsMac)
                {
                    rootCertificatePath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                }
                else
                {
                    string assemblyLocation = GetType().Assembly.Location;

                    // dynamically loaded assemblies returns string.Empty location
                    if (assemblyLocation == string.Empty)
                    {
                        assemblyLocation = Assembly.GetEntryAssembly().Location;
                    }

#if NETSTANDARD2_1
                    // single-file app returns string.Empty location
                    if (assemblyLocation == string.Empty)
                    {
                        assemblyLocation = AppContext.BaseDirectory;
                    }
#endif

                    string path = Path.GetDirectoryName(assemblyLocation);

                    rootCertificatePath = path ?? throw new NullReferenceException();
                }
            }

            return rootCertificatePath;
        }
    }
}
