using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.Network;

public sealed class DefaultCertificateDiskCache : ICertificateCache
{
    private const string DefaultCertificateDirectoryName = "crts";
    private const string DefaultCertificateFileExtension = ".pfx";
    private const string DefaultRootCertificateFileName = "rootCert" + DefaultCertificateFileExtension;
    private string? rootCertificatePath;

    public X509Certificate2? LoadRootCertificate(string pathOrName, string password, X509KeyStorageFlags storageFlags)
    {
        var path = GetRootCertificatePath(pathOrName);
        return LoadCertificate(path, password, storageFlags);
    }

    public void SaveRootCertificate(string pathOrName, string password, X509Certificate2 certificate)
    {
        var path = GetRootCertificatePath(pathOrName);
        var exported = certificate.Export(X509ContentType.Pkcs12, password);
        WriteFileAtomic(path, exported);
    }

    /// <inheritdoc />
    public X509Certificate2? LoadCertificate(string subjectName, X509KeyStorageFlags storageFlags)
    {
        var filePath = Path.Combine(GetCertificatePath(false), subjectName + DefaultCertificateFileExtension);
        return LoadCertificate(filePath, string.Empty, storageFlags);
    }

    /// <inheritdoc />
    public void SaveCertificate(string subjectName, X509Certificate2 certificate)
    {
        var filePath = Path.Combine(GetCertificatePath(true), subjectName + DefaultCertificateFileExtension);
        var exported = certificate.Export(X509ContentType.Pkcs12);
        WriteFileAtomic(filePath, exported);
    }

    /// <summary>
    ///     Writes a file atomically: the data is written to a temporary file in the same directory
    ///     and then moved into place, so a concurrent reader never observes a partially written
    ///     (and therefore corrupt) PKCS#12 file.
    /// </summary>
    private static void WriteFileAtomic(string path, byte[] contents)
    {
        var directory = Path.GetDirectoryName(path);
        var tempPath = Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory, Path.GetRandomFileName());

        File.WriteAllBytes(tempPath, contents);

        try
        {
            if (File.Exists(path))
                // File.Replace performs an atomic swap on the same volume (temp is in the same directory).
                File.Replace(tempPath, path, null);
            else
                File.Move(tempPath, path);
        }
        catch
        {
            // best-effort cleanup of the temp file if the move/replace failed
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // ignore
            }

            throw;
        }
    }

    public void Clear()
    {
        try
        {
            var path = GetCertificatePath(false);
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception)
        {
            // do nothing
        }
    }

    private X509Certificate2? LoadCertificate(string path, string password, X509KeyStorageFlags storageFlags)
    {
        byte[] exported;

        if (!File.Exists(path)) return null;

        try
        {
            exported = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            // file or directory not found, or a concurrent write is in progress
            return null;
        }

        try
        {
            return CertificateLoader.LoadPkcs12(exported, password, storageFlags);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // corrupt/partial pfx on disk: treat as a cache miss so a fresh certificate is generated
            return null;
        }
    }

    private string GetRootCertificatePath(string pathOrName)
    {
        if (Path.IsPathRooted(pathOrName)) return pathOrName;

        return Path.Combine(GetRootCertificateDirectory(),
            string.IsNullOrEmpty(pathOrName) ? DefaultRootCertificateFileName : pathOrName);
    }

    private string GetCertificatePath(bool create)
    {
        var path = GetRootCertificateDirectory();

        var certPath = Path.Combine(path, DefaultCertificateDirectoryName);
        if (create && !Directory.Exists(certPath)) Directory.CreateDirectory(certPath);

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
                var assemblyLocation = GetType().Assembly.Location;

                // dynamically loaded assemblies returns string.Empty location
                if (assemblyLocation == string.Empty)
                    assemblyLocation = Assembly.GetEntryAssembly()?.Location ?? string.Empty;

#if NET6_0_OR_GREATER
                // single-file app returns string.Empty location
                if (assemblyLocation == string.Empty)
                {
                    assemblyLocation = AppContext.BaseDirectory;
                }
#endif

                var path = Path.GetDirectoryName(assemblyLocation);

                rootCertificatePath = path ?? throw new NullReferenceException();
            }
        }

        return rootCertificatePath;
    }
}