using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.Network;

public sealed class DefaultCertificateDiskCache : ICertificateCache
{
    private const string DefaultCertificateDirectoryName = "crts";
    private const string DefaultCertificateFileExtension = ".pfx";
    private const string DefaultRootCertificateFileName = "rootCert" + DefaultCertificateFileExtension;

    /// <summary>
    ///     Dedicated app subfolder under the platform's per-user data root, so the root/leaf certificate
    ///     cache does not sit loose in a directory shared with unrelated applications.
    /// </summary>
    private const string AppDirectoryName = "Titanium.Web.Proxy";

    private static bool orphanedLegacyRootNoticeLogged;

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
            catch (Exception cleanupEx)
            {
                ProxyDiagnostics.ReportBenign(ProxyDiagnostics.Logger,
                    $"Failed to clean up temp certificate file '{tempPath}' after a failed atomic write.",
                    cleanupEx);
            }

            throw;
        }
    }

    /// <summary>
    ///     Deletes the oldest (by last-write time) leaf certificate files in the on-disk cache directory
    ///     until at most <paramref name="maxEntries" /> remain. Unlike the in-memory certificate cache,
    ///     nothing else ever removes entries here, so without this bound a long-running proxy that sees
    ///     traffic to many distinct hostnames accumulates one permanent .pfx file per hostname forever.
    ///     A <see langword="null" /> or non-positive <paramref name="maxEntries" /> disables the bound.
    /// </summary>
    public void PruneToMaxEntries(int? maxEntries)
    {
        if (maxEntries is not > 0) return;

        try
        {
            var certPath = GetCertificatePath(false);
            if (!Directory.Exists(certPath)) return;

            var files = new DirectoryInfo(certPath).GetFiles("*" + DefaultCertificateFileExtension);
            var excess = files.Length - maxEntries.Value;
            if (excess <= 0) return;

            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc).Take(excess))
                try { file.Delete(); }
                catch (IOException) { /* concurrent reader/writer - leave it for the next prune pass */ }
        }
        catch (Exception ex)
        {
            ProxyDiagnostics.ReportBenign(ProxyDiagnostics.Logger,
                "Failed to prune the on-disk certificate cache to its configured entry bound.", ex);
        }
    }

    public void Clear()
    {
        try
        {
            var path = GetCertificatePath(false);
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            ProxyDiagnostics.ReportBenign(ProxyDiagnostics.Logger,
                "Failed to clear the on-disk certificate cache directory.", ex);
        }
    }

    private static X509Certificate2? LoadCertificate(string path, string password, X509KeyStorageFlags storageFlags)
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

    /// <summary>
    ///     Per-user protected root/leaf certificate cache directory. Deliberately has no fallback read
    ///     of the pre-5.0 location: on Windows (non-UWP), that used to be the hosting assembly's own
    ///     directory (e.g. under <c>Program Files</c> or a shared build output folder, both readable -
    ///     and sometimes writable - by every user on the machine), which is not a "per-user protected
    ///     location" for a file holding the root CA's private key. 5.0.0 is unreleased, so a clean move
    ///     is preferred over a dual-path migration that would have to keep checking the old spot forever.
    /// </summary>
    private string GetRootCertificateDirectory()
    {
        if (rootCertificatePath == null)
        {
            var legacyPath = GetLegacyRootCertificateDirectory();

            string basePath;
            if (RunTime.IsWindows) // covers both UWP and desktop Windows
                basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            else // Linux/Mac
                basePath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            var appPath = Path.Combine(basePath, AppDirectoryName);
            EnsureProtectedDirectoryExists(appPath);
            WarnIfLegacyRootIsOrphaned(legacyPath, appPath);

            rootCertificatePath = appPath;
        }

        return rootCertificatePath;
    }

    /// <summary>
    ///     Reconstructs the pre-5.0 root certificate directory purely so <see cref="WarnIfLegacyRootIsOrphaned" />
    ///     can point the user at the file/trust-store entry they now need to remove manually - this is
    ///     never read from.
    /// </summary>
    private string GetLegacyRootCertificateDirectory()
    {
        if (RunTime.IsUwpOnWindows || RunTime.IsLinux || RunTime.IsMac)
            // These platforms already used a per-user data root pre-5.0; only the new dedicated
            // AppDirectoryName subfolder is new, so the "legacy" location is that same root without it.
            return RunTime.IsUwpOnWindows
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var assemblyLocation = GetType().Assembly.Location;

        // dynamically loaded assemblies returns string.Empty location
        if (assemblyLocation == string.Empty)
            assemblyLocation = Assembly.GetEntryAssembly()?.Location ?? string.Empty;

        // single-file app returns string.Empty location
        if (assemblyLocation == string.Empty) assemblyLocation = AppContext.BaseDirectory;

        return Path.GetDirectoryName(assemblyLocation) ?? AppContext.BaseDirectory;
    }

    /// <summary>
    ///     Creates <paramref name="path" /> if missing and, on a best-effort basis, tightens its
    ///     permissions to the current user only. Never throws: a failure here should not prevent the
    ///     proxy from starting, it just means the OS-default permissions on the parent special folder
    ///     (already per-user on all supported platforms) are what actually apply.
    /// </summary>
    private static void EnsureProtectedDirectoryExists(string path)
    {
        try
        {
            if (RunTime.IsWindows)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                // Owner-only (rwx------), defense-in-depth on top of the home directory's own
                // permissions, which are not always guaranteed to already be 0700.
                Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception ex)
        {
            ProxyDiagnostics.ReportBenign(ProxyDiagnostics.Logger,
                $"Failed to create or tighten permissions on the certificate cache directory '{path}'.", ex);
            Directory.CreateDirectory(path);
        }
    }

    /// <summary>
    ///     Logs a one-time notice when a root certificate exists at the pre-5.0 location but not yet at
    ///     the new one, so the user knows to remove the orphaned entry from their OS trust store (the
    ///     new root replacing it will have a different key/thumbprint).
    /// </summary>
    private static void WarnIfLegacyRootIsOrphaned(string legacyDirectory, string newDirectory)
    {
        if (orphanedLegacyRootNoticeLogged) return;

        try
        {
            var legacyRootPath = Path.Combine(legacyDirectory, DefaultRootCertificateFileName);
            var newRootPath = Path.Combine(newDirectory, DefaultRootCertificateFileName);
            if (!File.Exists(legacyRootPath) || File.Exists(newRootPath)) return;

            orphanedLegacyRootNoticeLogged = true;
            ProxyDiagnostics.ReportWarning(ProxyDiagnostics.Logger,
                $"Titanium.Web.Proxy 5.0 moved its root certificate cache to a per-user protected " +
                $"location. A root certificate from a previous version was found at '{legacyRootPath}' " +
                "and is no longer used - a new root will be generated at the new location. The old " +
                "certificate is now orphaned in your OS/browser trust store (Trusted Root Certification " +
                "Authorities, or the equivalent per-user store) and should be removed manually.");
        }
        catch (Exception ex)
        {
            ProxyDiagnostics.ReportBenign(ProxyDiagnostics.Logger,
                "Failed to check for an orphaned pre-5.0 root certificate.", ex);
        }
    }
}