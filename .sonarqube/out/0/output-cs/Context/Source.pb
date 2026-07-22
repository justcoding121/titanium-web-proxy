⁄
iD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Certificates\Cache\CachedCertificate.cs◊using System;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.Network;

/// <summary>
///     An object that holds the cached certificate
/// </summary>
internal sealed class CachedCertificate
{
    public CachedCertificate(X509Certificate2 certificate)
    {
        Certificate = certificate;
    }

    internal X509Certificate2 Certificate { get; }

    /// <summary>
    ///     Last time this certificate was used.
    ///     Useful in determining its cache lifetime.
    /// </summary>
    internal DateTime LastAccess { get; set; }
}ParseOptions.0.json≤1
sD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Certificates\Cache\DefaultCertificateDiskCache.cs•0using System;
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
}ParseOptions.0.jsonä	
iD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Certificates\Cache\ICertificateCache.csáusing System.Security.Cryptography.X509Certificates;

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
    ///     Loads certificate from the storage. Returns true if certificate does not exist.
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
}ParseOptions.0.json‹
cD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Certificates\CertificateLoader.csﬂusing System.Security.Cryptography.X509Certificates;

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
ParseOptions.0.jsonÕ°
dD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Certificates\CertificateManager.csŒ†using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network.Certificate;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Network;

/// <summary>
///     Certificate Engine option.
/// </summary>
public enum CertificateEngine
{
    /// <summary>
    ///     Uses BouncyCastle 3rd party library.
    ///     Default. Generates a fresh RSA key pair for every leaf certificate.
    /// </summary>
    BouncyCastle = 0,

    /// <summary>
    ///     Faster BouncyCastle variant.
    ///     Note: for performance it reuses a single pre-generated RSA key pair across ALL generated
    ///     leaf certificates. This means every intercepted host's certificate shares the same public key.
    ///     Prefer <see cref="BouncyCastle" /> if per-host key isolation matters for your threat model.
    /// </summary>
    BouncyCastleFast = 2,

    /// <summary>
    ///     Uses Windows Certification Generation API and only valid in Windows OS.
    ///     Observed to be faster than BouncyCastle.
    ///     Bug #468 Reported.
    ///     Note: this engine also reuses a shared private key across generated leaf certificates.
    /// </summary>
    DefaultWindows = 1
}

/// <summary>
///     A class to manage SSL certificates used by this proxy server.
/// </summary>
public sealed class CertificateManager : IDisposable
{
    private const string DefaultRootCertificateIssuer = "Titanium";

    private const string DefaultRootRootCertificateName = "Titanium Root Certificate Authority";

    private static readonly ConcurrentDictionary<string, object> _saveCertificateLocks = new();

    /// <summary>
    ///     Cache dictionary
    /// </summary>
    private readonly ConcurrentDictionary<string, CachedCertificate> cachedCertificates = new();

    private readonly CancellationTokenSource clearCertificatesTokenSource = new();

    /// <summary>
    ///     Used to prevent multiple threads working on same certificate generation
    ///     when burst certificate generation requests happen for same certificate.
    /// </summary>
    private readonly SemaphoreSlim pendingCertificateCreationTaskLock = new(1);

    /// <summary>
    ///     A list of pending certificate creation tasks.
    /// </summary>
    private readonly Dictionary<string, Task<X509Certificate2?>> pendingCertificateCreationTasks = new();

    private readonly object rootCertCreationLock = new();

    private ICertificateMaker? certEngineValue;

    private ICertificateCache certificateCache = new DefaultCertificateDiskCache();

    private bool disposed;

    private CertificateEngine engine;

    private string? issuer;

    private X509Certificate2? rootCertificate;

    private string? rootCertificateName;

    /// <summary>
    ///     Initializes a new instance of the <see cref="CertificateManager" /> class.
    /// </summary>
    /// <param name="rootCertificateName"></param>
    /// <param name="rootCertificateIssuerName"></param>
    /// <param name="userTrustRootCertificate">
    ///     Should fake HTTPS certificate be trusted by this machine's user certificate
    ///     store?
    /// </param>
    /// <param name="machineTrustRootCertificate">Should fake HTTPS certificate be trusted by this machine's certificate store?</param>
    /// <param name="trustRootCertificateAsAdmin">
    ///     Should we attempt to trust certificates with elevated permissions by
    ///     prompting for UAC if required?
    /// </param>
    /// <param name="exceptionFunc"></param>
    internal CertificateManager(string? rootCertificateName, string? rootCertificateIssuerName,
        bool userTrustRootCertificate, bool machineTrustRootCertificate, bool trustRootCertificateAsAdmin,
        ExceptionHandler? exceptionFunc)
    {
        ExceptionFunc = exceptionFunc;

        UserTrustRoot = userTrustRootCertificate || machineTrustRootCertificate;

        MachineTrustRoot = machineTrustRootCertificate;
        TrustRootAsAdministrator = trustRootCertificateAsAdmin;

        if (rootCertificateName != null) RootCertificateName = rootCertificateName;

        if (rootCertificateIssuerName != null) RootCertificateIssuerName = rootCertificateIssuerName;

        CertificateEngine = CertificateEngine.BouncyCastle;
    }

    private ICertificateMaker CertEngine
    {
        get
        {
            if (certEngineValue == null)
                switch (engine)
                {
                    case CertificateEngine.BouncyCastle:
                        certEngineValue = new BcCertificateMaker(ExceptionFunc, CertificateValidDays);
                        break;
                    case CertificateEngine.BouncyCastleFast:
                        certEngineValue = new BcCertificateMakerFast(ExceptionFunc, CertificateValidDays);
                        break;
                    case CertificateEngine.DefaultWindows:
                    default:
                        if (!RunTime.IsWindows)
                            throw new PlatformNotSupportedException("The Windows certificate engine requires Windows.");
                        certEngineValue = new WinCertificateMaker(ExceptionFunc, CertificateValidDays);
                        break;
                }

            return certEngineValue
                   ?? throw new InvalidOperationException("The certificate engine could not be initialized.");
        }
    }

    /// <summary>
    ///     Is the root certificate used by this proxy is valid?
    /// </summary>
    internal bool CertValidated => RootCertificate != null;

    /// <summary>
    ///     Trust the RootCertificate used by this proxy server for current user
    /// </summary>
    internal bool UserTrustRoot { get; set; }

    /// <summary>
    ///     Trust the RootCertificate used by this proxy server for current machine
    ///     Needs elevated permission, otherwise will fail silently.
    /// </summary>
    internal bool MachineTrustRoot { get; set; }

    /// <summary>
    ///     Whether trust operations should be done with elevated privileges
    ///     Will prompt with UAC if required. Works only on Windows.
    /// </summary>
    internal bool TrustRootAsAdministrator { get; set; }

    /// <summary>
    ///     Exception handler
    /// </summary>
    internal ExceptionHandler? ExceptionFunc { get; set; }

    /// <summary>
    ///     Select Certificate Engine.
    ///     Optionally set to BouncyCastle.
    ///     Mono only support BouncyCastle and it is the default.
    /// </summary>
    public CertificateEngine CertificateEngine
    {
        get => engine;
        set
        {
            // For Mono (or Non-Windows) only Bouncy Castle is supported
            if (!RunTime.IsWindows) value = CertificateEngine.BouncyCastle;

            if (value != engine)
            {
                certEngineValue = null;
                engine = value;
            }
        }
    }

    /// <summary>
    ///     Password of the Root certificate file.
    ///     <para>Set a password for the .pfx file</para>
    /// </summary>
    public string PfxPassword { get; set; } = string.Empty;

    /// <summary>
    ///     Name(path) of the Root certificate file.
    ///     <para>
    ///         Set the name(path) of the .pfx file. If it is string.Empty Root certificate file will be named as
    ///         "rootCert.pfx" (and will be saved in proxy dll directory)
    ///     </para>
    /// </summary>
    public string PfxFilePath { get; set; } = string.Empty;

    /// <summary>
    ///     Number of Days generated HTTPS certificates are valid for.
    ///     Maximum allowed on iOS 13 is 825 days and it is the default.
    /// </summary>
    public int CertificateValidDays { get; set; } = 825;

    /// <summary>
    ///     Name of the root certificate issuer.
    ///     (This is valid only when RootCertificate property is not set.)
    /// </summary>
    public string RootCertificateIssuerName
    {
        get => issuer ?? DefaultRootCertificateIssuer;
        set => issuer = value;
    }

    /// <summary>
    ///     Name of the root certificate.
    ///     (This is valid only when RootCertificate property is not set.)
    ///     If no certificate is provided then a default Root Certificate will be created and used.
    ///     The provided root certificate will be stored in proxy exe directory with the private key.
    ///     Root certificate file will be named as "rootCert.pfx".
    /// </summary>
    public string RootCertificateName
    {
        get => rootCertificateName ?? DefaultRootRootCertificateName;
        set => rootCertificateName = value;
    }

    /// <summary>
    ///     The root certificate.
    /// </summary>
    public X509Certificate2? RootCertificate
    {
        get => rootCertificate;
        set
        {
            ClearRootCertificate();
            rootCertificate = value;
        }
    }

    /// <summary>
    ///     Save all fake certificates using <seealso cref="CertificateStorage" />.
    ///     <para>for can load the certificate and not make new certificate every time. </para>
    /// </summary>
    public bool SaveFakeCertificates { get; set; } = false;

    /// <summary>
    ///     The fake certificate cache storage.
    ///     The default cache storage implementation saves certificates in folder "crts" (will be created in proxy dll
    ///     directory).
    ///     Implement ICertificateCache interface and assign concrete class here to customize.
    /// </summary>
    public ICertificateCache CertificateStorage
    {
        get => certificateCache;
        set => certificateCache = value ?? new DefaultCertificateDiskCache();
    }

    /// <summary>
    ///     Overwrite Root certificate file.
    ///     <para>true : replace an existing .pfx file if password is incorrect or if RootCertificate = null.</para>
    /// </summary>
    public bool OverwritePfxFile { get; set; } = true;

    /// <summary>
    ///     Minutes certificates should be kept in cache when not used.
    /// </summary>
    public int CertificateCacheTimeOutMinutes { get; set; } = 60;

    /// <summary>
    ///     Adjust behaviour when certificates are saved to filesystem.
    /// </summary>
    public X509KeyStorageFlags StorageFlag { get; set; } = X509KeyStorageFlags.Exportable;

    /// <summary>
    ///     Disable wild card certificates. Disabled by default.
    /// </summary>
    public bool DisableWildCardCertificates { get; set; } = false;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     For CertificateEngine.DefaultWindows to work we need to also check in personal store
    /// </summary>
    /// <param name="storeLocation"></param>
    /// <returns></returns>
    private bool RootCertificateInstalled(StoreLocation storeLocation)
    {
        var certificate = RootCertificate;
        if (certificate == null) throw new Exception("Root certificate is null.");

        var thumbprint = certificate.Thumbprint;
        return FindCertificates(StoreName.Root, storeLocation, thumbprint).Count > 0
               && (CertificateEngine != CertificateEngine.DefaultWindows
                   || FindCertificates(StoreName.My, storeLocation, thumbprint).Count > 0);
    }

    private static X509Certificate2Collection FindCertificates(StoreName storeName, StoreLocation storeLocation,
        string thumbprint)
    {
        var x509Store = new X509Store(storeName, storeLocation);
        try
        {
            x509Store.Open(OpenFlags.OpenExistingOnly);
            return x509Store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
        }
        finally
        {
            x509Store.Close();
        }
    }

    /// <summary>
    ///     Make current machine trust the Root Certificate used by this proxy
    /// </summary>
    /// <param name="storeName"></param>
    /// <param name="storeLocation"></param>
    private void InstallCertificate(StoreName storeName, StoreLocation storeLocation)
    {
        var certificate = RootCertificate;
        if (certificate == null) throw new Exception("Could not install certificate as it is null or empty.");

        if (FindCertificates(storeName, storeLocation, certificate.Thumbprint).Count > 0) return;

        var x509Store = new X509Store(storeName, storeLocation);

        try
        {
            x509Store.Open(OpenFlags.ReadWrite);
            x509Store.Add(certificate);
        }
        catch (Exception e)
        {
            OnException(
                new Exception("Failed to make system trust root certificate "
                              + $" for {storeName}\\{storeLocation} store location. You may need admin rights.",
                    e));
        }
        finally
        {
            x509Store.Close();
        }
    }

    /// <summary>
    ///     Remove the Root Certificate trust
    /// </summary>
    /// <param name="storeName"></param>
    /// <param name="storeLocation"></param>
    /// <param name="certificate"></param>
    private void UninstallCertificate(StoreName storeName, StoreLocation storeLocation, X509Certificate2? certificate)
    {
        if (certificate == null)
        {
            OnException(new Exception("Could not remove certificate as it is null or empty."));
            return;
        }

        var x509Store = new X509Store(storeName, storeLocation);

        try
        {
            x509Store.Open(OpenFlags.ReadWrite);

            x509Store.Remove(certificate);
        }
        catch (Exception e)
        {
            OnException(new Exception("Failed to remove root certificate trust "
                                      + $" for {storeLocation} store location. You may need admin rights.", e));
        }
        finally
        {
            x509Store.Close();
        }
    }

    private X509Certificate2 MakeCertificate(string certificateName, bool isRootCertificate)
    {
        //if (isRoot != (null == signingCertificate))
        //{
        //    throw new ArgumentException(
        //        "You must specify a Signing Certificate if and only if you are not creating a root.",
        //        nameof(signingCertificate));
        //}

        if (!isRootCertificate && RootCertificate == null) CreateRootCertificate();

        var certificate = CertEngine.MakeCertificate(certificateName, isRootCertificate ? null : RootCertificate);

        if (CertificateEngine == CertificateEngine.DefaultWindows)
            Task.Run(() => UninstallCertificate(StoreName.My, StoreLocation.CurrentUser, certificate));

        return certificate;
    }

    private void OnException(Exception exception)
    {
        ExceptionFunc?.Invoke(exception);
    }

    /// <summary>
    ///     Create an SSL certificate
    /// </summary>
    /// <param name="certificateName"></param>
    /// <param name="isRootCertificate"></param>
    /// <returns></returns>
    internal X509Certificate2? CreateCertificate(string certificateName, bool isRootCertificate)
    {
        X509Certificate2? certificate;
        try
        {
            if (!isRootCertificate && SaveFakeCertificates)
            {
                var subjectName = ProxyConstants.CnRemoverRegex
                    .Replace(certificateName, string.Empty)
                    .Replace("*", "$x$");

                try
                {
                    certificate = certificateCache.LoadCertificate(subjectName, StorageFlag);

                    if (certificate != null && certificate.NotAfter <= DateTime.Now)
                    {
                        OnException(new Exception($"Cached certificate for {subjectName} has expired."));
                        certificate = null;
                    }
                }
                catch (Exception e)
                {
                    OnException(new Exception("Failed to load fake certificate.", e));
                    certificate = null;
                }

                if (certificate == null)
                {
                    var createdCertificate = MakeCertificate(certificateName, false);
                    certificate = createdCertificate;

                    //Don't need to wait for save to complete
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var lockKey = subjectName.ToLower();
                            //acquire lock by subjectName
                            //Async lock is not needed. Since this is a rare race-condition
                            lock (_saveCertificateLocks.GetOrAdd(lockKey, new object()))
                            {
                                try
                                {
                                    //no two tasks with same subject name should together enter here 
                                    certificateCache.SaveCertificate(subjectName, createdCertificate);
                                }
                                finally
                                {
                                    //save operation is complete. Free lock from memory.
                                    _saveCertificateLocks.TryRemove(lockKey, out var _);
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            OnException(new Exception("Failed to save fake certificate.", e));
                        }
                    });
                }
            }
            else
            {
                certificate = MakeCertificate(certificateName, isRootCertificate);
            }
        }
        catch (Exception e)
        {
            OnException(e);
            certificate = null;
        }

        return certificate;
    }

    /// <summary>
    ///     Tries to get a still-valid certificate from the in-memory cache.
    ///     Expired cached certificates are evicted and disposed so that a fresh one is generated.
    /// </summary>
    private bool TryGetValidCachedCertificate(string certificateName, out X509Certificate2? certificate)
    {
        certificate = null;

        if (!cachedCertificates.TryGetValue(certificateName, out var cached)) return false;

        // do not serve an expired (or not-yet-valid) certificate from the cache
        var now = DateTime.Now;
        if (cached.Certificate.NotAfter <= now || cached.Certificate.NotBefore > now)
        {
            if (cachedCertificates.TryRemove(certificateName, out var removed)) removed.Certificate.Dispose();
            return false;
        }

        cached.LastAccess = DateTime.UtcNow;
        certificate = cached.Certificate;
        return true;
    }

    /// <summary>
    ///     Creates a server certificate signed by the root certificate.
    /// </summary>
    /// <param name="certificateName"></param>
    /// <returns></returns>
    public async Task<X509Certificate2?> CreateServerCertificate(string certificateName)
    {
        // check in cache first
        if (TryGetValidCachedCertificate(certificateName, out var cachedCertificate))
            return cachedCertificate;

        var createdTask = false;
        Task<X509Certificate2?> createCertificateTask;
        await pendingCertificateCreationTaskLock.WaitAsync();
        try
        {
            // check in cache first
            if (TryGetValidCachedCertificate(certificateName, out cachedCertificate))
                return cachedCertificate;

            // handle burst requests with same certificate name
            // by checking for existing task for same certificate name
            if (!pendingCertificateCreationTasks.TryGetValue(certificateName, out var existingTask)
                || existingTask == null)
            {
                // run certificate creation task & add it to pending tasks
                createCertificateTask = Task.Run(() =>
                {
                    var result = CreateCertificate(certificateName, false);
                    if (result != null)
                        cachedCertificates.TryAdd(certificateName,
                            new CachedCertificate(result) { LastAccess = DateTime.UtcNow });

                    return result;
                });

                pendingCertificateCreationTasks[certificateName] = createCertificateTask;
                createdTask = true;
            }
            else
            {
                createCertificateTask = existingTask;
            }
        }
        finally
        {
            pendingCertificateCreationTaskLock.Release();
        }

        var certificate = await createCertificateTask;

        if (createdTask)
        {
            // cleanup pending task
            await pendingCertificateCreationTaskLock.WaitAsync();
            try
            {
                pendingCertificateCreationTasks.Remove(certificateName);
            }
            finally
            {
                pendingCertificateCreationTaskLock.Release();
            }
        }

        return certificate;
    }

    /// <summary>
    ///     A method to clear outdated certificates
    /// </summary>
    internal async void ClearIdleCertificates()
    {
        var cancellationToken = clearCertificatesTokenSource.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            // this runs on a fire-and-forget (async void) task, so any exception here would go
            // unobserved and could crash the process; keep the sweep resilient.
            try
            {
                var cutOff = DateTime.UtcNow.AddMinutes(-CertificateCacheTimeOutMinutes);

                var outdated = cachedCertificates.Where(x => x.Value.LastAccess < cutOff).ToList();

                foreach (var cache in outdated)
                    // dispose the evicted certificate so its native handle is released promptly
                    // rather than waiting for finalization.
                    if (cachedCertificates.TryRemove(cache.Key, out var removed))
                        removed.Certificate.Dispose();
            }
            catch (Exception e)
            {
                OnException(e);
            }

            // after a minute come back to check for outdated certificates in cache
            try
            {
                await Task.Delay(1000 * 60, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    ///     Stops the certificate cache clear process
    /// </summary>
    internal void StopClearIdleCertificates()
    {
        clearCertificatesTokenSource.Cancel();
    }

    /// <summary>
    ///     Attempts to create a RootCertificate.
    /// </summary>
    /// <param name="persistToFile">if set to <c>true</c> try to load/save the certificate from rootCert.pfx.</param>
    /// <returns>
    ///     true if succeeded, else false.
    /// </returns>
    public bool CreateRootCertificate(bool persistToFile = true)
    {
        lock (rootCertCreationLock)
        {
            if (persistToFile && RootCertificate == null) RootCertificate = LoadRootCertificate();

            if (RootCertificate != null) return true;

            if (!OverwritePfxFile)
                try
                {
                    var rootCert = certificateCache.LoadRootCertificate(PfxFilePath, PfxPassword,
                        X509KeyStorageFlags.Exportable);

                    if (rootCert != null && rootCert.NotAfter <= DateTime.Now)
                    {
                        OnException(new Exception("Loaded root certificate has expired."));
                        return false;
                    }

                    if (rootCert != null)
                    {
                        RootCertificate = rootCert;
                        return true;
                    }
                }
                catch (Exception e)
                {
                    // root cert cannot be loaded
                    OnException(new Exception("Root cert cannot be loaded.", e));
                }

            try
            {
                RootCertificate = CreateCertificate(RootCertificateName, true);
            }
            catch (Exception e)
            {
                OnException(e);
            }

            if (persistToFile && RootCertificate != null)
                try
                {
                    try
                    {
                        certificateCache.Clear();
                    }
                    catch (Exception e)
                    {
                        OnException(new Exception("An error happened when clearing certificate cache.", e));
                    }

                    certificateCache.SaveRootCertificate(PfxFilePath, PfxPassword, RootCertificate);
                }
                catch (Exception e)
                {
                    OnException(e);
                }

            return RootCertificate != null;
        }
    }

    /// <summary>
    ///     Loads root certificate from current executing assembly location with expected name rootCert.pfx.
    /// </summary>
    /// <returns></returns>
    public X509Certificate2? LoadRootCertificate()
    {
        try
        {
            var rootCert =
                certificateCache.LoadRootCertificate(PfxFilePath, PfxPassword, X509KeyStorageFlags.Exportable);

            if (rootCert != null && rootCert.NotAfter <= DateTime.Now)
            {
                OnException(new ArgumentException("Loaded root certificate has expired."));
                return null;
            }

            return rootCert;
        }
        catch (Exception e)
        {
            OnException(e);
            return null;
        }
    }

    /// <summary>
    ///     Manually load a Root certificate file from give path (.pfx file).
    /// </summary>
    /// <param name="pfxFilePath">
    ///     Set the name(path) of the .pfx file. If it is string.Empty Root certificate file will be
    ///     named as "rootCert.pfx" (and will be saved in proxy dll directory).
    /// </param>
    /// <param name="password">Set a password for the .pfx file.</param>
    /// <param name="overwritePfXFile">
    ///     true : replace an existing .pfx file if password is incorrect or if
    ///     RootCertificate==null.
    /// </param>
    /// <param name="storageFlag"></param>
    /// <returns>
    ///     true if succeeded, else false.
    /// </returns>
    public bool LoadRootCertificate(string pfxFilePath, string password, bool overwritePfXFile = true,
        X509KeyStorageFlags storageFlag = X509KeyStorageFlags.Exportable)
    {
        PfxFilePath = pfxFilePath;
        PfxPassword = password;
        OverwritePfxFile = overwritePfXFile;
        StorageFlag = storageFlag;

        RootCertificate = LoadRootCertificate();

        return RootCertificate != null;
    }

    /// <summary>
    ///     Trusts the root certificate in user store, optionally also in machine store.
    ///     Machine trust would require elevated permissions (will silently fail otherwise).
    /// </summary>
    public void TrustRootCertificate(bool machineTrusted = false)
    {
        // currentUser\personal
        InstallCertificate(StoreName.My, StoreLocation.CurrentUser);

        if (!machineTrusted)
        {
            // currentUser\Root
            InstallCertificate(StoreName.Root, StoreLocation.CurrentUser);
        }
        else
        {
            // current system
            InstallCertificate(StoreName.My, StoreLocation.LocalMachine);

            // this adds to both currentUser\Root & currentMachine\Root
            InstallCertificate(StoreName.Root, StoreLocation.LocalMachine);
        }
    }

    /// <summary>
    ///     Puts the certificate to the user store, optionally also to machine store.
    ///     Prompts with UAC if elevated permissions are required. Works only on Windows.
    /// </summary>
    /// <returns>True if success.</returns>
    public bool TrustRootCertificateAsAdmin(bool machineTrusted = false)
    {
        if (!RunTime.IsWindows) return false;

        var certificate = RootCertificate;
        if (certificate == null) return false;

        // currentUser\Personal
        InstallCertificate(StoreName.My, StoreLocation.CurrentUser);

        var pfxFileName = Path.GetTempFileName();
        File.WriteAllBytes(pfxFileName, certificate.Export(X509ContentType.Pkcs12, PfxPassword));

        // currentUser\Root, currentMachine\Personal &  currentMachine\Root
        var info = new ProcessStartInfo
        {
            FileName = "certutil.exe",
            CreateNoWindow = true,
            UseShellExecute = true,
            Verb = "runas",
            ErrorDialog = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (!machineTrusted)
            info.Arguments = "-f -user -p \"" + PfxPassword + "\" -importpfx root \"" + pfxFileName + "\"";
        else
            info.Arguments = "-importPFX -p \"" + PfxPassword + "\" -f \"" + pfxFileName + "\"";

        try
        {
            var process = Process.Start(info);
            if (process == null) return false;

            process.WaitForExit();
            File.Delete(pfxFileName);
        }
        catch (Exception e)
        {
            OnException(e);
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Ensure certificates are setup (creates root if required).
    ///     Also makes root certificate trusted based on initial setup from proxy constructor for user/machine trust.
    /// </summary>
    public void EnsureRootCertificate()
    {
        if (!CertValidated) CreateRootCertificate();

        if (TrustRootAsAdministrator)
            TrustRootCertificateAsAdmin(MachineTrustRoot);
        else if (UserTrustRoot) TrustRootCertificate(MachineTrustRoot);
    }

    /// <summary>
    ///     Ensure certificates are setup (creates root if required).
    ///     Also makes root certificate trusted based on provided parameters.
    ///     Note:setting machineTrustRootCertificate to true will force userTrustRootCertificate to true.
    /// </summary>
    /// <param name="userTrustRootCertificate">
    ///     Should fake HTTPS certificate be trusted by this machine's user certificate
    ///     store?
    /// </param>
    /// <param name="machineTrustRootCertificate">Should fake HTTPS certificate be trusted by this machine's certificate store?</param>
    /// <param name="trustRootCertificateAsAdmin">
    ///     Should we attempt to trust certificates with elevated permissions by
    ///     prompting for UAC if required?
    /// </param>
    public void EnsureRootCertificate(bool userTrustRootCertificate,
        bool machineTrustRootCertificate, bool trustRootCertificateAsAdmin = false)
    {
        UserTrustRoot = userTrustRootCertificate || machineTrustRootCertificate;
        MachineTrustRoot = machineTrustRootCertificate;
        TrustRootAsAdministrator = trustRootCertificateAsAdmin;

        EnsureRootCertificate();
    }

    /// <summary>
    ///     Determines whether the root certificate is trusted.
    /// </summary>
    public bool IsRootCertificateUserTrusted()
    {
        return RootCertificateInstalled(StoreLocation.CurrentUser) || IsRootCertificateMachineTrusted();
    }

    /// <summary>
    ///     Determines whether the root certificate is machine trusted.
    /// </summary>
    public bool IsRootCertificateMachineTrusted()
    {
        return RootCertificateInstalled(StoreLocation.LocalMachine);
    }

    /// <summary>
    ///     Removes the trusted certificates from user store, optionally also from machine store.
    ///     To remove from machine store elevated permissions are required (will fail silently otherwise).
    /// </summary>
    /// <param name="machineTrusted">Should also remove from machine store?</param>
    public void RemoveTrustedRootCertificate(bool machineTrusted = false)
    {
        // currentUser\personal
        UninstallCertificate(StoreName.My, StoreLocation.CurrentUser, RootCertificate);

        if (!machineTrusted)
        {
            // currentUser\Root
            UninstallCertificate(StoreName.Root, StoreLocation.CurrentUser, RootCertificate);
        }
        else
        {
            // current system
            UninstallCertificate(StoreName.My, StoreLocation.LocalMachine, RootCertificate);

            // this adds to both currentUser\Root & currentMachine\Root
            UninstallCertificate(StoreName.Root, StoreLocation.LocalMachine, RootCertificate);
        }
    }

    /// <summary>
    ///     Removes the trusted certificates from user store, optionally also from machine store
    /// </summary>
    /// <returns>Should also remove from machine store?</returns>
    public bool RemoveTrustedRootCertificateAsAdmin(bool machineTrusted = false)
    {
        if (!RunTime.IsWindows) return false;

        // currentUser\Personal
        UninstallCertificate(StoreName.My, StoreLocation.CurrentUser, RootCertificate);

        var infos = new List<ProcessStartInfo>();
        if (!machineTrusted)
            infos.Add(new ProcessStartInfo
            {
                FileName = "certutil.exe",
                Arguments = "-delstore -user Root \"" + RootCertificateName + "\"",
                CreateNoWindow = true,
                UseShellExecute = true,
                Verb = "runas",
                ErrorDialog = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        else
            infos.AddRange(
                new List<ProcessStartInfo>
                {
                    // currentMachine\Personal
                    new()
                    {
                        FileName = "certutil.exe",
                        Arguments = "-delstore My \"" + RootCertificateName + "\"",
                        CreateNoWindow = true,
                        UseShellExecute = true,
                        Verb = "runas",
                        ErrorDialog = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    },

                    // currentUser\Personal & currentMachine\Personal
                    new()
                    {
                        FileName = "certutil.exe",
                        Arguments = "-delstore Root \"" + RootCertificateName + "\"",
                        CreateNoWindow = true,
                        UseShellExecute = true,
                        Verb = "runas",
                        ErrorDialog = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    }
                });

        var success = true;
        try
        {
            foreach (var info in infos)
            {
                var process = Process.Start(info);

                if (process == null) success = false;

                process?.WaitForExit();
            }
        }
        catch
        {
            success = false;
        }

        return success;
    }

    /// <summary>
    ///     Clear the root certificate and cache.
    /// </summary>
    public void ClearRootCertificate()
    {
        certificateCache.Clear();
        cachedCertificates.Clear();
        rootCertificate = null;
    }

    private void Dispose(bool disposing)
    {
        if (disposed) return;

        if (disposing) clearCertificatesTokenSource.Dispose();

        disposed = true;
    }

    ~CertificateManager()
    {
        Dispose(false);
    }
}ParseOptions.0.json»K
kD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Certificates\Makers\BCCertificateMaker.cs√Jusing System;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.X509;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Shared;
using X509Certificate = Org.BouncyCastle.X509.X509Certificate;

namespace Titanium.Web.Proxy.Network.Certificate;

/// <summary>
///     Implements certificate generation operations.
/// </summary>
internal class BcCertificateMaker : ICertificateMaker
{
    private const int CertificateGraceDays = 366;

    // The FriendlyName value cannot be set on Unix.
    // Set this flag to true when exception detected to avoid further exceptions
    private static bool _doNotSetFriendlyName;
    private readonly int certificateValidDays;

    private readonly ExceptionHandler? exceptionFunc;

    internal BcCertificateMaker(ExceptionHandler? exceptionFunc, int certificateValidDays)
    {
        this.certificateValidDays = certificateValidDays;
        this.exceptionFunc = exceptionFunc;
    }

    /// <summary>
    ///     Makes the certificate.
    /// </summary>
    /// <param name="sSubjectCn">The s subject cn.</param>
    /// <param name="signingCert">The signing cert.</param>
    /// <returns>X509Certificate2 instance.</returns>
    public X509Certificate2 MakeCertificate(string sSubjectCn, X509Certificate2? signingCert = null)
    {
        return MakeCertificateInternal(sSubjectCn, true, signingCert);
    }

    /// <summary>
    ///     Generates the certificate.
    /// </summary>
    /// <param name="subjectName">Name of the subject.</param>
    /// <param name="issuerName">Name of the issuer.</param>
    /// <param name="validFrom">The valid from.</param>
    /// <param name="validTo">The valid to.</param>
    /// <param name="keyStrength">The key strength.</param>
    /// <param name="signatureAlgorithm">The signature algorithm.</param>
    /// <param name="issuerPrivateKey">The issuer private key.</param>
    /// <param name="hostName">The host name</param>
    /// <returns>X509Certificate2 instance.</returns>
    /// <exception cref="PemException">Malformed sequence in RSA private key</exception>
    private static X509Certificate2 GenerateCertificate(string? hostName,
        string subjectName,
        string issuerName, DateTime validFrom,
        DateTime validTo, int keyStrength = 2048,
        string signatureAlgorithm = "SHA256WithRSA",
        AsymmetricKeyParameter? issuerPrivateKey = null)
    {
        // Generating Random Numbers
        var randomGenerator = new CryptoApiRandomGenerator();
        var secureRandom = new SecureRandom(randomGenerator);

        // The Certificate Generator
        var certificateGenerator = new X509V3CertificateGenerator();

        // Serial Number
        var serialNumber =
            BigIntegers.CreateRandomInRange(BigInteger.One, BigInteger.ValueOf(long.MaxValue), secureRandom);
        certificateGenerator.SetSerialNumber(serialNumber);

        // Issuer and Subject Name
        var subjectDn = new X509Name(subjectName);
        var issuerDn = new X509Name(issuerName);
        certificateGenerator.SetIssuerDN(issuerDn);
        certificateGenerator.SetSubjectDN(subjectDn);

        certificateGenerator.SetNotBefore(validFrom);
        certificateGenerator.SetNotAfter(validTo);

        if (hostName != null)
        {
            // add subject alternative names
            var nameType = GeneralName.DnsName;
            if (IPAddress.TryParse(hostName, out _)) nameType = GeneralName.IPAddress;

            var subjectAlternativeNames = new Asn1Encodable[] { new GeneralName(nameType, hostName) };

            var subjectAlternativeNamesExtension = new DerSequence(subjectAlternativeNames);
            certificateGenerator.AddExtension(X509Extensions.SubjectAlternativeName.Id, false,
                subjectAlternativeNamesExtension);
        }

        // Subject Public Key
        var keyGenerationParameters = new KeyGenerationParameters(secureRandom, keyStrength);
        var keyPairGenerator = new RsaKeyPairGenerator();
        keyPairGenerator.Init(keyGenerationParameters);
        var subjectKeyPair = keyPairGenerator.GenerateKeyPair();

        certificateGenerator.SetPublicKey(subjectKeyPair.Public);

        // Set certificate intended purposes to only Server Authentication
        certificateGenerator.AddExtension(X509Extensions.ExtendedKeyUsage.Id, false,
            new ExtendedKeyUsage(KeyPurposeID.id_kp_serverAuth));
        if (issuerPrivateKey == null)
            certificateGenerator.AddExtension(X509Extensions.BasicConstraints.Id, true, new BasicConstraints(true));

        var signatureFactory = new Asn1SignatureFactory(signatureAlgorithm,
            issuerPrivateKey ?? subjectKeyPair.Private, secureRandom);

        // Self-sign the certificate
        var certificate = certificateGenerator.Generate(signatureFactory);

        // Corresponding private key
        var privateKeyInfo = PrivateKeyInfoFactory.CreatePrivateKeyInfo(subjectKeyPair.Private);

        var seq = (Asn1Sequence)Asn1Object.FromByteArray(privateKeyInfo.ParsePrivateKey().GetDerEncoded());

        if (seq.Count != 9) throw new PemException("Malformed sequence in RSA private key");

        var rsa = RsaPrivateKeyStructure.GetInstance(seq);
        var rsaparams = new RsaPrivateCrtKeyParameters(rsa.Modulus, rsa.PublicExponent, rsa.PrivateExponent,
            rsa.Prime1, rsa.Prime2, rsa.Exponent1,
            rsa.Exponent2, rsa.Coefficient);

        // Set private key onto certificate instance
        var x509Certificate = WithPrivateKey(certificate, rsaparams);

        if (!_doNotSetFriendlyName && RunTime.IsWindows)
            try
            {
                x509Certificate.FriendlyName = ProxyConstants.CnRemoverRegex.Replace(subjectName, string.Empty);
            }
            catch (PlatformNotSupportedException)
            {
                _doNotSetFriendlyName = true;
            }

        return x509Certificate;
    }

    private static X509Certificate2 WithPrivateKey(X509Certificate certificate, AsymmetricKeyParameter privateKey)
    {
        const string password = "password";

        var builder = new Pkcs12StoreBuilder();
        if (RunTime.IsRunningOnMono)
        {
            builder.SetUseDerEncoding(true);
        }

        var store = builder.Build(); var entry = new X509CertificateEntry(certificate);
        store.SetCertificateEntry(certificate.SubjectDN.ToString(), entry);

        store.SetKeyEntry(certificate.SubjectDN.ToString(), new AsymmetricKeyEntry(privateKey), new[] { entry });
        using (var ms = new MemoryStream())
        {
            store.Save(ms, password.ToCharArray(), new SecureRandom(new CryptoApiRandomGenerator()));

            return CertificateLoader.LoadPkcs12(ms.ToArray(), password, X509KeyStorageFlags.Exportable);
        }
    }

    /// <summary>
    ///     Makes the certificate internal.
    /// </summary>
    /// <param name="hostName">hostname for certificate</param>
    /// <param name="subjectName">The full subject.</param>
    /// <param name="validFrom">The valid from.</param>
    /// <param name="validTo">The valid to.</param>
    /// <param name="signingCertificate">The signing certificate.</param>
    /// <returns>X509Certificate2 instance.</returns>
    /// <exception cref="System.ArgumentException">
    ///     You must specify a Signing Certificate if and only if you are not creating a
    ///     root.
    /// </exception>
    private X509Certificate2 MakeCertificateInternal(string hostName, string subjectName,
        DateTime validFrom, DateTime validTo, X509Certificate2? signingCertificate)
    {
        if (signingCertificate == null) return GenerateCertificate(null, subjectName, subjectName, validFrom, validTo);

        using var privateKey = signingCertificate.GetRSAPrivateKey()
                               ?? throw new InvalidOperationException("The signing certificate has no RSA private key.");
        var kp = DotNetUtilities.GetKeyPair(privateKey);
        return GenerateCertificate(hostName, subjectName, signingCertificate.Subject, validFrom, validTo,
            issuerPrivateKey: kp.Private);
    }

    /// <summary>
    ///     Makes the certificate internal.
    /// </summary>
    /// <param name="subject">The s subject cn.</param>
    /// <param name="switchToMtaIfNeeded">if set to <c>true</c> [switch to MTA if needed].</param>
    /// <param name="signingCert">The signing cert.</param>
    /// <returns>X509Certificate2.</returns>
    private X509Certificate2 MakeCertificateInternal(string subject,
        bool switchToMtaIfNeeded, X509Certificate2? signingCert = null)
    {
        return MakeCertificateInternal(subject, $"CN={subject}",
            DateTime.UtcNow.AddDays(-CertificateGraceDays), DateTime.UtcNow.AddDays(certificateValidDays),
            signingCert);
    }
}ParseOptions.0.json∞N
oD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Certificates\Makers\BCCertificateMakerFast.csßMusing System;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.X509;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Shared;
using X509Certificate = Org.BouncyCastle.X509.X509Certificate;

namespace Titanium.Web.Proxy.Network.Certificate;

/// <summary>
///     Implements certificate generation operations.
/// </summary>
internal class BcCertificateMakerFast : ICertificateMaker
{
    private const int CertificateGraceDays = 366;

    // The FriendlyName value cannot be set on Unix.
    // Set this flag to true when exception detected to avoid further exceptions
    private static bool _doNotSetFriendlyName;

    private readonly ExceptionHandler? exceptionFunc;
    private readonly int certificateValidDays;

    internal BcCertificateMakerFast(ExceptionHandler? exceptionFunc, int certificateValidDays)
    {
        this.certificateValidDays = certificateValidDays;
        this.exceptionFunc = exceptionFunc;
        KeyPair = GenerateKeyPair();
    }

    public AsymmetricCipherKeyPair KeyPair { get; set; }

    /// <summary>
    ///     Makes the certificate.
    /// </summary>
    /// <param name="sSubjectCn">The s subject cn.</param>
    /// <param name="signingCert">The signing cert.</param>
    /// <returns>X509Certificate2 instance.</returns>
    public X509Certificate2 MakeCertificate(string sSubjectCn, X509Certificate2? signingCert = null)
    {
        return MakeCertificateInternal(sSubjectCn, true, signingCert);
    }

    /// <summary>
    ///     Generates the certificate.
    /// </summary>
    /// <param name="subjectName">Name of the subject.</param>
    /// <param name="issuerName">Name of the issuer.</param>
    /// <param name="validFrom">The valid from.</param>
    /// <param name="validTo">The valid to.</param>
    /// <param name="subjectKeyPair">The key pair.</param>
    /// <param name="signatureAlgorithm">The signature algorithm.</param>
    /// <param name="issuerPrivateKey">The issuer private key.</param>
    /// <param name="hostName">The host name</param>
    /// <returns>X509Certificate2 instance.</returns>
    /// <exception cref="PemException">Malformed sequence in RSA private key</exception>
    private static X509Certificate2 GenerateCertificate(string? hostName,
        string subjectName,
        string issuerName, DateTime validFrom,
        DateTime validTo, AsymmetricCipherKeyPair subjectKeyPair,
        string signatureAlgorithm = "SHA256WithRSA",
        AsymmetricKeyParameter? issuerPrivateKey = null)
    {
        // Generating Random Numbers
        var randomGenerator = new CryptoApiRandomGenerator();
        var secureRandom = new SecureRandom(randomGenerator);

        // The Certificate Generator
        var certificateGenerator = new X509V3CertificateGenerator();

        // Serial Number
        var serialNumber =
            BigIntegers.CreateRandomInRange(BigInteger.One, BigInteger.ValueOf(long.MaxValue), secureRandom);
        certificateGenerator.SetSerialNumber(serialNumber);

        // Issuer and Subject Name
        var subjectDn = new X509Name(subjectName);
        var issuerDn = new X509Name(issuerName);
        certificateGenerator.SetIssuerDN(issuerDn);
        certificateGenerator.SetSubjectDN(subjectDn);

        certificateGenerator.SetNotBefore(validFrom);
        certificateGenerator.SetNotAfter(validTo);

        if (hostName != null)
        {
            // add subject alternative names
            var nameType = GeneralName.DnsName;
            if (IPAddress.TryParse(hostName, out _)) nameType = GeneralName.IPAddress;

            var subjectAlternativeNames = new Asn1Encodable[] { new GeneralName(nameType, hostName) };

            var subjectAlternativeNamesExtension = new DerSequence(subjectAlternativeNames);
            certificateGenerator.AddExtension(X509Extensions.SubjectAlternativeName.Id, false,
                subjectAlternativeNamesExtension);
        }

        // Subject Public Key
        certificateGenerator.SetPublicKey(subjectKeyPair.Public);

        // Set certificate intended purposes to only Server Authentication
        certificateGenerator.AddExtension(X509Extensions.ExtendedKeyUsage.Id, false,
            new ExtendedKeyUsage(KeyPurposeID.id_kp_serverAuth));
        if (issuerPrivateKey == null)
            certificateGenerator.AddExtension(X509Extensions.BasicConstraints.Id, true, new BasicConstraints(true));

        var signatureFactory = new Asn1SignatureFactory(signatureAlgorithm,
            issuerPrivateKey ?? subjectKeyPair.Private, secureRandom);

        // Self-sign the certificate
        var certificate = certificateGenerator.Generate(signatureFactory);

        // Corresponding private key
        var privateKeyInfo = PrivateKeyInfoFactory.CreatePrivateKeyInfo(subjectKeyPair.Private);

        var seq = (Asn1Sequence)Asn1Object.FromByteArray(privateKeyInfo.ParsePrivateKey().GetDerEncoded());

        if (seq.Count != 9) throw new PemException("Malformed sequence in RSA private key");

        var rsa = RsaPrivateKeyStructure.GetInstance(seq);
        var rsaparams = new RsaPrivateCrtKeyParameters(rsa.Modulus, rsa.PublicExponent, rsa.PrivateExponent,
            rsa.Prime1, rsa.Prime2, rsa.Exponent1,
            rsa.Exponent2, rsa.Coefficient);

        // Set private key onto certificate instance
        var x509Certificate = WithPrivateKey(certificate, rsaparams);

        if (!_doNotSetFriendlyName && RunTime.IsWindows)
            try
            {
                x509Certificate.FriendlyName = ProxyConstants.CnRemoverRegex.Replace(subjectName, string.Empty);
            }
            catch (PlatformNotSupportedException)
            {
                _doNotSetFriendlyName = true;
            }

        return x509Certificate;
    }

    public AsymmetricCipherKeyPair GenerateKeyPair(int keyStrength = 2048)
    {
        var randomGenerator = new CryptoApiRandomGenerator();
        var secureRandom = new SecureRandom(randomGenerator);

        var keyGenerationParameters = new KeyGenerationParameters(secureRandom, keyStrength);
        var keyPairGenerator = new RsaKeyPairGenerator();
        keyPairGenerator.Init(keyGenerationParameters);
        return keyPairGenerator.GenerateKeyPair();
    }

    private static X509Certificate2 WithPrivateKey(X509Certificate certificate, AsymmetricKeyParameter privateKey)
    {
        const string password = "password";

        var builder = new Pkcs12StoreBuilder();
        if (RunTime.IsRunningOnMono)
        {
            builder.SetUseDerEncoding(true);
        }

        var store = builder.Build(); var entry = new X509CertificateEntry(certificate);
        store.SetCertificateEntry(certificate.SubjectDN.ToString(), entry);

        store.SetKeyEntry(certificate.SubjectDN.ToString(), new AsymmetricKeyEntry(privateKey), new[] { entry });
        using (var ms = new MemoryStream())
        {
            store.Save(ms, password.ToCharArray(), new SecureRandom(new CryptoApiRandomGenerator()));

            return CertificateLoader.LoadPkcs12(ms.ToArray(), password, X509KeyStorageFlags.Exportable);
        }
    }

    /// <summary>
    ///     Makes the certificate internal.
    /// </summary>
    /// <param name="hostName">hostname for certificate</param>
    /// <param name="subjectName">The full subject.</param>
    /// <param name="validFrom">The valid from.</param>
    /// <param name="validTo">The valid to.</param>
    /// <param name="signingCertificate">The signing certificate.</param>
    /// <returns>X509Certificate2 instance.</returns>
    /// <exception cref="System.ArgumentException">
    ///     You must specify a Signing Certificate if and only if you are not creating a
    ///     root.
    /// </exception>
    private X509Certificate2 MakeCertificateInternal(string hostName, string subjectName,
        DateTime validFrom, DateTime validTo, X509Certificate2? signingCertificate)
    {
        if (signingCertificate == null)
            return GenerateCertificate(null, subjectName, subjectName, validFrom, validTo, KeyPair);

        using var privateKey = signingCertificate.GetRSAPrivateKey()
                               ?? throw new InvalidOperationException("The signing certificate has no RSA private key.");
        var kp = DotNetUtilities.GetKeyPair(privateKey);
        return GenerateCertificate(hostName, subjectName, signingCertificate.Subject, validFrom, validTo, KeyPair,
            issuerPrivateKey: kp.Private);
    }

    /// <summary>
    ///     Makes the certificate internal.
    /// </summary>
    /// <param name="subject">The s subject cn.</param>
    /// <param name="switchToMtaIfNeeded">if set to <c>true</c> [switch to MTA if needed].</param>
    /// <param name="signingCert">The signing cert.</param>
    /// <returns>X509Certificate2.</returns>
    private X509Certificate2 MakeCertificateInternal(string subject,
        bool switchToMtaIfNeeded, X509Certificate2? signingCert = null)
    {
        return MakeCertificateInternal(subject, $"CN={subject}",
            DateTime.UtcNow.AddDays(-CertificateGraceDays), DateTime.UtcNow.AddDays(certificateValidDays),
            signingCert);
    }
}ParseOptions.0.json◊
jD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Certificates\Makers\ICertificateMaker.cs”using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.Network.Certificate;

/// <summary>
///     Abstract interface for different Certificate Maker Engines
/// </summary>
internal interface ICertificateMaker
{
    X509Certificate2 MakeCertificate(string sSubjectCn, X509Certificate2? signingCert);
}ParseOptions.0.jsonœu
lD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Certificates\Makers\WinCertificateMaker.cs…tusing System;
using System.Net;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Network.Certificate;

/// <inheritdoc />
/// <summary>
///     Certificate Maker - uses MakeCert
///     Calls COM objects using reflection
/// </summary>
#if !NETFRAMEWORK
[SupportedOSPlatform("windows")]
#endif
internal class WinCertificateMaker : ICertificateMaker
{
    private readonly ExceptionHandler? exceptionFunc;

    private readonly string sProviderName = "Microsoft Enhanced Cryptographic Provider v1.0";

    private readonly Type typeAltNamesCollection;

    private readonly Type typeBasicConstraints;

    private readonly Type typeCAlternativeName;

    private readonly Type typeEkuExt;

    private readonly Type typeExtNames;

    private readonly Type typeKuExt;

    private readonly Type typeOid;

    private readonly Type typeOids;

    private readonly Type typeRequestCert;

    private readonly Type typeSignerCertificate;
    private readonly Type typeX500Dn;

    private readonly Type typeX509Enrollment;

    private readonly Type typeX509Extensions;

    private readonly Type typeX509PrivateKey;

    // Validity Days for Root Certificates Generated.
    private readonly int certificateValidDays;

    private object? sharedPrivateKey;

    /// <summary>
    ///     Constructor.
    /// </summary>
    internal WinCertificateMaker(ExceptionHandler? exceptionFunc, int certificateValidDays)
    {
        this.certificateValidDays = certificateValidDays;
        this.exceptionFunc = exceptionFunc;

        typeX500Dn = GetComType("X509Enrollment.CX500DistinguishedName");
        typeX509PrivateKey = GetComType("X509Enrollment.CX509PrivateKey");
        typeOid = GetComType("X509Enrollment.CObjectId");
        typeOids = GetComType("X509Enrollment.CObjectIds.1");
        typeEkuExt = GetComType("X509Enrollment.CX509ExtensionEnhancedKeyUsage");
        typeKuExt = GetComType("X509Enrollment.CX509ExtensionKeyUsage");
        typeRequestCert = GetComType("X509Enrollment.CX509CertificateRequestCertificate");
        typeX509Extensions = GetComType("X509Enrollment.CX509Extensions");
        typeBasicConstraints = GetComType("X509Enrollment.CX509ExtensionBasicConstraints");
        typeSignerCertificate = GetComType("X509Enrollment.CSignerCertificate");
        typeX509Enrollment = GetComType("X509Enrollment.CX509Enrollment");

        // for alternative names
        typeAltNamesCollection = GetComType("X509Enrollment.CAlternativeNames");
        typeExtNames = GetComType("X509Enrollment.CX509ExtensionAlternativeNames");
        typeCAlternativeName = GetComType("X509Enrollment.CAlternativeName");
    }

    private static Type GetComType(string progId)
    {
        return Type.GetTypeFromProgID(progId, true)
               ?? throw new PlatformNotSupportedException($"COM type '{progId}' is unavailable.");
    }

    private static object CreateComObject(Type type)
    {
        return Activator.CreateInstance(type)
               ?? throw new InvalidOperationException($"Could not create COM type '{type.FullName}'.");
    }

    /// <summary>
    ///     Make certificate.
    /// </summary>
    public X509Certificate2 MakeCertificate(string sSubjectCn, X509Certificate2? signingCert = null)
    {
        return MakeCertificate(sSubjectCn, true, signingCert);
    }

    private X509Certificate2 MakeCertificate(string sSubjectCn,
        bool switchToMtaIfNeeded, X509Certificate2? signingCertificate = null,
        CancellationToken cancellationToken = default)
    {
        if (switchToMtaIfNeeded && Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
            return Task.Run(() => MakeCertificate(sSubjectCn, false, signingCertificate),
                cancellationToken).Result;

        // Subject
        var fullSubject = $"CN={sSubjectCn}";

        // Sig Algo
        const string hashAlgo = "SHA256";

        // Grace Days
        const int graceDays = -366;

        // KeyLength
        const int keyLength = 2048;

        var now = DateTime.UtcNow;
        var graceTime = now.AddDays(graceDays);
        var certificate = MakeCertificate(sSubjectCn, fullSubject, keyLength, hashAlgo, graceTime,
            now.AddDays(certificateValidDays), signingCertificate);
        return certificate;
    }

    private X509Certificate2 MakeCertificate(string subject, string fullSubject,
        int privateKeyLength, string hashAlg, DateTime validFrom, DateTime validTo,
        X509Certificate2? signingCertificate)
    {
        var x500CertDn = CreateComObject(typeX500Dn);
        object?[] typeValue = { fullSubject, 0 };
        typeX500Dn.InvokeMember("Encode", BindingFlags.InvokeMethod, null, x500CertDn, typeValue);

        var x500RootCertDn = CreateComObject(typeX500Dn);

        if (signingCertificate != null) typeValue[0] = signingCertificate.Subject;

        typeX500Dn.InvokeMember("Encode", BindingFlags.InvokeMethod, null, x500RootCertDn, typeValue);

        object? sharedPrivateKey = null;
        if (signingCertificate != null) sharedPrivateKey = this.sharedPrivateKey;

        if (sharedPrivateKey == null)
        {
            sharedPrivateKey = CreateComObject(typeX509PrivateKey);
            typeValue = new object?[] { sProviderName };
            typeX509PrivateKey.InvokeMember("ProviderName", BindingFlags.PutDispProperty, null, sharedPrivateKey,
                typeValue);
            typeValue[0] = 2;
            typeX509PrivateKey.InvokeMember("ExportPolicy", BindingFlags.PutDispProperty, null, sharedPrivateKey,
                typeValue);
            typeValue = new object?[] { signingCertificate == null ? 2 : 1 };
            typeX509PrivateKey.InvokeMember("KeySpec", BindingFlags.PutDispProperty, null, sharedPrivateKey,
                typeValue);

            if (signingCertificate != null)
            {
                typeValue = new object?[] { 176 };
                typeX509PrivateKey.InvokeMember("KeyUsage", BindingFlags.PutDispProperty, null, sharedPrivateKey,
                    typeValue);
            }

            typeValue[0] = privateKeyLength;
            typeX509PrivateKey.InvokeMember("Length", BindingFlags.PutDispProperty, null, sharedPrivateKey,
                typeValue);
            typeX509PrivateKey.InvokeMember("Create", BindingFlags.InvokeMethod, null, sharedPrivateKey, null);

            if (signingCertificate != null) this.sharedPrivateKey = sharedPrivateKey;
        }

        typeValue = new object?[1];

        var oid = CreateComObject(typeOid);
        typeValue[0] = "1.3.6.1.5.5.7.3.1";
        typeOid.InvokeMember("InitializeFromValue", BindingFlags.InvokeMethod, null, oid, typeValue);

        var oids = CreateComObject(typeOids);
        typeValue[0] = oid;
        typeOids.InvokeMember("Add", BindingFlags.InvokeMethod, null, oids, typeValue);

        var ekuExt = CreateComObject(typeEkuExt);
        typeValue[0] = oids;
        typeEkuExt.InvokeMember("InitializeEncode", BindingFlags.InvokeMethod, null, ekuExt, typeValue);

        var requestCert = CreateComObject(typeRequestCert);

        typeValue = new object?[] { 1, sharedPrivateKey, string.Empty };
        typeRequestCert.InvokeMember("InitializeFromPrivateKey", BindingFlags.InvokeMethod, null, requestCert,
            typeValue);
        typeValue = new object?[] { x500CertDn };
        typeRequestCert.InvokeMember("Subject", BindingFlags.PutDispProperty, null, requestCert, typeValue);
        typeValue[0] = x500RootCertDn;
        typeRequestCert.InvokeMember("Issuer", BindingFlags.PutDispProperty, null, requestCert, typeValue);
        typeValue[0] = validFrom;
        typeRequestCert.InvokeMember("NotBefore", BindingFlags.PutDispProperty, null, requestCert, typeValue);
        typeValue[0] = validTo;
        typeRequestCert.InvokeMember("NotAfter", BindingFlags.PutDispProperty, null, requestCert, typeValue);

        var kuExt = CreateComObject(typeKuExt);

        typeValue[0] = 176;
        typeKuExt.InvokeMember("InitializeEncode", BindingFlags.InvokeMethod, null, kuExt, typeValue);

        var certificate =
            typeRequestCert.InvokeMember("X509Extensions", BindingFlags.GetProperty, null, requestCert, null)
            ?? throw new InvalidOperationException("The enrollment request did not return X509 extensions.");
        typeValue = new object?[1];

        if (signingCertificate != null)
        {
            typeValue[0] = kuExt;
            typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, certificate, typeValue);
        }

        typeValue[0] = ekuExt;
        typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, certificate, typeValue);

        if (signingCertificate != null)
        {
            // add alternative names 
            // https://forums.iis.net/t/1180823.aspx

            var altNameCollection = CreateComObject(typeAltNamesCollection);
            var extNames = CreateComObject(typeExtNames);
            var altDnsNames = CreateComObject(typeCAlternativeName);

            if (IPAddress.TryParse(subject, out var ip))
            {
                var ipBase64 = Convert.ToBase64String(ip.GetAddressBytes());
                typeValue = new object?[]
                    { AlternativeNameType.XcnCertAltNameIpAddress, EncodingType.XcnCryptStringBase64, ipBase64 };
                typeCAlternativeName.InvokeMember("InitializeFromRawData", BindingFlags.InvokeMethod, null, altDnsNames,
                    typeValue);
            }
            else
            {
                typeValue = new object?[] { 3, subject }; //3==DNS, 8==IP ADDR
                typeCAlternativeName.InvokeMember("InitializeFromString", BindingFlags.InvokeMethod, null, altDnsNames,
                    typeValue);
            }

            typeValue = new object?[] { altDnsNames };
            typeAltNamesCollection.InvokeMember("Add", BindingFlags.InvokeMethod, null, altNameCollection,
                typeValue);


            typeValue = new object?[] { altNameCollection };
            typeExtNames.InvokeMember("InitializeEncode", BindingFlags.InvokeMethod, null, extNames, typeValue);

            typeValue[0] = extNames;
            typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, certificate, typeValue);
        }

        if (signingCertificate != null)
        {
            var signerCertificate = CreateComObject(typeSignerCertificate);

            typeValue = new object?[] { 0, 0, 12, signingCertificate.Thumbprint };
            typeSignerCertificate.InvokeMember("Initialize", BindingFlags.InvokeMethod, null, signerCertificate,
                typeValue);
            typeValue = new object?[] { signerCertificate };
            typeRequestCert.InvokeMember("SignerCertificate", BindingFlags.PutDispProperty, null, requestCert,
                typeValue);
        }
        else
        {
            var basicConstraints = CreateComObject(typeBasicConstraints);

            typeValue = new object?[] { "true", "0" };
            typeBasicConstraints.InvokeMember("InitializeEncode", BindingFlags.InvokeMethod, null, basicConstraints,
                typeValue);
            typeValue = new object?[] { basicConstraints };
            typeX509Extensions.InvokeMember("Add", BindingFlags.InvokeMethod, null, certificate, typeValue);
        }

        oid = CreateComObject(typeOid);

        typeValue = new object?[] { 1, 0, 0, hashAlg };
        typeOid.InvokeMember("InitializeFromAlgorithmName", BindingFlags.InvokeMethod, null, oid, typeValue);

        typeValue = new object?[] { oid };
        typeRequestCert.InvokeMember("HashAlgorithm", BindingFlags.PutDispProperty, null, requestCert, typeValue);
        typeRequestCert.InvokeMember("Encode", BindingFlags.InvokeMethod, null, requestCert, null);

        var x509Enrollment = CreateComObject(typeX509Enrollment);

        typeValue[0] = requestCert;
        typeX509Enrollment.InvokeMember("InitializeFromRequest", BindingFlags.InvokeMethod, null, x509Enrollment,
            typeValue);

        if (signingCertificate == null)
        {
            typeValue[0] = fullSubject;
            typeX509Enrollment.InvokeMember("CertificateFriendlyName", BindingFlags.PutDispProperty, null,
                x509Enrollment, typeValue);
        }

        typeValue[0] = 0;

        var createCertRequest = typeX509Enrollment.InvokeMember("CreateRequest", BindingFlags.InvokeMethod, null,
            x509Enrollment, typeValue)
            ?? throw new InvalidOperationException("The enrollment request could not be created.");
        typeValue = new object?[] { 2, createCertRequest, 0, string.Empty };

        typeX509Enrollment.InvokeMember("InstallResponse", BindingFlags.InvokeMethod, null, x509Enrollment,
            typeValue);
        typeValue = new object?[] { null, 0, 1 };

        var empty = typeX509Enrollment.InvokeMember("CreatePFX", BindingFlags.InvokeMethod, null,
                        x509Enrollment, typeValue) as string
                    ?? throw new InvalidOperationException("The enrollment API did not return a PFX.");

        return CertificateLoader.LoadPkcs12(Convert.FromBase64String(empty), string.Empty,
            X509KeyStorageFlags.Exportable);
    }
}

public enum EncodingType
{
    XcnCryptStringAny = 7,
    XcnCryptStringBase64 = 1,
    XcnCryptStringBase64Any = 6,
    XcnCryptStringBase64Header = 0,
    XcnCryptStringBase64Requestheader = 3,
    XcnCryptStringBase64Uri = 13,
    XcnCryptStringBase64X509Crlheader = 9,
    XcnCryptStringBinary = 2,
    XcnCryptStringChain = 0x100,
    XcnCryptStringEncodemask = 0xff,
    XcnCryptStringHashdata = 0x10000000,
    XcnCryptStringHex = 4,
    XcnCryptStringHexAny = 8,
    XcnCryptStringHexaddr = 10,
    XcnCryptStringHexascii = 5,
    XcnCryptStringHexasciiaddr = 11,
    XcnCryptStringHexraw = 12,
    XcnCryptStringNocr = -2147483648,
    XcnCryptStringNocrlf = 0x40000000,
    XcnCryptStringPercentescape = 0x8000000,
    XcnCryptStringStrict = 0x20000000,
    XcnCryptStringText = 0x200
}

public enum AlternativeNameType
{
    XcnCertAltNameDirectoryName = 5,
    XcnCertAltNameDnsName = 3,
    XcnCertAltNameGuid = 10,
    XcnCertAltNameIpAddress = 8,
    XcnCertAltNameOtherName = 1,
    XcnCertAltNameRegisteredId = 9,
    XcnCertAltNameRfc822Name = 2,
    XcnCertAltNameUnknown = 0,
    XcnCertAltNameUrl = 7,
    XcnCertAltNameUserPrincipleName = 11
}ParseOptions.0.jsonÅ
cD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Compression\CompressionFactory.csÑusing System;
using System.IO;
using System.IO.Compression;

namespace Titanium.Web.Proxy.Compression
{
    /// <summary>
    ///     A factory to generate the compression methods based on the type of compression
    /// </summary>
    internal static class CompressionFactory
    {
        internal static Stream Create(HttpCompression type, Stream stream, bool leaveOpen = true)
        {
            return type switch
            {
                HttpCompression.Gzip => new GZipStream(stream, CompressionMode.Compress, leaveOpen),
                HttpCompression.Deflate => new DeflateStream(stream, CompressionMode.Compress, leaveOpen),
                HttpCompression.Brotli => new BrotliSharpLib.BrotliStream(stream, CompressionMode.Compress, leaveOpen),
                _ => throw new Exception($"Unsupported compression mode: {type}")
            };
        }
    }
}ParseOptions.0.json∆
eD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Compression\DecompressionFactory.cs«using System;
using System.IO;
using System.IO.Compression;

namespace Titanium.Web.Proxy.Compression;

/// <summary>
///     A factory to generate the de-compression methods based on the type of compression
/// </summary>
internal class DecompressionFactory
{
    internal static Stream Create(HttpCompression type, Stream stream, bool leaveOpen = true)
    {
        return type switch
        {
            HttpCompression.Gzip => new GZipStream(stream, CompressionMode.Decompress, leaveOpen),
            HttpCompression.Deflate => new DeflateStream(stream, CompressionMode.Decompress, leaveOpen),
            HttpCompression.Brotli => new BrotliSharpLib.BrotliStream(stream, CompressionMode.Decompress, leaveOpen),
            _ => throw new Exception($"Unsupported decompression mode: {type}")
        };
    }
}ParseOptions.0.json
lD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\EventArguments\BeforeBodyWriteEventArgs.csÍnamespace Titanium.Web.Proxy.EventArguments
{

    public class BeforeBodyWriteEventArgs : ProxyEventArgsBase
    {
        internal BeforeBodyWriteEventArgs(SessionEventArgs session, byte[] bodyBytes, bool isChunked, bool isLastChunk) : base(session.Server, session.ClientConnection)
        {
            Session = session;
            BodyBytes = bodyBytes;
            IsChunked = isChunked;
            IsLastChunk = isLastChunk;
        }


        /// <value>
        ///     The session arguments.
        /// </value>
        public SessionEventArgs Session { get; }

        /// <summary>
        ///  Indicates whether the body is written as a chunked stream.
        ///  If this is true, OnRequestBodyWrite/OnResponseBodyWrite will be called
        ///  for each chunk until IsLastChunk becomes true.
        /// </summary>
        public bool IsChunked { get; }

        /// <summary>
        /// Indicates whether this is the last chunk from the client or server stream, when the body is chunked.
        /// This is true when the source stream has reached its end. Set this to true from a handler to stop
        /// writing further chunks to the target stream (the terminating chunk will be written).
        /// </summary>
        public bool IsLastChunk { get; set; }

        /// <summary>
        /// The bytes about to be written. If IsChunked is true, this will be a chunk of the bytes to be written.
        /// Override this property with custom bytes if needed, and adjust IsLastChunk accordingly.
        /// </summary>
        public byte[] BodyBytes { get; set; }
    }
}
ParseOptions.0.jsonÂ
rD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\EventArguments\BeforeSslAuthenticateEventArgs.csŸusing System.Threading;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
///     This is used in transparent endpoint before authenticating client.
/// </summary>
public class BeforeSslAuthenticateEventArgs : ProxyEventArgsBase
{
    internal readonly CancellationTokenSource TaskCancellationSource;

    internal BeforeSslAuthenticateEventArgs(ProxyServer server, TcpClientConnection clientConnection,
        CancellationTokenSource taskCancellationSource, string sniHostName) : base(server, clientConnection)
    {
        TaskCancellationSource = taskCancellationSource;
        SniHostName = sniHostName;
        ForwardHttpsHostName = sniHostName;
    }

    /// <summary>
    ///     The server name indication hostname if available.
    ///     Otherwise the GenericCertificateName property of TransparentEndPoint.
    /// </summary>
    public string SniHostName { get; }

    /// <summary>
    ///     Should we decrypt the SSL request?
    ///     If true we decrypt with fake certificate.
    ///     If false we relay the connection to the hostname mentioned in SniHostname.
    /// </summary>
    public bool DecryptSsl { get; set; } = true;

    /// <summary>
    ///     We need to know the server hostname we are forwarding the request to.
    ///     By default its the SNI hostname indicated in SSL handshake, when SNI is available.
    ///     When SNI is not available, it will use the GenericCertificateName of TransparentEndPoint.
    ///     This property is used only when DecryptSsl or when BeforeSslAuthenticateEventArgs.DecryptSsl is false.
    ///     When DecryptSsl is true, we need to explicitly set the Forwarded host and port by setting
    ///     e.HttpClient.Request.Url inside BeforeRequest event handler.
    /// </summary>
    public string ForwardHttpsHostName { get; set; }

    /// <summary>
    ///     We need to know the server port we are forwarding the request to.
    ///     By default its the standard https port, 443.
    ///     This property is used only when DecryptSsl or when BeforeSslAuthenticateEventArgs.DecryptSsl is false.
    ///     When DecryptSsl is true, we need to explicitly set the Forwarded host and port by setting
    ///     e.HttpClient.Request.Url inside BeforeRequest event handler.
    /// </summary>
    public int ForwardHttpsPort { get; set; } = 443;

    /// <summary>
    ///     Terminate the request abruptly by closing client/server connections.
    /// </summary>
    public void TerminateSession()
    {
        TaskCancellationSource.Cancel();
    }
}ParseOptions.0.jsonŸ
qD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\EventArguments\CertificateSelectionEventArgs.csŒusing System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
///     An argument passed on to user for client certificate selection during mutual SSL authentication.
/// </summary>
public class CertificateSelectionEventArgs : ProxyEventArgsBase
{
    public CertificateSelectionEventArgs(SessionEventArgsBase session, string targetHost,
        X509CertificateCollection localCertificates, X509Certificate? remoteCertificate,
        string[] acceptableIssuers) :
        base(session.Server, session.ClientConnection)
    {
        Session = session;
        TargetHost = targetHost;
        LocalCertificates = localCertificates;
        RemoteCertificate = remoteCertificate;
        AcceptableIssuers = acceptableIssuers;
    }

    /// <value>
    ///     The session.
    /// </value>
    public SessionEventArgsBase Session { get; }

    /// <summary>
    ///     The remote hostname to which we are authenticating against.
    /// </summary>
    public string TargetHost { get; }

    /// <summary>
    ///     Local certificates in store with matching issuers requested by TargetHost website.
    /// </summary>
    public X509CertificateCollection LocalCertificates { get; }

    /// <summary>
    ///     Certificate of the remote server.
    /// </summary>
    public X509Certificate? RemoteCertificate { get; }

    /// <summary>
    ///     Acceptable issuers as listed by remote server.
    /// </summary>
    public string[] AcceptableIssuers { get; }

    /// <summary>
    ///     Client Certificate we selected. Set this value to override.
    /// </summary>
    public X509Certificate? ClientCertificate { get; set; }
}ParseOptions.0.json∑
rD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\EventArguments\CertificateValidationEventArgs.cs´
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
///     An argument passed on to the user for validating the server certificate
///     during SSL authentication.
/// </summary>
public class CertificateValidationEventArgs : ProxyEventArgsBase
{
    public CertificateValidationEventArgs(SessionEventArgsBase session, X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors) : base(session.Server, session.ClientConnection)
    {
        Session = session;
        Certificate = certificate;
        Chain = chain;
        SslPolicyErrors = sslPolicyErrors;
    }

    /// <value>
    ///     The session.
    /// </value>
    public SessionEventArgsBase Session { get; }

    /// <summary>
    ///     Server certificate.
    /// </summary>
    public X509Certificate? Certificate { get; }

    /// <summary>
    ///     Certificate chain.
    /// </summary>
    public X509Chain? Chain { get; }

    /// <summary>
    ///     SSL policy errors.
    /// </summary>
    public SslPolicyErrors SslPolicyErrors { get; }

    /// <summary>
    ///     Is the given server certificate valid?
    /// </summary>
    public bool IsValid { get; set; }
}ParseOptions.0.json÷
aD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\EventArguments\DataEventArgs.cs€using System;

namespace Titanium.Web.Proxy.StreamExtended.Network;

/// <summary>
///     Wraps the data sent/received event argument.
/// </summary>
public class DataEventArgs : EventArgs
{
    public DataEventArgs(byte[] buffer, int offset, int count)
    {
        Buffer = buffer;
        Offset = offset;
        Count = count;
    }

    /// <summary>
    ///     The buffer with data.
    /// </summary>
    public byte[] Buffer { get; }

    /// <summary>
    ///     Offset in buffer from which valid data begins.
    /// </summary>
    public int Offset { get; }

    /// <summary>
    ///     Length from offset in buffer with valid data.
    /// </summary>
    public int Count { get; }
}ParseOptions.0.json©
gD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\EventArguments\EmptyProxyEventArgs.cs®using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.EventArguments;

public class EmptyProxyEventArgs : ProxyEventArgsBase
{
    internal EmptyProxyEventArgs(ProxyServer server, TcpClientConnection clientConnection) : base(server,
        clientConnection)
    {
    }
}ParseOptions.0.jsonÛ
uD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\EventArguments\MultipartRequestPartSentEventArgs.cs‰using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
///     Class that wraps the multipart sent request arguments.
/// </summary>
public class MultipartRequestPartSentEventArgs : ProxyEventArgsBase
{
    internal MultipartRequestPartSentEventArgs(SessionEventArgs session, string boundary, HeaderCollection headers) :
        base(session.Server, session.ClientConnection)
    {
        Session = session;
        Boundary = boundary;
        Headers = headers;
    }

    /// <value>
    ///     The session arguments.
    /// </value>
    public SessionEventArgs Session { get; }

    /// <summary>
    ///     Boundary.
    /// </summary>
    public string Boundary { get; }

    /// <summary>
    ///     The header collection.
    /// </summary>
    public HeaderCollection Headers { get; }
}ParseOptions.0.json»
fD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\EventArguments\ProxyEventArgsBase.cs»using System;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
///     The base event arguments
/// </summary>
/// <seealso cref="System.EventArgs" />
public abstract class ProxyEventArgsBase : EventArgs
{
    private readonly TcpClientConnection clientConnection;
    internal readonly ProxyServer Server;

    internal ProxyEventArgsBase(ProxyServer server, TcpClientConnection clientConnection)
    {
        this.clientConnection = clientConnection;
        Server = server;
    }

    public object? ClientUserData
    {
        get => clientConnection.ClientUserData;
        set => clientConnection.ClientUserData = value;
    }
}ParseOptions.0.json›Ê
dD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\EventArguments\SessionEventArgs.csﬁÂusing System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
/// Holds info related to a single proxy session (single request/response sequence).
/// A proxy session is bounded to a single connection from client.
/// A proxy session ends when client terminates connection to proxy
/// or when server terminates connection from proxy.
/// </summary>
public class SessionEventArgs : SessionEventArgsBase
{
    private bool disposed;

    /// <summary>
    /// Backing field for corresponding public property
    /// </summary>
    private bool reRequest;

    private WebSocketDecoder? webSocketDecoderReceive;

    private WebSocketDecoder? webSocketDecoderSend;

    /// <summary>
    /// Constructor to initialize the proxy
    /// </summary>
    internal SessionEventArgs(ProxyServer server, ProxyEndPoint endPoint, HttpClientStream clientStream, ConnectRequest? connectRequest, CancellationTokenSource cancellationTokenSource)
        : base(server, endPoint, clientStream, connectRequest, new Request(), cancellationTokenSource)
    {
    }

    /// <summary>
    ///     Is this session a HTTP/2 promise?
    /// </summary>
    public bool IsPromise { get; internal set; }

    private bool HasMulipartEventSubscribers => MultipartRequestPartSent != null;

    /// <summary>
    /// Should we send the request again ?
    /// </summary>
    public bool ReRequest
    {
        get => reRequest;
        set
        {
            if (HttpClient.Response.StatusCode == 0) throw new Exception("Response status code is empty. Cannot request again a request " + "which was never send to server.");

            reRequest = value;
        }
    }

    [Obsolete("Use [WebSocketDecoderReceive] instead")]
    public WebSocketDecoder WebSocketDecoder => WebSocketDecoderReceive;

    public WebSocketDecoder WebSocketDecoderSend => webSocketDecoderSend ??= new WebSocketDecoder(BufferPool);

    public WebSocketDecoder WebSocketDecoderReceive => webSocketDecoderReceive ??= new WebSocketDecoder(BufferPool);

    /// <summary>
    /// Occurs when multipart request part sent.
    /// </summary>
    public event EventHandler<MultipartRequestPartSentEventArgs>? MultipartRequestPartSent;

    /// <summary>
    /// Read request body content as bytes[] for current session
    /// </summary>
    private async Task ReadRequestBodyAsync(CancellationToken cancellationToken)
    {
        HttpClient.Request.EnsureBodyAvailable(false);

        var request = HttpClient.Request;

        // If not already read (not cached yet)
        if (!request.IsBodyRead)
        {
            if (request.IsBodyReceived) throw new Exception("Request body was already received.");

            if (request.HttpVersion == HttpHeader.Version20)
            {
                // do not send to the remote endpoint
                request.Http2IgnoreBodyFrames = true;

                request.Http2BodyData = new MemoryStream();

                var tcs = new TaskCompletionSource<bool>();
                request.ReadHttp2BodyTaskCompletionSource = tcs;

                // signal to HTTP/2 copy frame method to continue
                request.ReadHttp2BeforeHandlerTaskCompletionSource!.SetResult(true);

                await tcs.Task;

                // Now set the flag to true
                // So that next time we can deliver body from cache
                request.IsBodyRead = true;
                request.IsBodyReceived = true;
            }
            else
            {
                var body = await ReadBodyAsync(true, cancellationToken);
                if (!request.BodyAvailable) request.Body = body;

                // Now set the flag to true
                // So that next time we can deliver body from cache
                request.IsBodyRead = true;
                request.IsBodyReceived = true;
            }
        }
    }

    /// <summary>
    /// reinit response object
    /// </summary>
    internal async Task ClearResponse(CancellationToken cancellationToken)
    {
        // syphon out the response body from server
        await SyphonOutBodyAsync(false, cancellationToken);
        HttpClient.Response = new Response();
    }

    internal void OnMultipartRequestPartSent(ReadOnlySpan<char> boundary, HeaderCollection headers)
    {
        try
        {
            MultipartRequestPartSent?.Invoke(this, new MultipartRequestPartSentEventArgs(this, boundary.ToString(), headers));
        }
        catch (Exception ex)
        {
            OnException(new Exception("Exception thrown in user event", ex));
        }
    }

    /// <summary>
    /// Read response body as byte[] for current response
    /// </summary>
    private async Task ReadResponseBodyAsync(CancellationToken cancellationToken)
    {
        if (!HttpClient.Request.Locked) throw new Exception("You cannot read the response body before request is made to server.");

        var response = HttpClient.Response;
        if (!response.HasBody) return;

        // If not already read (not cached yet)
        if (!response.IsBodyRead)
        {
            if (response.IsBodyReceived) throw new Exception("Response body was already received.");

            if (response.HttpVersion == HttpHeader.Version20)
            {
                // do not send to the remote endpoint
                response.Http2IgnoreBodyFrames = true;

                response.Http2BodyData = new MemoryStream();

                var tcs = new TaskCompletionSource<bool>();
                response.ReadHttp2BodyTaskCompletionSource = tcs;

                // signal to HTTP/2 copy frame method to continue
                response.ReadHttp2BeforeHandlerTaskCompletionSource!.SetResult(true);

                await tcs.Task;

                // Now set the flag to true
                // So that next time we can deliver body from cache
                response.IsBodyRead = true;
                response.IsBodyReceived = true;
            }
            else
            {
                var body = await ReadBodyAsync(false, cancellationToken);
                if (!response.BodyAvailable) response.Body = body;

                // Now set the flag to true
                // So that next time we can deliver body from cache
                response.IsBodyRead = true;
                response.IsBodyReceived = true;
            }
        }
    }

    private async Task<byte[]> ReadBodyAsync(bool isRequest, CancellationToken cancellationToken)
    {
        using var bodyStream = new MemoryStream();
        using var writer = new HttpStream(Server, bodyStream, BufferPool, cancellationToken);

        if (isRequest)
            await CopyRequestBodyAsync(writer, TransformationMode.Uncompress, cancellationToken);
        else
            await CopyResponseBodyAsync(writer, TransformationMode.Uncompress, cancellationToken);

        return bodyStream.ToArray();
    }

    /// <summary>
    ///     Syphon out any left over data in given request/response from backing tcp connection.
    ///     When user modifies the response/request we need to do this to reuse tcp connections.
    /// </summary>
    /// <param name="isRequest"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    internal async Task SyphonOutBodyAsync(bool isRequest, CancellationToken cancellationToken)
    {
        var requestResponse = isRequest ? (RequestResponseBase)HttpClient.Request : HttpClient.Response;
        if (requestResponse.IsBodyReceived || !requestResponse.OriginalHasBody) return;

        var reader = isRequest ? (HttpStream)ClientStream : HttpClient.Connection.Stream;

        await reader.CopyBodyAsync(requestResponse, true, NullWriter.Instance, TransformationMode.None, isRequest, this, cancellationToken);
        requestResponse.IsBodyReceived = true;
    }

    /// <summary>
    ///  This is called when the request is PUT/POST/PATCH to read the body
    /// </summary>
    /// <returns></returns>
    internal async Task CopyRequestBodyAsync(IHttpStreamWriter writer, TransformationMode transformation, CancellationToken cancellationToken)
    {
        var request = HttpClient.Request;
        var reader = ClientStream;

        var contentLength = request.ContentLength;

        // send the request body bytes to server
        if (contentLength > 0 && HasMulipartEventSubscribers && request.IsMultipartFormData)
        {
            var boundary = HttpHelper.GetBoundaryFromContentType(request.ContentType);

            using (var copyStream = new CopyStream(reader, writer, BufferPool))
            {
                while (contentLength > copyStream.ReadBytes)
                {
                    var read = await ReadUntilBoundaryAsync(copyStream, contentLength, boundary, cancellationToken);
                    if (read == 0) break;

                    if (contentLength > copyStream.ReadBytes)
                    {
                        var headers = new HeaderCollection();
                        await HeaderParser.ReadHeaders(copyStream, headers, cancellationToken);
                        OnMultipartRequestPartSent(boundary.Span, headers);
                    }
                }

                await copyStream.FlushAsync(cancellationToken);
            }
        }
        else
        {
            await reader.CopyBodyAsync(request, false, writer, transformation, true, this, cancellationToken);
        }

        request.IsBodyReceived = true;
    }

    private async Task CopyResponseBodyAsync(IHttpStreamWriter writer, TransformationMode transformation, CancellationToken cancellationToken)
    {
        var response = HttpClient.Response;
        await HttpClient.Connection.Stream.CopyBodyAsync(response, false, writer, transformation, false, this, cancellationToken);
        response.IsBodyReceived = true;
    }

    /// <summary>
    /// Read a line from the byte stream
    /// </summary>
    /// <returns></returns>
    private async Task<long> ReadUntilBoundaryAsync(ILineStream reader, long totalBytesToRead, ReadOnlyMemory<char> boundary, CancellationToken cancellationToken)
    {
        var bufferDataLength = 0;

        var buffer = BufferPool.GetBuffer();
        try
        {
            var boundaryLength = boundary.Length + 4;
            long bytesRead = 0;

            while (bytesRead < totalBytesToRead && (reader.DataAvailable || await reader.FillBufferAsync(cancellationToken)))
            {
                var newChar = reader.ReadByteFromBuffer();
                buffer[bufferDataLength] = newChar;

                bufferDataLength++;
                bytesRead++;

                if (bufferDataLength >= boundaryLength)
                {
                    var startIdx = bufferDataLength - boundaryLength;
                    if (buffer[startIdx] == '-' && buffer[startIdx + 1] == '-')
                    {
                        startIdx += 2;
                        var ok = true;
                        for (var i = 0; i < boundary.Length; i++)
                            if (buffer[startIdx + i] != boundary.Span[i])
                            {
                                ok = false;
                                break;
                            }

                        if (ok) break;
                    }
                }

                if (bufferDataLength == buffer.Length)
                {
                    // boundary is not longer than 70 bytes according to the specification, so keeping the last 100 (minimum 74) bytes is enough
                    const int bytesToKeep = 100;
                    Buffer.BlockCopy(buffer, buffer.Length - bytesToKeep, buffer, 0, bytesToKeep);
                    bufferDataLength = bytesToKeep;
                }
            }

            return bytesRead;
        }
        finally
        {
            BufferPool.ReturnBuffer(buffer);
        }
    }

    /// <summary>
    /// Gets the request body as bytes.
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
    /// <returns>The body as bytes.</returns>
    public async Task<byte[]> GetRequestBody(CancellationToken cancellationToken = default)
    {
        if (!HttpClient.Request.IsBodyRead) await ReadRequestBodyAsync(cancellationToken);

        return HttpClient.Request.Body;
    }

    /// <summary>
    /// Gets the request body as string.
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
    /// <returns>The body as string.</returns>
    public async Task<string> GetRequestBodyAsString(CancellationToken cancellationToken = default)
    {
        if (!HttpClient.Request.IsBodyRead) await ReadRequestBodyAsync(cancellationToken);

        return HttpClient.Request.BodyString;
    }

    /// <summary>
    /// Sets the request body.
    /// </summary>
    /// <param name="body">The request body bytes.</param>
    public void SetRequestBody(byte[] body)
    {
        var request = HttpClient.Request;
        if (request.Locked) throw new Exception("You cannot call this function after request is made to server.");

        request.Body = body;
    }

    /// <summary>
    /// Sets the body with the specified string.
    /// </summary>
    /// <param name="body">The request body string to set.</param>
    public void SetRequestBodyString(string body)
    {
        if (HttpClient.Request.Locked) throw new Exception("You cannot call this function after request is made to server.");

        SetRequestBody(HttpClient.Request.Encoding.GetBytes(body));
    }


    /// <summary>
    /// Gets the response body as bytes.
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
    /// <returns>The resulting bytes.</returns>
    public async Task<byte[]> GetResponseBody(CancellationToken cancellationToken = default)
    {
        if (!HttpClient.Response.IsBodyRead) await ReadResponseBodyAsync(cancellationToken);

        return HttpClient.Response.Body;
    }

    /// <summary>
    /// Gets the response body as string.
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
    /// <returns>The string body.</returns>
    public async Task<string> GetResponseBodyAsString(CancellationToken cancellationToken = default)
    {
        if (!HttpClient.Response.IsBodyRead) await ReadResponseBodyAsync(cancellationToken);

        return HttpClient.Response.BodyString;
    }

    /// <summary>
    /// Set the response body bytes.
    /// </summary>
    /// <param name="body">The body bytes to set.</param>
    public void SetResponseBody(byte[] body)
    {
        if (!HttpClient.Request.Locked) throw new Exception("You cannot call this function before request is made to server.");

        var response = HttpClient.Response;
        response.Body = body;
    }

    /// <summary>
    /// Replace the response body with the specified string.
    /// </summary>
    /// <param name="body">The body string to set.</param>
    public void SetResponseBodyString(string body)
    {
        if (!HttpClient.Request.Locked) throw new Exception("You cannot call this function before request is made to server.");

        var bodyBytes = HttpClient.Response.Encoding.GetBytes(body);

        SetResponseBody(bodyBytes);
    }

    /// <summary>
    /// Before request is made to server respond with the specified HTML string to client
    /// and ignore the request. 
    /// </summary>
    /// <param name="html">HTML content to sent.</param>
    /// <param name="headers">HTTP response headers.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void Ok(string html, IDictionary<string, HttpHeader>? headers,
        bool closeServerConnection = false)
    {
        Ok(html, headers?.Values, closeServerConnection);
    }

    /// <summary>
    /// Before request is made to server respond with the specified HTML string to client
    /// and ignore the request. 
    /// </summary>
    /// <param name="html">HTML content to sent.</param>
    /// <param name="headers">HTTP response headers.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void Ok(string html, IEnumerable<HttpHeader>? headers = null,
        bool closeServerConnection = false)
    {
        var response = new OkResponse();
        if (headers != null) response.Headers.AddHeaders(headers);

        response.HttpVersion = HttpClient.Request.HttpVersion;
        response.Body = response.Encoding.GetBytes(html ?? string.Empty);

        Respond(response, closeServerConnection);
    }

    /// <summary>
    /// Before request is made to server respond with the specified byte[] to client
    /// and ignore the request. 
    /// </summary>
    /// <param name="result">The html content bytes.</param>
    /// <param name="headers">The HTTP headers.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void Ok(byte[] result, IDictionary<string, HttpHeader>? headers,
        bool closeServerConnection = false)
    {
        Ok(result, headers?.Values, closeServerConnection);
    }

    /// <summary>
    /// Before request is made to server respond with the specified byte[] to client
    /// and ignore the request. 
    /// </summary>
    /// <param name="result">The html content bytes.</param>
    /// <param name="headers">The HTTP headers.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void Ok(byte[] result, IEnumerable<HttpHeader>? headers = null,
        bool closeServerConnection = false)
    {
        var response = new OkResponse();
        response.Headers.AddHeaders(headers);
        response.HttpVersion = HttpClient.Request.HttpVersion;
        response.Body = result;

        Respond(response, closeServerConnection);
    }

    /// <summary>
    /// Before¬†request¬†is¬†made¬†to¬†server¬†
    ///¬†respond¬†with¬†the¬†specified¬†HTML¬†string and the¬†specified¬†status to¬†client.
    ///¬†And¬†then ignore¬†the¬†request.¬†
    /// </summary>
    /// <param name="html">The html content.</param>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="headers">The HTTP headers.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void GenericResponse(string html, HttpStatusCode status,
        IDictionary<string, HttpHeader>? headers, bool closeServerConnection = false)
    {
        GenericResponse(html, status, headers?.Values, closeServerConnection);
    }

    /// <summary>
    /// Before¬†request¬†is¬†made¬†to¬†server¬†
    ///¬†respond¬†with¬†the¬†specified¬†HTML¬†string and the¬†specified¬†status to¬†client.
    ///¬†And¬†then ignore¬†the¬†request.¬†
    /// </summary>
    /// <param name="html">The html content.</param>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="headers">The HTTP headers.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void GenericResponse(string html, HttpStatusCode status,
        IEnumerable<HttpHeader>? headers = null, bool closeServerConnection = false)
    {
        var response = new GenericResponse(status);
        response.HttpVersion = HttpClient.Request.HttpVersion;
        response.Headers.AddHeaders(headers);
        response.Body = response.Encoding.GetBytes(html ?? string.Empty);

        Respond(response, closeServerConnection);
    }

    /// <summary>
    /// Before request is made to server respond with the specified byte[],
    /// the specified status  to client. And then ignore the request.
    /// </summary>
    /// <param name="result">The bytes to sent.</param>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="headers">The HTTP headers.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void GenericResponse(byte[] result, HttpStatusCode status,
        IDictionary<string, HttpHeader> headers, bool closeServerConnection = false)
    {
        GenericResponse(result, status, headers?.Values, closeServerConnection);
    }

    /// <summary>
    /// Before request is made to server respond with the specified byte[],
    /// the specified status  to client. And then ignore the request.
    /// </summary>
    /// <param name="result">The bytes to sent.</param>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="headers">The HTTP headers.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void GenericResponse(byte[] result, HttpStatusCode status,
        IEnumerable<HttpHeader>? headers, bool closeServerConnection = false)
    {
        var response = new GenericResponse(status);
        response.HttpVersion = HttpClient.Request.HttpVersion;
        response.Headers.AddHeaders(headers);
        response.Body = result;

        Respond(response, closeServerConnection);
    }

    /// <summary>
    /// Redirect to provided URL.
    /// </summary>
    /// <param name="url">The URL to redirect.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void Redirect(string url, bool closeServerConnection = false)
    {
        var response = new RedirectResponse();
        response.HttpVersion = HttpClient.Request.HttpVersion;
        response.Headers.AddHeader(KnownHeaders.Location, url);
        response.Body = Array.Empty<byte>();

        Respond(response, closeServerConnection);
    }

    /// <summary>
    /// Respond with given response object to client.
    /// </summary>
    /// <remarks>
    /// If the server response was already received, the original server body (if any) is drained (syphoned) so the
    /// server connection stays reusable. To avoid reading a large or endless server body, pass
    /// <paramref name="closeServerConnection" /> = true (or call <see cref="TerminateServerConnection" />), which
    /// closes the connection instead of draining. Note that an HTTP/1.1 connection cannot be both reused and have
    /// its body skipped.
    /// </remarks>
    /// <param name="response">The response object.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void Respond(Response response, bool closeServerConnection = false)
    {
        // request already send/ready to be sent.
        if (HttpClient.Request.Locked)
        {
            // response already received from server and ready to be sent to client.
            if (HttpClient.Response.Locked) throw new Exception("You cannot call this function after response is sent to the client.");

            // cleanup original response.
            if (closeServerConnection)
            // no need to cleanup original connection.
            // it will be closed any way.
                TerminateServerConnection();

            response.SetOriginalHeaders(HttpClient.Response);

            // response already received from server but not yet ready to sent to client.         
            HttpClient.Response = response;
            HttpClient.Response.Locked = true;
        }
        // request not yet sent/not yet ready to be sent.
        else
        {
            HttpClient.Request.Locked = true;
            HttpClient.Request.CancelRequest = true;

            // set new response.
            HttpClient.Response = response;
            HttpClient.Response.Locked = true;
        }
    }

    /// <summary>
    ///     Respond to the client with a streamed body produced on the fly, without buffering the whole body in
    ///     memory. Use this to serve large or endless bodies (e.g. a multi-gigabyte file or a synthetic
    ///     server-sent-events stream) from scratch.
    /// </summary>
    /// <remarks>
    ///     Framing is chosen from the response headers: if a Content-Length is set on <paramref name="response" />
    ///     the body is written raw (the delegate must write exactly that many bytes); otherwise the response is sent
    ///     using chunked transfer-encoding and each write becomes a chunk. The delegate receives a write-only stream;
    ///     only a single buffer is in flight at a time, so memory stays bounded regardless of the total size.
    ///     See <see cref="Respond" /> for the server body syphon-vs-close trade-off controlled by
    ///     <paramref name="closeServerConnection" />.
    /// </remarks>
    /// <param name="response">The response object (status and headers).</param>
    /// <param name="writeBody">Delegate that writes the body to the provided stream.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void RespondStreaming(Response response, Func<Stream, CancellationToken, Task> writeBody,
        bool closeServerConnection = false)
    {
        if (response == null) throw new ArgumentNullException(nameof(response));
        if (writeBody == null) throw new ArgumentNullException(nameof(writeBody));

        // Choose framing: fixed-length when the caller declared a Content-Length, otherwise chunked.
        if (response.ContentLength < 0 && !response.IsChunked) response.IsChunked = true;

        response.StreamBodyWriter = writeBody;

        Respond(response, closeServerConnection);
    }

    /// <summary>
    ///     Terminate the connection to server at the end of this HTTP request/response session.
    /// </summary>
    public void TerminateServerConnection()
    {
        HttpClient.CloseServerConnection = true;
    }

    /// <summary>
    ///     Drains (reads and discards) any unread server response body from the backing TCP connection so the
    ///     connection can be reused. This reads the bytes off the wire without buffering them in memory. It is a
    ///     no-op if the body was already received or the response has no body.
    /// </summary>
    /// <remarks>
    ///     Warning: for an endless chunked response (one that never sends its terminating zero chunk) this will
    ///     block until the passed <paramref name="cancellationToken" /> is cancelled or the connection closes. In
    ///     that case prefer closing the connection (e.g. <see cref="TerminateServerConnection" />) instead.
    /// </remarks>
    public Task DrainServerBodyAsync(CancellationToken cancellationToken = default)
    {
        return SyphonOutBodyAsync(false, cancellationToken);
    }

    /// <summary>
    ///     Drains (reads and discards) any unread client request body from the backing TCP connection so the
    ///     client's keep-alive connection can be reused. This reads the bytes off the wire without buffering them
    ///     in memory. It is a no-op if the body was already received or the request has no body.
    /// </summary>
    /// <remarks>
    ///     Useful when short-circuiting a request (e.g. <see cref="Respond" />, <see cref="RespondStreaming" />, or
    ///     blocking) while the client is uploading a body: draining leaves the client connection in a reusable
    ///     state. Note the proxy already drains the client body automatically on the normal synthetic-response
    ///     path, so this is only needed for advanced/manual control.
    ///     Warning: for an endless chunked request (one that never sends its terminating zero chunk) this will
    ///     block until the passed <paramref name="cancellationToken" /> is cancelled or the connection closes.
    /// </remarks>
    public Task DrainClientBodyAsync(CancellationToken cancellationToken = default)
    {
        return SyphonOutBodyAsync(true, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposed) return;

        MultipartRequestPartSent = null;
        disposed = true;

        base.Dispose(disposing);
    }

    ~SessionEventArgs()
    {
#if DEBUG
            // Finalizer should not be called
            System.Diagnostics.Debugger.Break();
#endif

        Dispose(false);
    }
}ParseOptions.0.json∑:
hD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\EventArguments\SessionEventArgsBase.csµ9using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.StreamExtended.BufferPool;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
///     Holds info related to a single proxy session (single request/response sequence).
///     A proxy session is bounded to a single connection from client.
///     A proxy session ends when client terminates connection to proxy
///     or when server terminates connection from proxy.
/// </summary>
public abstract class SessionEventArgsBase : ProxyEventArgsBase, IDisposable
{
    protected readonly IBufferPool BufferPool;

    internal readonly CancellationTokenSource CancellationTokenSource;
    protected readonly ExceptionHandler? ExceptionFunc;

    private bool disposed;
    private bool enableWinAuth;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SessionEventArgsBase" /> class.
    /// </summary>
    private protected SessionEventArgsBase(ProxyServer server, ProxyEndPoint endPoint,
        HttpClientStream clientStream, ConnectRequest? connectRequest, Request request,
        CancellationTokenSource cancellationTokenSource) : base(server, clientStream.Connection)
    {
        BufferPool = server.BufferPool;
        ExceptionFunc = server.ExceptionFunc;
        TimeLine["Session Created"] = DateTime.UtcNow;

        CancellationTokenSource = cancellationTokenSource;

        ClientStream = clientStream;
        HttpClient = new HttpWebClient(connectRequest, request,
            new Lazy<int>(() => clientStream.Connection.GetProcessId(endPoint)));
        ProxyEndPoint = endPoint;
        EnableWinAuth = server.EnableWinAuth && IsWindowsAuthenticationSupported;
    }

    private static bool IsWindowsAuthenticationSupported => RunTime.IsWindows;

    internal TcpServerConnection ServerConnection => HttpClient.Connection;

    /// <summary>
    ///     Holds a reference to client
    /// </summary>
    internal TcpClientConnection ClientConnection => ClientStream.Connection;

    internal HttpClientStream ClientStream { get; }

    public Guid ClientConnectionId => ClientConnection.Id;

    public Guid ServerConnectionId => HttpClient.HasConnection ? ServerConnection.Id : Guid.Empty;

    /// <summary>
    ///     Relative milliseconds for various events.
    /// </summary>
    public Dictionary<string, DateTime> TimeLine { get; } = new();

    /// <summary>
    ///     Returns a user data for this request/response session which is
    ///     same as the user data of HttpClient.
    /// </summary>
    public object? UserData
    {
        get => HttpClient.UserData;
        set => HttpClient.UserData = value;
    }

    /// <summary>
    ///     Enable/disable Windows Authentication (NTLM/Kerberos) for the current session.
    /// </summary>
    public bool EnableWinAuth
    {
        get => enableWinAuth;
        set
        {
            if (value && !IsWindowsAuthenticationSupported)
                throw new Exception("Windows Authentication is not supported");

            enableWinAuth = value;
        }
    }

    /// <summary>
    ///     Does this session uses SSL?
    /// </summary>
    public bool IsHttps => HttpClient.Request.IsHttps;

    /// <summary>
    ///     Client Local End Point.
    /// </summary>
    public IPEndPoint ClientLocalEndPoint => (IPEndPoint)ClientConnection.LocalEndPoint;

    /// <summary>
    ///     Client Remote End Point.
    /// </summary>
    public IPEndPoint ClientRemoteEndPoint => (IPEndPoint)ClientConnection.RemoteEndPoint;

    [Obsolete("Use ClientRemoteEndPoint instead.")]
    public IPEndPoint ClientEndPoint => ClientRemoteEndPoint;

    /// <summary>
    ///     The web client used to communicate with server for this session.
    /// </summary>
    public HttpWebClient HttpClient { get; }

    [Obsolete("Use HttpClient instead.")] public HttpWebClient WebSession => HttpClient;

    /// <summary>
    ///     Gets or sets the custom up stream proxy.
    /// </summary>
    /// <value>
    ///     The custom up stream proxy.
    /// </value>
    public IExternalProxy? CustomUpStreamProxy { get; set; }

    /// <summary>
    ///     Are we using a custom upstream HTTP(S) proxy?
    /// </summary>
    public IExternalProxy? CustomUpStreamProxyUsed { get; internal set; }

    /// <summary>
    ///     Local endpoint via which we make the request.
    /// </summary>
    public ProxyEndPoint ProxyEndPoint { get; }

    [Obsolete("Use ProxyEndPoint instead.")]
    public ProxyEndPoint LocalEndPoint => ProxyEndPoint;

    /// <summary>
    ///     Is this a transparent endpoint?
    /// </summary>
    public bool IsTransparent => ProxyEndPoint is TransparentProxyEndPoint;

    /// <summary>
    ///     Is this a SOCKS endpoint?
    /// </summary>
    public bool IsSocks => ProxyEndPoint is SocksProxyEndPoint;

    /// <summary>
    ///     The last exception that happened.
    /// </summary>
    public Exception? Exception { get; internal set; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected void OnException(Exception exception)
    {
        ExceptionFunc?.Invoke(exception);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposed) return;

        if (disposing)
        {
            CustomUpStreamProxyUsed = null;

            HttpClient.FinishSession();
        }

        DataSent = null;
        DataReceived = null;
        Exception = null;

        disposed = true;
    }

    ~SessionEventArgsBase()
    {
#if DEBUG
            // Finalizer should not be called
            System.Diagnostics.Debugger.Break();
#endif

        Dispose(false);
    }

    /// <summary>
    ///     Fired when data is sent within this session to server/client.
    /// </summary>
    public event EventHandler<DataEventArgs>? DataSent;

    /// <summary>
    ///     Fired when data is received within this session from client/server.
    /// </summary>
    public event EventHandler<DataEventArgs>? DataReceived;

    internal void OnDataSent(byte[] buffer, int offset, int count)
    {
        try
        {
            DataSent?.Invoke(this, new DataEventArgs(buffer, offset, count));
        }
        catch (Exception ex)
        {
            OnException(new Exception("Exception thrown in user event", ex));
        }
    }

    internal void OnDataReceived(byte[] buffer, int offset, int count)
    {
        try
        {
            DataReceived?.Invoke(this, new DataEventArgs(buffer, offset, count));
        }
        catch (Exception ex)
        {
            OnException(new Exception("Exception thrown in user event", ex));
        }
    }

    /// <summary>
    ///     Terminates the session abruptly by terminating client/server connections.
    /// </summary>
    public void TerminateSession()
    {
        CancellationTokenSource.Cancel();
    }
}ParseOptions.0.json’
fD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\EventArguments\TransformationMode.cs’namespace Titanium.Web.Proxy.EventArguments;

internal enum TransformationMode
{
    None,

    /// <summary>
    ///     Removes the chunked encoding
    /// </summary>
    RemoveChunked,

    /// <summary>
    ///     Uncompress the body (this also removes the chunked encoding if exists)
    /// </summary>
    Uncompress
}ParseOptions.0.jsonï
jD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\EventArguments\TunnelConnectEventArgs.csëusing System;
using System.Threading;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
///     A class that wraps the state when a tunnel connect event happen for Explicit endpoints.
/// </summary>
public class TunnelConnectSessionEventArgs : SessionEventArgsBase
{
    private bool? isHttpsConnect;

    internal TunnelConnectSessionEventArgs(ProxyServer server, ProxyEndPoint endPoint, ConnectRequest connectRequest,
        HttpClientStream clientStream, CancellationTokenSource cancellationTokenSource)
        : base(server, endPoint, clientStream, connectRequest, connectRequest, cancellationTokenSource)
    {
    }

    /// <summary>
    ///     Should we decrypt the Ssl or relay it to server?
    ///     Default is true.
    /// </summary>
    public bool DecryptSsl { get; set; } = true;

    /// <summary>
    ///     When set to true it denies the connect request with a Forbidden status.
    /// </summary>
    public bool DenyConnect { get; set; }

    /// <summary>
    ///     Is this a connect request to secure HTTP server? Or is it to some other protocol.
    /// </summary>
    public bool IsHttpsConnect
    {
        get => isHttpsConnect ??
               throw new Exception("The value of this property is known in the BeforeTunnelConnectResponse event");

        internal set => isHttpsConnect = value;
    }

    /// <summary>
    ///     Fired when decrypted data is sent within this session to server/client.
    /// </summary>
    public event EventHandler<DataEventArgs>? DecryptedDataSent;

    /// <summary>
    ///     Fired when decrypted data is received within this session from client/server.
    /// </summary>
    public event EventHandler<DataEventArgs>? DecryptedDataReceived;

    internal void OnDecryptedDataSent(byte[] buffer, int offset, int count)
    {
        try
        {
            DecryptedDataSent?.Invoke(this, new DataEventArgs(buffer, offset, count));
        }
        catch (Exception ex)
        {
            OnException(new Exception("Exception thrown in user event", ex));
        }
    }

    internal void OnDecryptedDataReceived(byte[] buffer, int offset, int count)
    {
        try
        {
            DecryptedDataReceived?.Invoke(this, new DataEventArgs(buffer, offset, count));
        }
        catch (Exception ex)
        {
            OnException(new Exception("Exception thrown in user event", ex));
        }
    }

    ~TunnelConnectSessionEventArgs()
    {
#if DEBUG
            // Finalizer should not be called
            System.Diagnostics.Debugger.Break();
#endif

        Dispose(false);
    }
}ParseOptions.0.json¸
eD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Exceptions\BodyNotFoundException.cs˝namespace Titanium.Web.Proxy.Exceptions;

/// <summary>
///     An exception thrown when body is unexpectedly empty.
/// </summary>
public class BodyNotFoundException : ProxyException
{
    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="message"></param>
    internal BodyNotFoundException(string message) : base(message)
    {
    }
}ParseOptions.0.json“
kD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Exceptions\ProxyAuthorizationException.csÕ
using System;
using System.Collections.Generic;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Exceptions;

/// <summary>
///     Proxy authorization exception.
/// </summary>
public class ProxyAuthorizationException : ProxyException
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ProxyAuthorizationException" /> class.
    /// </summary>
    /// <param name="message">Exception message.</param>
    /// <param name="session">The <see cref="SessionEventArgs" /> instance containing the event data.</param>
    /// <param name="innerException">Inner exception associated to upstream proxy authorization</param>
    /// <param name="headers">Http's headers associated</param>
    internal ProxyAuthorizationException(string message, SessionEventArgsBase session, Exception innerException,
        IEnumerable<HttpHeader> headers) : base(message, innerException)
    {
        Session = session;
        Headers = headers;
    }

    /// <summary>
    ///     The current session within which this error happened.
    /// </summary>
    public SessionEventArgsBase Session { get; }

    /// <summary>
    ///     Headers associated with the authorization exception.
    /// </summary>
    public IEnumerable<HttpHeader> Headers { get; }
}ParseOptions.0.jsonÿ	
eD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Exceptions\ProxyConnectException.csŸusing System;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Exceptions;

/// <summary>
///     Proxy Connection exception.
/// </summary>
public class ProxyConnectException : ProxyException
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ProxyConnectException" /> class.
    /// </summary>
    /// <param name="message">Message for this exception</param>
    /// <param name="innerException">Associated inner exception</param>
    /// <param name="session">
    ///     Instance of <see cref="EventArguments.TunnelConnectSessionEventArgs" /> associated to the
    ///     exception
    /// </param>
    internal ProxyConnectException(string message, Exception innerException, SessionEventArgsBase session) : base(
        message, innerException)
    {
        Session = session;
    }

    /// <summary>
    ///     Gets session info associated to the exception.
    /// </summary>
    /// <remarks>
    ///     This object properties should not be edited.
    /// </remarks>
    public SessionEventArgsBase Session { get; }
}ParseOptions.0.json¡
^D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Exceptions\ProxyException.cs…using System;

namespace Titanium.Web.Proxy.Exceptions;

/// <summary>
///     Base class exception associated with this proxy server.
/// </summary>
public abstract class ProxyException : Exception
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ProxyException" /> class.
    ///     - must be invoked by derived classes' constructors
    /// </summary>
    /// <param name="message">Exception message</param>
    protected ProxyException(string message) : base(message)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="ProxyException" /> class.
    ///     - must be invoked by derived classes' constructors
    /// </summary>
    /// <param name="message">Exception message</param>
    /// <param name="innerException">Inner exception associated</param>
    protected ProxyException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}ParseOptions.0.jsonè	
bD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Exceptions\ProxyHttpException.csìusing System;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Exceptions;

/// <summary>
///     Proxy HTTP exception.
/// </summary>
public class ProxyHttpException : ProxyException
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ProxyHttpException" /> class.
    /// </summary>
    /// <param name="message">Message for this exception</param>
    /// <param name="innerException">Associated inner exception</param>
    /// <param name="session">Instance of <see cref="EventArguments.SessionEventArgs" /> associated to the exception</param>
    internal ProxyHttpException(string message, Exception? innerException, SessionEventArgs? session) : base(
        message, innerException)
    {
        Session = session;
    }

    /// <summary>
    ///     Gets session info associated to the exception.
    /// </summary>
    /// <remarks>
    ///     This object properties should not be edited.
    /// </remarks>
    public SessionEventArgs? Session { get; }
}ParseOptions.0.json†
rD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Exceptions\RetryableServerConnectionException.csîusing System;

namespace Titanium.Web.Proxy.Exceptions;

/// <summary>
///     The server connection was closed upon first write with the new connection from pool.
///     Should retry the request with a new connection.
/// </summary>
public class RetryableServerConnectionException : ProxyException
{
    internal RetryableServerConnectionException(string message) : base(message)
    {
    }

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="e"></param>
    internal RetryableServerConnectionException(string message, Exception e) : base(message, e)
    {
    }
}ParseOptions.0.json±ê
ZD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\ExplicitClientHandler.csºèusing System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.StreamExtended;
using SslExtensions = Titanium.Web.Proxy.Extensions.SslExtensions;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>
    ///     This is called when client is aware of proxy
    ///     So for HTTPS requests client would send CONNECT header to negotiate a secure tcp tunnel via proxy
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    /// <param name="clientConnection">The client connection.</param>
    /// <returns>The task.</returns>
    private async Task HandleClient(ExplicitProxyEndPoint endPoint, TcpClientConnection clientConnection)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        var clientStream = new HttpClientStream(this, clientConnection, clientConnection.GetStream(), BufferPool,
            cancellationToken);

        Task<TcpServerConnection?>? prefetchConnectionTask = null;
        var closeServerConnection = false;

        TunnelConnectSessionEventArgs? connectArgs = null;

        try
        {
            var method = await HttpHelper.GetMethod(clientStream, BufferPool, cancellationToken);
            if (clientStream.IsClosed) return;

            // Client wants to create a secure tcp tunnel (probably its a HTTPS or Websocket request)
            if (method == KnownMethod.Connect)
            {
                // read the first line HTTP command
                var requestLine = await clientStream.ReadRequestLine(cancellationToken);
                if (requestLine.IsEmpty()) return;

                var connectRequest = new ConnectRequest(requestLine.RequestUri)
                {
                    RequestUriString8 = requestLine.RequestUri,
                    HttpVersion = requestLine.Version
                };

                await HeaderParser.ReadHeaders(clientStream, connectRequest.Headers, cancellationToken);

                connectArgs = new TunnelConnectSessionEventArgs(this, endPoint, connectRequest, clientStream,
                    cancellationTokenSource);
                clientStream.DataRead += (o, args) => connectArgs.OnDataSent(args.Buffer, args.Offset, args.Count);
                clientStream.DataWrite += (o, args) => connectArgs.OnDataReceived(args.Buffer, args.Offset, args.Count);

                await endPoint.InvokeBeforeTunnelConnectRequest(this, connectArgs, ExceptionFunc);

                // filter out excluded host names
                var decryptSsl = endPoint.DecryptSsl && connectArgs.DecryptSsl;
                var sendRawData = !decryptSsl;

                if (connectArgs.DenyConnect)
                {
                    if (connectArgs.HttpClient.Response.StatusCode == 0)
                        connectArgs.HttpClient.Response = new Response
                        {
                            HttpVersion = HttpHeader.Version11,
                            StatusCode = (int)HttpStatusCode.Forbidden,
                            StatusDescription = "Forbidden"
                        };

                    // send the response
                    await clientStream.WriteResponseAsync(connectArgs.HttpClient.Response, cancellationToken);
                    return;
                }

                if (await CheckAuthorization(connectArgs) == false)
                {
                    await endPoint.InvokeBeforeTunnelConnectResponse(this, connectArgs, ExceptionFunc);

                    // send the response
                    await clientStream.WriteResponseAsync(connectArgs.HttpClient.Response, cancellationToken);
                    return;
                }

                // write back successful CONNECT response
                var response = ConnectResponse.CreateSuccessfulConnectResponse(connectRequest.HttpVersion);

                // Set ContentLength explicitly to properly handle HTTP 1.0
                response.ContentLength = 0;
                response.Headers.FixProxyHeaders();
                connectArgs.HttpClient.Response = response;

                await clientStream.WriteResponseAsync(response, cancellationToken);

                var clientHelloInfo = await SslTools.PeekClientHello(clientStream, BufferPool, cancellationToken);
                if (clientStream.IsClosed) return;

                var isClientHello = clientHelloInfo != null;
                if (clientHelloInfo != null)
                {
                    connectRequest.TunnelType = TunnelType.Https;
                    connectRequest.ClientHelloInfo = clientHelloInfo;
                }

                await endPoint.InvokeBeforeTunnelConnectResponse(this, connectArgs, ExceptionFunc, isClientHello);

                if (decryptSsl && clientHelloInfo != null)
                {
                    connectRequest.IsHttps = true; // todo: move this line to the previous "if"

                    var sslProtocol = clientHelloInfo.SslProtocol & SupportedSslProtocols;
                    if (sslProtocol == SslProtocols.None)
                    {
                        throw new Exception("Unsupported client SSL version.");
                    }

                    clientStream.Connection.SslProtocol = sslProtocol;

                    var http2Supported = false;

                    if (EnableHttp2)
                    {
                        var alpn = clientHelloInfo.GetAlpn();
                        if (alpn != null && alpn.Contains(SslApplicationProtocol.Http2))
                            // test server HTTP/2 support
                            try
                            {
                                // todo: this is a hack, because Titanium does not support HTTP protocol changing currently
                                var connection = await TcpConnectionFactory.GetServerConnection(this, connectArgs,
                                    true, SslExtensions.Http2ProtocolAsList,
                                    true, true, cancellationToken);

                                if (connection != null)
                                {
                                    http2Supported = connection.NegotiatedApplicationProtocol ==
                                                     SslApplicationProtocol.Http2;

                                    // release connection back to pool instead of closing when connection pool is enabled.
                                    await TcpConnectionFactory.Release(connection, true);
                                }
                            }
                            catch (Exception)
                            {
                                // ignore
                            }
                    }

                    if (EnableTcpServerConnectionPrefetch)
                        // don't pass cancellation token here
                        // it could cause floating server connections when client exits
                        prefetchConnectionTask = TcpConnectionFactory.GetServerConnection(this, connectArgs,
                            true, null, false, true,
                            CancellationToken.None);

                    var connectHostname = requestLine.RequestUri.GetString();
                    var idx = connectHostname.IndexOf(":");
                    if (idx >= 0) connectHostname = connectHostname.Substring(0, idx);

                    X509Certificate2? certificate = null;
                    SslStream? sslStream = null;
                    try
                    {
                        sslStream = new SslStream(clientStream, false);

                        var certName = HttpHelper.GetWildCardDomainName(connectHostname,
                            CertificateManager.DisableWildCardCertificates);
                        certificate = endPoint.GenericCertificate ??
                                      await CertificateManager.CreateServerCertificate(certName);

                        // Successfully managed to authenticate the client using the fake certificate
                        var options = new SslServerAuthenticationOptions();
                        if (EnableHttp2 && http2Supported)
                        {
                            options.ApplicationProtocols = clientHelloInfo.GetAlpn();
                            if (options.ApplicationProtocols == null || options.ApplicationProtocols.Count == 0)
                                options.ApplicationProtocols = SslExtensions.Http11ProtocolAsList;
                        }

                        options.ServerCertificate = certificate;
                        options.ClientCertificateRequired = false;
                        options.EnabledSslProtocols = SupportedSslProtocols;
                        options.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
                        await sslStream.AuthenticateAsServerAsync(options, cancellationToken);

#if NET6_0_OR_GREATER
                            clientStream.Connection.NegotiatedApplicationProtocol =
 sslStream.NegotiatedApplicationProtocol;
#endif

                        // HTTPS server created - we can now decrypt the client's traffic
                        clientStream = new HttpClientStream(this, clientStream.Connection, sslStream, BufferPool,
                            cancellationToken);
                        sslStream = null; // clientStream was created, no need to keep SSL stream reference

                        clientStream.DataRead += (o, args) =>
                            connectArgs.OnDecryptedDataSent(args.Buffer, args.Offset, args.Count);
                        clientStream.DataWrite += (o, args) =>
                            connectArgs.OnDecryptedDataReceived(args.Buffer, args.Offset, args.Count);
                    }
                    catch (Exception e)
                    {
                        sslStream?.Dispose();

                        var certName = certificate?.GetNameInfo(X509NameType.SimpleName, false);
                        throw new ProxyConnectException(
                            $"Couldn't authenticate host '{connectHostname}' with certificate '{certName}'.", e,
                            connectArgs);
                    }

                    method = await HttpHelper.GetMethod(clientStream, BufferPool, cancellationToken);
                    if (clientStream.IsClosed) return;

                    if (method == KnownMethod.Invalid)
                    {
                        sendRawData = true;
                        await TcpConnectionFactory.Release(prefetchConnectionTask, true);
                        prefetchConnectionTask = null;
                    }
                }
                else if (clientHelloInfo == null)
                {
                    method = await HttpHelper.GetMethod(clientStream, BufferPool, cancellationToken);
                    if (clientStream.IsClosed) return;
                }

                if (cancellationTokenSource.IsCancellationRequested)
                    throw new Exception("Session was terminated by user.");

                if (method == KnownMethod.Invalid) sendRawData = true;

                // Hostname is excluded or it is not an HTTPS connect
                if (sendRawData)
                {
                    // create new connection to server.
                    // If we detected that client tunnel CONNECTs without SSL by checking for empty client hello then 
                    // this connection should not be HTTPS.
                    var connection = (await TcpConnectionFactory.GetServerConnection(this, connectArgs,
                        true, null,
                        true, false, cancellationToken))!;

                    try
                    {
                        if (isClientHello)
                        {
                            var available = clientStream.Available;
                            if (available > 0)
                            {
                                // send the buffered data
                                var data = BufferPool.GetBuffer();

                                try
                                {
                                    // clientStream.Available should be at most BufferSize because it is using the same buffer size
                                    var read = await clientStream.ReadAsync(data, 0, available, cancellationToken);
                                    if (read != available) throw new Exception("Internal error.");

                                    await connection.Stream.WriteAsync(data, 0, available, true, cancellationToken);
                                }
                                finally
                                {
                                    BufferPool.ReturnBuffer(data);
                                }
                            }

                            var serverHelloInfo =
                                await SslTools.PeekServerHello(connection.Stream, BufferPool, cancellationToken);
                            ((ConnectResponse)connectArgs.HttpClient.Response).ServerHelloInfo = serverHelloInfo;
                        }

                        if (!clientStream.IsClosed && !connection.Stream.IsClosed)
                            await TcpHelper.SendRaw(clientStream, connection.Stream, BufferPool,
                                null, null, connectArgs.CancellationTokenSource, ExceptionFunc);
                    }
                    finally
                    {
                        await TcpConnectionFactory.Release(connection, true);
                    }

                    return;
                }
            }

            if (connectArgs != null && method == KnownMethod.Pri)
            {
                // todo
                var httpCmd = await clientStream.ReadLineAsync(cancellationToken);
                if (httpCmd == "PRI * HTTP/2.0")
                {
                    connectArgs.HttpClient.ConnectRequest!.TunnelType = TunnelType.Http2;

                    // HTTP/2 Connection Preface
                    var line = await clientStream.ReadLineAsync(cancellationToken);
                    if (line != string.Empty)
                        throw new Exception($"HTTP/2 Protocol violation. Empty string expected, '{line}' received");

                    line = await clientStream.ReadLineAsync(cancellationToken);
                    if (line != "SM")
                        throw new Exception($"HTTP/2 Protocol violation. 'SM' expected, '{line}' received");

                    line = await clientStream.ReadLineAsync(cancellationToken);
                    if (line != string.Empty)
                        throw new Exception($"HTTP/2 Protocol violation. Empty string expected, '{line}' received");

                    var connection = (await TcpConnectionFactory.GetServerConnection(this, connectArgs,
                        true, SslExtensions.Http2ProtocolAsList,
                        true, false, cancellationToken))!;
                    try
                    {
#if NET6_0_OR_GREATER
                            var connectionPreface = new ReadOnlyMemory<byte>(Http2Helper.ConnectionPreface);
                            await connection.Stream.WriteAsync(connectionPreface, cancellationToken);
                            await Http2Helper.SendHttp2(clientStream, connection.Stream,
                                () => new SessionEventArgs(this, endPoint, clientStream, connectArgs?.HttpClient.ConnectRequest, cancellationTokenSource)
                                {
                                    UserData = connectArgs?.UserData
                                },
                                async args => { await OnBeforeRequest(args); },
                                async args => { await OnBeforeResponse(args); },
                                connectArgs.CancellationTokenSource, clientStream.Connection.Id, ExceptionFunc);
#endif
                    }
                    finally
                    {
                        await TcpConnectionFactory.Release(connection, true);
                    }
                }
            }

            var prefetchTask = prefetchConnectionTask;
            prefetchConnectionTask = null;

            // Now create the request
            await HandleHttpSessionRequest(endPoint, clientStream, cancellationTokenSource, connectArgs, prefetchTask);
        }
        catch (ProxyException e)
        {
            closeServerConnection = true;
            OnException(clientStream, e);
        }
        catch (IOException e)
        {
            closeServerConnection = true;
            OnException(clientStream, new Exception("Connection was aborted", e));
        }
        catch (SocketException e)
        {
            closeServerConnection = true;
            OnException(clientStream, new Exception("Could not connect", e));
        }
        catch (Exception e)
        {
            closeServerConnection = true;
            OnException(clientStream, new Exception("Error occured in whilst handling the client", e));
        }
        finally
        {
            if (!cancellationTokenSource.IsCancellationRequested) cancellationTokenSource.Cancel();

            await TcpConnectionFactory.Release(prefetchConnectionTask, closeServerConnection);

            clientStream.Dispose();
            connectArgs?.Dispose();
        }
    }
}ParseOptions.0.json†
^D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Extensions\FuncExtensions.cs®using System;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Extensions;

internal static class FuncExtensions
{
    internal static async Task InvokeAsync<T>(this AsyncEventHandler<T> callback, object sender, T args,
        ExceptionHandler? exceptionFunc)
    {
        var invocationList = callback.GetInvocationList();

        foreach (var @delegate in invocationList)
            await InternalInvokeAsync((AsyncEventHandler<T>)@delegate, sender, args, exceptionFunc);
    }

    private static async Task InternalInvokeAsync<T>(AsyncEventHandler<T> callback, object sender, T args,
        ExceptionHandler? exceptionFunc)
    {
        try
        {
            await callback(sender, args);
        }
        catch (Exception e)
        {
            exceptionFunc?.Invoke(new Exception("Exception thrown in user event", e));
        }
    }
}ParseOptions.0.jsonÓ
dD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Extensions\HttpHeaderExtensions.csusing System;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Extensions;

internal static class HttpHeaderExtensions
{
    internal static string GetString(this ByteString str)
    {
        return GetString(str.Span);
    }

    internal static string GetString(this ReadOnlySpan<byte> bytes)
    {
#if NET6_0_OR_GREATER
        return HttpHeader.Encoding.GetString(bytes);
#else
        return HttpHeader.Encoding.GetString(bytes.ToArray());
#endif
    }

    internal static ByteString GetByteString(this string str)
    {
        return HttpHeader.Encoding.GetBytes(str);
    }
}ParseOptions.0.jsonÏ2
]D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Extensions\SslExtensions.csı1using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.StreamExtended;
using Titanium.Web.Proxy.StreamExtended.Models;

namespace Titanium.Web.Proxy.Extensions
{
    internal static class SslExtensions
    {
        internal static readonly List<SslApplicationProtocol> Http11ProtocolAsList =
            new() { SslApplicationProtocol.Http11 };

        internal static readonly List<SslApplicationProtocol> Http2ProtocolAsList =
            new() { SslApplicationProtocol.Http2 };

        internal static string? GetServerName(this ClientHelloInfo clientHelloInfo)
        {
            if (clientHelloInfo.Extensions != null &&
                clientHelloInfo.Extensions.TryGetValue("server_name", out var serverNameExtension))
                return serverNameExtension.Data;

            return null;
        }

#if NET6_0_OR_GREATER
        internal static List<SslApplicationProtocol>? GetAlpn(this ClientHelloInfo clientHelloInfo)
        {
            if (clientHelloInfo.Extensions != null && clientHelloInfo.Extensions.TryGetValue("ALPN", out var alpnExtension))
            {
                var alpn = alpnExtension.Alpns;
                if (alpn.Count != 0)
                {
                    return alpn;
                }
            }

            return null;
        }

        internal static List<string>? GetSslProtocols(this ClientHelloInfo clientHelloInfo)
        {
            if (clientHelloInfo.Extensions != null && clientHelloInfo.Extensions.TryGetValue("supported_versions", out var versions))
            {
                var protocols = versions.Protocols;
                if (protocols.Count != 0)
                {
                    return protocols;
                }
            }

            return null;
        }
#else
        internal static List<SslApplicationProtocol> GetAlpn(this ClientHelloInfo clientHelloInfo)
        {
            return Http11ProtocolAsList;
        }

        internal static Task AuthenticateAsClientAsync(this SslStream sslStream, SslClientAuthenticationOptions option,
            CancellationToken token)
        {
            return sslStream.AuthenticateAsClientAsync(option.TargetHost, option.ClientCertificates,
                option.EnabledSslProtocols, option.CertificateRevocationCheckMode != X509RevocationMode.NoCheck);
        }

        internal static Task AuthenticateAsServerAsync(this SslStream sslStream, SslServerAuthenticationOptions options,
            CancellationToken token)
        {
            return sslStream.AuthenticateAsServerAsync(options.ServerCertificate, options.ClientCertificateRequired,
                options.EnabledSslProtocols, options.CertificateRevocationCheckMode != X509RevocationMode.NoCheck);
        }
#endif
    }
}

#if !NET6_0_OR_GREATER
namespace System.Net.Security
{
    internal struct SslApplicationProtocol
    {
        public static readonly SslApplicationProtocol Http11 = new SslApplicationProtocol(SslExtension.Http11Utf8);

        public static readonly SslApplicationProtocol Http2 = new SslApplicationProtocol(SslExtension.Http2Utf8);
        
        public static readonly SslApplicationProtocol Http3 = new SslApplicationProtocol(SslExtension.Http3Utf8);

        private readonly byte[] readOnlyProtocol;

        public ReadOnlyMemory<byte> Protocol => readOnlyProtocol;

        public SslApplicationProtocol(byte[] protocol)
        {
            readOnlyProtocol = protocol;
        }

        public bool Equals(SslApplicationProtocol other) => Protocol.Span.SequenceEqual(other.Protocol.Span);

        public override bool Equals(object? obj) => obj is SslApplicationProtocol protocol && Equals(protocol);

        public override int GetHashCode()
        {
            var arr = Protocol;
            if (arr.Length == 0)
            {
                return 0;
            }

            int hash = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                hash = ((hash << 5) + hash) ^ arr.Span[i];
            }

            return hash;
        }

        public override string ToString()
        {
            return Encoding.UTF8.GetString(readOnlyProtocol);
        }

        public static bool operator ==(SslApplicationProtocol left, SslApplicationProtocol right) =>
            left.Equals(right);

        public static bool operator !=(SslApplicationProtocol left, SslApplicationProtocol right) =>
            !(left == right);
    }

    [SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1402:FileMayOnlyContainASingleType", Justification =
        "Reviewed.")]
    internal class SslClientAuthenticationOptions
    {
        internal bool AllowRenegotiation { get; set; }

        internal string? TargetHost { get; set; }

        internal X509CertificateCollection? ClientCertificates { get; set; }

        internal LocalCertificateSelectionCallback? LocalCertificateSelectionCallback { get; set; }

        internal SslProtocols EnabledSslProtocols { get; set; }

        internal X509RevocationMode CertificateRevocationCheckMode { get; set; }

        internal List<SslApplicationProtocol>? ApplicationProtocols { get; set; }

        internal RemoteCertificateValidationCallback? RemoteCertificateValidationCallback { get; set; }

        internal EncryptionPolicy EncryptionPolicy { get; set; }
    }

    internal class SslServerAuthenticationOptions
    {
        internal bool AllowRenegotiation { get; set; }

        internal X509Certificate? ServerCertificate { get; set; }

        internal bool ClientCertificateRequired { get; set; }

        internal SslProtocols EnabledSslProtocols { get; set; }

        internal X509RevocationMode CertificateRevocationCheckMode { get; set; }

        internal List<SslApplicationProtocol>? ApplicationProtocols { get; set; }

        internal RemoteCertificateValidationCallback? RemoteCertificateValidationCallback { get; set; }

        internal EncryptionPolicy EncryptionPolicy { get; set; }
    }
}
#endifParseOptions.0.json÷
`D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Extensions\StreamExtensions.cs‹using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.Extensions;

/// <summary>
///     Extensions used for Stream and CustomBinaryReader objects
/// </summary>
internal static class StreamExtensions
{
    /// <summary>
    ///     Copy streams asynchronously
    /// </summary>
    /// <param name="input"></param>
    /// <param name="output"></param>
    /// <param name="onCopy"></param>
    /// <param name="bufferPool"></param>
    internal static Task CopyToAsync(this Stream input, Stream output, Action<byte[], int, int> onCopy,
        IBufferPool bufferPool)
    {
        return CopyToAsync(input, output, onCopy, bufferPool, CancellationToken.None);
    }

    /// <summary>
    ///     Copy streams asynchronously
    /// </summary>
    /// <param name="input"></param>
    /// <param name="output"></param>
    /// <param name="onCopy"></param>
    /// <param name="bufferPool"></param>
    /// <param name="cancellationToken"></param>
    internal static async Task CopyToAsync(this Stream input, Stream output, Action<byte[], int, int>? onCopy,
        IBufferPool bufferPool, CancellationToken cancellationToken)
    {
        var buffer = bufferPool.GetBuffer();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // cancellation is not working on Socket ReadAsync
                // https://github.com/dotnet/corefx/issues/15033
                var num = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                    .WithCancellation(cancellationToken);
                int bytesRead;
                if ((bytesRead = num) != 0 && !cancellationToken.IsCancellationRequested)
                {
                    await output.WriteAsync(buffer, 0, bytesRead, CancellationToken.None);
                    onCopy?.Invoke(buffer, 0, bytesRead);
                }
                else
                {
                    break;
                }
            }
        }
        finally
        {
            bufferPool.ReturnBuffer(buffer);
        }
    }

    internal static async Task<T> WithCancellation<T>(this Task<T> task, CancellationToken cancellationToken)
        where T : struct
    {
        var tcs = new TaskCompletionSource<bool>();
        using (cancellationToken.Register(() => tcs.TrySetResult(true)))
        {
            if (task != await Task.WhenAny(task, tcs.Task)) return default;
        }

        return await task;
    }
}ParseOptions.0.jsonñ
`D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Extensions\StringExtensions.csúusing System;
using System.Buffers.Text;
using System.Buffers;
using System.Globalization;
using System.Text;

namespace Titanium.Web.Proxy.Extensions;

internal static class StringExtensions
{
    internal static bool EqualsIgnoreCase(this string str, string? value)
    {
        return str.Equals(value, StringComparison.CurrentCultureIgnoreCase);
    }

    internal static bool EqualsIgnoreCase(this ReadOnlySpan<char> str, ReadOnlySpan<char> value)
    {
        return str.Equals(value, StringComparison.CurrentCultureIgnoreCase);
    }

    internal static bool ContainsIgnoreCase(this string str, string value)
    {
        return CultureInfo.CurrentCulture.CompareInfo.IndexOf(str, value, CompareOptions.IgnoreCase) >= 0;
    }

    internal static int IndexOfIgnoreCase(this string str, string value)
    {
        return CultureInfo.CurrentCulture.CompareInfo.IndexOf(str, value, CompareOptions.IgnoreCase);
    }

    internal static unsafe string ByteArrayToHexString(this ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return string.Empty;
        }

        int length = data.Length * 3;
        Span<byte> buf = stackalloc byte[length];
        var buf2 = buf;
        foreach (var b in data)
        {
            Utf8Formatter.TryFormat(b, buf2, out _, new StandardFormat('X', 2));
            buf2[2] = 32; // space
            buf2 = buf2.Slice(3);
        }

#if NET6_0_OR_GREATER
        return Encoding.UTF8.GetString(buf.Slice(0, length - 1));
#else
        fixed (byte* bp = buf)
        {
            return Encoding.UTF8.GetString(bp, length -1);
        }
#endif
    }
}ParseOptions.0.json≈
]D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Extensions\TcpExtensions.csŒ
using System.Net.Sockets;

namespace Titanium.Web.Proxy.Extensions;

internal static class TcpExtensions
{
    /// <summary>
    ///     Check if a TcpClient is good to be used.
    ///     This only checks if send is working so local socket is still connected.
    ///     Receive can only be verified by doing a valid read from server without exceptions.
    ///     So in our case we should retry with new connection from pool if first read after getting the connection fails.
    ///     https://msdn.microsoft.com/en-us/library/system.net.sockets.socket.connected(v=vs.110).aspx
    /// </summary>
    /// <param name="socket"></param>
    /// <returns></returns>
    internal static bool IsGoodConnection(this Socket socket)
    {
        if (!socket.Connected) return false;

        // This is how you can determine whether a socket is still connected.
        var blockingState = socket.Blocking;
        try
        {
            var tmp = new byte[1];

            socket.Blocking = false;
            socket.Send(tmp, 0, 0);
            // Connected.
        }
        catch
        {
            // Should we let 10035 == WSAEWOULDBLOCK as valid connection?
            return false;
        }
        finally
        {
            socket.Blocking = blockingState;
        }

        return true;
    }
}ParseOptions.0.json¬

]D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Extensions\UriExtensions.csÀ	using System;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Extensions;

internal static class UriExtensions
{
    public static string GetOriginalPathAndQuery(this Uri uri)
    {
        var leftPart = uri.GetLeftPart(UriPartial.Authority);
        if (uri.OriginalString.StartsWith(leftPart))
            return uri.OriginalString.Substring(leftPart.Length);

        return uri.IsWellFormedOriginalString()
            ? uri.PathAndQuery
            : uri.GetComponents(UriComponents.PathAndQuery, UriFormat.Unescaped);
    }

    public static ByteString GetScheme(ByteString str)
    {
        if (str.Length < 3) return ByteString.Empty;

        // regex: "^[a-z]*://"
        int i;

        for (i = 0; i < str.Length - 3; i++)
        {
            var ch = str[i];
            if (ch == ':') break;

            if (ch < 'A' || ch > 'z' || ch > 'Z' && ch < 'a') // ASCII letter
                return ByteString.Empty;
        }

        if (str[i++] != ':') return ByteString.Empty;

        if (str[i++] != '/') return ByteString.Empty;

        if (str[i] != '/') return ByteString.Empty;

        return new ByteString(str.Data.Slice(0, i - 2));
    }
}ParseOptions.0.json»
_D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Handlers\AsyncEventHandler.csœusing System.Threading.Tasks;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
///     A generic asynchronous event handler used by the proxy.
/// </summary>
/// <typeparam name="TEventArgs">Event argument type.</typeparam>
/// <param name="sender">The proxy server instance.</param>
/// <param name="e">The event arguments.</param>
/// <returns></returns>
public delegate Task AsyncEventHandler<in TEventArgs>(object sender, TEventArgs e);ParseOptions.0.json∑
`D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Handlers\CertificateHandler.csΩusing System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>
    ///     Call back to override server certificate validation
    /// </summary>
    /// <param name="sender">The sender object.</param>
    /// <param name="sessionArgs">The http session.</param>
    /// <param name="certificate">The remote certificate.</param>
    /// <param name="chain">The certificate chain.</param>
    /// <param name="sslPolicyErrors">Ssl policy errors</param>
    /// <returns>Return true if valid certificate.</returns>
    internal bool ValidateServerCertificate(object sender, SessionEventArgsBase? sessionArgs,
        X509Certificate? certificate, X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        // if user callback is registered then do it
        if (ServerCertificateValidationCallback != null && sessionArgs != null)
        {
            var args = new CertificateValidationEventArgs(sessionArgs, certificate, chain, sslPolicyErrors);

            // why is the sender null?
            ServerCertificateValidationCallback.InvokeAsync(this, args, ExceptionFunc).Wait();
            return args.IsValid;
        }

        if (sslPolicyErrors == SslPolicyErrors.None) return true;

        // By default
        // do not allow this client to communicate with unauthenticated servers.
        return false;
    }

    /// <summary>
    ///     Call back to select client certificate used for mutual authentication
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="sessionArgs">The http session.</param>
    /// <param name="targetHost">The remote hostname.</param>
    /// <param name="localCertificates">Selected local certificates by SslStream.</param>
    /// <param name="remoteCertificate">The remote certificate of server.</param>
    /// <param name="acceptableIssuers">The acceptable issues for client certificate as listed by server.</param>
    /// <returns></returns>
    internal X509Certificate? SelectClientCertificate(object sender, SessionEventArgsBase? sessionArgs,
        string targetHost,
        X509CertificateCollection? localCertificates,
        X509Certificate? remoteCertificate, string[]? acceptableIssuers)
    {
        X509Certificate? clientCertificate = null;

        //fallback to the first client certificate from proxy machine certificate store
        if (acceptableIssuers != null && acceptableIssuers.Length > 0 && localCertificates != null &&
            localCertificates.Count > 0)
            foreach (var certificate in localCertificates)
            {
                var issuer = certificate.Issuer;
                if (Array.IndexOf(acceptableIssuers, issuer) != -1) clientCertificate = certificate;
            }

        //fallback to the first client certificate from proxy machine certificate store
        if (clientCertificate == null
            && localCertificates != null && localCertificates.Count > 0)
            clientCertificate = localCertificates[0];

        // If user call back is registered
        if (ClientCertificateSelectionCallback != null && sessionArgs != null)
        {
            var args = new CertificateSelectionEventArgs(sessionArgs, targetHost,
                localCertificates ?? new X509CertificateCollection(), remoteCertificate,
                acceptableIssuers ?? Array.Empty<string>())
            {
                ClientCertificate = clientCertificate
            };


            ClientCertificateSelectionCallback.InvokeAsync(this, args, ExceptionFunc).Wait();
            return args.ClientCertificate;
        }

        return clientCertificate;
    }
}ParseOptions.0.jsonÖ
^D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Handlers\ExceptionHandler.csçusing System;

namespace Titanium.Web.Proxy;

/// <summary>
///     A delegate to catch exceptions occuring in proxy.
/// </summary>
/// <param name="exception">The exception occurred in proxy.</param>
public delegate void ExceptionHandler(Exception exception);ParseOptions.0.json∏/
gD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Handlers\ProxyAuthorizationHandler.cs∑.using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>
    ///     Callback to authorize clients of this proxy instance.
    /// </summary>
    /// <param name="session">The session event arguments.</param>
    /// <returns>True if authorized.</returns>
    private async Task<bool> CheckAuthorization(SessionEventArgsBase session)
    {
        // If we are not authorizing clients return true
        if (ProxyBasicAuthenticateFunc == null && ProxySchemeAuthenticateFunc == null) return true;

        var httpHeaders = session.HttpClient.Request.Headers;

        try
        {
            var headerObj = httpHeaders.GetFirstHeader(KnownHeaders.ProxyAuthorization);
            if (headerObj == null)
            {
                session.HttpClient.Response = CreateAuthentication407Response("Proxy Authentication Required");
                return false;
            }

            var header = headerObj.Value;
            var firstSpace = header.IndexOf(' ');

            // header value should contain exactly 1 space
            if (firstSpace == -1 || header.IndexOf(' ', firstSpace + 1) != -1)
            {
                // Return not authorized
                session.HttpClient.Response = CreateAuthentication407Response("Proxy Authentication Invalid");
                return false;
            }

            var authenticationType = header.AsMemory(0, firstSpace);
            var credentials = header.AsMemory(firstSpace + 1);

            if (ProxyBasicAuthenticateFunc != null)
                return await AuthenticateUserBasic(session, authenticationType, credentials,
                    ProxyBasicAuthenticateFunc);

            if (ProxySchemeAuthenticateFunc != null)
            {
                var result =
                    await ProxySchemeAuthenticateFunc(session, authenticationType.ToString(), credentials.ToString());

                if (result.Result == ProxyAuthenticationResult.ContinuationNeeded)
                {
                    session.HttpClient.Response =
                        CreateAuthentication407Response("Proxy Authentication Invalid", result.Continuation);

                    return false;
                }

                return result.Result == ProxyAuthenticationResult.Success;
            }

            return false;
        }
        catch (Exception e)
        {
            OnException(null, new ProxyAuthorizationException("Error whilst authorizing request", session, e,
                httpHeaders));

            // Return not authorized
            session.HttpClient.Response = CreateAuthentication407Response("Proxy Authentication Invalid");
            return false;
        }
    }

    private async Task<bool> AuthenticateUserBasic(SessionEventArgsBase session,
        ReadOnlyMemory<char> authenticationType, ReadOnlyMemory<char> credentials,
        Func<SessionEventArgsBase, string, string, Task<bool>> proxyBasicAuthenticateFunc)
    {
        if (!KnownHeaders.ProxyAuthorizationBasic.Equals(authenticationType.Span))
        {
            // Return not authorized
            session.HttpClient.Response = CreateAuthentication407Response("Proxy Authentication Invalid");
            return false;
        }

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(credentials.ToString()));
        var colonIndex = decoded.IndexOf(':');
        if (colonIndex == -1)
        {
            // Return not authorized
            session.HttpClient.Response = CreateAuthentication407Response("Proxy Authentication Invalid");
            return false;
        }

        var username = decoded.Substring(0, colonIndex);
        var password = decoded.Substring(colonIndex + 1);
        var authenticated = await proxyBasicAuthenticateFunc(session, username, password);
        if (!authenticated)
            session.HttpClient.Response = CreateAuthentication407Response("Proxy Authentication Invalid");

        return authenticated;
    }

    /// <summary>
    ///     Create an authentication required response.
    /// </summary>
    /// <param name="description">Response description.</param>
    /// <param name="continuation">The continuation.</param>
    /// <returns></returns>
    private Response CreateAuthentication407Response(string description, string? continuation = null)
    {
        var response = new Response
        {
            HttpVersion = HttpHeader.Version11,
            StatusCode = (int)HttpStatusCode.ProxyAuthenticationRequired,
            StatusDescription = description
        };

        if (!string.IsNullOrWhiteSpace(continuation)) return CreateContinuationResponse(response, continuation!);

        if (ProxyBasicAuthenticateFunc != null)
            response.Headers.AddHeader(KnownHeaders.ProxyAuthenticate, $"Basic realm=\"{ProxyAuthenticationRealm}\"");

        if (ProxySchemeAuthenticateFunc != null)
            foreach (var scheme in ProxyAuthenticationSchemes)
                response.Headers.AddHeader(KnownHeaders.ProxyAuthenticate, scheme);

        response.Headers.AddHeader(KnownHeaders.ProxyConnection, KnownHeaders.ProxyConnectionClose);

        response.Headers.FixProxyHeaders();
        return response;
    }

    private Response CreateContinuationResponse(Response response, string continuation)
    {
        response.Headers.AddHeader(KnownHeaders.ProxyAuthenticate, continuation);

        response.Headers.AddHeader(KnownHeaders.ProxyConnection, KnownHeaders.ConnectionKeepAlive);

        response.Headers.FixProxyHeaders();

        return response;
    }
}ParseOptions.0.json˙
^D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Handlers\WebSocketHandler.csÇusing System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>
    ///     Handle upgrade to websocket
    /// </summary>
    private async Task HandleWebSocketUpgrade(SessionEventArgs args,
        HttpClientStream clientStream, TcpServerConnection serverConnection,
        CancellationTokenSource cancellationTokenSource, CancellationToken cancellationToken)
    {
        await serverConnection.Stream.WriteRequestAsync(args.HttpClient.Request, cancellationToken);

        var httpStatus = await serverConnection.Stream.ReadResponseStatus(cancellationToken);

        var response = args.HttpClient.Response;
        response.HttpVersion = httpStatus.Version;
        response.StatusCode = httpStatus.StatusCode;
        response.StatusDescription = httpStatus.Description;

        await HeaderParser.ReadHeaders(serverConnection.Stream, response.Headers,
            cancellationToken);

        await clientStream.WriteResponseAsync(response, cancellationToken);

        // If user requested call back then do it
        if (!args.HttpClient.Response.Locked) await OnBeforeResponse(args);

        await TcpHelper.SendRaw(clientStream, serverConnection.Stream, BufferPool,
            args.OnDataSent, args.OnDataReceived, cancellationTokenSource, ExceptionFunc);
    }
}ParseOptions.0.jsonìS
\D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Handlers\WinAuthHandler.csùRusing System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.WinAuth;
using Titanium.Web.Proxy.Network.WinAuth.Security;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>
    ///     possible header names.
    /// </summary>
    private static readonly HashSet<string> authHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "WWW-Authenticate",

        // IIS 6.0 messed up names below
        "WWWAuthenticate",
        "NTLMAuthorization",
        "NegotiateAuthorization",
        "KerberosAuthorization"
    };

    private static readonly HashSet<string> proxyAuthHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Proxy-Authenticate"
    };

    /// <summary>
    ///     supported authentication schemes.
    /// </summary>
    private static readonly HashSet<string> authSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "NTLM",
        "Negotiate",
        "Kerberos"
    };

    /// <summary>
    ///     Handle windows NTLM/Kerberos authentication.
    ///     Note: NTLM/Kerberos cannot do a man in middle operation
    ///     we do for HTTPS requests.
    ///     As such we will be sending local credentials of current
    ///     User to server to authenticate requests.
    ///     To disable this set ProxyServer.EnableWinAuth to false.
    /// </summary>
    private async Task Handle401UnAuthorized(SessionEventArgs args)
    {
        string? headerName = null;
        HttpHeader? authHeader = null;

        var response = args.HttpClient.Response;

        // check in non-unique headers first
        var header = response.Headers.NonUniqueHeaders.FirstOrDefault(x => authHeaderNames.Contains(x.Key));

        if (!header.Equals(new KeyValuePair<string, List<HttpHeader>>())) headerName = header.Key;

        if (headerName != null)
            authHeader = response.Headers.NonUniqueHeaders[headerName]
                .FirstOrDefault(
                    x => authSchemes.Any(y => x.Value.StartsWith(y, StringComparison.OrdinalIgnoreCase)));

        // check in unique headers
        if (authHeader == null)
        {
            headerName = null;

            // check in non-unique headers first
            var uHeader = response.Headers.Headers.FirstOrDefault(x => authHeaderNames.Contains(x.Key));

            if (!uHeader.Equals(new KeyValuePair<string, HttpHeader>())) headerName = uHeader.Key;

            if (headerName != null)
                authHeader = authSchemes.Any(x => response.Headers.Headers[headerName].Value
                    .StartsWith(x, StringComparison.OrdinalIgnoreCase))
                    ? response.Headers.Headers[headerName]
                    : null;
        }

        if (authHeader != null)
        {
            var scheme = authSchemes.Contains(authHeader.Value) ? authHeader.Value : null;

            var expectedAuthState =
                scheme == null ? State.WinAuthState.InitialToken : State.WinAuthState.Unauthorized;

            if (!WinAuthEndPoint.ValidateWinAuthState(args.HttpClient.Data, expectedAuthState))
            {
                // Invalid state, create proper error message to client
                await RewriteUnauthorizedResponse(args);
                return;
            }

            var request = args.HttpClient.Request;

            // clear any existing headers to avoid confusing bad servers
            request.Headers.RemoveHeader(KnownHeaders.Authorization);

            // initial value will match exactly any of the schemes
            if (scheme != null)
            {
                var clientToken = WinAuthHandler.GetInitialAuthToken(request.Host!, scheme, args.HttpClient.Data);

                var auth = string.Concat(scheme, clientToken);

                // replace existing authorization header if any
                request.Headers.SetOrAddHeaderValue(KnownHeaders.Authorization, auth);

                // don't need to send body for Authorization request
                if (request.HasBody) request.ContentLength = 0;
            }
            else
            {
                // challenge value will start with any of the scheme selected
                scheme = authSchemes.First(x =>
                    authHeader.Value.StartsWith(x, StringComparison.OrdinalIgnoreCase) &&
                    authHeader.Value.Length > x.Length + 1);

                var serverToken = authHeader.Value.Substring(scheme.Length + 1);
                var clientToken = WinAuthHandler.GetFinalAuthToken(request.Host!, serverToken, args.HttpClient.Data);

                var auth = string.Concat(scheme, clientToken);

                // there will be an existing header from initial client request 
                request.Headers.SetOrAddHeaderValue(KnownHeaders.Authorization, auth);

                // send body for final auth request
                if (request.OriginalHasBody) request.ContentLength = request.Body.Length;

                args.HttpClient.Connection.IsWinAuthenticated = true;
            }

            // Need to revisit this.
            // Should we cache all Set-Cookie headers from server during auth process
            // and send it to client after auth?

            // Let ResponseHandler send the updated request
            args.ReRequest = true;
        }
    }

    /// <summary>
    ///     Handles NTLM/Kerberos authentication challenges from an upstream proxy.
    /// </summary>
    private async Task Handle407ProxyAuthorization(SessionEventArgs args)
    {
        if (!args.HttpClient.HasConnection) return;

        var upstreamProxy = args.HttpClient.Connection.UpStreamProxy;
        if (upstreamProxy?.UseDefaultCredentials != true) return;

        var response = args.HttpClient.Response;
        var authHeader = response.Headers.GetHeaders(KnownHeaders.ProxyAuthenticate.String)?
            .FirstOrDefault(x => authSchemes.Any(y =>
                x.Value.Equals(y, StringComparison.OrdinalIgnoreCase) ||
                x.Value.StartsWith(y + " ", StringComparison.OrdinalIgnoreCase)));
        if (authHeader == null) return;

        var scheme = authSchemes.Contains(authHeader.Value) ? authHeader.Value : null;
        var expectedAuthState =
            scheme == null ? State.WinAuthState.InitialToken : State.WinAuthState.Unauthorized;

        if (UpstreamProxyWinAuthTokenGenerator == null &&
            !WinAuthEndPoint.ValidateWinAuthState(args.HttpClient.Data, expectedAuthState))
        {
            await RewriteUnauthorizedResponse(args);
            return;
        }

        var request = args.HttpClient.Request;
        request.Headers.RemoveHeader(KnownHeaders.ProxyAuthorization);

        if (scheme != null)
        {
            var clientToken = GenerateUpstreamProxyWinAuthToken(upstreamProxy, scheme, null, args.HttpClient.Data);
            if (string.IsNullOrEmpty(clientToken))
            {
                await RewriteUnauthorizedResponse(args);
                return;
            }

            request.Headers.SetOrAddHeaderValue(KnownHeaders.ProxyAuthorization,
                string.Concat(scheme, clientToken));
            if (request.HasBody) request.ContentLength = 0;
        }
        else
        {
            scheme = authSchemes.First(x =>
                authHeader.Value.StartsWith(x, StringComparison.OrdinalIgnoreCase) &&
                authHeader.Value.Length > x.Length &&
                char.IsWhiteSpace(authHeader.Value[x.Length]));

            var serverToken = authHeader.Value.Substring(scheme.Length).Trim();
            var clientToken =
                GenerateUpstreamProxyWinAuthToken(upstreamProxy, scheme, serverToken, args.HttpClient.Data);
            if (string.IsNullOrEmpty(clientToken))
            {
                await RewriteUnauthorizedResponse(args);
                return;
            }

            request.Headers.SetOrAddHeaderValue(KnownHeaders.ProxyAuthorization,
                string.Concat(scheme, clientToken));
            if (request.OriginalHasBody) request.ContentLength = request.Body.Length;

            args.HttpClient.Connection.IsWinAuthenticated = true;
        }

        args.ReRequest = true;
    }

    /// <summary>
    ///     Rewrites the response body for failed authentication
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    private async Task RewriteUnauthorizedResponse(SessionEventArgs args)
    {
        var response = args.HttpClient.Response;

        // Strip authentication headers to avoid credentials prompt in client web browser
        foreach (var authHeaderName in authHeaderNames) response.Headers.RemoveHeader(authHeaderName);
        foreach (var proxyAuthHeaderName in proxyAuthHeaderNames) response.Headers.RemoveHeader(proxyAuthHeaderName);

        // Add custom div to body to clarify that the proxy (not the client browser) failed authentication
        var authErrorMessage =
            "<div class=\"inserted-by-proxy\"><h2>NTLM authentication through Titanium.Web.Proxy (" +
            args.ClientLocalEndPoint +
            ") failed. Please check credentials.</h2></div>";
        var originalErrorMessage =
            "<div class=\"inserted-by-proxy\"><h3>Response from remote web server below.</h3></div><br/>";
        var body = await args.GetResponseBodyAsString(args.CancellationTokenSource.Token);
        var idx = body.IndexOfIgnoreCase("<body>");
        if (idx >= 0)
        {
            var bodyPos = idx + "<body>".Length;
            body = body.Insert(bodyPos, authErrorMessage + originalErrorMessage);
        }
        else
        {
            // Cannot parse response body, replace it
            body =
                "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Strict//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd\">" +
                "<html xmlns=\"http://www.w3.org/1999/xhtml\">" +
                "<body>" +
                authErrorMessage +
                "</body>" +
                "</html>";
        }

        args.SetResponseBodyString(body);
    }
}ParseOptions.0.json≥
\D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Helpers\CompressionUtil.csΩusing Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Compression;

internal static class CompressionUtil
{
    public static HttpCompression CompressionNameToEnum(string name)
    {
        if (KnownHeaders.ContentEncodingGzip.Equals(name))
            return HttpCompression.Gzip;

        if (KnownHeaders.ContentEncodingDeflate.Equals(name))
            return HttpCompression.Deflate;

        if (KnownHeaders.ContentEncodingBrotli.Equals(name))
            return HttpCompression.Brotli;

        return HttpCompression.Unsupported;
    }
}ParseOptions.0.jsonÉJ
WD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Helpers\HttpHelper.csíIusing System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Shared;
using Titanium.Web.Proxy.StreamExtended.BufferPool;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Helpers;

internal static class HttpHelper
{
    /// <summary>
    ///     Gets the character encoding of request/response from content-type header
    /// </summary>
    /// <param name="contentType"></param>
    /// <returns></returns>
    internal static Encoding GetEncodingFromContentType(string? contentType)
    {
        try
        {
            // return default if not specified
            if (contentType == null) return HttpHeader.DefaultEncoding;

            // extract the encoding by finding the charset
            foreach (var p in new SemicolonSplitEnumerator(contentType))
            {
                var parameter = p.Span;
                var equalsIndex = parameter.IndexOf('=');
                if (equalsIndex != -1 &&
                    KnownHeaders.ContentTypeCharset.Equals(parameter.Slice(0, equalsIndex).TrimStart()))
                {
                    var value = parameter.Slice(equalsIndex + 1);
                    if (value.EqualsIgnoreCase("x-user-defined".AsSpan())) continue;

                    if (value.Length > 2 && value[0] == '"' && value[value.Length - 1] == '"')
                        value = value.Slice(1, value.Length - 2);

                    return Encoding.GetEncoding(value.ToString());
                }
            }
        }
        catch
        {
            // parsing errors
            // ignored
        }

        // return default if not specified
        return HttpHeader.DefaultEncoding;
    }

    internal static ReadOnlyMemory<char> GetBoundaryFromContentType(string? contentType)
    {
        if (contentType != null)
            // extract the boundary
            foreach (var parameter in new SemicolonSplitEnumerator(contentType))
            {
                var equalsIndex = parameter.Span.IndexOf('=');
                if (equalsIndex != -1 &&
                    KnownHeaders.ContentTypeBoundary.Equals(parameter.Span.Slice(0, equalsIndex).TrimStart()))
                {
                    var value = parameter.Slice(equalsIndex + 1);
                    if (value.Length > 2 && value.Span[0] == '"' && value.Span[value.Length - 1] == '"')
                        value = value.Slice(1, value.Length - 2);

                    return value;
                }
            }

        // return null if not specified
        return null;
    }

    /// <summary>
    ///     Tries to get root domain from a given hostname
    ///     Adapted from below answer
    ///     https://stackoverflow.com/questions/16473838/get-domain-name-of-a-url-in-c-sharp-net
    /// </summary>
    /// <param name="hostname"></param>
    /// <returns></returns>
    internal static string GetWildCardDomainName(string hostname, bool disableWildCardCertificates)
    {
        // only for subdomains we need wild card
        // example www.google.com or gstatic.google.com
        // but NOT for google.com or IP address

        if (IPAddress.TryParse(hostname, out _)) return hostname;

        if (disableWildCardCertificates) return hostname;

        var split = hostname.Split(ProxyConstants.DotSplit);

        if (split.Length > 2)
        {
            // issue #769
            // do not create wildcard if second level domain like: pay.vn.ua
            if (split[0] != "www" && split[1].Length <= 3) return hostname;

            var idx = hostname.IndexOf(ProxyConstants.DotSplit);

            // issue #352
            if (hostname.Substring(0, idx).Contains("-")) return hostname;

            var rootDomain = hostname.Substring(idx + 1);
            return "*." + rootDomain;
        }

        // return as it is
        return hostname;
    }

    /// <summary>
    ///     Gets the HTTP method from the stream.
    /// </summary>
    public static async ValueTask<KnownMethod> GetMethod(IPeekStream httpReader, IBufferPool bufferPool,
        CancellationToken cancellationToken = default)
    {
        const int lengthToCheck = 20;
        if (bufferPool.BufferSize < lengthToCheck)
            throw new Exception($"Buffer is too small. Minimum size is {lengthToCheck} bytes");

        var buffer = bufferPool.GetBuffer(bufferPool.BufferSize);
        try
        {
            var i = 0;
            while (i < lengthToCheck)
            {
                var peeked = await httpReader.PeekBytesAsync(buffer, i, i, lengthToCheck - i, cancellationToken);
                if (peeked <= 0)
                    return KnownMethod.Invalid;

                peeked += i;

                while (i < peeked)
                {
                    int b = buffer[i];

                    if (b == ' ' && i > 2)
                        return GetKnownMethod(buffer.AsSpan(0, i));

                    var ch = (char)b;
                    if ((ch < 'A' || ch > 'z' || ch > 'Z' && ch < 'a') && ch != '-') // ASCII letter
                        return KnownMethod.Invalid;

                    i++;
                }
            }

            // only letters, but no space (or shorter than 3 characters)
            return KnownMethod.Invalid;
        }
        finally
        {
            bufferPool.ReturnBuffer(buffer);
        }
    }

    private static KnownMethod GetKnownMethod(ReadOnlySpan<byte> method)
    {
        // the following methods are supported:
        // Connect
        // Delete
        // Get
        // Head
        // Options
        // Post
        // Put
        // Trace
        // Pri

        // method parameter should have at least 3 bytes
        var b1 = method[0];
        var b2 = method[1];
        var b3 = method[2];

        switch (method.Length)
        {
            case 3:
                // Get or Put
                if (b1 == 'G')
                    return b2 == 'E' && b3 == 'T' ? KnownMethod.Get : KnownMethod.Unknown;

                if (b1 == 'P')
                {
                    if (b2 == 'U')
                        return b3 == 'T' ? KnownMethod.Put : KnownMethod.Unknown;

                    if (b2 == 'R')
                        return b3 == 'I' ? KnownMethod.Pri : KnownMethod.Unknown;
                }

                break;
            case 4:
                // Head or Post
                if (b1 == 'H')
                    return b2 == 'E' && b3 == 'A' && method[3] == 'D' ? KnownMethod.Head : KnownMethod.Unknown;

                if (b1 == 'P')
                    return b2 == 'O' && b3 == 'S' && method[3] == 'T' ? KnownMethod.Post : KnownMethod.Unknown;

                break;
            case 5:
                // Trace
                if (b1 == 'T')
                    return b2 == 'R' && b3 == 'A' && method[3] == 'C' && method[4] == 'E'
                        ? KnownMethod.Trace
                        : KnownMethod.Unknown;

                break;
            case 6:
                // Delete
                if (b1 == 'D')
                    return b2 == 'E' && b3 == 'L' && method[3] == 'E' && method[4] == 'T' && method[5] == 'E'
                        ? KnownMethod.Delete
                        : KnownMethod.Unknown;

                break;
            case 7:
                // Connect or Options
                if (b1 == 'C')
                    return b2 == 'O' && b3 == 'N' && method[3] == 'N' && method[4] == 'E' && method[5] == 'C' &&
                           method[6] == 'T'
                        ? KnownMethod.Connect
                        : KnownMethod.Unknown;

                if (b1 == 'O')
                    return b2 == 'P' && b3 == 'T' && method[3] == 'I' && method[4] == 'O' && method[5] == 'N' &&
                           method[6] == 'S'
                        ? KnownMethod.Options
                        : KnownMethod.Unknown;

                break;
        }


        return KnownMethod.Unknown;
    }

    private struct SemicolonSplitEnumerator
    {
        private readonly ReadOnlyMemory<char> data;

        private int idx;

        public SemicolonSplitEnumerator(string str) : this(str.AsMemory())
        {
        }

        public SemicolonSplitEnumerator(ReadOnlyMemory<char> data)
        {
            this.data = data;
            Current = null;
            idx = 0;
        }

        public SemicolonSplitEnumerator GetEnumerator()
        {
            return this;
        }

        public bool MoveNext()
        {
            if (this.idx > data.Length) return false;

            var idx = data.Span.Slice(this.idx).IndexOf(';');
            if (idx == -1)
                idx = data.Length;
            else
                idx += this.idx;

            Current = data.Slice(this.idx, idx - this.idx);
            this.idx = idx + 1;
            return true;
        }


        public ReadOnlyMemory<char> Current { get; private set; }
    }
}ParseOptions.0.json‘
fD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Helpers\NativeMethods.SystemProxy.cs‘using System;
using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Helpers;

internal partial class NativeMethods
{
    // Keeps it from getting garbage collected
    internal static ConsoleEventDelegate? Handler;

    [DllImport("wininet.dll")]
    internal static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer,
        int dwBufferLength);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetConsoleCtrlHandler(ConsoleEventDelegate callback, bool add);

    /// <summary>
    ///     <see href="https://docs.microsoft.com/en-us/windows/desktop/api/winuser/nf-winuser-getsystemmetrics" />
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    // Pinvoke
    internal delegate bool ConsoleEventDelegate(int eventType);
}ParseOptions.0.jsonÆ
^D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Helpers\NativeMethods.Tcp.cs∂using System;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Helpers;

internal partial class NativeMethods
{
    internal const int AfInet = 2;
    internal const int AfInet6 = 23;

    /// <summary>
    ///     <see href="http://msdn2.microsoft.com/en-us/library/aa365928.aspx" />
    /// </summary>
    [DllImport("iphlpapi.dll", SetLastError = true)]
    internal static extern uint GetExtendedTcpTable(IntPtr tcpTable, ref int size, bool sort, int ipVersion,
        int tableClass, int reserved);

    internal enum TcpTableType
    {
        BasicListener,
        BasicConnections,
        BasicAll,
        OwnerPidListener,
        OwnerPidConnections,
        OwnerPidAll,
        OwnerModuleListener,
        OwnerModuleConnections,
        OwnerModuleAll
    }

    /// <summary>
    ///     <see href="http://msdn2.microsoft.com/en-us/library/aa366913.aspx" />
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TcpRow
    {
        public TcpState state;
        public uint localAddr;
        public uint localPort; // in network byte order (order of bytes - 1,0,3,2)
        public uint remoteAddr;
        public uint remotePort; // in network byte order (order of bytes - 1,0,3,2)
        public int owningPid;
    }

    /// <summary>
    ///     <see href="https://msdn.microsoft.com/en-us/library/aa366896.aspx" />
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct Tcp6Row
    {
        public fixed byte localAddr[16];
        public uint localScopeId;
        public uint localPort; // in network byte order (order of bytes - 1,0,3,2)
        public fixed byte remoteAddr[16];
        public uint remoteScopeId;
        public uint remotePort; // in network byte order (order of bytes - 1,0,3,2)
        public TcpState state;
        public int owningPid;
    }
}ParseOptions.0.jsonä
_D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Helpers\Net45Compatibility.csë#if NET451
using System.Threading.Tasks;

namespace Titanium.Web.Proxy
{
    internal class Net45Compatibility
    {
        public static byte[] EmptyArray = new byte[0];

        public static Task CompletedTask = Task.FromResult<object>(null);
    }
}
#endifParseOptions.0.jsonœ
TD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Helpers\Network.cs·using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Titanium.Web.Proxy.Helpers;

internal class NetworkHelper
{
    private static readonly string localhostName = Dns.GetHostName();
    private static readonly IPHostEntry localhostEntry = Dns.GetHostEntry(string.Empty);

    /// <summary>
    ///     Adapated from below link
    ///     http://stackoverflow.com/questions/11834091/how-to-check-if-localhost
    /// </summary>
    /// <param name="address"></param>
    /// <returns></returns>
    internal static bool IsLocalIpAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        // test if host IP equals any local IP
        return localhostEntry.AddressList.Contains(address);
    }

    internal static bool IsLocalIpAddress(string hostName, bool proxyDnsRequests = false)
    {
        if (IPAddress.TryParse(hostName, out var ipAddress)
            && IsLocalIpAddress(ipAddress))
            return true;

        if (hostName.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;

        // if hostname matches local host name
        if (hostName.Equals(localhostName, StringComparison.OrdinalIgnoreCase)) return true;

        // if hostname matches fully qualified local DNS name
        if (hostName.Equals(localhostEntry.HostName, StringComparison.OrdinalIgnoreCase)) return true;

        if (!proxyDnsRequests)
            try
            {
                // do reverse DNS lookup even if hostName is an IP address
                var hostEntry = Dns.GetHostEntry(hostName);
                // if DNS resolved hostname matches local DNS name,
                // or if host IP address list contains any local IP address
                if (hostEntry.HostName.Equals(localhostEntry.HostName, StringComparison.OrdinalIgnoreCase)
                    || hostEntry.AddressList.Any(hostIp => localhostEntry.AddressList.Contains(hostIp)))
                    return true;
            }
            catch (SocketException)
            {
            }

        return false;
    }
}ParseOptions.0.json–-
VD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Helpers\ProxyInfo.cs‡,using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Helpers;

internal class ProxyInfo
{
    internal ProxyInfo(bool? autoDetect, string? autoConfigUrl, int? proxyEnable, string? proxyServer,
        string? proxyOverride)
    {
        AutoDetect = autoDetect;
        AutoConfigUrl = autoConfigUrl;
        ProxyEnable = proxyEnable;
        ProxyServer = proxyServer;
        ProxyOverride = proxyOverride;

        if (proxyServer != null) Proxies = GetSystemProxyValues(proxyServer).ToDictionary(x => x.ProtocolType);

        if (proxyOverride != null)
        {
            var overrides = proxyOverride.Split(';');
            var overrides2 = new List<string>();
            foreach (var overrideHost in overrides)
                if (overrideHost == "<-loopback>")
                    BypassLoopback = true;
                else if (overrideHost == "<local>")
                    BypassOnLocal = true;
                else
                    overrides2.Add(BypassStringEscape(overrideHost));

            if (overrides2.Count > 0) BypassList = overrides2.ToArray();

            Proxies = GetSystemProxyValues(proxyServer).ToDictionary(x => x.ProtocolType);
        }
    }

    internal bool? AutoDetect { get; }

    internal string? AutoConfigUrl { get; }

    internal int? ProxyEnable { get; }

    internal string? ProxyServer { get; }

    internal string? ProxyOverride { get; }

    internal bool BypassLoopback { get; }

    internal bool BypassOnLocal { get; }

    internal Dictionary<ProxyProtocolType, HttpSystemProxyValue>? Proxies { get; }

    internal string[]? BypassList { get; }

    private static string BypassStringEscape(string rawString)
    {
        var match =
            new Regex("^(?<scheme>.*://)?(?<host>[^:]*)(?<port>:[0-9]{1,5})?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Match(rawString);
        string empty1;
        string rawString1;
        string empty2;
        if (match.Success)
        {
            empty1 = match.Groups["scheme"].Value;
            rawString1 = match.Groups["host"].Value;
            empty2 = match.Groups["port"].Value;
        }
        else
        {
            empty1 = string.Empty;
            rawString1 = rawString;
            empty2 = string.Empty;
        }

        var str1 = ConvertRegexReservedChars(empty1);
        var str2 = ConvertRegexReservedChars(rawString1);
        var str3 = ConvertRegexReservedChars(empty2);
        if (str1 == string.Empty) str1 = "(?:.*://)?";

        if (str3 == string.Empty) str3 = "(?::[0-9]{1,5})?";

        return "^" + str1 + str2 + str3 + "$";
    }

    private static string ConvertRegexReservedChars(string rawString)
    {
        if (rawString.Length == 0) return rawString;

        var stringBuilder = new StringBuilder();
        foreach (var ch in rawString)
        {
            if ("#$()+.?[\\^{|".IndexOf(ch) != -1)
                stringBuilder.Append('\\');
            else if (ch == 42) stringBuilder.Append('.');

            stringBuilder.Append(ch);
        }

        return stringBuilder.ToString();
    }

    internal static ProxyProtocolType? ParseProtocolType(string protocolTypeStr)
    {
        if (protocolTypeStr == null) return null;

        ProxyProtocolType? protocolType = null;
        if (protocolTypeStr.Equals(Proxy.ProxyServer.UriSchemeHttp, StringComparison.InvariantCultureIgnoreCase))
            protocolType = ProxyProtocolType.Http;
        else if (protocolTypeStr.Equals(Proxy.ProxyServer.UriSchemeHttps,
                     StringComparison.InvariantCultureIgnoreCase))
            protocolType = ProxyProtocolType.Https;

        return protocolType;
    }

    /// <summary>
    ///     Parse the system proxy setting values
    /// </summary>
    /// <param name="proxyServerValues"></param>
    /// <returns></returns>
    internal static List<HttpSystemProxyValue> GetSystemProxyValues(string? proxyServerValues)
    {
        var result = new List<HttpSystemProxyValue>();

        if (string.IsNullOrWhiteSpace(proxyServerValues)) return result;

        var proxyValues = proxyServerValues!.Split(';');

        if (proxyValues.Length > 0)
        {
            foreach (var str in proxyValues)
            {
                var proxyValue = ParseProxyValue(str);
                if (proxyValue != null) result.Add(proxyValue);
            }
        }
        else
        {
            var parsedValue = ParseProxyValue(proxyServerValues);
            if (parsedValue != null) result.Add(parsedValue);
        }

        return result;
    }

    /// <summary>
    ///     Parses the system proxy setting string
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    private static HttpSystemProxyValue? ParseProxyValue(string value)
    {
        var tmp = Regex.Replace(value, @"\s+", " ").Trim();

        var equalsIndex = tmp.IndexOf("=", StringComparison.InvariantCulture);
        if (equalsIndex >= 0)
        {
            var protocolTypeStr = tmp.Substring(0, equalsIndex);
            var protocolType = ParseProtocolType(protocolTypeStr);

            if (protocolType.HasValue)
            {
                var endPointParts = tmp.Substring(equalsIndex + 1).Split(':');
                return new HttpSystemProxyValue(endPointParts[0], int.Parse(endPointParts[1]), protocolType.Value);
            }
        }

        return null;
    }
}ParseOptions.0.jsonÅ-
TD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Helpers\RunTime.csì,using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Run time helpers
/// </summary>
public static class RunTime
{
    private static readonly Lazy<bool> isRunningOnMono = new(() => Type.GetType("Mono.Runtime") != null);

#if NETFRAMEWORK
    /// <summary>
    ///     cache for Windows platform check
    /// </summary>
    /// <returns></returns>
    private static bool IsRunningOnWindows => true;

    /// <summary>
    ///     cache for mono runtime check
    /// </summary>
    /// <returns></returns>
    private static bool IsRunningOnLinux => false;

    /// <summary>
    ///     cache for mac runtime check
    /// </summary>
    /// <returns></returns>
    private static bool IsRunningOnMac => false;
#else
        /// <summary>
        /// cache for Windows platform check
        /// </summary>
        /// <returns></returns>
        private static bool IsRunningOnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>
        ///     cache for mono runtime check
        /// </summary>
        /// <returns></returns>
        private static bool IsRunningOnLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        /// <summary>
        ///     cache for mac runtime check
        /// </summary>
        /// <returns></returns>
        private static bool IsRunningOnMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
#endif

    /// <summary>
    ///     Is running on Mono?
    /// </summary>
    internal static bool IsRunningOnMono => isRunningOnMono.Value;

    public static bool IsLinux => IsRunningOnLinux;

#if !NETFRAMEWORK
    [SupportedOSPlatformGuard("windows")]
#endif
    public static bool IsWindows => IsRunningOnWindows;

#if !NETFRAMEWORK
    [SupportedOSPlatformGuard("windows")]
#endif
    public static bool IsUwpOnWindows => IsWindows && UwpHelper.IsRunningAsUwp();

    public static bool IsMac => IsRunningOnMac;

    private static bool? _isSocketReuseAvailable;

    /// <summary>
    ///     Is socket reuse available to use?
    /// </summary>
    public static bool IsSocketReuseAvailable()
    {
        // use the cached value if we have one
        if (_isSocketReuseAvailable != null)
            return _isSocketReuseAvailable.Value;

        try
        {
            if (IsWindows)
            {
                // since we are on windows just return true
                // store the result in our static object so we don't have to be bothered going through all this more than once
                _isSocketReuseAvailable = true;
                return true;
            }

            // get the currently running framework name and version (EX: .NETFramework,Version=v4.5.1) (Ex: .NETCoreApp,Version=v2.0)
            var ver = Assembly.GetEntryAssembly()?.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;

            if (ver == null)
                return false; // play it safe if we can not figure out what the framework is

            // make sure we are on .NETCoreApp
            ver = ver.ToLower(); // make everything lowercase to simplify comparison
            if (ver.Contains(".netcoreapp"))
            {
                var versionString = ver.Replace(".netcoreapp,version=v", "");
                var versionArr = versionString.Split('.');
                var majorVersion = Convert.ToInt32(versionArr[0]);

                var result = majorVersion >= 3; // version 3 and up supports socket reuse

                // store the result in our static object so we don't have to be bothered going through all this more than once
                _isSocketReuseAvailable = result;
                return result;
            }

            // store the result in our static object so we don't have to be bothered going through all this more than once
            _isSocketReuseAvailable = false;
            return false;
        }
        catch
        {
            // store the result in our static object so we don't have to be bothered going through all this more than once
            _isSocketReuseAvailable = false;
            return false;
        }
    }

    // https://github.com/qmatteoq/DesktopBridgeHelpers/blob/master/DesktopBridge.Helpers/Helpers.cs
    private class UwpHelper
    {
        private const long AppmodelErrorNoPackage = 15700L;

        private static bool IsWindows7OrLower
        {
            get
            {
                var versionMajor = Environment.OSVersion.Version.Major;
                var versionMinor = Environment.OSVersion.Version.Minor;
                var version = versionMajor + (double)versionMinor / 10;
                return version <= 6.1;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetCurrentPackageFullName(ref int packageFullNameLength,
            StringBuilder packageFullName);

        internal static bool IsRunningAsUwp()
        {
            if (IsWindows7OrLower)
            {
                return false;
            }

            var length = 0;
            var sb = new StringBuilder(0);
            var result = GetCurrentPackageFullName(ref length, sb);

            sb = new StringBuilder(length);
            result = GetCurrentPackageFullName(ref length, sb);

            return result != AppmodelErrorNoPackage;
        }
    }
}ParseOptions.0.jsonÚU
XD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Helpers\SystemProxy.csÄUusing System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Titanium.Web.Proxy.Models;

// Helper classes for setting system proxy settings
namespace Titanium.Web.Proxy.Helpers;

internal class HttpSystemProxyValue
{
    public HttpSystemProxyValue(string hostName, int port, ProxyProtocolType protocolType)
    {
        HostName = hostName;
        Port = port;
        ProtocolType = protocolType;
    }

    internal string HostName { get; }

    internal int Port { get; }

    internal ProxyProtocolType ProtocolType { get; }

    public override string ToString()
    {
        string protocol;
        switch (ProtocolType)
        {
            case ProxyProtocolType.Http:
                protocol = ProxyServer.UriSchemeHttp;
                break;
            case ProxyProtocolType.Https:
                protocol = ProxyServer.UriSchemeHttps;
                break;
            default:
                throw new Exception("Unsupported protocol type");
        }

        return $"{protocol}={HostName}:{Port}";
    }
}

/// <summary>
///     Manage system proxy settings
/// </summary>
[SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1402:FileMayOnlyContainASingleType",
    Justification = "Reviewed.")]
#if !NETFRAMEWORK
[SupportedOSPlatform("windows")]
#endif
internal class SystemProxyManager
{
    private const string RegKeyInternetSettings = "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";
    private const string RegAutoConfigUrl = "AutoConfigURL";
    private const string RegProxyEnable = "ProxyEnable";
    private const string RegProxyServer = "ProxyServer";
    private const string RegProxyOverride = "ProxyOverride";

    internal const int InternetOptionSettingsChanged = 39;
    internal const int InternetOptionRefresh = 37;

    private ProxyInfo? originalValues;

    public SystemProxyManager()
    {
        AppDomain.CurrentDomain.ProcessExit += (o, args) => RestoreOriginalSettings();
        if (Environment.UserInteractive && NativeMethods.GetConsoleWindow() != IntPtr.Zero)
        {
            var handler = new NativeMethods.ConsoleEventDelegate(eventType =>
            {
                if (eventType != 2) return false;

                RestoreOriginalSettings();
                return false;
            });
            NativeMethods.Handler = handler;

            // On Console exit make sure we also exit the proxy
            NativeMethods.SetConsoleCtrlHandler(handler, true);
        }
    }

    /// <summary>
    ///     Set the HTTP and/or HTTPS proxy server for current machine
    /// </summary>
    /// <param name="hostname"></param>
    /// <param name="port"></param>
    /// <param name="protocolType"></param>
    internal void SetProxy(string hostname, int port, ProxyProtocolType protocolType)
    {
        SetProxy(hostname, port, protocolType, null);
    }

    /// <summary>
    ///     Set the HTTP and/or HTTPS proxy server for current machine.
    /// </summary>
    /// <param name="hostname"></param>
    /// <param name="port"></param>
    /// <param name="protocolType"></param>
    /// <param name="proxyOverride">
    ///     The proxy bypass list to set, or <see langword="null"/> to preserve the current list.
    /// </param>
    internal void SetProxy(string hostname, int port, ProxyProtocolType protocolType, string? proxyOverride)
    {
        using (var reg = OpenInternetSettingsKey())
        {
            if (reg == null) return;

            SaveOriginalProxyConfiguration(reg);
            PrepareRegistry(reg);

            var existingContent = reg.GetValue(RegProxyServer) as string;
            var existingSystemProxyValues = ProxyInfo.GetSystemProxyValues(existingContent);
            existingSystemProxyValues.RemoveAll(x => (protocolType & x.ProtocolType) != 0);
            if ((protocolType & ProxyProtocolType.Http) != 0)
                existingSystemProxyValues.Add(new HttpSystemProxyValue(hostname, port, ProxyProtocolType.Http));

            if ((protocolType & ProxyProtocolType.Https) != 0)
                existingSystemProxyValues.Add(new HttpSystemProxyValue(hostname, port, ProxyProtocolType.Https));

            reg.DeleteValue(RegAutoConfigUrl, false);
            reg.SetValue(RegProxyEnable, 1);
            reg.SetValue(RegProxyServer,
                string.Join(";", existingSystemProxyValues.Select(x => x.ToString()).ToArray()));
            if (proxyOverride != null) reg.SetValue(RegProxyOverride, proxyOverride);

            Refresh();
        }
    }

    /// <summary>
    ///     Remove the HTTP and/or HTTPS proxy setting from current machine
    /// </summary>
    internal void RemoveProxy(ProxyProtocolType protocolType, bool saveOriginalConfig = true)
    {
        using (var reg = OpenInternetSettingsKey())
        {
            if (reg == null) return;

            if (saveOriginalConfig) SaveOriginalProxyConfiguration(reg);

            if (reg.GetValue(RegProxyServer) != null)
            {
                var existingContent = reg.GetValue(RegProxyServer) as string;

                var existingSystemProxyValues = ProxyInfo.GetSystemProxyValues(existingContent);
                existingSystemProxyValues.RemoveAll(x => (protocolType & x.ProtocolType) != 0);

                if (existingSystemProxyValues.Count != 0)
                {
                    reg.SetValue(RegProxyEnable, 1);
                    reg.SetValue(RegProxyServer,
                        string.Join(";", existingSystemProxyValues.Select(x => x.ToString()).ToArray()));
                }
                else
                {
                    reg.SetValue(RegProxyEnable, 0);
                    reg.SetValue(RegProxyServer, string.Empty);
                }
            }

            Refresh();
        }
    }

    /// <summary>
    ///     Removes all types of proxy settings (both http and https)
    /// </summary>
    internal void DisableAllProxy()
    {
        using (var reg = OpenInternetSettingsKey())
        {
            if (reg == null) return;

            SaveOriginalProxyConfiguration(reg);

            reg.SetValue(RegProxyEnable, 0);
            reg.SetValue(RegProxyServer, string.Empty);

            Refresh();
        }
    }

    internal void SetAutoProxyUrl(string url)
    {
        using (var reg = OpenInternetSettingsKey())
        {
            if (reg == null) return;

            SaveOriginalProxyConfiguration(reg);
            reg.SetValue(RegAutoConfigUrl, url);
            Refresh();
        }
    }

    internal void SetProxyOverride(string proxyOverride)
    {
        using (var reg = OpenInternetSettingsKey())
        {
            if (reg == null) return;

            SaveOriginalProxyConfiguration(reg);
            reg.SetValue(RegProxyOverride, proxyOverride);
            Refresh();
        }
    }

    internal void RestoreOriginalSettings()
    {
        var ov = originalValues;
        if (ov == null) return;

        using (var reg = Registry.CurrentUser.OpenSubKey(RegKeyInternetSettings, true))
        {
            if (reg == null) return;

            if (ov.AutoConfigUrl != null)
                reg.SetValue(RegAutoConfigUrl, ov.AutoConfigUrl);
            else
                reg.DeleteValue(RegAutoConfigUrl, false);

            if (ov.ProxyEnable.HasValue)
                reg.SetValue(RegProxyEnable, ov.ProxyEnable.Value);
            else
                reg.DeleteValue(RegProxyEnable, false);

            if (ov.ProxyServer != null)
                reg.SetValue(RegProxyServer, ov.ProxyServer);
            else
                reg.DeleteValue(RegProxyServer, false);

            if (ov.ProxyOverride != null)
                reg.SetValue(RegProxyOverride, ov.ProxyOverride);
            else
                reg.DeleteValue(RegProxyOverride, false);

            // This should not be needed, but sometimes the values are not stored into the registry
            // at system shutdown without flushing.
            reg.Flush();

            originalValues = null;

            const int smShuttingdown = 0x2000;
            var windows7Version = new Version(6, 1);
            if (Environment.OSVersion.Version > windows7Version ||
                NativeMethods.GetSystemMetrics(smShuttingdown) == 0)
                // Do not call refresh() in Windows 7 or earlier at system shutdown.
                // SetInternetOption in the refresh method re-enables ProxyEnable registry value
                // in Windows 7 or earlier at system shutdown.
                Refresh();
        }
    }

    internal ProxyInfo? GetProxyInfoFromRegistry()
    {
        using (var reg = OpenInternetSettingsKey())
        {
            if (reg == null) return null;

            return GetProxyInfoFromRegistry(reg);
        }
    }

    private ProxyInfo GetProxyInfoFromRegistry(RegistryKey reg)
    {
        var proxyEnableValue = reg.GetValue(RegProxyEnable);
        var pi = new ProxyInfo(null,
            reg.GetValue(RegAutoConfigUrl) as string,
            proxyEnableValue is int proxyEnable ? proxyEnable : null,
            reg.GetValue(RegProxyServer) as string,
            reg.GetValue(RegProxyOverride) as string);

        return pi;
    }

    private void SaveOriginalProxyConfiguration(RegistryKey reg)
    {
        if (originalValues != null) return;

        originalValues = GetProxyInfoFromRegistry(reg);
    }

    /// <summary>
    ///     Prepares the proxy server registry (create empty values if they don't exist)
    /// </summary>
    /// <param name="reg"></param>
    private static void PrepareRegistry(RegistryKey reg)
    {
        if (reg.GetValue(RegProxyEnable) == null) reg.SetValue(RegProxyEnable, 0);

        if (reg.GetValue(RegProxyServer) == null ||
            reg.GetValue(RegProxyEnable) is int proxyEnable && proxyEnable == 0)
            reg.SetValue(RegProxyServer, string.Empty);
    }

    /// <summary>
    ///     Refresh the settings so that the system know about a change in proxy setting
    /// </summary>
    private static void Refresh()
    {
        NativeMethods.InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        NativeMethods.InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    /// <summary>
    ///     Opens the registry key with the internet settings
    /// </summary>
    private static RegistryKey? OpenInternetSettingsKey()
    {
        return Registry.CurrentUser?.OpenSubKey(RegKeyInternetSettings, true);
    }
}ParseOptions.0.jsonä*
VD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Helpers\TcpHelper.csö)using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.Helpers;

internal class TcpHelper
{
    /// <summary>
    ///     Gets the process id by local port number.
    /// </summary>
    /// <returns>Process id.</returns>
    internal static unsafe int GetProcessIdByLocalPort(AddressFamily addressFamily, int localPort)
    {
        var tcpTable = IntPtr.Zero;
        var tcpTableLength = 0;

        var addressFamilyValue =
            addressFamily == AddressFamily.InterNetwork ? NativeMethods.AfInet : NativeMethods.AfInet6;
        const int allPid = (int)NativeMethods.TcpTableType.OwnerPidAll;

        if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, false, addressFamilyValue, allPid, 0) != 0)
            try
            {
                tcpTable = Marshal.AllocHGlobal(tcpTableLength);
                if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, true, addressFamilyValue, allPid,
                        0) == 0)
                {
                    var rowCount = *(int*)tcpTable;
                    var portInNetworkByteOrder = ToNetworkByteOrder((uint)localPort);

                    if (addressFamily == AddressFamily.InterNetwork)
                    {
                        var rowPtr = (NativeMethods.TcpRow*)(tcpTable + 4);

                        for (var i = 0; i < rowCount; ++i)
                        {
                            if (rowPtr->localPort == portInNetworkByteOrder) return rowPtr->owningPid;

                            rowPtr++;
                        }
                    }
                    else
                    {
                        var rowPtr = (NativeMethods.Tcp6Row*)(tcpTable + 4);

                        for (var i = 0; i < rowCount; ++i)
                        {
                            if (rowPtr->localPort == portInNetworkByteOrder) return rowPtr->owningPid;

                            rowPtr++;
                        }
                    }
                }
            }
            finally
            {
                if (tcpTable != IntPtr.Zero) Marshal.FreeHGlobal(tcpTable);
            }

        return 0;
    }

    /// <summary>
    ///     Converts 32-bit integer from native byte order (little-endian)
    ///     to network byte order for port,
    ///     switches 0th and 1st bytes, and 2nd and 3rd bytes
    /// </summary>
    /// <param name="port"></param>
    /// <returns></returns>
    private static uint ToNetworkByteOrder(uint port)
    {
        return ((port >> 8) & 0x00FF00FFu) | ((port << 8) & 0xFF00FF00u);
    }

    /// <summary>
    ///     relays the input clientStream to the server at the specified host name and port with the given httpCmd and headers
    ///     as prefix
    ///     Useful for websocket requests
    ///     Task-based Asynchronous Pattern
    /// </summary>
    /// <param name="clientStream"></param>
    /// <param name="serverStream"></param>
    /// <param name="bufferPool"></param>
    /// <param name="onDataSend"></param>
    /// <param name="onDataReceive"></param>
    /// <param name="cancellationTokenSource"></param>
    /// <returns></returns>
    private static async Task SendRawTap(Stream clientStream, Stream serverStream, IBufferPool bufferPool,
        Action<byte[], int, int>? onDataSend, Action<byte[], int, int>? onDataReceive,
        CancellationTokenSource cancellationTokenSource)
    {
        // Now async relay all server=>client & client=>server data
        var sendRelay =
            clientStream.CopyToAsync(serverStream, onDataSend, bufferPool, cancellationTokenSource.Token);
        var receiveRelay =
            serverStream.CopyToAsync(clientStream, onDataReceive, bufferPool, cancellationTokenSource.Token);

        await Task.WhenAny(sendRelay, receiveRelay);
        cancellationTokenSource.Cancel();

        await Task.WhenAll(sendRelay, receiveRelay);
    }

    /// <summary>
    ///     relays the input clientStream to the server at the specified host name and port with the given httpCmd and headers
    ///     as prefix
    ///     Useful for websocket requests
    /// </summary>
    /// <param name="clientStream"></param>
    /// <param name="serverStream"></param>
    /// <param name="bufferPool"></param>
    /// <param name="onDataSend"></param>
    /// <param name="onDataReceive"></param>
    /// <param name="cancellationTokenSource"></param>
    /// <param name="exceptionFunc"></param>
    /// <returns></returns>
    internal static Task SendRaw(Stream clientStream, Stream serverStream, IBufferPool bufferPool,
        Action<byte[], int, int>? onDataSend, Action<byte[], int, int>? onDataReceive,
        CancellationTokenSource cancellationTokenSource,
        ExceptionHandler? exceptionFunc)
    {
        // todo: fix APM mode
        return SendRawTap(clientStream, serverStream, bufferPool, onDataSend, onDataReceive,
            cancellationTokenSource);
    }
}ParseOptions.0.json±'
jD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Helpers\WinHttp\NativeMethods.WinHttp.cs≠&using System;
using System.Runtime.InteropServices;

// Helper classes for setting system proxy settings
namespace Titanium.Web.Proxy.Helpers.WinHttp;

internal class NativeMethods
{
    internal static class WinHttp
    {
        [DllImport("winhttp.dll", SetLastError = true)]
        internal static extern bool WinHttpGetIEProxyConfigForCurrentUser(
            ref WinhttpCurrentUserIeProxyConfig proxyConfig);

        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern WinHttpHandle WinHttpOpen(string? userAgent, AccessType accessType, string? proxyName,
            string? proxyBypass, int dwFlags);

        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool WinHttpSetTimeouts(WinHttpHandle session, int resolveTimeout,
            int connectTimeout, int sendTimeout, int receiveTimeout);

        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool WinHttpGetProxyForUrl(WinHttpHandle session, string url,
            [In] ref WinhttpAutoproxyOptions autoProxyOptions,
            out WinhttpProxyInfo proxyInfo);

        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool WinHttpCloseHandle(IntPtr httpSession);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WinhttpCurrentUserIeProxyConfig
        {
            public bool AutoDetect;
            public IntPtr AutoConfigUrl;
            public IntPtr Proxy;
            public IntPtr ProxyBypass;
        }

        [Flags]
        internal enum AutoProxyFlags
        {
            AutoDetect = 1,
            AutoProxyConfigUrl = 2,
            RunInProcess = 65536,
            RunOutProcessOnly = 131072
        }

        internal enum AccessType
        {
            DefaultProxy = 0,
            NoProxy = 1,
            NamedProxy = 3
        }

        [Flags]
        internal enum AutoDetectType
        {
            None = 0,
            Dhcp = 1,
            DnsA = 2
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WinhttpAutoproxyOptions
        {
            public AutoProxyFlags Flags;
            public AutoDetectType AutoDetectFlags;
            [MarshalAs(UnmanagedType.LPWStr)] public string? AutoConfigUrl;
            private readonly IntPtr lpvReserved;
            private readonly int dwReserved;
            public bool AutoLogonIfChallenged;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WinhttpProxyInfo
        {
            public AccessType AccessType;
            public IntPtr Proxy;
            public IntPtr ProxyBypass;
        }

        internal enum ErrorCodes
        {
            Success = 0,
            OutOfHandles = 12001,
            Timeout = 12002,
            InternalError = 12004,
            InvalidUrl = 12005,
            UnrecognizedScheme = 12006,
            NameNotResolved = 12007,
            InvalidOption = 12009,
            OptionNotSettable = 12011,
            Shutdown = 12012,
            LoginFailure = 12015,
            OperationCancelled = 12017,
            IncorrectHandleType = 12018,
            IncorrectHandleState = 12019,
            CannotConnect = 12029,
            ConnectionError = 12030,
            ResendRequest = 12032,
            SecureCertDateInvalid = 12037,
            SecureCertCnInvalid = 12038,
            AuthCertNeeded = 12044,
            SecureInvalidCa = 12045,
            SecureCertRevFailed = 12057,
            CannotCallBeforeOpen = 12100,
            CannotCallBeforeSend = 12101,
            CannotCallAfterSend = 12102,
            CannotCallAfterOpen = 12103,
            HeaderNotFound = 12150,
            InvalidServerResponse = 12152,
            InvalidHeader = 12153,
            InvalidQueryRequest = 12154,
            HeaderAlreadyExists = 12155,
            RedirectFailed = 12156,
            SecureChannelError = 12157,
            BadAutoProxyScript = 12166,
            UnableToDownloadScript = 12167,
            SecureInvalidCert = 12169,
            SecureCertRevoked = 12170,
            NotInitialized = 12172,
            SecureFailure = 12175,
            AutoProxyServiceError = 12178,
            SecureCertWrongUsage = 12179,
            AudodetectionFailed = 12180,
            HeaderCountExceeded = 12181,
            HeaderSizeOverflow = 12182,
            ChunkedEncodingHeaderSizeOverflow = 12183,
            ResponseDrainOverflow = 12184,
            ClientCertNoPrivateKey = 12185,
            ClientCertNoAccessPrivateKey = 12186
        }
    }
}ParseOptions.0.jsonó
bD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Helpers\WinHttp\WinHttpHandle.csõusing System;
using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Helpers.WinHttp;

internal class WinHttpHandle : SafeHandle
{
    public WinHttpHandle() : base(IntPtr.Zero, true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        return NativeMethods.WinHttp.WinHttpCloseHandle(handle);
    }
}ParseOptions.0.jsonÅd
jD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Helpers\WinHttp\WinHttpWebProxyFinder.cs˝busing System;
using System.Collections.Generic;
using System.Net;
#if NETFRAMEWORK
using System.Runtime.CompilerServices;
#endif
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Helpers.WinHttp;

#if !NETFRAMEWORK
[SupportedOSPlatform("windows")]
#endif
internal sealed class WinHttpWebProxyFinder : IDisposable
{
    private readonly WinHttpHandle? session;
    private bool autoDetectFailed;

    private bool disposed;
    private AutoWebProxyState state;

    public WinHttpWebProxyFinder()
    {
        session = NativeMethods.WinHttp.WinHttpOpen(null, NativeMethods.WinHttp.AccessType.NoProxy, null, null, 0);
        if (session == null || session.IsInvalid)
        {
            var lastWin32Error = GetLastWin32Error();
        }
        else
        {
            var downloadTimeout = 60 * 1000;
            if (NativeMethods.WinHttp.WinHttpSetTimeouts(session, downloadTimeout, downloadTimeout, downloadTimeout,
                    downloadTimeout))
                return;

            var lastWin32Error = GetLastWin32Error();
        }
    }

    public ICredentials? Credentials { get; set; }

    public ProxyInfo? ProxyInfo { get; internal set; }

    public bool BypassLoopback { get; internal set; }

    public bool BypassOnLocal { get; internal set; }

    public Uri? AutomaticConfigurationScript { get; internal set; }

    public bool AutomaticallyDetectSettings { get; internal set; }

    private WebProxy? Proxy { get; set; }

    public void Dispose()
    {
        if (disposed) return;

        if (session == null || session.IsInvalid) return;

        session.Close();

        disposed = true;
    }

    public bool GetAutoProxies(Uri destination, out IList<string>? proxyList)
    {
        proxyList = null;
        if (session == null || session.IsInvalid || state == AutoWebProxyState.UnrecognizedScheme) return false;

        string? proxyListString = null;
        var errorCode = NativeMethods.WinHttp.ErrorCodes.AudodetectionFailed;
        if (AutomaticallyDetectSettings && !autoDetectFailed)
        {
            errorCode = (NativeMethods.WinHttp.ErrorCodes)GetAutoProxies(destination, null, out proxyListString);
            autoDetectFailed = IsErrorFatalForAutoDetect(errorCode);
            if (errorCode == NativeMethods.WinHttp.ErrorCodes.UnrecognizedScheme)
            {
                state = AutoWebProxyState.UnrecognizedScheme;
                return false;
            }
        }

        if (AutomaticConfigurationScript != null && IsRecoverableAutoProxyError(errorCode))
            errorCode = (NativeMethods.WinHttp.ErrorCodes)GetAutoProxies(destination, AutomaticConfigurationScript,
                out proxyListString);

        state = GetStateFromErrorCode(errorCode);
        if (state != AutoWebProxyState.Completed) return false;

        if (!string.IsNullOrEmpty(proxyListString))
        {
            proxyListString = RemoveWhitespaces(proxyListString!);
            proxyList = proxyListString.Split(';');
        }

        return true;
    }

    public IExternalProxy? GetProxy(Uri destination)
    {
        // Known limitations of system-proxy resolution:
        //  - Only the first proxy returned by the PAC/auto-config script is used; additional
        //    fallback proxies in the list are ignored.
        //  - The static system bypass list is not re-applied to PAC results here (the PAC script
        //    itself is expected to return DIRECT for bypassed hosts).
        if (GetAutoProxies(destination, out var proxies))
        {
            if (proxies == null) return null;

            var proxyStr = proxies[0];
            var port = 80;
            if (proxyStr.Contains(":"))
            {
                var parts = proxyStr.Split(new[] { ':' }, 2);
                proxyStr = parts[0];
                port = int.Parse(parts[1]);
            }

            // Authenticate to the system proxy with the current user's default credentials via
            // integrated auth (NTLM/Negotiate). This only takes effect if the proxy issues a 407
            // challenge, and mirrors how Windows authenticates to auto-detected proxies. Explicit
            // Basic credentials cannot be recovered from WinHTTP auto-config, so they are not set.
            var systemProxy = new ExternalProxy(proxyStr, port) { UseDefaultCredentials = true };

            return systemProxy;
        }

        if (Proxy?.IsBypassed(destination) == true) return null;

        var protocolType = ProxyInfo.ParseProtocolType(destination.Scheme);
        if (protocolType.HasValue)
        {
            HttpSystemProxyValue? value = null;
            if (ProxyInfo?.Proxies?.TryGetValue(protocolType.Value, out value) == true)
            {
                var systemProxy = new ExternalProxy(value!.HostName, value.Port);
                return systemProxy;
            }
        }

        return null;
    }

    public void LoadFromIe()
    {
        var pi = GetProxyInfo();
        ProxyInfo = pi;
        AutomaticallyDetectSettings = pi.AutoDetect == true;
        AutomaticConfigurationScript = pi.AutoConfigUrl == null ? null : new Uri(pi.AutoConfigUrl);
        BypassLoopback = pi.BypassLoopback;
        BypassOnLocal = pi.BypassOnLocal;
        Proxy = new WebProxy(new Uri("http://localhost"), BypassOnLocal, pi.BypassList);
    }

    internal void UsePacFile(Uri upstreamProxyConfigurationScript)
    {
        AutomaticallyDetectSettings = true;
        AutomaticConfigurationScript = upstreamProxyConfigurationScript;
        BypassLoopback = true;
        BypassOnLocal = false;
        Proxy = new WebProxy(new Uri("http://localhost"), BypassOnLocal);
    }

    private ProxyInfo GetProxyInfo()
    {
        var proxyConfig = new NativeMethods.WinHttp.WinhttpCurrentUserIeProxyConfig();
#if NETFRAMEWORK
        RuntimeHelpers.PrepareConstrainedRegions();
#endif
        try
        {
            ProxyInfo result;
            if (NativeMethods.WinHttp.WinHttpGetIEProxyConfigForCurrentUser(ref proxyConfig))
            {
                result = new ProxyInfo(
                    proxyConfig.AutoDetect,
                    Marshal.PtrToStringUni(proxyConfig.AutoConfigUrl),
                    null,
                    Marshal.PtrToStringUni(proxyConfig.Proxy),
                    Marshal.PtrToStringUni(proxyConfig.ProxyBypass));
            }
            else
            {
                if (Marshal.GetLastWin32Error() == 8) throw new OutOfMemoryException();

                result = new ProxyInfo(true, null, null, null, null);
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(proxyConfig.Proxy);
            Marshal.FreeHGlobal(proxyConfig.ProxyBypass);
            Marshal.FreeHGlobal(proxyConfig.AutoConfigUrl);
        }
    }

    public void Reset()
    {
        state = AutoWebProxyState.Uninitialized;
        autoDetectFailed = false;
    }

    private int GetAutoProxies(Uri destination, Uri? scriptLocation, out string? proxyListString)
    {
        var num = 0;
        var autoProxyOptions = new NativeMethods.WinHttp.WinhttpAutoproxyOptions();
        autoProxyOptions.AutoLogonIfChallenged = false;
        if (scriptLocation == null)
        {
            autoProxyOptions.Flags = NativeMethods.WinHttp.AutoProxyFlags.AutoDetect;
            autoProxyOptions.AutoConfigUrl = null;
            autoProxyOptions.AutoDetectFlags =
                NativeMethods.WinHttp.AutoDetectType.Dhcp | NativeMethods.WinHttp.AutoDetectType.DnsA;
        }
        else
        {
            autoProxyOptions.Flags = NativeMethods.WinHttp.AutoProxyFlags.AutoProxyConfigUrl;
            autoProxyOptions.AutoConfigUrl = scriptLocation.ToString();
            autoProxyOptions.AutoDetectFlags = NativeMethods.WinHttp.AutoDetectType.None;
        }

        if (!WinHttpGetProxyForUrl(destination.ToString(), ref autoProxyOptions, out proxyListString))
        {
            num = GetLastWin32Error();

            if (num == (int)NativeMethods.WinHttp.ErrorCodes.LoginFailure && Credentials != null)
            {
                autoProxyOptions.AutoLogonIfChallenged = true;
                if (!WinHttpGetProxyForUrl(destination.ToString(), ref autoProxyOptions, out proxyListString))
                    num = GetLastWin32Error();
            }
        }

        return num;
    }

    private bool WinHttpGetProxyForUrl(string destination,
        ref NativeMethods.WinHttp.WinhttpAutoproxyOptions autoProxyOptions, out string? proxyListString)
    {
        proxyListString = null;
        var currentSession = session;
        if (currentSession == null || currentSession.IsInvalid) return false;

        bool flag;
        var proxyInfo = new NativeMethods.WinHttp.WinhttpProxyInfo();
#if NETFRAMEWORK
        RuntimeHelpers.PrepareConstrainedRegions();
#endif
        try
        {
            flag = NativeMethods.WinHttp.WinHttpGetProxyForUrl(currentSession, destination, ref autoProxyOptions,
                out proxyInfo);
            if (flag) proxyListString = Marshal.PtrToStringUni(proxyInfo.Proxy);
        }
        finally
        {
            Marshal.FreeHGlobal(proxyInfo.Proxy);
            Marshal.FreeHGlobal(proxyInfo.ProxyBypass);
        }

        return flag;
    }

    private static int GetLastWin32Error()
    {
        var lastWin32Error = Marshal.GetLastWin32Error();
        if (lastWin32Error == 8) throw new OutOfMemoryException();

        return lastWin32Error;
    }

    private static bool IsRecoverableAutoProxyError(NativeMethods.WinHttp.ErrorCodes errorCode)
    {
        switch (errorCode)
        {
            case NativeMethods.WinHttp.ErrorCodes.AutoProxyServiceError:
            case NativeMethods.WinHttp.ErrorCodes.AudodetectionFailed:
            case NativeMethods.WinHttp.ErrorCodes.BadAutoProxyScript:
            case NativeMethods.WinHttp.ErrorCodes.UnableToDownloadScript:
            case NativeMethods.WinHttp.ErrorCodes.LoginFailure:
            case NativeMethods.WinHttp.ErrorCodes.OperationCancelled:
            case NativeMethods.WinHttp.ErrorCodes.Timeout:
            case NativeMethods.WinHttp.ErrorCodes.UnrecognizedScheme:
                return true;
            default:
                return false;
        }
    }

    private static AutoWebProxyState GetStateFromErrorCode(NativeMethods.WinHttp.ErrorCodes errorCode)
    {
        if (errorCode == 0L) return AutoWebProxyState.Completed;

        switch (errorCode)
        {
            case NativeMethods.WinHttp.ErrorCodes.UnableToDownloadScript:
                return AutoWebProxyState.DownloadFailure;
            case NativeMethods.WinHttp.ErrorCodes.AutoProxyServiceError:
            case NativeMethods.WinHttp.ErrorCodes.InvalidUrl:
            case NativeMethods.WinHttp.ErrorCodes.BadAutoProxyScript:
                return AutoWebProxyState.Completed;
            case NativeMethods.WinHttp.ErrorCodes.AudodetectionFailed:
                return AutoWebProxyState.DiscoveryFailure;
            case NativeMethods.WinHttp.ErrorCodes.UnrecognizedScheme:
                return AutoWebProxyState.UnrecognizedScheme;
            default:
                return AutoWebProxyState.CompilationFailure;
        }
    }

    private static string RemoveWhitespaces(string value)
    {
        var stringBuilder = new StringBuilder();
        foreach (var c in value)
            if (!char.IsWhiteSpace(c))
                stringBuilder.Append(c);

        return stringBuilder.ToString();
    }

    private static bool IsErrorFatalForAutoDetect(NativeMethods.WinHttp.ErrorCodes errorCode)
    {
        switch (errorCode)
        {
            case NativeMethods.WinHttp.ErrorCodes.BadAutoProxyScript:
            case NativeMethods.WinHttp.ErrorCodes.AutoProxyServiceError:
            case NativeMethods.WinHttp.ErrorCodes.Success:
            case NativeMethods.WinHttp.ErrorCodes.InvalidUrl:
                return false;
            default:
                return true;
        }
    }

    private enum AutoWebProxyState
    {
        Uninitialized,
        DiscoveryFailure,
        DownloadFailure,
        CompilationFailure,
        UnrecognizedScheme,
        Completed
    }
}ParseOptions.0.jsonìØ
XD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http2\Hpack\Decoder.cs†Æ/*
 * Copyright 2014 Twitter, Inc
 * This file is a derivative work modified by Ringo Leese
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.IO;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http2.Hpack;

internal class Decoder
{
    private readonly DynamicTable dynamicTable;

    private readonly int maxHeaderSize;
    private int encoderMaxDynamicTableSize;

    private long headerSize;
    private bool huffmanEncoded;
    private int index;
    private HpackUtil.IndexType indexType;
    private int maxDynamicTableSize;
    private bool maxDynamicTableSizeChangeRequired;
    private ByteString name = ByteString.Empty;
    private int nameLength;
    private int skipLength;
    private State state;
    private int valueLength;

    /// <summary>
    ///     Initializes a new instance of the <see cref="Decoder" /> class.
    /// </summary>
    /// <param name="maxHeaderSize">Max header size.</param>
    /// <param name="maxHeaderTableSize">Max header table size.</param>
    public Decoder(int maxHeaderSize, int maxHeaderTableSize)
    {
        dynamicTable = new DynamicTable(maxHeaderTableSize);
        this.maxHeaderSize = maxHeaderSize;
        maxDynamicTableSize = maxHeaderTableSize;
        encoderMaxDynamicTableSize = maxHeaderTableSize;
        maxDynamicTableSizeChangeRequired = false;
        Reset();
    }

    private void Reset()
    {
        headerSize = 0;
        state = State.ReadHeaderRepresentation;
        indexType = HpackUtil.IndexType.None;
    }

    /// <summary>
    ///     Decode the header block into header fields.
    /// </summary>
    /// <param name="input">Input.</param>
    /// <param name="headerListener">Header listener.</param>
    public void Decode(BinaryReader input, IHeaderListener headerListener)
    {
        while (input.BaseStream.Length - input.BaseStream.Position > 0)
            switch (state)
            {
                case State.ReadHeaderRepresentation:
                    var b = input.ReadSByte();
                    if (maxDynamicTableSizeChangeRequired && (b & 0xE0) != 0x20)
                        // Encoder MUST signal maximum dynamic table size change
                        throw new IOException("max dynamic table size change required");

                    if (b < 0)
                    {
                        // Indexed Header Field
                        index = b & 0x7F;
                        if (index == 0)
                            throw new IOException("illegal index value (" + index + ")");
                        if (index == 0x7F)
                            state = State.ReadIndexedHeader;
                        else
                            IndexHeader(index, headerListener);
                    }
                    else if ((b & 0x40) == 0x40)
                    {
                        // Literal Header Field with Incremental Indexing
                        indexType = HpackUtil.IndexType.Incremental;
                        index = b & 0x3F;
                        if (index == 0)
                        {
                            state = State.ReadLiteralHeaderNameLengthPrefix;
                        }
                        else if (index == 0x3F)
                        {
                            state = State.ReadIndexedHeaderName;
                        }
                        else
                        {
                            // Index was stored as the prefix
                            ReadName(index);
                            state = State.ReadLiteralHeaderValueLengthPrefix;
                        }
                    }
                    else if ((b & 0x20) == 0x20)
                    {
                        // Dynamic Table Size Update
                        index = b & 0x1F;
                        if (index == 0x1F)
                        {
                            state = State.ReadMaxDynamicTableSize;
                        }
                        else
                        {
                            SetDynamicTableSize(index);
                            state = State.ReadHeaderRepresentation;
                        }
                    }
                    else
                    {
                        // Literal Header Field without Indexing / never Indexed
                        indexType = (b & 0x10) == 0x10 ? HpackUtil.IndexType.Never : HpackUtil.IndexType.None;
                        index = b & 0x0F;
                        if (index == 0)
                        {
                            state = State.ReadLiteralHeaderNameLengthPrefix;
                        }
                        else if (index == 0x0F)
                        {
                            state = State.ReadIndexedHeaderName;
                        }
                        else
                        {
                            // Index was stored as the prefix
                            ReadName(index);
                            state = State.ReadLiteralHeaderValueLengthPrefix;
                        }
                    }

                    break;

                case State.ReadMaxDynamicTableSize:
                    var maxSize = DecodeUle128(input);
                    if (maxSize == -1) return;

                    // Check for numerical overflow
                    if (maxSize > int.MaxValue - index) throw new IOException("decompression failure");

                    SetDynamicTableSize(index + maxSize);
                    state = State.ReadHeaderRepresentation;
                    break;

                case State.ReadIndexedHeader:
                    var headerIndex = DecodeUle128(input);
                    if (headerIndex == -1) return;

                    // Check for numerical overflow
                    if (headerIndex > int.MaxValue - index) throw new IOException("decompression failure");

                    IndexHeader(index + headerIndex, headerListener);
                    state = State.ReadHeaderRepresentation;
                    break;

                case State.ReadIndexedHeaderName:
                    // Header Name matches an entry in the Header Table
                    var nameIndex = DecodeUle128(input);
                    if (nameIndex == -1) return;

                    // Check for numerical overflow
                    if (nameIndex > int.MaxValue - index) throw new IOException("decompression failure");

                    ReadName(index + nameIndex);
                    state = State.ReadLiteralHeaderValueLengthPrefix;
                    break;

                case State.ReadLiteralHeaderNameLengthPrefix:
                    b = input.ReadSByte();
                    huffmanEncoded = (b & 0x80) == 0x80;
                    index = b & 0x7F;
                    if (index == 0x7f)
                    {
                        state = State.ReadLiteralHeaderNameLength;
                    }
                    else
                    {
                        nameLength = index;

                        // Disallow empty names -- they cannot be represented in HTTP/1.x
                        if (nameLength == 0) throw new IOException("decompression failure");

                        // Check name length against max header size
                        if (ExceedsMaxHeaderSize(nameLength))
                        {
                            if (indexType == HpackUtil.IndexType.None)
                            {
                                // Name is unused so skip bytes
                                name = ByteString.Empty;
                                skipLength = nameLength;
                                state = State.SkipLiteralHeaderName;
                                break;
                            }

                            // Check name length against max dynamic table size
                            if (nameLength + HttpHeader.HttpHeaderOverhead > dynamicTable.Capacity)
                            {
                                dynamicTable.Clear();
                                name = Array.Empty<byte>();
                                skipLength = nameLength;
                                state = State.SkipLiteralHeaderName;
                                break;
                            }
                        }

                        state = State.ReadLiteralHeaderName;
                    }

                    break;

                case State.ReadLiteralHeaderNameLength:
                    // Header Name is a Literal String
                    nameLength = DecodeUle128(input);
                    if (nameLength == -1) return;

                    // Check for numerical overflow
                    if (nameLength > int.MaxValue - index) throw new IOException("decompression failure");

                    nameLength += index;

                    // Check name length against max header size
                    if (ExceedsMaxHeaderSize(nameLength))
                    {
                        if (indexType == HpackUtil.IndexType.None)
                        {
                            // Name is unused so skip bytes
                            name = ByteString.Empty;
                            skipLength = nameLength;
                            state = State.SkipLiteralHeaderName;
                            break;
                        }

                        // Check name length against max dynamic table size
                        if (nameLength + HttpHeader.HttpHeaderOverhead > dynamicTable.Capacity)
                        {
                            dynamicTable.Clear();
                            name = ByteString.Empty;
                            skipLength = nameLength;
                            state = State.SkipLiteralHeaderName;
                            break;
                        }
                    }

                    state = State.ReadLiteralHeaderName;
                    break;

                case State.ReadLiteralHeaderName:
                    // Wait until entire name is readable
                    if (input.BaseStream.Length - input.BaseStream.Position < nameLength) return;

                    name = ReadStringLiteral(input, nameLength);
                    state = State.ReadLiteralHeaderValueLengthPrefix;
                    break;

                case State.SkipLiteralHeaderName:

                    skipLength -= (int)input.BaseStream.Seek(skipLength, SeekOrigin.Current);
                    if (skipLength < 0) skipLength = 0;

                    if (skipLength == 0) state = State.ReadLiteralHeaderValueLengthPrefix;

                    break;

                case State.ReadLiteralHeaderValueLengthPrefix:
                    b = input.ReadSByte();
                    huffmanEncoded = (b & 0x80) == 0x80;
                    index = b & 0x7F;
                    if (index == 0x7f)
                    {
                        state = State.ReadLiteralHeaderValueLength;
                    }
                    else
                    {
                        valueLength = index;

                        // Check new header size against max header size
                        var newHeaderSize1 = (long)nameLength + valueLength;
                        if (ExceedsMaxHeaderSize(newHeaderSize1))
                        {
                            // truncation will be reported during endHeaderBlock
                            headerSize = maxHeaderSize + 1;

                            if (indexType == HpackUtil.IndexType.None)
                            {
                                // Value is unused so skip bytes
                                state = State.SkipLiteralHeaderValue;
                                break;
                            }

                            // Check new header size against max dynamic table size
                            if (newHeaderSize1 + HttpHeader.HttpHeaderOverhead > dynamicTable.Capacity)
                            {
                                dynamicTable.Clear();
                                state = State.SkipLiteralHeaderValue;
                                break;
                            }
                        }

                        if (valueLength == 0)
                        {
                            //InsertHeader(headerListener, name, Net45Compatibility.EmptyArray, indexType);
                            name = Array.Empty<byte>();
                            state = State.ReadHeaderRepresentation;
                        }
                        else
                        {
                            state = State.ReadLiteralHeaderValue;
                        }
                    }

                    break;

                case State.ReadLiteralHeaderValueLength:
                    // Header Value is a Literal String
                    valueLength = DecodeUle128(input);
                    if (valueLength == -1) return;

                    // Check for numerical overflow
                    if (valueLength > int.MaxValue - index) throw new IOException("decompression failure");

                    valueLength += index;

                    // Check new header size against max header size
                    var newHeaderSize2 = (long)nameLength + valueLength;
                    if (newHeaderSize2 + headerSize > maxHeaderSize)
                    {
                        // truncation will be reported during endHeaderBlock
                        headerSize = maxHeaderSize + 1;

                        if (indexType == HpackUtil.IndexType.None)
                        {
                            // Value is unused so skip bytes
                            state = State.SkipLiteralHeaderValue;
                            break;
                        }

                        // Check new header size against max dynamic table size
                        if (newHeaderSize2 + HttpHeader.HttpHeaderOverhead > dynamicTable.Capacity)
                        {
                            dynamicTable.Clear();
                            state = State.SkipLiteralHeaderValue;
                            break;
                        }
                    }

                    state = State.ReadLiteralHeaderValue;
                    break;

                case State.ReadLiteralHeaderValue:
                    // Wait until entire value is readable
                    if (input.BaseStream.Length - input.BaseStream.Position < valueLength) return;

                    var value = ReadStringLiteral(input, valueLength);
                    InsertHeader(headerListener, name, value, indexType);
                    state = State.ReadHeaderRepresentation;
                    break;

                case State.SkipLiteralHeaderValue:
                    valueLength -= (int)input.BaseStream.Seek(valueLength, SeekOrigin.Current);
                    if (valueLength < 0) valueLength = 0;

                    if (valueLength == 0) state = State.ReadHeaderRepresentation;

                    break;

                default:
                    throw new Exception("should not reach here");
            }
    }

    /// <summary>
    ///     End the current header block. Returns if the header field has been truncated.
    ///     This must be called after the header block has been completely decoded.
    /// </summary>
    /// <returns><c>true</c>, if header block was ended, <c>false</c> otherwise.</returns>
    public bool EndHeaderBlock()
    {
        var truncated = headerSize > maxHeaderSize;
        Reset();
        return truncated;
    }

    /// <summary>
    ///     Set the maximum table size.
    ///     If this is below the maximum size of the dynamic table used by the encoder,
    ///     the beginning of the next header block MUST signal this change.
    /// </summary>
    /// <param name="maxHeaderTableSize">Max header table size.</param>
    public void SetMaxHeaderTableSize(int maxHeaderTableSize)
    {
        maxDynamicTableSize = maxHeaderTableSize;
        if (maxDynamicTableSize < encoderMaxDynamicTableSize)
        {
            // decoder requires less space than encoder
            // encoder MUST signal this change
            maxDynamicTableSizeChangeRequired = true;
            dynamicTable.SetCapacity(maxDynamicTableSize);
        }
    }

    /// <summary>
    ///     Return the maximum table size.
    ///     This is the maximum size allowed by both the encoder and the decoder.
    /// </summary>
    /// <returns>The max header table size.</returns>
    public int GetMaxHeaderTableSize()
    {
        return dynamicTable.Capacity;
    }

    private void SetDynamicTableSize(int dynamicTableSize)
    {
        if (dynamicTableSize > maxDynamicTableSize) throw new IOException("invalid max dynamic table size");

        encoderMaxDynamicTableSize = dynamicTableSize;
        maxDynamicTableSizeChangeRequired = false;
        dynamicTable.SetCapacity(dynamicTableSize);
    }

    private HttpHeader GetHeaderField(int index)
    {
        if (index <= StaticTable.Length)
        {
            var headerField = StaticTable.Get(index);
            return headerField;
        }

        if (index - StaticTable.Length <= dynamicTable.Length())
        {
            var headerField = dynamicTable.GetEntry(index - StaticTable.Length);
            return headerField;
        }

        throw new IOException("illegal index value (" + index + ")");
    }

    private void ReadName(int index)
    {
        name = GetHeaderField(index).NameData;
    }

    private void IndexHeader(int index, IHeaderListener headerListener)
    {
        var headerField = GetHeaderField(index);
        AddHeader(headerListener, headerField.NameData, headerField.ValueData, false);
    }

    private void InsertHeader(IHeaderListener headerListener, ByteString name, ByteString value,
        HpackUtil.IndexType indexType)
    {
        AddHeader(headerListener, name, value, indexType == HpackUtil.IndexType.Never);

        switch (indexType)
        {
            case HpackUtil.IndexType.None:
            case HpackUtil.IndexType.Never:
                break;

            case HpackUtil.IndexType.Incremental:
                dynamicTable.Add(new HttpHeader(name, value));
                break;

            default:
                throw new Exception("should not reach here");
        }
    }

    private void AddHeader(IHeaderListener headerListener, ByteString name, ByteString value, bool sensitive)
    {
        if (name.Length == 0) throw new ArgumentException("name is empty");

        var newSize = headerSize + name.Length + value.Length;
        if (newSize <= maxHeaderSize)
        {
            headerListener.AddHeader(name, value, sensitive);
            headerSize = (int)newSize;
        }
        else
        {
            // truncation will be reported during endHeaderBlock
            headerSize = maxHeaderSize + 1;
        }
    }

    private bool ExceedsMaxHeaderSize(long size)
    {
        // Check new header size against max header size
        if (size + headerSize <= maxHeaderSize) return false;

        // truncation will be reported during endHeaderBlock
        headerSize = maxHeaderSize + 1;
        return true;
    }

    private ByteString ReadStringLiteral(BinaryReader input, int length)
    {
        var buf = new byte[length];
        var totalRead = 0;
        while (totalRead < length)
        {
            var read = input.Read(buf, totalRead, length - totalRead);
            if (read == 0) throw new IOException("decompression failure");

            totalRead += read;
        }

        return new ByteString(huffmanEncoded ? HuffmanDecoder.Instance.Decode(buf) : buf);
    }

    // Unsigned Little Endian Base 128 Variable-Length Integer Encoding
    private static int DecodeUle128(BinaryReader input)
    {
        var markedPosition = input.BaseStream.Position;
        var result = 0;
        var shift = 0;
        while (shift < 32)
        {
            if (input.BaseStream.Length - input.BaseStream.Position == 0)
            {
                // Buffer does not contain entire integer,
                // reset reader index and return -1.
                input.BaseStream.Position = markedPosition;
                return -1;
            }

            var b = input.ReadSByte();
            if (shift == 28 && (b & 0xF8) != 0) break;

            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;

            shift += 7;
        }

        // Value exceeds Integer.MAX_VALUE
        input.BaseStream.Position = markedPosition;
        throw new IOException("decompression failure");
    }

    private enum State
    {
        ReadHeaderRepresentation,
        ReadMaxDynamicTableSize,
        ReadIndexedHeader,
        ReadIndexedHeaderName,
        ReadLiteralHeaderNameLengthPrefix,
        ReadLiteralHeaderNameLength,
        ReadLiteralHeaderName,
        SkipLiteralHeaderName,
        ReadLiteralHeaderValueLengthPrefix,
        ReadLiteralHeaderValueLength,
        ReadLiteralHeaderValue,
        SkipLiteralHeaderValue
    }
}ParseOptions.0.json∂/
]D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http2\Hpack\DynamicTable.csø./*
 * Copyright 2014 Twitter, Inc
 * This file is a derivative work modified by Ringo Leese
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http2.Hpack;

public class DynamicTable
{
    // a circular queue of header fields
    private HttpHeader?[] headerFields = Array.Empty<HttpHeader?>();
    private int head;
    private int tail;

    /// <summary>
    ///     Return the maximum allowable size of the dynamic table.
    /// </summary>
    /// <value>
    ///     The capacity.
    /// </value>
    // ensure setCapacity creates the array
    public int Capacity { get; private set; } = -1;

    /// <summary>
    ///     Return the current size of the dynamic table.
    ///     This is the sum of the size of the entries.
    /// </summary>
    /// <value>
    ///     The size.
    /// </value>
    public int Size { get; private set; }

    /// <summary>
    ///     Creates a new dynamic table with the specified initial capacity.
    /// </summary>
    /// <param name="initialCapacity">Initial capacity.</param>
    public DynamicTable(int initialCapacity)
    {
        SetCapacity(initialCapacity);
    }

    /// <summary>
    ///     Return the number of header fields in the dynamic table.
    /// </summary>
    public int Length()
    {
        int length;
        if (head < tail)
            length = headerFields.Length - tail + head;
        else
            length = head - tail;

        return length;
    }

    /// <summary>
    ///     Return the header field at the given index.
    ///     The first and newest entry is always at index 1,
    ///     and the oldest entry is at the index length().
    /// </summary>
    /// <returns>The entry.</returns>
    /// <param name="index">Index.</param>
    public HttpHeader GetEntry(int index)
    {
        if (index <= 0 || index > Length()) throw new IndexOutOfRangeException();

        var i = head - index;
        if (i < 0) i += headerFields.Length;

        return headerFields[i] ??
               throw new InvalidOperationException("The HPACK dynamic table contains an empty active entry.");
    }

    /// <summary>
    ///     Add the header field to the dynamic table.
    ///     Entries are evicted from the dynamic table until the size of the table
    ///     and the new header field is less than or equal to the table's capacity.
    ///     If the size of the new entry is larger than the table's capacity,
    ///     the dynamic table will be cleared.
    /// </summary>
    /// <param name="header">Header.</param>
    public void Add(HttpHeader header)
    {
        var headerSize = header.Size;
        if (headerSize > Capacity)
        {
            Clear();
            return;
        }

        while (Size + headerSize > Capacity) Remove();

        headerFields[head++] = header;
        Size += header.Size;
        if (head == headerFields.Length) head = 0;
    }

    /// <summary>
    ///     Remove and return the oldest header field from the dynamic table.
    /// </summary>
    public HttpHeader? Remove()
    {
        var removed = headerFields[tail];
        if (removed == null) return null;

        Size -= removed.Size;
        headerFields[tail++] = null;
        if (tail == headerFields.Length) tail = 0;

        return removed;
    }

    /// <summary>
    ///     Remove all entries from the dynamic table.
    /// </summary>
    public void Clear()
    {
        while (tail != head)
        {
            headerFields[tail++] = null;
            if (tail == headerFields.Length) tail = 0;
        }

        head = 0;
        tail = 0;
        Size = 0;
    }

    /// <summary>
    ///     Set the maximum size of the dynamic table.
    ///     Entries are evicted from the dynamic table until the size of the table
    ///     is less than or equal to the maximum size.
    /// </summary>
    /// <param name="capacity">Capacity.</param>
    public void SetCapacity(int capacity)
    {
        if (capacity < 0) throw new ArgumentException("Illegal Capacity: " + capacity);

        // initially capacity will be -1 so init won't return here
        if (Capacity == capacity) return;

        Capacity = capacity;

        if (capacity == 0)
            Clear();
        else
            // initially size will be 0 so remove won't be called
            while (Size > capacity)
                Remove();

        var maxEntries = capacity / HttpHeader.HttpHeaderOverhead;
        if (capacity % HttpHeader.HttpHeaderOverhead != 0) maxEntries++;

        // check if capacity change requires us to reallocate the array
        if (headerFields.Length == maxEntries) return;

        var tmp = new HttpHeader?[maxEntries];

        // initially length will be 0 so there will be no copy
        var len = Length();
        var cursor = tail;
        for (var i = 0; i < len; i++)
        {
            var entry = headerFields[cursor++];
            tmp[i] = entry ??
                     throw new InvalidOperationException("The HPACK dynamic table contains an empty active entry.");
            if (cursor == headerFields.Length) cursor = 0;
        }

        tail = 0;
        head = tail + len;
        headerFields = tmp;
    }
}ParseOptions.0.json„ê
XD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http2\Hpack\Encoder.csè#if NET6_0_OR_GREATER
/*
 * Copyright 2014 Twitter, Inc
 * This file is a derivative work modified by Ringo Leese
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System;
using System.IO;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http2.Hpack
{
    internal class Encoder
    {
        private const int BucketSize = 17;

        // a linked hash map of header fields
        private readonly HeaderEntry?[] headerFields = new HeaderEntry?[BucketSize];
        private readonly HeaderEntry head = new HeaderEntry(-1, ByteString.Empty, ByteString.Empty, int.MaxValue, null);
        private int size;

        /// <summary>
        /// Gets the the maximum table size.
        /// </summary>
        /// <value>
        /// The max header table size.
        /// </value>
        public int MaxHeaderTableSize { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="Encoder"/> class.
        /// </summary>
        /// <param name="maxHeaderTableSize">Max header table size.</param>
        public Encoder(int maxHeaderTableSize)
        {
            if (maxHeaderTableSize < 0)
            {
                throw new ArgumentException("Illegal Capacity: " + maxHeaderTableSize);
            }

            MaxHeaderTableSize = maxHeaderTableSize;
            head.Before = head.After = head;
        }

        /// <summary>
        /// Encode the header field into the header block.
        /// </summary>
        /// <param name="output">Output.</param>
        /// <param name="name">Name.</param>
        /// <param name="value">Value.</param>
        /// <param name="sensitive">If set to <c>true</c> sensitive.</param>
        /// <param name="indexType">Index type.</param>
        /// <param name="useStaticName">Use static name.</param>
        public void EncodeHeader(BinaryWriter output, ByteString name, ByteString value, bool sensitive =
 false, HpackUtil.IndexType indexType = HpackUtil.IndexType.Incremental, bool useStaticName = true)
        {
            // If the header value is sensitive then it must never be indexed
            if (sensitive)
            {
                int nameIndex = GetNameIndex(name);
                EncodeLiteral(output, name, value, HpackUtil.IndexType.Never, nameIndex);
                return;
            }

            // If the peer will only use the static table
            if (MaxHeaderTableSize == 0)
            {
                int staticTableIndex = StaticTable.GetIndex(name, value);
                if (staticTableIndex == -1)
                {
                    int nameIndex = StaticTable.GetIndex(name);
                    EncodeLiteral(output, name, value, HpackUtil.IndexType.None, nameIndex);
                }
                else
                {
                    EncodeInteger(output, 0x80, 7, staticTableIndex);
                }

                return;
            }

            int headerSize = HttpHeader.SizeOf(name, value);

            // If the headerSize is greater than the max table size then it must be encoded literally
            if (headerSize > MaxHeaderTableSize)
            {
                int nameIndex = GetNameIndex(name);
                EncodeLiteral(output, name, value, HpackUtil.IndexType.None, nameIndex);
                return;
            }

            var headerField = GetEntry(name, value);
            if (headerField != null)
            {
                int index = GetIndex(headerField.Index) + StaticTable.Length;

                // Section 6.1. Indexed Header Field Representation
                EncodeInteger(output, 0x80, 7, index);
            }
            else
            {
                int staticTableIndex = StaticTable.GetIndex(name, value);
                if (staticTableIndex != -1)
                {
                    // Section 6.1. Indexed Header Field Representation
                    EncodeInteger(output, 0x80, 7, staticTableIndex);
                }
                else
                {
                    int nameIndex = useStaticName ? GetNameIndex(name) : -1;
                    EnsureCapacity(headerSize);

                    EncodeLiteral(output, name, value, indexType, nameIndex);
                    Add(name, value);
                }
            }
        }

        /// <summary>
        /// Set the maximum table size.
        /// </summary>
        /// <param name="output">Output.</param>
        /// <param name="maxHeaderTableSize">Max header table size.</param>
        public void SetMaxHeaderTableSize(BinaryWriter output, int maxHeaderTableSize)
        {
            if (maxHeaderTableSize < 0)
            {
                throw new ArgumentException("Illegal Capacity", nameof(maxHeaderTableSize));
            }

            if (MaxHeaderTableSize == maxHeaderTableSize)
            {
                return;
            }

            MaxHeaderTableSize = maxHeaderTableSize;
            EnsureCapacity(0);
            EncodeInteger(output, 0x20, 5, maxHeaderTableSize);
        }

        /// <summary>
        /// Encode integer according to Section 5.1.
        /// </summary>
        /// <param name="output">Output.</param>
        /// <param name="mask">Mask.</param>
        /// <param name="n">N.</param>
        /// <param name="i">The index.</param>
        private static void EncodeInteger(BinaryWriter output, int mask, int n, int i)
        {
            if (n < 0 || n > 8)
            {
                throw new ArgumentException("N: " + n);
            }

            int nbits = 0xFF >> (8 - n);
            if (i < nbits)
            {
                output.Write((byte)(mask | i));
            }
            else
            {
                output.Write((byte)(mask | nbits));
                int length = i - nbits;
                while (true)
                {
                    if ((length & ~0x7F) == 0)
                    {
                        output.Write((byte)length);
                        return;
                    }

                    output.Write((byte)((length & 0x7F) | 0x80));
                    length >>= 7;
                }
            }
        }

        /// <summary>
        /// Encode string literal according to Section 5.2.
        /// </summary>
        /// <param name="output">Output.</param>
        /// <param name="stringData">String data.</param>
        private void EncodeStringLiteral(BinaryWriter output, ByteString stringData)
        {
            int huffmanLength = HuffmanEncoder.Instance.GetEncodedLength(stringData);
            if (huffmanLength < stringData.Length)
            {
                EncodeInteger(output, 0x80, 7, huffmanLength);
                HuffmanEncoder.Instance.Encode(output, stringData);
            }
            else
            {
                EncodeInteger(output, 0x00, 7, stringData.Length);
                output.Write(stringData.Span);
            }
        }

        /// <summary>
        /// Encode literal header field according to Section 6.2.
        /// </summary>
        /// <param name="output">Output.</param>
        /// <param name="name">Name.</param>
        /// <param name="value">Value.</param>
        /// <param name="indexType">Index type.</param>
        /// <param name="nameIndex">Name index.</param>
        private void EncodeLiteral(BinaryWriter output, ByteString name, ByteString value, HpackUtil.IndexType indexType,
            int nameIndex)
        {
            int mask;
            int prefixBits;
            switch (indexType)
            {
                case HpackUtil.IndexType.Incremental:
                    mask = 0x40;
                    prefixBits = 6;
                    break;

                case HpackUtil.IndexType.None:
                    mask = 0x00;
                    prefixBits = 4;
                    break;

                case HpackUtil.IndexType.Never:
                    mask = 0x10;
                    prefixBits = 4;
                    break;

                default:
                    throw new Exception("should not reach here");
            }

            EncodeInteger(output, mask, prefixBits, nameIndex == -1 ? 0 : nameIndex);
            if (nameIndex == -1)
            {
                EncodeStringLiteral(output, name);
            }

            EncodeStringLiteral(output, value);
        }

        private int GetNameIndex(ByteString name)
        {
            int index = StaticTable.GetIndex(name);
            if (index == -1)
            {
                index = GetIndex(name);
                if (index >= 0)
                {
                    index += StaticTable.Length;
                }
            }

            return index;
        }

        /// <summary>
        /// Ensure that the dynamic table has enough room to hold 'headerSize' more bytes.
        /// Removes the oldest entry from the dynamic table until sufficient space is available.
        /// </summary>
        /// <param name="headerSize">Header size.</param>
        private void EnsureCapacity(int headerSize)
        {
            while (size + headerSize > MaxHeaderTableSize)
            {
                int index = Length();
                if (index == 0)
                {
                    break;
                }

                Remove();
            }
        }

        /// <summary>
        /// Return the number of header fields in the dynamic table.
        /// </summary>
        private int Length()
        {
            return size == 0 ? 0 : head.After.Index - head.Before.Index + 1;
        }

        /// <summary>
        /// Returns the header entry with the lowest index value for the header field.
        /// Returns null if header field is not in the dynamic table.
        /// </summary>
        /// <returns>The entry.</returns>
        /// <param name="name">Name.</param>
        /// <param name="value">Value.</param>
        private HeaderEntry? GetEntry(ByteString name, ByteString value)
        {
            if (Length() == 0 || name.Length == 0 || value.Length == 0)
            {
                return null;
            }

            int h = Hash(name);
            int i = Index(h);
            for (var e = headerFields[i]; e != null; e = e.Next)
            {
                if (e.Hash == h && name.Equals(e.NameData) && Equals(value, e.ValueData))
                {
                    return e;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the lowest index value for the header field name in the dynamic table.
        /// Returns -1 if the header field name is not in the dynamic table.
        /// </summary>
        /// <returns>The index.</returns>
        /// <param name="name">Name.</param>
        private int GetIndex(ByteString name)
        {
            if (Length() == 0 || name.Length == 0)
            {
                return -1;
            }

            int h = Hash(name);
            int i = Encoder.Index(h);
            int index = -1;
            for (HeaderEntry? e = headerFields[i]; e != null; e = e.Next)
            {
                if (e.Hash == h && name.Equals(e.NameData))
                {
                    index = e.Index;
                    break;
                }
            }

            return GetIndex(index);
        }

        /// <summary>
        /// Compute the index into the dynamic table given the index in the header entry.
        /// </summary>
        /// <returns>The index.</returns>
        /// <param name="index">Index.</param>
        private int GetIndex(int index)
        {
            if (index == -1)
            {
                return index;
            }

            return index - head.Before.Index + 1;
        }

        /// <summary>
        /// Add the header field to the dynamic table.
        /// Entries are evicted from the dynamic table until the size of the table
        /// and the new header field is less than the table's capacity.
        /// If the size of the new entry is larger than the table's capacity,
        /// the dynamic table will be cleared.
        /// </summary>
        /// <param name="name">Name.</param>
        /// <param name="value">Value.</param>
        private void Add(ByteString name, ByteString value)
        {
            int headerSize = HttpHeader.SizeOf(name, value);

            // Clear the table if the header field size is larger than the capacity.
            if (headerSize > MaxHeaderTableSize)
            {
                Clear();
                return;
            }

            // Evict oldest entries until we have enough capacity.
            while (size + headerSize > MaxHeaderTableSize)
            {
                Remove();
            }

            int h = Hash(name);
            int i = Index(h);
            var old = headerFields[i];
            var e = new HeaderEntry(h, name, value, head.Before.Index - 1, old);
            headerFields[i] = e;
            e.AddBefore(head);
            size += headerSize;
        }

        /// <summary>
        /// Remove and return the oldest header field from the dynamic table.
        /// </summary>
        private HttpHeader? Remove()
        {
            if (size == 0)
            {
                return null;
            }

            var eldest = head.After;
            int h = eldest.Hash;
            int i = Index(h);
            var prev = headerFields[i];
            var e = prev;
            while (e != null)
            {
                var next = e.Next;
                if (e == eldest)
                {
                    if (prev == eldest)
                    {
                        headerFields[i] = next;
                    }
                    else
                    {
                        prev!.Next = next;
                    }

                    eldest.Remove();
                    size -= eldest.Size;
                    return eldest;
                }

                prev = e;
                e = next;
            }

            return null;
        }

        /// <summary>
        /// Remove all entries from the dynamic table.
        /// </summary>
        private void Clear()
        {
            for (int i = 0; i < headerFields.Length; i++)
            {
                headerFields[i] = null;
            }

            head.Before = head.After = head;
            size = 0;
        }

        /// <summary>
        /// Returns the hash code for the given header field name.
        /// </summary>
        /// <returns><c>true</c> if hash name; otherwise, <c>false</c>.</returns>
        /// <param name="name">Name.</param>
        private static int Hash(ByteString name)
        {
            int h = 0;
            for (int i = 0; i < name.Length; i++)
            {
                h = 31 * h + name.Span[i];
            }

            if (h > 0)
            {
                return h;
            }

            if (h == int.MinValue)
            {
                return int.MaxValue;
            }

            return -h;
        }

        /// <summary>
        /// Returns the index into the hash table for the hash code h.
        /// </summary>
        /// <param name="h">The height.</param>
        private static int Index(int h)
        {
            return h % BucketSize;
        }

        /// <summary>
        /// A linked hash map HeaderField entry.
        /// </summary>
        private class HeaderEntry : HttpHeader
        {
            // This is used to compute the index in the dynamic table.

            // These properties comprise the doubly linked list used for iteration.
            public HeaderEntry Before { get; set; }

            public HeaderEntry After { get; set; }

            // These fields comprise the chained list for header fields with the same hash.
            public HeaderEntry? Next { get; set; }

            public int Hash { get; }

            public int Index { get; }

            /// <summary>
            /// Creates new entry.
            /// </summary>
            /// <param name="hash">Hash.</param>
            /// <param name="name">Name.</param>
            /// <param name="value">Value.</param>
            /// <param name="index">Index.</param>
            /// <param name="next">Next.</param>
            public HeaderEntry(int hash, ByteString name, ByteString value, int index, HeaderEntry? next) : base(name, value, true)
            {
                Index = index;
                Hash = hash;
                Next = next;
                Before = this;
                After = this;
            }

            /// <summary>
            /// Removes this entry from the linked list.
            /// </summary>
            public void Remove()
            {
                Before.After = After;
                After.Before = Before;
            }

            /// <summary>
            /// Inserts this entry before the specified existing entry in the list.
            /// </summary>
            /// <param name="existingEntry">Existing entry.</param>
            public void AddBefore(HeaderEntry existingEntry)
            {
                After = existingEntry;
                Before = existingEntry.Before;
                Before.After = this;
                After.Before = this;
            }
        }
    }
}
#endifParseOptions.0.jsonÅ8
ZD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http2\Hpack\HpackUtil.csç7/*
 * Copyright 2014 Twitter, Inc
 * This file is a derivative work modified by Ringo Leese
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

namespace Titanium.Web.Proxy.Http2.Hpack;

public static class HpackUtil
{
    // Section 6.2. Literal Header Field Representation
    public enum IndexType
    {
        Incremental, // Section 6.2.1. Literal Header Field with Incremental Indexing
        None, // Section 6.2.2. Literal Header Field without Indexing
        Never // Section 6.2.3. Literal Header Field never Indexed
    }

    public const int HuffmanEos = 256;

    // Appendix B: Huffman Codes
    // http://tools.ietf.org/html/rfc7541#appendix-B
    public static readonly int[] HuffmanCodes =
    {
        0x1ff8,
        0x7fffd8,
        0xfffffe2,
        0xfffffe3,
        0xfffffe4,
        0xfffffe5,
        0xfffffe6,
        0xfffffe7,
        0xfffffe8,
        0xffffea,
        0x3ffffffc,
        0xfffffe9,
        0xfffffea,
        0x3ffffffd,
        0xfffffeb,
        0xfffffec,
        0xfffffed,
        0xfffffee,
        0xfffffef,
        0xffffff0,
        0xffffff1,
        0xffffff2,
        0x3ffffffe,
        0xffffff3,
        0xffffff4,
        0xffffff5,
        0xffffff6,
        0xffffff7,
        0xffffff8,
        0xffffff9,
        0xffffffa,
        0xffffffb,
        0x14,
        0x3f8,
        0x3f9,
        0xffa,
        0x1ff9,
        0x15,
        0xf8,
        0x7fa,
        0x3fa,
        0x3fb,
        0xf9,
        0x7fb,
        0xfa,
        0x16,
        0x17,
        0x18,
        0x0,
        0x1,
        0x2,
        0x19,
        0x1a,
        0x1b,
        0x1c,
        0x1d,
        0x1e,
        0x1f,
        0x5c,
        0xfb,
        0x7ffc,
        0x20,
        0xffb,
        0x3fc,
        0x1ffa,
        0x21,
        0x5d,
        0x5e,
        0x5f,
        0x60,
        0x61,
        0x62,
        0x63,
        0x64,
        0x65,
        0x66,
        0x67,
        0x68,
        0x69,
        0x6a,
        0x6b,
        0x6c,
        0x6d,
        0x6e,
        0x6f,
        0x70,
        0x71,
        0x72,
        0xfc,
        0x73,
        0xfd,
        0x1ffb,
        0x7fff0,
        0x1ffc,
        0x3ffc,
        0x22,
        0x7ffd,
        0x3,
        0x23,
        0x4,
        0x24,
        0x5,
        0x25,
        0x26,
        0x27,
        0x6,
        0x74,
        0x75,
        0x28,
        0x29,
        0x2a,
        0x7,
        0x2b,
        0x76,
        0x2c,
        0x8,
        0x9,
        0x2d,
        0x77,
        0x78,
        0x79,
        0x7a,
        0x7b,
        0x7ffe,
        0x7fc,
        0x3ffd,
        0x1ffd,
        0xffffffc,
        0xfffe6,
        0x3fffd2,
        0xfffe7,
        0xfffe8,
        0x3fffd3,
        0x3fffd4,
        0x3fffd5,
        0x7fffd9,
        0x3fffd6,
        0x7fffda,
        0x7fffdb,
        0x7fffdc,
        0x7fffdd,
        0x7fffde,
        0xffffeb,
        0x7fffdf,
        0xffffec,
        0xffffed,
        0x3fffd7,
        0x7fffe0,
        0xffffee,
        0x7fffe1,
        0x7fffe2,
        0x7fffe3,
        0x7fffe4,
        0x1fffdc,
        0x3fffd8,
        0x7fffe5,
        0x3fffd9,
        0x7fffe6,
        0x7fffe7,
        0xffffef,
        0x3fffda,
        0x1fffdd,
        0xfffe9,
        0x3fffdb,
        0x3fffdc,
        0x7fffe8,
        0x7fffe9,
        0x1fffde,
        0x7fffea,
        0x3fffdd,
        0x3fffde,
        0xfffff0,
        0x1fffdf,
        0x3fffdf,
        0x7fffeb,
        0x7fffec,
        0x1fffe0,
        0x1fffe1,
        0x3fffe0,
        0x1fffe2,
        0x7fffed,
        0x3fffe1,
        0x7fffee,
        0x7fffef,
        0xfffea,
        0x3fffe2,
        0x3fffe3,
        0x3fffe4,
        0x7ffff0,
        0x3fffe5,
        0x3fffe6,
        0x7ffff1,
        0x3ffffe0,
        0x3ffffe1,
        0xfffeb,
        0x7fff1,
        0x3fffe7,
        0x7ffff2,
        0x3fffe8,
        0x1ffffec,
        0x3ffffe2,
        0x3ffffe3,
        0x3ffffe4,
        0x7ffffde,
        0x7ffffdf,
        0x3ffffe5,
        0xfffff1,
        0x1ffffed,
        0x7fff2,
        0x1fffe3,
        0x3ffffe6,
        0x7ffffe0,
        0x7ffffe1,
        0x3ffffe7,
        0x7ffffe2,
        0xfffff2,
        0x1fffe4,
        0x1fffe5,
        0x3ffffe8,
        0x3ffffe9,
        0xffffffd,
        0x7ffffe3,
        0x7ffffe4,
        0x7ffffe5,
        0xfffec,
        0xfffff3,
        0xfffed,
        0x1fffe6,
        0x3fffe9,
        0x1fffe7,
        0x1fffe8,
        0x7ffff3,
        0x3fffea,
        0x3fffeb,
        0x1ffffee,
        0x1ffffef,
        0xfffff4,
        0xfffff5,
        0x3ffffea,
        0x7ffff4,
        0x3ffffeb,
        0x7ffffe6,
        0x3ffffec,
        0x3ffffed,
        0x7ffffe7,
        0x7ffffe8,
        0x7ffffe9,
        0x7ffffea,
        0x7ffffeb,
        0xffffffe,
        0x7ffffec,
        0x7ffffed,
        0x7ffffee,
        0x7ffffef,
        0x7fffff0,
        0x3ffffee,
        0x3fffffff // EOS
    };

    public static readonly byte[] HuffmanCodeLengths =
    {
        13, 23, 28, 28, 28, 28, 28, 28, 28, 24, 30, 28, 28, 30, 28, 28,
        28, 28, 28, 28, 28, 28, 30, 28, 28, 28, 28, 28, 28, 28, 28, 28,
        6, 10, 10, 12, 13, 6, 8, 11, 10, 10, 8, 11, 8, 6, 6, 6,
        5, 5, 5, 6, 6, 6, 6, 6, 6, 6, 7, 8, 15, 6, 12, 10,
        13, 6, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
        7, 7, 7, 7, 7, 7, 7, 7, 8, 7, 8, 13, 19, 13, 14, 6,
        15, 5, 6, 5, 6, 5, 6, 6, 6, 5, 7, 7, 6, 6, 6, 5,
        6, 7, 6, 5, 5, 6, 7, 7, 7, 7, 7, 15, 11, 14, 13, 28,
        20, 22, 20, 20, 22, 22, 22, 23, 22, 23, 23, 23, 23, 23, 24, 23,
        24, 24, 22, 23, 24, 23, 23, 23, 23, 21, 22, 23, 22, 23, 23, 24,
        22, 21, 20, 22, 22, 23, 23, 21, 23, 22, 22, 24, 21, 22, 23, 23,
        21, 21, 22, 21, 23, 22, 23, 23, 20, 22, 22, 22, 23, 22, 22, 23,
        26, 26, 20, 19, 22, 23, 22, 25, 26, 26, 26, 27, 27, 26, 24, 25,
        19, 21, 26, 27, 27, 26, 27, 24, 21, 21, 26, 26, 28, 27, 27, 27,
        20, 24, 20, 21, 22, 21, 21, 23, 22, 22, 25, 25, 24, 24, 26, 23,
        26, 27, 26, 26, 27, 27, 27, 27, 27, 28, 27, 27, 27, 27, 27, 26,
        30 // EOS
    };
}ParseOptions.0.jsoné3
_D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http2\Hpack\HuffmanDecoder.csï2/*
 * Copyright 2014 Twitter, Inc
 * This file is a derivative work modified by Ringo Leese
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.IO;

namespace Titanium.Web.Proxy.Http2.Hpack;

public class HuffmanDecoder
{
    /// <summary>
    ///     Huffman Decoder
    /// </summary>
    public static readonly HuffmanDecoder Instance = new();

    private readonly Node root;

    /// <summary>
    ///     Creates a new Huffman decoder with the specified Huffman coding.
    /// </summary>
    private HuffmanDecoder()
    {
        // the Huffman codes indexed by symbol
        var codes = HpackUtil.HuffmanCodes;

        // the length of each Huffman code
        var lengths = HpackUtil.HuffmanCodeLengths;
        if (codes.Length != 257 || codes.Length != lengths.Length)
            throw new ArgumentException("invalid Huffman coding");

        root = BuildTree(codes, lengths);
    }

    /// <summary>
    ///     Decompresses the given Huffman coded string literal.
    /// </summary>
    /// <param name="buf">the string literal to be decoded</param>
    /// <returns>the output stream for the compressed data</returns>
    /// <exception cref="IOException">
    ///     throws IOException if an I/O error occurs. In particular, an <code>IOException</code> may
    ///     be thrown if the output stream has been closed.
    /// </exception>
    public ReadOnlyMemory<byte> Decode(byte[] buf)
    {
        var resultBuf = new byte[buf.Length * 2];
        var resultSize = 0;
        var node = root;
        var current = 0;
        var bits = 0;
        for (var i = 0; i < buf.Length; i++)
        {
            int b = buf[i];
            current = (current << 8) | b;
            bits += 8;
            while (bits >= 8)
            {
                var c = (current >> (bits - 8)) & 0xFF;
                var children = node.Children ??
                               throw new IOException("Invalid Huffman code: terminal node has trailing bits.");
                node = children[c] ?? throw new IOException("Invalid Huffman code.");
                bits -= node.Bits;
                if (node.IsTerminal)
                {
                    if (node.Symbol == HpackUtil.HuffmanEos) throw new IOException("EOS Decoded");

                    resultBuf[resultSize++] = (byte)node.Symbol;
                    node = root;
                }
            }
        }

        while (bits > 0)
        {
            var c = (current << (8 - bits)) & 0xFF;
            var children = node.Children ??
                           throw new IOException("Invalid Huffman code: terminal node has trailing bits.");
            node = children[c] ?? throw new IOException("Invalid Huffman code.");
            if (node.IsTerminal && node.Bits <= bits)
            {
                bits -= node.Bits;
                resultBuf[resultSize++] = (byte)node.Symbol;
                node = root;
            }
            else
            {
                break;
            }
        }

        // Section 5.2. String Literal Representation
        // Padding not corresponding to the most significant bits of the code
        // for the EOS symbol (0xFF) MUST be treated as a decoding error.
        var mask = (1 << bits) - 1;
        if ((current & mask) != mask) throw new IOException("Invalid Padding");

        return resultBuf.AsMemory(0, resultSize);
    }

    private static Node BuildTree(int[] codes, byte[] lengths)
    {
        var root = new Node();
        for (var i = 0; i < codes.Length; i++) Insert(root, i, codes[i], lengths[i]);

        return root;
    }

    private static void Insert(Node root, int symbol, int code, byte length)
    {
        // traverse tree using the most significant bytes of code
        var current = root;
        while (length > 8)
        {
            if (current.IsTerminal) throw new InvalidDataException("invalid Huffman code: prefix not unique");

            length -= 8;
            var i = (code >> length) & 0xFF;
            var children = current.Children ??
                           throw new InvalidDataException("invalid Huffman code: terminal node has trailing bits");
            children[i] ??= new Node();

            current = children[i]!;
        }

        var terminal = new Node(symbol, length);
        var shift = 8 - length;
        var start = (code << shift) & 0xFF;
        var end = 1 << shift;
        var currentChildren = current.Children ??
                              throw new InvalidDataException("invalid Huffman code: prefix not unique");
        for (var i = start; i < start + end; i++) currentChildren[i] = terminal;
    }

    private class Node
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="HuffmanDecoder" /> class.
        /// </summary>
        public Node()
        {
            Symbol = 0;
            Bits = 8;
            Children = new Node?[256];
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="HuffmanDecoder" /> class.
        /// </summary>
        /// <param name="symbol">the symbol the node represents</param>
        /// <param name="bits">the number of bits matched by this node</param>
        public Node(int symbol, int bits)
        {
            //assert(bits > 0 && bits <= 8);
            Symbol = symbol;
            Bits = bits;
            Children = null;
        }

        // terminal nodes have a symbol
        public int Symbol { get; }

        // number of bits matched by the node
        public int Bits { get; }

        // internal nodes have children
        public Node?[]? Children { get; }

        public bool IsTerminal => Children == null;
    }
}ParseOptions.0.json˝
_D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http2\Hpack\HuffmanEncoder.csÑ/*
 * Copyright 2014 Twitter, Inc
 * This file is a derivative work modified by Ringo Leese
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.IO;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http2.Hpack;

internal class HuffmanEncoder
{
    /// <summary>
    ///     Huffman Encoder
    /// </summary>
    public static readonly HuffmanEncoder Instance = new();

    /// <summary>
    ///     the Huffman codes indexed by symbol
    /// </summary>
    private readonly int[] codes = HpackUtil.HuffmanCodes;

    /// <summary>
    ///     the length of each Huffman code
    /// </summary>
    private readonly byte[] lengths = HpackUtil.HuffmanCodeLengths;

    /// <summary>
    ///     Compresses the input string literal using the Huffman coding.
    /// </summary>
    /// <param name="output">the output stream for the compressed data</param>
    /// <param name="data">the string literal to be Huffman encoded</param>
    /// <exception cref="IOException">
    ///     if an I/O error occurs. In particular, an <code>IOException</code> may be thrown if the
    ///     output stream has been closed.
    /// </exception>
    public void Encode(BinaryWriter output, ByteString data)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));

        if (data.Length == 0) return;

        var current = 0L;
        var n = 0;

        for (var i = 0; i < data.Length; i++)
        {
            var b = data.Span[i] & 0xFF;
            var code = (uint)codes[b];
            int nbits = lengths[b];

            current <<= nbits;
            current |= code;
            n += nbits;

            while (n >= 8)
            {
                n -= 8;
                output.Write((byte)(current >> n));
            }
        }

        if (n > 0)
        {
            current <<= 8 - n;
            current |= (uint)(0xFF >> n); // this should be EOS symbol
            output.Write((byte)current);
        }
    }

    /// <summary>
    ///     Returns the number of bytes required to Huffman encode the input string literal.
    /// </summary>
    /// <returns>the number of bytes required to Huffman encode <code>data</code></returns>
    /// <param name="data">the string literal to be Huffman encoded</param>
    public int GetEncodedLength(ByteString data)
    {
        var len = 0L;
        foreach (var b in data.Span) len += lengths[b];

        return (int)((len + 7) >> 3);
    }
}ParseOptions.0.json≥

`D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http2\Hpack\IHeaderListener.csπ	/*
 * Copyright 2014 Twitter, Inc
 * This file is a derivative work modified by Ringo Leese
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http2.Hpack;

internal interface IHeaderListener
{
    /// <summary>
    ///     EmitHeader is called by the decoder during header field emission.
    ///     The name and value byte arrays must not be modified.
    /// </summary>
    /// <param name="name">Name.</param>
    /// <param name="value">Value.</param>
    /// <param name="sensitive">If set to <c>true</c> sensitive.</param>
    void AddHeader(ByteString name, ByteString value, bool sensitive);
}ParseOptions.0.jsonõ7
\D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http2\Hpack\StaticTable.cs•6/*
 * Copyright 2014 Twitter, Inc
 * This file is a derivative work modified by Ringo Leese
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Collections.Generic;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http2.Hpack;

internal static class StaticTable
{
    /// <summary>
    ///     Appendix A: Static Table Definition
    /// </summary>
    /// <see cref="http://tools.ietf.org/html/rfc7541#appendix-A" />
    private static readonly List<HttpHeader> staticTable;

    private static readonly Dictionary<ByteString, int> staticIndexByName;

    public static ByteString KnownHeaderAuhtority = (ByteString)":authority";

    public static ByteString KnownHeaderMethod = (ByteString)":method";

    public static ByteString KnownHeaderPath = (ByteString)":path";

    public static ByteString KnownHeaderScheme = (ByteString)":scheme";

    public static ByteString KnownHeaderStatus = (ByteString)":status";

    static StaticTable()
    {
        const int entryCount = 61;
        staticTable = new List<HttpHeader>(entryCount);
        staticIndexByName = new Dictionary<ByteString, int>(entryCount);
        Create(KnownHeaderAuhtority, string.Empty); // 1
        Create(KnownHeaderMethod, "GET"); // 2
        Create(KnownHeaderMethod, "POST"); // 3
        Create(KnownHeaderPath, "/"); // 4
        Create(KnownHeaderPath, "/index.html"); // 5
        Create(KnownHeaderScheme, "http"); // 6
        Create(KnownHeaderScheme, "https"); // 7
        Create(KnownHeaderStatus, "200"); // 8
        Create(KnownHeaderStatus, "204"); // 9
        Create(KnownHeaderStatus, "206"); // 10
        Create(KnownHeaderStatus, "304"); // 11
        Create(KnownHeaderStatus, "400"); // 12
        Create(KnownHeaderStatus, "404"); // 13
        Create(KnownHeaderStatus, "500"); // 14
        Create("Accept-Charset", string.Empty); // 15
        Create("Accept-Encoding", "gzip, deflate"); // 16
        Create("Accept-Language", string.Empty); // 17
        Create("Accept-Ranges", string.Empty); // 18
        Create("Accept", string.Empty); // 19
        Create("Access-Control-Allow-Origin", string.Empty); // 20
        Create("Age", string.Empty); // 21
        Create("Allow", string.Empty); // 22
        Create("Authorization", string.Empty); // 23
        Create("Cache-Control", string.Empty); // 24
        Create("Content-Disposition", string.Empty); // 25
        Create("Content-Encoding", string.Empty); // 26
        Create("Content-Language", string.Empty); // 27
        Create("Content-Length", string.Empty); // 28
        Create("Content-Location", string.Empty); // 29
        Create("Content-Range", string.Empty); // 30
        Create("Content-Type", string.Empty); // 31
        Create("Cookie", string.Empty); // 32
        Create("Date", string.Empty); // 33
        Create("ETag", string.Empty); // 34
        Create("Expect", string.Empty); // 35
        Create("Expires", string.Empty); // 36
        Create("From", string.Empty); // 37
        Create("Host", string.Empty); // 38
        Create("If-Match", string.Empty); // 39
        Create("If-Modified-Since", string.Empty); // 40
        Create("If-None-Match", string.Empty); // 41
        Create("If-Range", string.Empty); // 42
        Create("If-Unmodified-Since", string.Empty); // 43
        Create("Last-Modified", string.Empty); // 44
        Create("Link", string.Empty); // 45
        Create("Location", string.Empty); // 46
        Create("Max-Forwards", string.Empty); // 47
        Create("Proxy-Authenticate", string.Empty); // 48
        Create("Proxy-Authorization", string.Empty); // 49
        Create("Range", string.Empty); // 50
        Create("Referer", string.Empty); // 51
        Create("Refresh", string.Empty); // 52
        Create("Retry-After", string.Empty); // 53
        Create("Server", string.Empty); // 54
        Create("Set-Cookie", string.Empty); // 55
        Create("Strict-Transport-Security", string.Empty); // 56
        Create("Transfer-Encoding", string.Empty); // 57
        Create("User-Agent", string.Empty); // 58
        Create("Vary", string.Empty); // 59
        Create("Via", string.Empty); // 60
        Create("WWW-Authenticate", string.Empty); // 61
    }

    /// <summary>
    ///     The number of header fields in the static table.
    /// </summary>
    /// <value>The length.</value>
    public static int Length => staticTable.Count;

    /// <summary>
    ///     Return the http header field at the given index value.
    /// </summary>
    /// <returns>The header field.</returns>
    /// <param name="index">Index.</param>
    public static HttpHeader Get(int index)
    {
        return staticTable[index - 1];
    }

    /// <summary>
    ///     Returns the lowest index value for the given header field name in the static table.
    ///     Returns -1 if the header field name is not in the static table.
    /// </summary>
    /// <returns>The index.</returns>
    /// <param name="name">Name.</param>
    public static int GetIndex(ByteString name)
    {
        if (!staticIndexByName.TryGetValue(name, out var index)) return -1;

        return index;
    }

    /// <summary>
    ///     Returns the index value for the given header field in the static table.
    ///     Returns -1 if the header field is not in the static table.
    /// </summary>
    /// <returns>The index.</returns>
    /// <param name="name">Name.</param>
    /// <param name="value">Value.</param>
    public static int GetIndex(ByteString name, ByteString value)
    {
        var index = GetIndex(name);
        if (index == -1) return -1;

        // Note this assumes all entries for a given header field are sequential.
        while (index <= Length)
        {
            var entry = Get(index);
            if (!name.Equals(entry.NameData)) break;

            if (Equals(value, entry.Value)) return index;

            index++;
        }

        return -1;
    }

    private static void Create(string name, string value)
    {
        Create((ByteString)name.ToLower(), value);
    }

    private static void Create(ByteString name, string value)
    {
        staticTable.Add(new HttpHeader(name, (ByteString)value));
        staticIndexByName[name] = staticTable.Count;
    }
}ParseOptions.0.json∆
YD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http2\Http2FrameFlag.cs”using System;

namespace Titanium.Web.Proxy.Http2;

[Flags]
internal enum Http2FrameFlag : byte
{
    Ack = 0x01,
    EndStream = 0x01,
    EndHeaders = 0x04,
    Padded = 0x08,
    Priority = 0x20
}ParseOptions.0.jsonæ
[D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http2\Http2FrameHeader.cs…namespace Titanium.Web.Proxy.Http2;

internal class Http2FrameHeader
{
    public Http2FrameFlag Flags;
    public int Length;

    public int StreamId;

    public Http2FrameType Type;

    public void CopyToBuffer(byte[] buf)
    {
        var length = Length;
        buf[0] = (byte)((length >> 16) & 0xff);
        buf[1] = (byte)((length >> 8) & 0xff);
        buf[2] = (byte)(length & 0xff);
        buf[3] = (byte)Type;
        buf[4] = (byte)Flags;
        var streamId = StreamId;
        buf[5] = (byte)((streamId >> 24) & 0x7f);
        buf[6] = (byte)((streamId >> 16) & 0xff);
        buf[7] = (byte)((streamId >> 8) & 0xff);
        buf[8] = (byte)(streamId & 0xff);
    }
}ParseOptions.0.jsonü
YD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http2\Http2FrameType.cs¨namespace Titanium.Web.Proxy.Http2;

internal enum Http2FrameType : byte
{
    Data = 0x00,
    Headers = 0x01,
    Priority = 0x02,
    RstStream = 0x03,
    Settings = 0x04,
    PushPromise = 0x05,
    Ping = 0x06,
    GoAway = 0x07,
    WindowUpdate = 0x08,
    Continuation = 0x09
}ParseOptions.0.jsonÿ”
VD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http2\Http2Helper.csÁ“#if NET6_0_OR_GREATER
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Models;
using Decoder = Titanium.Web.Proxy.Http2.Hpack.Decoder;
using Encoder = Titanium.Web.Proxy.Http2.Hpack.Encoder;

namespace Titanium.Web.Proxy.Http2
{
    internal class Http2Helper
    {
        public static readonly byte[] ConnectionPreface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

        /// <summary>
        ///     relays the input clientStream to the server at the specified host name and port with the given httpCmd and headers
        ///     as prefix
        ///     Useful for websocket requests
        ///     Task-based Asynchronous Pattern
        /// </summary>
        /// <returns></returns>
        internal static async Task SendHttp2(Stream clientStream, Stream serverStream,
            Func<SessionEventArgs> sessionFactory,
            Func<SessionEventArgs, Task> onBeforeRequest, Func<SessionEventArgs, Task> onBeforeResponse,
            CancellationTokenSource cancellationTokenSource, Guid connectionId,
            ExceptionHandler? exceptionFunc)
        {
            var clientSettings = new Http2Settings();
            var serverSettings = new Http2Settings();

            var sessions = new ConcurrentDictionary<int, SessionEventArgs>();

            // Writes toward the client can originate from the server=>client relay as well as from a
            // synthetic response emitted on the client=>server relay. Serialize them so frames never interleave.
            var clientWriteLock = new SemaphoreSlim(1, 1);

            // Completed once the server's connection SETTINGS frame has been relayed to the client. A synthetic
            // response must not send HEADERS before this, or the client rejects the connection.
            var serverSettingsRelayed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Now async relay all server=>client & client=>server data
            var sendRelay =
                CopyHttp2FrameAsync(clientStream, serverStream, clientSettings, serverSettings,
                    sessionFactory, sessions, onBeforeRequest,
                    connectionId, true, clientWriteLock, serverSettingsRelayed, cancellationTokenSource.Token, exceptionFunc);
            var receiveRelay =
                CopyHttp2FrameAsync(serverStream, clientStream, serverSettings, clientSettings,
                    sessionFactory, sessions, onBeforeResponse,
                    connectionId, false, clientWriteLock, serverSettingsRelayed, cancellationTokenSource.Token, exceptionFunc);

            await Task.WhenAny(sendRelay, receiveRelay);
            cancellationTokenSource.Cancel();

            await Task.WhenAll(sendRelay, receiveRelay);
        }

        private static async Task CopyHttp2FrameAsync(Stream input, Stream output,
            Http2Settings localSettings, Http2Settings remoteSettings,
            Func<SessionEventArgs> sessionFactory, ConcurrentDictionary<int, SessionEventArgs> sessions,
            Func<SessionEventArgs, Task> onBeforeRequestResponse,
            Guid connectionId, bool isClient, SemaphoreSlim clientWriteLock,
            TaskCompletionSource<bool> serverSettingsRelayed, CancellationToken cancellationToken,
            ExceptionHandler? exceptionFunc)
        {
            int headerTableSize = 0;
            Decoder? decoder = null;

            // stream ids that were answered with a synthetic (proxy-generated) response and therefore must not
            // be forwarded to the server. Only relevant on the client=>server relay.
            var syntheticStreams = new HashSet<int>();

            // Writes toward the client must be serialized against the other relay.
            async Task lockedClientWrite(Func<Task> writeAction)
            {
                await clientWriteLock.WaitAsync(cancellationToken);
                try
                {
                    await writeAction();
                }
                finally
                {
                    clientWriteLock.Release();
                }
            }

            var frameHeader = new Http2FrameHeader();
            var frameHeaderBuffer = new byte[9];
            byte[]? buffer = null;
            while (true)
            {
                int read = await ForceRead(input, frameHeaderBuffer, 0, 9, cancellationToken);
                if (read != 9)
                {
                    return;
                }

                int length = (frameHeaderBuffer[0] << 16) + (frameHeaderBuffer[1] << 8) + frameHeaderBuffer[2];
                var type = (Http2FrameType)frameHeaderBuffer[3];
                var flags = (Http2FrameFlag)frameHeaderBuffer[4];
                int streamId = ((frameHeaderBuffer[5] & 0x7f) << 24) + (frameHeaderBuffer[6] << 16) +
                               (frameHeaderBuffer[7] << 8) + frameHeaderBuffer[8];

                frameHeader.Length = length;
                frameHeader.Type = type;
                frameHeader.Flags = flags;
                frameHeader.StreamId = streamId;

                if (buffer == null || buffer.Length < localSettings.MaxFrameSize)
                {
                    buffer = new byte[localSettings.MaxFrameSize];
                }

                read = await ForceRead(input, buffer, 0, length, cancellationToken);
                if (read != length)
                {
                    return;
                }

                bool sendPacket = true;
                bool endStream = false;

                SessionEventArgs? args = null;
                RequestResponseBase? rr = null;
                if (type == Http2FrameType.Data || type == Http2FrameType.Headers/* || type == Http2FrameType.PushPromise*/)
                {
                    if (!sessions.TryGetValue(streamId, out args))
                    {
                        //if (type == Http2FrameType.Data)
                        //{
                        //    throw new ProxyHttpException("HTTP Body data received before any header frame.", null, args);
                        //}

                        //if (type == Http2FrameType.Headers && !isClient)
                        //{
                        //    throw new ProxyHttpException("HTTP Response received before any Request header frame.", null, args);
                        //}

                        if (type == Http2FrameType.PushPromise && isClient)
                        {
                            throw new ProxyHttpException("HTTP Push promise received from the client.", null, args);
                        }
                    }
                }

                //System.Diagnostics.Debug.WriteLine("CONN: " + connectionId + ", CLIENT: " + isClient + ", STREAM: " + streamId + ", TYPE: " + type);
                if (isClient && syntheticStreams.Contains(streamId))
                {
                    // this stream was answered with a synthetic response; never forward its request frames upstream.
                    sendPacket = false;
                }
                else if (type == Http2FrameType.Data && args != null)
                {
                    if (isClient)
                        args.OnDataSent(buffer, 0, read);
                    else
                        args.OnDataReceived(buffer, 0, read);

                    rr = isClient ? (RequestResponseBase)args.HttpClient.Request : args.HttpClient.Response;

                    bool padded = (flags & Http2FrameFlag.Padded) != 0;
                    bool endStreamFlag = (flags & Http2FrameFlag.EndStream) != 0;
                    if (endStreamFlag)
                    {
                        endStream = true;
                    }

                    if (rr.Http2IgnoreBodyFrames)
                    {
                        sendPacket = false;
                    }

                    if (rr.ReadHttp2BodyTaskCompletionSource != null)
                    {
                        // Get body method was called in the "before" event handler

                        var data = rr.Http2BodyData;
                        int offset = 0;
                        if (padded)
                        {
                            offset++;
                            length--;
                            length -= buffer[0];
                        }

                        if (data == null)
                            throw new InvalidOperationException("HTTP/2 body buffering was requested without a buffer.");

                        data.Write(buffer, offset, length);
                    }
                    else if (!rr.Http2IgnoreBodyFrames && !rr.IsBodyRead &&
                             (isClient
                                 ? args.Server.ShouldCallBeforeRequestBodyWrite()
                                 : args.Server.ShouldCallBeforeResponseBodyWrite()))
                    {
                        // per-DATA-frame inspection/modification hook (streams without buffering the whole body)
                        int dataOffset = 0;
                        int dataLength = length;
                        if (padded)
                        {
                            var padLength = buffer[0];
                            dataOffset = 1;
                            dataLength = length - 1 - padLength;
                            if (dataLength < 0) dataLength = 0;
                        }

                        var dataBytes = new byte[dataLength];
                        Buffer.BlockCopy(buffer, dataOffset, dataBytes, 0, dataLength);

                        var bodyWriteArgs = new BeforeBodyWriteEventArgs(args, dataBytes, true, endStreamFlag);
                        if (isClient)
                            await args.Server.OnBeforeRequestBodyWrite(bodyWriteArgs);
                        else
                            await args.Server.OnBeforeResponseBodyWrite(bodyWriteArgs);

                        var outBytes = bodyWriteArgs.BodyBytes ?? Array.Empty<byte>();

                        if (isClient)
                            await SendData(frameHeader, frameHeaderBuffer, streamId, outBytes, endStreamFlag,
                                remoteSettings.MaxFrameSize, output);
                        else
                            await lockedClientWrite(() => SendData(frameHeader, frameHeaderBuffer, streamId, outBytes,
                                endStreamFlag, remoteSettings.MaxFrameSize, output));

                        // we have emitted our own (possibly re-sized) DATA frame(s); suppress the default relay
                        sendPacket = false;
                    }
                }
                else if (type == Http2FrameType.Headers/* || type == Http2FrameType.PushPromise*/)
                {
                    bool endHeaders = (flags & Http2FrameFlag.EndHeaders) != 0;
                    bool padded = (flags & Http2FrameFlag.Padded) != 0;
                    bool priority = (flags & Http2FrameFlag.Priority) != 0;
                    bool endStreamFlag = (flags & Http2FrameFlag.EndStream) != 0;
                    if (endStreamFlag)
                    {
                        endStream = true;
                    }

                    int offset = 0;
                    if (padded)
                    {
                        offset = 1;
                        Breakpoint();
                    }

                    if (type == Http2FrameType.PushPromise)
                    {
                        int promisedStreamId =
 (buffer[offset++] << 24) + (buffer[offset++] << 16) + (buffer[offset++] << 8) + buffer[offset++];
                        if (!sessions.TryGetValue(streamId, out args))
                        {
                            args = sessionFactory();
                            args.IsPromise = true;
                            _ = sessions.TryAdd(streamId, args);
                            _ = sessions.TryAdd(promisedStreamId, args);
                        }

                        System.Diagnostics.Debug.WriteLine("PROMISE STREAM: " + streamId + ", " + promisedStreamId +
                                                           ", CONN: " + connectionId);
                        rr = args.HttpClient.Request;

                        if (isClient)
                        {
                            // push_promise from client???
                            Breakpoint();
                        }
                    }
                    else
                    {
                        if (!sessions.TryGetValue(streamId, out args))
                        {
                            args = sessionFactory();
                            _ = sessions.TryAdd(streamId, args);
                        }

                        rr = isClient ? (RequestResponseBase)args.HttpClient.Request : args.HttpClient.Response;
                        if (priority)
                        {
                            var priorityData = ((long)buffer[offset++] << 32) + ((long)buffer[offset++] << 24) +
                                               (buffer[offset++] << 16) + (buffer[offset++] << 8) + buffer[offset++];
                            rr.Priority = priorityData;
                        }
                    }


                    int dataLength = length - offset;
                    if (padded)
                    {
                        dataLength -= buffer[0];
                    }

                    var sessionArgs = args ??
                                      throw new InvalidOperationException("An HTTP/2 header frame has no session.");
                    var headerListener = new MyHeaderListener(
                        (name, value) =>
                        {
                            var headers = isClient
                                ? sessionArgs.HttpClient.Request.Headers
                                : sessionArgs.HttpClient.Response.Headers;
                            headers.AddHeader(new HttpHeader(name, value));
                        });
                    try
                    {
                        // recreate the decoder when new value is bigger
                        // should we recreate when smaller, too?
                        if (decoder == null || headerTableSize < localSettings.HeaderTableSize)
                        {
                            headerTableSize = localSettings.HeaderTableSize;
                            decoder = new Decoder(8192, headerTableSize);
                        }

                        decoder.Decode(new BinaryReader(new MemoryStream(buffer, offset, dataLength)),
                            headerListener);
                        decoder.EndHeaderBlock();

                        if (rr is Request request)
                        {
                            var method = headerListener.Method;
                            var path = headerListener.Path;
                            if (method.Length == 0 || path.Length == 0)
                            {
                                throw new Exception("HTTP/2 Missing method or path");
                            }

                            request.HttpVersion = HttpVersion.Version20;
                            request.Method = method.GetString();
                            request.IsHttps = headerListener.Scheme == ProxyServer.UriSchemeHttps;
                            request.Authority = headerListener.Authority;
                            request.RequestUriString8 = path;

                            //request.RequestUri = headerListener.GetUri();
                        }
                        else
                        {
                            var response = (Response)rr;
                            response.HttpVersion = HttpVersion.Version20;

                            // todo: avoid string conversion
                            string statusHack = HttpHeader.Encoding.GetString(headerListener.Status.Span);
                            int.TryParse(statusHack, out int statusCode);
                            response.StatusCode = statusCode;
                            response.StatusDescription = string.Empty;
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptionFunc?.Invoke(new ProxyHttpException("Failed to decode HTTP/2 headers", ex, args));
                    }

                    if (!endHeaders)
                    {
                        Breakpoint();
                    }

                    if (endHeaders)
                    {
                        var tcs = new TaskCompletionSource<bool>();
                        rr.ReadHttp2BeforeHandlerTaskCompletionSource = tcs;

                        var handler = onBeforeRequestResponse(sessionArgs);
                        rr.Http2BeforeHandlerTask = handler;

                        if (handler == await Task.WhenAny(tcs.Task, handler))
                        {
                            rr.ReadHttp2BeforeHandlerTaskCompletionSource = null;
                            tcs.SetResult(true);

                            // Did the consumer request a synthetic streamed response during BeforeRequest?
                            if (isClient && sessionArgs.HttpClient.Response.StreamBodyWriter != null)
                            {
                                // do not forward the request upstream; answer the client directly.
                                syntheticStreams.Add(streamId);
                                await EmitSyntheticResponseAsync(sessionArgs, streamId, localSettings, input,
                                    clientWriteLock, serverSettingsRelayed, cancellationToken);
                            }
                            else if (isClient)
                            {
                                await SendHeader(remoteSettings, frameHeader, frameHeaderBuffer, rr, endStream, output,
                                    sessionArgs.IsPromise);
                            }
                            else
                            {
                                await lockedClientWrite(() => SendHeader(remoteSettings, frameHeader, frameHeaderBuffer,
                                    rr, endStream, output, sessionArgs.IsPromise));
                            }
                        }
                        else
                        {
                            rr.Http2IgnoreBodyFrames = true;
                        }

                        rr.Locked = true;
                    }

                    sendPacket = false;
                }
                else if (type == Http2FrameType.Continuation)
                {
                    // todo: implementing this type is mandatory for multi-part headers
                    Breakpoint();
                }
                else if (type == Http2FrameType.Settings)
                {
                    if (length % 6 != 0)
                    {
                        // https://httpwg.org/specs/rfc7540.html#SETTINGS
                        // 6.5. SETTINGS
                        // A SETTINGS frame with a length other than a multiple of 6 octets MUST be treated as a connection error (Section 5.4.1) of type FRAME_SIZE_ERROR
                        throw new ProxyHttpException("Invalid settings length", null, null);
                    }

                    int pos = 0;
                    while (pos < length)
                    {
                        int identifier = (buffer[pos++] << 8) + buffer[pos++];
                        int value =
 (buffer[pos++] << 24) + (buffer[pos++] << 16) + (buffer[pos++] << 8) + buffer[pos++];
                        if (identifier == 1 /*SETTINGS_HEADER_TABLE_SIZE*/)
                        {
                            //System.Diagnostics.Debug.WriteLine("HEADER SIZE CONN: " + connectionId + ", CLIENT: " + isClient + ", value: " + value);
                            remoteSettings.HeaderTableSize = value;
                        }
                        else if (identifier == 5 /*SETTINGS_MAX_FRAME_SIZE*/)
                        {
                            remoteSettings.MaxFrameSize = value;
                        }
                    }
                }

                if (type == Http2FrameType.RstStream)
                {
                    int errorCode = (buffer[0] << 24) + (buffer[1] << 16) + (buffer[2] << 8) + buffer[3];
                    if (streamId == 0)
                    {
                        // connection error
                        exceptionFunc?.Invoke(new ProxyHttpException("HTTP/2 connection error. Error code: " + errorCode, null, args));
                        return;
                    }
                    else
                    {
                        // stream error
                        sessions.TryRemove(streamId, out _);

                        if (errorCode != 8 /*cancel*/)
                        {
                            exceptionFunc?.Invoke(new ProxyHttpException("HTTP/2 stream error. Error code: " + errorCode, null, args));
                        }
                    }
                }

                if (endStream && rr == null)
                    throw new InvalidOperationException("An HTTP/2 end-stream frame has no request or response.");

                if (endStream && rr!.ReadHttp2BodyTaskCompletionSource != null)
                {
                    if (!rr.BodyAvailable)
                    {
                        var data = rr.Http2BodyData;
                        if (data == null)
                            throw new InvalidOperationException("HTTP/2 body completion was signaled without a buffer.");

                        var body = data.ToArray();

                        if (rr.ContentEncoding != null)
                        {
                            using (var ms = new MemoryStream())
                            {
                                using (var zip =
                                    DecompressionFactory.Create(CompressionUtil.CompressionNameToEnum(rr.ContentEncoding), new MemoryStream(body)))
                                {
                                    zip.CopyTo(ms);
                                }

                                body = ms.ToArray();
                            }
                        }

                        if (!rr.BodyAvailable)
                        {
                            rr.Body = body;
                        }
                    }

                    rr.IsBodyRead = true;
                    rr.IsBodyReceived = true;

                    var tcs = rr.ReadHttp2BodyTaskCompletionSource;
                    rr.ReadHttp2BodyTaskCompletionSource = null;

                    if (!tcs.Task.IsCompleted)
                    {
                        tcs.SetResult(true);
                    }

                    rr.Http2BodyData = null;

                    if (rr.Http2BeforeHandlerTask != null)
                    {
                        await rr.Http2BeforeHandlerTask;
                    }

                    if (args == null)
                        throw new InvalidOperationException("HTTP/2 body completion has no session.");

                    if (args.IsPromise)
                    {
                        Breakpoint();
                    }

                    if (isClient)
                        await SendBody(remoteSettings, rr, frameHeader, frameHeaderBuffer, buffer, output);
                    else
                        await lockedClientWrite(() =>
                            SendBody(remoteSettings, rr, frameHeader, frameHeaderBuffer, buffer, output));
                }

                if (!isClient && endStream)
                {
                    sessions.TryRemove(streamId, out _);
                    System.Diagnostics.Debug.WriteLine("REMOVED CONN: " + connectionId + ", CLIENT: " + isClient + ", STREAM: " + streamId + ", TYPE: " + type);
                }

                if (sendPacket)
                {
                    var frameLength = length;

                    async Task writeFrame()
                    {
                        // do not cancel the write operation
                        frameHeader.CopyToBuffer(frameHeaderBuffer);
                        await output.WriteAsync(frameHeaderBuffer, 0, frameHeaderBuffer.Length/*, cancellationToken*/);
                        await output.WriteAsync(buffer, 0, frameLength /*, cancellationToken*/);
                    }

                    if (isClient)
                        await writeFrame();
                    else
                        await lockedClientWrite(writeFrame);

                    // signal once the server's SETTINGS frame has actually reached the client, so a synthetic
                    // response on the other relay can safely send HEADERS afterwards.
                    if (!isClient && type == Http2FrameType.Settings && (flags & Http2FrameFlag.Ack) == 0)
                        serverSettingsRelayed.TrySetResult(true);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                /*using (var fs = new System.IO.FileStream($@"c:\temp\{connectionId}.{streamId}.dat", FileMode.Append))
                {
                    fs.Write(headerBuffer, 0, headerBuffer.Length);
                    fs.Write(buffer, 0, length);
                }*/
            }
        }

        [Conditional("DEBUG")]
        private static void Breakpoint()
        {
            // when this method is called something received which is not yet implemented
        }

        private static async Task SendHeader(Http2Settings settings, Http2FrameHeader frameHeader, byte[] frameHeaderBuffer, RequestResponseBase rr, bool endStream, Stream output, bool pushPromise)
        {
            var encoder = new Encoder(settings.HeaderTableSize);
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            if (rr.Priority.HasValue)
            {
                long p = rr.Priority.Value;
                writer.Write((byte)((p >> 32) & 0xff));
                writer.Write((byte)((p >> 24) & 0xff));
                writer.Write((byte)((p >> 16) & 0xff));
                writer.Write((byte)((p >> 8) & 0xff));
                writer.Write((byte)(p & 0xff));
            }

            if (rr is Request request)
            {
                var uri = request.RequestUri;
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderMethod, request.Method.GetByteString());
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderAuhtority, uri.Authority.GetByteString());
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderScheme, uri.Scheme.GetByteString());
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderPath, request.RequestUriString8, false,
                    HpackUtil.IndexType.None, false);
            }
            else
            {
                var response = (Response)rr;
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderStatus, response.StatusCode.ToString().GetByteString());
            }

            foreach (var header in rr.Headers)
            {
                encoder.EncodeHeader(writer, header.NameData, header.ValueData);
            }

            var data = ms.ToArray();
            int newLength = data.Length;

            frameHeader.Length = newLength;
            frameHeader.Type = pushPromise ? Http2FrameType.PushPromise : Http2FrameType.Headers;

            var flags = Http2FrameFlag.EndHeaders;
            if (endStream)
            {
                flags |= Http2FrameFlag.EndStream;
            }

            if (rr.Priority.HasValue)
            {
                flags |= Http2FrameFlag.Priority;
            }

            frameHeader.Flags = flags;

            // clear the padding flag
            //headerBuffer[4] = (byte)(flags & ~((int)Http2FrameFlag.Padded));

            // send the header
            frameHeader.CopyToBuffer(frameHeaderBuffer);
            await output.WriteAsync(frameHeaderBuffer, 0, frameHeaderBuffer.Length/*, cancellationToken*/);
            await output.WriteAsync(data, 0, data.Length /*, cancellationToken*/);
        }

        private static async Task SendBody(Http2Settings settings, RequestResponseBase rr, Http2FrameHeader frameHeader, byte[] frameHeaderBuffer, byte[] buffer, Stream output)
        {
            var body = rr.CompressBodyAndUpdateContentLength();
            await SendHeader(settings, frameHeader, frameHeaderBuffer, rr, !(rr.HasBody && rr.IsBodyRead), output, false);

            if (rr.HasBody && rr.IsBodyRead)
            {
                if (body == null)
                    throw new InvalidOperationException("An HTTP/2 body was marked as read but is unavailable.");

                int pos = 0;
                while (pos < body.Length)
                {
                    int bodyFrameLength = Math.Min(buffer.Length, body.Length - pos);
                    Buffer.BlockCopy(body, pos, buffer, 0, bodyFrameLength);
                    pos += bodyFrameLength;

                    frameHeader.Length = bodyFrameLength;
                    frameHeader.Type = Http2FrameType.Data;
                    frameHeader.Flags = pos < body.Length ? (Http2FrameFlag)0 : Http2FrameFlag.EndStream;

                    frameHeader.CopyToBuffer(frameHeaderBuffer);
                    await output.WriteAsync(frameHeaderBuffer, 0, frameHeaderBuffer.Length/*, cancellationToken*/);
                    await output.WriteAsync(buffer, 0, bodyFrameLength /*, cancellationToken*/);
                }
            }
        }

        /// <summary>
        ///     Sends the given bytes as one or more HTTP/2 DATA frames on the specified stream, splitting on
        ///     the peer's max frame size. An END_STREAM flag is set on the final frame when endStream is true.
        /// </summary>
        private static async Task SendData(Http2FrameHeader frameHeader, byte[] frameHeaderBuffer, int streamId,
            byte[] data, bool endStream, int maxFrameSize, Stream output)
        {
            if (maxFrameSize <= 0) maxFrameSize = 16384;

            frameHeader.StreamId = streamId;
            frameHeader.Type = Http2FrameType.Data;

            if (data.Length == 0)
            {
                frameHeader.Length = 0;
                frameHeader.Flags = endStream ? Http2FrameFlag.EndStream : (Http2FrameFlag)0;
                frameHeader.CopyToBuffer(frameHeaderBuffer);
                await output.WriteAsync(frameHeaderBuffer, 0, frameHeaderBuffer.Length);
                return;
            }

            var pos = 0;
            while (pos < data.Length)
            {
                var frameLength = Math.Min(maxFrameSize, data.Length - pos);
                var isLastFrame = pos + frameLength >= data.Length;

                frameHeader.Length = frameLength;
                frameHeader.Flags = isLastFrame && endStream ? Http2FrameFlag.EndStream : (Http2FrameFlag)0;
                frameHeader.CopyToBuffer(frameHeaderBuffer);

                await output.WriteAsync(frameHeaderBuffer, 0, frameHeaderBuffer.Length);
                await output.WriteAsync(data, pos, frameLength);

                pos += frameLength;
            }
        }

        /// <summary>
        ///     Emits a proxy-generated (synthetic) response to the client on the given stream without contacting
        ///     the server. The response body is streamed from the consumer's RespondStreaming delegate as DATA
        ///     frames, so it is never buffered. HTTP/2 frames the body with END_STREAM (Transfer-Encoding is not
        ///     used), so the chunked header is stripped.
        /// </summary>
        private static async Task EmitSyntheticResponseAsync(SessionEventArgs args, int streamId,
            Http2Settings settings, Stream clientStream, SemaphoreSlim clientWriteLock,
            TaskCompletionSource<bool> serverSettingsRelayed, CancellationToken cancellationToken)
        {
            var response = args.HttpClient.Response;

            // HTTP/2 does not use chunked transfer-encoding; body framing is done via DATA frames + END_STREAM.
            response.Headers.RemoveHeader(KnownHeaders.TransferEncoding);

            var frameHeader = new Http2FrameHeader { StreamId = streamId };
            var frameHeaderBuffer = new byte[9];

            // The client must receive the connection SETTINGS frame (relayed from the server) before any
            // HEADERS frame, otherwise it treats the connection as a protocol error. Wait for that relay,
            // but honor cancellation so we never hang if the server never sends SETTINGS / closes early.
            await serverSettingsRelayed.Task.WaitAsync(cancellationToken);

            // send the response headers first; the body (if any) follows as DATA frames.
            await clientWriteLock.WaitAsync(cancellationToken);
            try
            {
                await SendHeader(settings, frameHeader, frameHeaderBuffer, response, false, clientStream, false);
            }
            finally
            {
                clientWriteLock.Release();
            }

            var bodyWriter = new Http2BodyStreamWriter(streamId, clientStream, clientWriteLock, cancellationToken);

            if (response.StreamBodyWriter != null) await response.StreamBodyWriter(bodyWriter, cancellationToken);

            await bodyWriter.CompleteAsync();

            response.IsBodySent = true;
        }

        private static async Task<int> ForceRead(Stream input, byte[] buffer, int offset, int bytesToRead,
            CancellationToken cancellationToken)
        {
            int totalRead = 0;
            while (bytesToRead > 0)
            {
                int read = await input.ReadAsync(buffer, offset, bytesToRead, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
                bytesToRead -= read;
                offset += read;
            }

            return totalRead;
        }


        class Http2Settings
        {
            public int HeaderTableSize { get; set; } = 4096;

            public int MaxFrameSize { get; set; } = 16384;
        }

        /// <summary>
        ///     A write-only stream handed to consumers of RespondStreaming over HTTP/2. Each write is emitted as
        ///     one or more DATA frames on the given stream (split at the guaranteed-safe 16384 byte frame size).
        ///     The terminating empty END_STREAM DATA frame is sent by <see cref="CompleteAsync" />.
        ///     Writes are serialized against the other relay via a shared lock so frames never interleave.
        /// </summary>
        private sealed class Http2BodyStreamWriter : Stream
        {
            // every HTTP/2 endpoint must accept frames up to 16384 octets, so this is always safe.
            private const int SafeMaxFrameSize = 16384;

            private readonly int streamId;
            private readonly Stream clientStream;
            private readonly SemaphoreSlim clientWriteLock;
            private readonly CancellationToken cancellationToken;
            private readonly Http2FrameHeader frameHeader = new Http2FrameHeader();
            private readonly byte[] frameHeaderBuffer = new byte[9];
            private bool completed;

            internal Http2BodyStreamWriter(int streamId, Stream clientStream, SemaphoreSlim clientWriteLock,
                CancellationToken cancellationToken)
            {
                this.streamId = streamId;
                this.clientStream = clientStream;
                this.clientWriteLock = clientWriteLock;
                this.cancellationToken = cancellationToken;
            }

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override Task FlushAsync(CancellationToken ct)
            {
                return Task.CompletedTask;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
            }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                if (count == 0) return;

                var data = new byte[count];
                Buffer.BlockCopy(buffer, offset, data, 0, count);

                await clientWriteLock.WaitAsync(cancellationToken);
                try
                {
                    await SendData(frameHeader, frameHeaderBuffer, streamId, data, false, SafeMaxFrameSize,
                        clientStream);
                }
                finally
                {
                    clientWriteLock.Release();
                }
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
                CancellationToken ct = default)
            {
                if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(buffer, out var segment) &&
                    segment.Array != null)
                    await WriteAsync(segment.Array, segment.Offset, segment.Count, ct);
                else
                {
                    var array = buffer.ToArray();
                    await WriteAsync(array, 0, array.Length, ct);
                }
            }

            internal async Task CompleteAsync()
            {
                if (completed) return;
                completed = true;

                await clientWriteLock.WaitAsync(cancellationToken);
                try
                {
                    await SendData(frameHeader, frameHeaderBuffer, streamId, Array.Empty<byte>(), true,
                        SafeMaxFrameSize, clientStream);
                }
                finally
                {
                    clientWriteLock.Release();
                }
            }
        }

        class MyHeaderListener : IHeaderListener
        {
            private readonly Action<ByteString, ByteString> addHeaderFunc;

            public ByteString Method { get; private set; }

            public ByteString Status { get; private set; }

            public ByteString Authority { get; private set; }

            private ByteString scheme;

            public ByteString Path { get; private set; }

            public string Scheme
            {
                get
                {
                    if (scheme.Equals(ProxyServer.UriSchemeHttp8))
                    {
                        return ProxyServer.UriSchemeHttp;
                    }

                    if (scheme.Equals(ProxyServer.UriSchemeHttps8))
                    {
                        return ProxyServer.UriSchemeHttps;
                    }

                    return string.Empty;
                }
            }

            public MyHeaderListener(Action<ByteString, ByteString> addHeaderFunc)
            {
                this.addHeaderFunc = addHeaderFunc;
            }

            public void AddHeader(ByteString name, ByteString value, bool sensitive)
            {
                if (name.Span[0] == ':')
                {
                    string nameStr = Encoding.ASCII.GetString(name.Span);
                    switch (nameStr)
                    {
                        case ":method":
                            Method = value;
                            return;
                        case ":authority":
                            Authority = value;
                            return;
                        case ":scheme":
                            scheme = value;
                            return;
                        case ":path":
                            Path = value;
                            return;
                        case ":status":
                            Status = value;
                            return;
                    }
                }

                addHeaderFunc(name, value);
            }

            public Uri GetUri()
            {
                if (Authority.Length == 0)
                {
                    // todo
                    Authority = HttpHeader.Encoding.GetBytes("abc.abc");
                }

                var bytes = new byte[scheme.Length + 3 + Authority.Length + Path.Length];
                scheme.Span.CopyTo(bytes);
                int idx = scheme.Length;
                bytes[idx++] = (byte)':';
                bytes[idx++] = (byte)'/';
                bytes[idx++] = (byte)'/';
                Authority.Span.CopyTo(bytes.AsSpan(idx, Authority.Length));
                idx += Authority.Length;
                Path.Span.CopyTo(bytes.AsSpan(idx, Path.Length));

                return new Uri(HttpHeader.Encoding.GetString(bytes));
            }
        }
    }
}
#endifParseOptions.0.jsonœ
XD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http\ConnectRequest.cs›using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.StreamExtended;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     The tcp tunnel Connect request.
/// </summary>
public class ConnectRequest : Request
{
    internal ConnectRequest(ByteString authority)
    {
        Method = "CONNECT";
        Authority = authority;
    }

    public TunnelType TunnelType { get; internal set; }

    public ClientHelloInfo? ClientHelloInfo { get; set; }
}ParseOptions.0.jsonü
YD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http\ConnectResponse.cs¨using System;
using System.Net;
using Titanium.Web.Proxy.StreamExtended;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     The tcp tunnel connect response object.
/// </summary>
public class ConnectResponse : Response
{
    public ServerHelloInfo? ServerHelloInfo { get; set; }

    /// <summary>
    ///     Creates a successful CONNECT response
    /// </summary>
    /// <param name="httpVersion"></param>
    /// <returns></returns>
    internal static ConnectResponse CreateSuccessfulConnectResponse(Version httpVersion)
    {
        var response = new ConnectResponse
        {
            HttpVersion = httpVersion,
            StatusCode = (int)HttpStatusCode.OK,
            StatusDescription = "Connection Established"
        };

        return response;
    }
}ParseOptions.0.json§
WD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http\HeaderBuilder.cs≥using System;
using System.Buffers;
using System.IO;
using System.Text;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Http;

internal class HeaderBuilder
{
    private readonly MemoryStream stream = new();

    public void WriteRequestLine(string httpMethod, string httpUrl, Version version)
    {
        // "{httpMethod} {httpUrl} HTTP/{version.Major}.{version.Minor}";

        Write(httpMethod);
        Write(" ");
        Write(httpUrl);
        Write(" HTTP/");
        Write(version.Major.ToString());
        Write(".");
        Write(version.Minor.ToString());
        WriteLine();
    }

    public void WriteResponseLine(Version version, int statusCode, string statusDescription)
    {
        // "HTTP/{version.Major}.{version.Minor} {statusCode} {statusDescription}";

        Write("HTTP/");
        Write(version.Major.ToString());
        Write(".");
        Write(version.Minor.ToString());
        Write(" ");
        Write(statusCode.ToString());
        Write(" ");
        Write(statusDescription);
        WriteLine();
    }

    public void WriteHeaders(HeaderCollection headers, bool sendProxyAuthorization = true,
        string? upstreamProxyUserName = null, string? upstreamProxyPassword = null)
    {
        if (upstreamProxyUserName != null && upstreamProxyPassword != null)
        {
            WriteHeader(HttpHeader.ProxyConnectionKeepAlive);
            WriteHeader(HttpHeader.GetProxyAuthorizationHeader(upstreamProxyUserName, upstreamProxyPassword));
        }

        foreach (var header in headers)
            if (sendProxyAuthorization || !KnownHeaders.ProxyAuthorization.Equals(header.Name))
                WriteHeader(header);

        WriteLine();
    }

    public void WriteHeader(HttpHeader header)
    {
        Write(header.Name);
        Write(": ");
        Write(header.Value);
        WriteLine();
    }

    public void WriteLine()
    {
        var data = ProxyConstants.NewLineBytes;
        stream.Write(data, 0, data.Length);
    }

    public void Write(string str)
    {
        var encoding = HttpHeader.Encoding;

#if NET6_0_OR_GREATER
        var buf = ArrayPool<byte>.Shared.Rent(encoding.GetMaxByteCount(str.Length));
        try
        {
            var span = new Span<byte>(buf);
            int bytes = encoding.GetBytes(str.AsSpan(), span);
            stream.Write(span.Slice(0, bytes));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
#else
        var data = encoding.GetBytes(str);
        stream.Write(data, 0, data.Length);
#endif
    }

    public ArraySegment<byte> GetBuffer()
    {
        if (!stream.TryGetBuffer(out var buffer))
            throw new InvalidOperationException("The header buffer is unexpectedly unavailable.");

        return buffer;
    }

    public string GetString(Encoding encoding)
    {
        var buffer = GetBuffer();
        if (buffer.Array == null)
            throw new InvalidOperationException("The header buffer has no backing array.");

        return encoding.GetString(buffer.Array, buffer.Offset, buffer.Count);
    }
}ParseOptions.0.jsonÀL
ZD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http\HeaderCollection.cs◊Kusing System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     The http header collection.
/// </summary>
[TypeConverter(typeof(ExpandableObjectConverter))]
public class HeaderCollection : IEnumerable<HttpHeader>
{
    private readonly Dictionary<string, HttpHeader> headers;

    private readonly Dictionary<string, List<HttpHeader>> nonUniqueHeaders;

    /// <summary>
    ///     Initializes a new instance of the <see cref="HeaderCollection" /> class.
    /// </summary>
    public HeaderCollection()
    {
        headers = new Dictionary<string, HttpHeader>(StringComparer.OrdinalIgnoreCase);
        nonUniqueHeaders = new Dictionary<string, List<HttpHeader>>(StringComparer.OrdinalIgnoreCase);
        Headers = new ReadOnlyDictionary<string, HttpHeader>(headers);
        NonUniqueHeaders = new ReadOnlyDictionary<string, List<HttpHeader>>(nonUniqueHeaders);
    }

    /// <summary>
    ///     Unique Request header collection.
    /// </summary>
    public ReadOnlyDictionary<string, HttpHeader> Headers { get; }

    /// <summary>
    ///     Non Unique headers.
    /// </summary>
    public ReadOnlyDictionary<string, List<HttpHeader>> NonUniqueHeaders { get; }

    /// <summary>
    ///     Returns an enumerator that iterates through the collection.
    /// </summary>
    /// <returns>
    ///     An enumerator that can be used to iterate through the collection.
    /// </returns>
    public IEnumerator<HttpHeader> GetEnumerator()
    {
        return headers.Values.Concat(nonUniqueHeaders.Values.SelectMany(x => x)).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <summary>
    ///     True if header exists
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public bool HeaderExists(string name)
    {
        return headers.ContainsKey(name) || nonUniqueHeaders.ContainsKey(name);
    }

    /// <summary>
    ///     Returns all headers with given name if exists
    ///     Returns null if doesn't exist
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public List<HttpHeader>? GetHeaders(string name)
    {
        if (headers.ContainsKey(name))
            return new List<HttpHeader>
            {
                headers[name]
            };

        if (nonUniqueHeaders.ContainsKey(name)) return new List<HttpHeader>(nonUniqueHeaders[name]);

        return null;
    }

    public HttpHeader? GetFirstHeader(string name)
    {
        if (headers.TryGetValue(name, out var header)) return header;

        if (nonUniqueHeaders.TryGetValue(name, out var h)) return h.FirstOrDefault();

        return null;
    }

    internal HttpHeader? GetFirstHeader(KnownHeader name)
    {
        if (headers.TryGetValue(name.String, out var header)) return header;

        if (nonUniqueHeaders.TryGetValue(name.String, out var h)) return h.FirstOrDefault();

        return null;
    }

    /// <summary>
    ///     Returns all headers
    /// </summary>
    /// <returns></returns>
    public List<HttpHeader> GetAllHeaders()
    {
        var result = new List<HttpHeader>();

        result.AddRange(headers.Select(x => x.Value));
        result.AddRange(nonUniqueHeaders.SelectMany(x => x.Value));

        return result;
    }

    /// <summary>
    ///     Add a new header with given name and value
    /// </summary>
    /// <param name="name"></param>
    /// <param name="value"></param>
    public void AddHeader(string name, string value)
    {
        AddHeader(new HttpHeader(name, value));
    }

    internal void AddHeader(KnownHeader name, string value)
    {
        AddHeader(new HttpHeader(name, value));
    }

    internal void AddHeader(KnownHeader name, KnownHeader value)
    {
        AddHeader(new HttpHeader(name, value));
    }

    /// <summary>
    ///     Adds the given header object to Request
    /// </summary>
    /// <param name="newHeader"></param>
    public void AddHeader(HttpHeader newHeader)
    {
        // if header exist in non-unique header collection add it there
        if (nonUniqueHeaders.TryGetValue(newHeader.Name, out var list))
        {
            list.Add(newHeader);
            return;
        }

        // if header is already in unique header collection then move both to non-unique collection
        if (headers.TryGetValue(newHeader.Name, out var existing))
        {
            headers.Remove(newHeader.Name);

            nonUniqueHeaders.Add(newHeader.Name, new List<HttpHeader>
            {
                existing,
                newHeader
            });
        }
        else
        {
            // add to unique header collection
            headers.Add(newHeader.Name, newHeader);
        }
    }

    /// <summary>
    ///     Adds the given header objects to Request
    /// </summary>
    /// <param name="newHeaders"></param>
    public void AddHeaders(IEnumerable<HttpHeader>? newHeaders)
    {
        if (newHeaders == null) return;

        foreach (var header in newHeaders) AddHeader(header);
    }

    /// <summary>
    ///     Adds the given header objects to Request
    /// </summary>
    /// <param name="newHeaders"></param>
    public void AddHeaders(IEnumerable<KeyValuePair<string, string>>? newHeaders)
    {
        if (newHeaders == null) return;

        foreach (var header in newHeaders) AddHeader(header.Key, header.Value);
    }

    /// <summary>
    ///     Adds the given header objects to Request
    /// </summary>
    /// <param name="newHeaders"></param>
    public void AddHeaders(IEnumerable<KeyValuePair<string, HttpHeader>>? newHeaders)
    {
        if (newHeaders == null) return;

        foreach (var header in newHeaders)
        {
            if (header.Key != header.Value.Name)
                throw new Exception(
                    "Header name mismatch. Key and the name of the HttpHeader object should be the same.");

            AddHeader(header.Value);
        }
    }

    /// <summary>
    ///     removes all headers with given name
    /// </summary>
    /// <param name="headerName"></param>
    /// <returns>
    ///     True if header was removed
    ///     False if no header exists with given name
    /// </returns>
    public bool RemoveHeader(string headerName)
    {
        var result = headers.Remove(headerName);

        // do not convert to '||' expression to avoid lazy evaluation
        if (nonUniqueHeaders.Remove(headerName)) result = true;

        return result;
    }

    /// <summary>
    ///     removes all headers with given name
    /// </summary>
    /// <param name="headerName"></param>
    /// <returns>
    ///     True if header was removed
    ///     False if no header exists with given name
    /// </returns>
    public bool RemoveHeader(KnownHeader headerName)
    {
        var result = headers.Remove(headerName.String);

        // do not convert to '||' expression to avoid lazy evaluation
        if (nonUniqueHeaders.Remove(headerName.String)) result = true;

        return result;
    }

    /// <summary>
    ///     Removes given header object if it exist
    /// </summary>
    /// <param name="header">Returns true if header exists and was removed </param>
    public bool RemoveHeader(HttpHeader header)
    {
        if (headers.ContainsKey(header.Name))
        {
            if (headers[header.Name].Equals(header))
            {
                headers.Remove(header.Name);
                return true;
            }
        }
        else if (nonUniqueHeaders.ContainsKey(header.Name))
        {
            if (nonUniqueHeaders[header.Name].RemoveAll(x => x.Equals(header)) > 0) return true;
        }

        return false;
    }

    /// <summary>
    ///     Removes all the headers.
    /// </summary>
    public void Clear()
    {
        headers.Clear();
        nonUniqueHeaders.Clear();
    }

    internal string? GetHeaderValueOrNull(KnownHeader headerName)
    {
        if (headers.TryGetValue(headerName.String, out var header)) return header.Value;

        return null;
    }

    internal void SetOrAddHeaderValue(KnownHeader headerName, string? value)
    {
        if (value == null)
        {
            RemoveHeader(headerName);
            return;
        }

        if (headers.TryGetValue(headerName.String, out var header))
            header.SetValue(value);
        else
            headers.Add(headerName.String, new HttpHeader(headerName, value));
    }

    internal void SetOrAddHeaderValue(KnownHeader headerName, KnownHeader value)
    {
        if (headers.TryGetValue(headerName.String, out var header))
            header.SetValue(value);
        else
            headers.Add(headerName.String, new HttpHeader(headerName, value));
    }

    /// <summary>
    ///     Fix proxy specific headers
    /// </summary>
    internal void FixProxyHeaders()
    {
        // If proxy-connection close was returned inform to close the connection
        var proxyHeader = GetHeaderValueOrNull(KnownHeaders.ProxyConnection);
        RemoveHeader(KnownHeaders.ProxyConnection);

        if (proxyHeader != null) SetOrAddHeaderValue(KnownHeaders.Connection, proxyHeader);
    }
}ParseOptions.0.jsonÌ
VD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http\HeaderParser.cs˝using System;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Http;

internal static class HeaderParser
{
    internal static async ValueTask ReadHeaders(ILineStream reader, HeaderCollection headerCollection,
        CancellationToken cancellationToken)
    {
        string? tmpLine;
        while (!string.IsNullOrEmpty(tmpLine = await reader.ReadLineAsync(cancellationToken)))
        {
            var colonIndex = tmpLine!.IndexOf(':');
            if (colonIndex == -1) throw new Exception("Header line should contain a colon character.");

            var headerName = tmpLine.AsSpan(0, colonIndex).ToString();
            var headerValue = tmpLine.AsSpan(colonIndex + 1).TrimStart().ToString();
            headerCollection.AddHeader(headerName, headerValue);
        }
    }
}ParseOptions.0.json⁄
[D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http\InternalDataStore.csÂusing System.Collections.Generic;

namespace Titanium.Web.Proxy.Http;

internal class InternalDataStore : Dictionary<string, object>
{
    public bool TryGetValueAs<T>(string key, out T? value)
    {
        if (TryGetValue(key, out var storedValue))
        {
            value = (T)storedValue;
            return true;
        }

        value = default;
        return false;
    }

    public T GetAs<T>(string key)
    {
        return (T)this[key];
    }
}ParseOptions.0.json’
UD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http\KnownHeader.csÊusing System;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http;

public class KnownHeader
{
    public string String;
    internal ByteString String8;

    private KnownHeader(string str)
    {
        String8 = (ByteString)str;
        String = str;
    }

    public override string ToString()
    {
        return String;
    }

    internal bool Equals(ReadOnlySpan<char> value)
    {
        return String.AsSpan().EqualsIgnoreCase(value);
    }

    internal bool Equals(string? value)
    {
        return String.EqualsIgnoreCase(value);
    }

    public static implicit operator KnownHeader(string str)
    {
        return new(str);
    }
}ParseOptions.0.jsonÜ
VD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http\KnownHeaders.csñnamespace Titanium.Web.Proxy.Http;

/// <summary>
///     Well known http headers.
/// </summary>
public static class KnownHeaders
{
    // Both
    public static KnownHeader Connection = "Connection";
    public static KnownHeader ConnectionClose = "close";
    public static KnownHeader ConnectionKeepAlive = "keep-alive";

    public static KnownHeader ContentLength = "Content-Length";
    public static KnownHeader ContentLengthHttp2 = "content-length";

    public static KnownHeader ContentType = "Content-Type";
    public static KnownHeader ContentTypeCharset = "charset";
    public static KnownHeader ContentTypeBoundary = "boundary";

    public static KnownHeader Upgrade = "Upgrade";
    public static KnownHeader UpgradeWebsocket = "websocket";

    // Request headers
    public static KnownHeader AcceptEncoding = "Accept-Encoding";

    public static KnownHeader Authorization = "Authorization";

    public static KnownHeader Expect = "Expect";
    public static KnownHeader Expect100Continue = "100-continue";

    public static KnownHeader Host = "Host";

    public static KnownHeader ProxyAuthorization = "Proxy-Authorization";
    public static KnownHeader ProxyAuthorizationBasic = "basic";

    public static KnownHeader ProxyConnection = "Proxy-Connection";
    public static KnownHeader ProxyConnectionClose = "close";

    // Response headers
    public static KnownHeader ContentEncoding = "Content-Encoding";
    public static KnownHeader ContentEncodingDeflate = "deflate";
    public static KnownHeader ContentEncodingGzip = "gzip";
    public static KnownHeader ContentEncodingBrotli = "br";

    public static KnownHeader Location = "Location";

    public static KnownHeader ProxyAuthenticate = "Proxy-Authenticate";

    public static KnownHeader TransferEncoding = "Transfer-Encoding";
    public static KnownHeader TransferEncodingChunked = "chunked";
}ParseOptions.0.jsonà@
QD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http\Request.csù?using System;
using System.ComponentModel;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     Http(s) request object
/// </summary>
[TypeConverter(typeof(ExpandableObjectConverter))]
public class Request : RequestResponseBase
{
    private ByteString requestUriString8;

    /// <summary>
    ///     Request Method.
    /// </summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>
    ///     Is Https?
    /// </summary>
    public bool IsHttps { get; internal set; }

    internal ByteString RequestUriString8
    {
        get => requestUriString8;
        set
        {
            requestUriString8 = value;
            var scheme = UriExtensions.GetScheme(value);
            if (scheme.Length > 0) IsHttps = scheme.Equals(ProxyServer.UriSchemeHttps8);
        }
    }

    internal ByteString Authority { get; set; }

    /// <summary>
    ///     Request HTTP Uri.
    /// </summary>
    public Uri RequestUri
    {
        get
        {
            var url = Url;
            try
            {
                return new Uri(url);
            }
            catch (Exception ex)
            {
                throw new Exception($"Invalid URI: '{url}'", ex);
            }
        }
        set => Url = value.OriginalString;
    }

    /// <summary>
    ///     The request url as it is in the HTTP header
    /// </summary>
    public string Url
    {
        get
        {
            var url = RequestUriString8.GetString();
            if (UriExtensions.GetScheme(RequestUriString8).Length == 0)
            {
                var hostAndPath = Host ?? Authority.GetString();

                if (url.StartsWith("/"))
                {
                    hostAndPath += url;
                }

                url = string.Concat(IsHttps ? "https://" : "http://", hostAndPath);
            }

            return url;
        }
        set => RequestUriString = value;
    }

    /// <summary>
    ///     The request uri as it is in the HTTP header
    /// </summary>
    public string RequestUriString
    {
        get => RequestUriString8.GetString();
        set
        {
            RequestUriString8 = (ByteString)value;

            var scheme = UriExtensions.GetScheme(RequestUriString8);
            if (scheme.Length > 0 && Host != null)
            {
                var uri = new Uri(value);
                Host = uri.Authority;
                Authority = ByteString.Empty;
            }
        }
    }

    /// <summary>
    ///     Has request body?
    /// </summary>
    public override bool HasBody
    {
        get
        {
            var contentLength = ContentLength;

            // If content length is set to 0 the request has no body
            if (contentLength == 0) return false;

            // Has body only if request is chunked or content length >0
            if (IsChunked || contentLength > 0) return true;

            // has body if POST and when version is http/1.0
            if (Method == "POST" && HttpVersion == HttpHeader.Version10) return true;

            return false;
        }
    }

    /// <summary>
    ///     Http hostname header value if exists.
    ///     Note: Changing this does NOT change host in RequestUri.
    ///     Users can set new RequestUri separately.
    /// </summary>
    public string? Host
    {
        get => Headers.GetHeaderValueOrNull(KnownHeaders.Host);
        set => Headers.SetOrAddHeaderValue(KnownHeaders.Host, value);
    }

    /// <summary>
    ///     Does this request has a 100-continue header?
    /// </summary>
    public bool ExpectContinue
    {
        get
        {
            var headerValue = Headers.GetHeaderValueOrNull(KnownHeaders.Expect);
            return KnownHeaders.Expect100Continue.Equals(headerValue);
        }
    }

    /// <summary>
    ///     Does this request contain multipart/form-data?
    /// </summary>
    public bool IsMultipartFormData => ContentType?.StartsWith("multipart/form-data") == true;

    /// <summary>
    ///     Cancels the client HTTP request without sending to server.
    ///     This should be set when API user responds with custom response.
    /// </summary>
    internal bool CancelRequest { get; set; }

    /// <summary>
    ///     Does this request has an upgrade to websocket header?
    /// </summary>
    public bool UpgradeToWebSocket
    {
        get
        {
            var headerValue = Headers.GetHeaderValueOrNull(KnownHeaders.Upgrade);

            if (headerValue == null) return false;

            return headerValue.EqualsIgnoreCase(KnownHeaders.UpgradeWebsocket.String);
        }
    }

    /// <summary>
    ///     Did server respond positively for 100 continue request?
    /// </summary>
    public bool ExpectationSucceeded { get; internal set; }

    /// <summary>
    ///     Did server respond negatively for 100 continue request?
    /// </summary>
    public bool ExpectationFailed { get; internal set; }

    /// <summary>
    ///     Gets the header text.
    /// </summary>
    public override string HeaderText
    {
        get
        {
            var headerBuilder = new HeaderBuilder();
            headerBuilder.WriteRequestLine(Method, RequestUriString, HttpVersion);
            headerBuilder.WriteHeaders(Headers);
            return headerBuilder.GetString(HttpHeader.Encoding);
        }
    }

    internal override void EnsureBodyAvailable(bool throwWhenNotReadYet = true)
    {
        if (BodyInternal != null) return;

        // GET request don't have a request body to read
        if (!HasBody)
            throw new BodyNotFoundException("Request don't have a body. " +
                                            "Please verify that this request is a Http POST/PUT/PATCH and request " +
                                            "content length is greater than zero before accessing the body.");

        if (!IsBodyRead)
        {
            if (Locked) throw new Exception("You cannot get the request body after request is made to server.");

            if (throwWhenNotReadYet)
                throw new Exception("Request body is not read yet. " +
                                    "Use SessionEventArgs.GetRequestBody() or SessionEventArgs.GetRequestBodyAsString() " +
                                    "method to read the request body.");
        }
    }

    internal static void ParseRequestLine(string httpCmd, out string method, out ByteString requestUri,
        out Version version)
    {
        var firstSpace = httpCmd.IndexOf(' ');
        if (firstSpace == -1)
            // does not contain at least 2 parts
            throw new Exception("Invalid HTTP request line: " + httpCmd);

        var lastSpace = httpCmd.LastIndexOf(' ');

        // break up the line into three components (method, remote URL & Http Version)

        // Find the request Verb
        method = httpCmd.Substring(0, firstSpace);
        if (!IsAllUpper(method)) method = method.ToUpper();

        version = HttpHeader.Version11;

        if (firstSpace == lastSpace)
        {
            requestUri = (ByteString)httpCmd.AsSpan(firstSpace + 1).ToString();
        }
        else
        {
            requestUri = (ByteString)httpCmd.AsSpan(firstSpace + 1, lastSpace - firstSpace - 1).ToString();

            // parse the HTTP version
            var httpVersion = httpCmd.AsSpan(lastSpace + 1);

            if (httpVersion.EqualsIgnoreCase("HTTP/1.0".AsSpan(0))) version = HttpHeader.Version10;
        }
    }

    private static bool IsAllUpper(string input)
    {
        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (ch < 'A' || ch > 'Z') return false;
        }

        return true;
    }
}ParseOptions.0.json„H
]D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http\RequestResponseBase.csÏGusing System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     Abstract base class for similar objects shared by both request and response objects.
/// </summary>
public abstract class RequestResponseBase
{
    /// <summary>
    ///     Cached body as string.
    /// </summary>
    private string? bodyString;

    internal Task? Http2BeforeHandlerTask;

    internal MemoryStream? Http2BodyData;

    internal bool Http2IgnoreBodyFrames;

    /// <summary>
    ///     Priority used only in HTTP/2
    /// </summary>
    internal long? Priority;

    internal TaskCompletionSource<bool>? ReadHttp2BeforeHandlerTaskCompletionSource;

    internal TaskCompletionSource<bool>? ReadHttp2BodyTaskCompletionSource;

    /// <summary>
    ///     Cached body content as byte array.
    /// </summary>
    protected byte[]? BodyInternal { get; private set; }

    /// <summary>
    ///     Store whether the original request/response has body or not, since the user may change the parameters.
    ///     We need this detail to syphon out attached tcp connection for reuse.
    /// </summary>
    internal bool OriginalHasBody { get; set; }

    /// <summary>
    ///     Store original content-length, since the user setting the body may change the parameters.
    ///     We need this detail to tcp syphon out attached connection for reuse.
    /// </summary>
    internal long OriginalContentLength { get; set; }

    /// <summary>
    ///     Store whether the original request/response was a chunked body, since the user may change the parameters.
    ///     We need this detail to syphon out attached tcp connection for reuse.
    /// </summary>
    internal bool OriginalIsChunked { get; set; }

    /// <summary>
    ///     Store whether the original request/response content-encoding, since the user may change the parameters.
    ///     We need this detail to syphon out attached tcp connection for reuse.
    /// </summary>
    internal string? OriginalContentEncoding { get; set; }

    /// <summary>
    ///     Keeps the body data after the session is finished.
    /// </summary>
    public bool KeepBody { get; set; }

    /// <summary>
    ///     Http Version.
    /// </summary>
    public Version HttpVersion { get; set; } = HttpHeader.VersionUnknown;

    /// <summary>
    ///     Collection of all headers.
    /// </summary>
    public HeaderCollection Headers { get; } = new();

    /// <summary>
    ///     Length of the body.
    /// </summary>
    public long ContentLength
    {
        get
        {
            var headerValue = Headers.GetHeaderValueOrNull(KnownHeaders.ContentLength);

            if (headerValue == null) return -1;

            if (long.TryParse(headerValue, out var contentLen) && contentLen >= 0) return contentLen;

            return -1;
        }

        set
        {
            if (value >= 0)
            {
                Headers.SetOrAddHeaderValue(
                    HttpVersion >= HttpHeader.Version20
                        ? KnownHeaders.ContentLengthHttp2
                        : KnownHeaders.ContentLength, value.ToString());
                IsChunked = false;
            }
            else
            {
                Headers.RemoveHeader(KnownHeaders.ContentLength);
            }
        }
    }

    /// <summary>
    ///     Content encoding for this request/response.
    /// </summary>
    public string? ContentEncoding => Headers.GetHeaderValueOrNull(KnownHeaders.ContentEncoding)?.Trim();

    /// <summary>
    ///     Encoding for this request/response.
    /// </summary>
    public Encoding Encoding => HttpHelper.GetEncodingFromContentType(ContentType);

    /// <summary>
    ///     Content-type of the request/response.
    /// </summary>
    public string? ContentType
    {
        get => Headers.GetHeaderValueOrNull(KnownHeaders.ContentType);
        set => Headers.SetOrAddHeaderValue(KnownHeaders.ContentType, value);
    }

    /// <summary>
    ///     Is body send as chunked bytes.
    /// </summary>
    public bool IsChunked
    {
        get
        {
            var headerValue = Headers.GetHeaderValueOrNull(KnownHeaders.TransferEncoding);
            return headerValue != null && headerValue.ContainsIgnoreCase(KnownHeaders.TransferEncodingChunked.String);
        }

        set
        {
            if (value)
            {
                Headers.SetOrAddHeaderValue(KnownHeaders.TransferEncoding, KnownHeaders.TransferEncodingChunked);
                ContentLength = -1;
            }
            else
            {
                Headers.RemoveHeader(KnownHeaders.TransferEncoding);
            }
        }
    }

    /// <summary>
    ///     The header text.
    /// </summary>
    public abstract string HeaderText { get; }

    /// <summary>
    ///     Body as byte array
    /// </summary>
    [Browsable(false)]
    public byte[] Body
    {
        get
        {
            EnsureBodyAvailable();
            return BodyInternal!;
        }

        internal set
        {
            BodyInternal = value;
            bodyString = null;

            // If there is a content length header update it
            UpdateContentLength();
        }
    }

    /// <summary>
    ///     Has the request/response body?
    /// </summary>
    public abstract bool HasBody { get; }

    /// <summary>
    ///     Body as string.
    ///     Use the encoding specified to decode the byte[] data to string
    /// </summary>
    [Browsable(false)]
    public string BodyString => bodyString ??= Encoding.GetString(Body);

    /// <summary>
    ///     Was the body read by user?
    /// </summary>
    public bool IsBodyRead { get; internal set; }

    /// <summary>
    ///     Is the request/response no more modifiable by user (user callbacks complete?)
    ///     Also if user set this as a custom response then this should be true.
    /// </summary>
    internal bool Locked { get; set; }

    internal bool BodyAvailable => BodyInternal != null;

    internal bool IsBodyReceived { get; set; }

    internal bool IsBodySent { get; set; }

    internal abstract void EnsureBodyAvailable(bool throwWhenNotReadYet = true);

    /// <summary>
    ///     get the compressed body from given bytes
    /// </summary>
    /// <param name="encodingType"></param>
    /// <param name="body"></param>
    /// <returns></returns>
    internal byte[] GetCompressedBody(HttpCompression encodingType, byte[] body)
    {
        using (var ms = new MemoryStream())
        {
            using (var zip = CompressionFactory.Create(encodingType, ms))
            {
                zip.Write(body, 0, body.Length);
            }

            return ms.ToArray();
        }
    }

    internal byte[]? CompressBodyAndUpdateContentLength()
    {
        if (!IsBodyRead && BodyInternal == null) return null;

        var isChunked = IsChunked;
        var contentEncoding = ContentEncoding;

        if (HasBody)
        {
            var body = Body;
            if (contentEncoding != null && body != null)
            {
                body = GetCompressedBody(CompressionUtil.CompressionNameToEnum(contentEncoding), body);

                if (isChunked == false)
                    ContentLength = body.Length;
                else
                    ContentLength = -1;
            }

            return body;
        }

        ContentLength = 0;
        return null;
    }

    internal void UpdateContentLength()
    {
        ContentLength = IsChunked ? -1 : BodyInternal?.Length ?? 0;
    }

    /// <summary>
    ///     Set values for original headers using current headers.
    /// </summary>
    internal void SetOriginalHeaders()
    {
        OriginalHasBody = HasBody;
        OriginalContentLength = ContentLength;
        OriginalIsChunked = IsChunked;
        OriginalContentEncoding = ContentEncoding;
    }

    /// <summary>
    ///     Copy original header values.
    /// </summary>
    /// <param name="requestResponseBase"></param>
    internal void SetOriginalHeaders(RequestResponseBase requestResponseBase)
    {
        OriginalHasBody = requestResponseBase.OriginalHasBody;
        OriginalContentLength = requestResponseBase.OriginalContentLength;
        OriginalIsChunked = requestResponseBase.OriginalIsChunked;
        OriginalContentEncoding = requestResponseBase.OriginalContentEncoding;
    }

    /// <summary>
    ///     Finish the session
    /// </summary>
    internal void FinishSession()
    {
        if (!KeepBody)
        {
            BodyInternal = null;
            bodyString = null;
        }
    }

    public override string ToString()
    {
        return HeaderText;
    }
}ParseOptions.0.jsonî.
RD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http\Response.cs®-using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     Http(s) response object
/// </summary>
[TypeConverter(typeof(ExpandableObjectConverter))]
public class Response : RequestResponseBase
{
    /// <summary>
    ///     Constructor.
    /// </summary>
    public Response()
    {
    }

    /// <summary>
    ///     Constructor.
    /// </summary>
    public Response(byte[] body)
    {
        Body = body;
    }

    /// <summary>
    ///     Response Status Code.
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    ///     Response Status description.
    /// </summary>
    public string StatusDescription { get; set; } = string.Empty;

    internal string RequestMethod { get; set; } = string.Empty;

    /// <summary>
    ///     When set via SessionEventArgs.RespondStreaming, this delegate is invoked to produce the response
    ///     body as a live stream (without buffering it in memory). The provided stream frames writes as HTTP/1.1
    ///     chunks when the response is chunked, or writes raw bytes when a Content-Length is set.
    /// </summary>
    internal Func<Stream, CancellationToken, Task>? StreamBodyWriter { get; set; }

    /// <summary>
    ///     Has response body?
    /// </summary>
    public override bool HasBody
    {
        get
        {
            if (RequestMethod == "HEAD") return false;

            var contentLength = ContentLength;

            // If content length is set to 0 the response has no body
            if (contentLength == 0) return false;

            // Has body only if response is chunked or content length >0
            // If none are true then check if connection:close header exist, if so write response until server or client terminates the connection
            if (IsChunked || contentLength > 0 || !KeepAlive) return true;

            if (ContentLength == -1 && HttpVersion == HttpHeader.Version20) return true;

            // has response if connection:keep-alive header exist and when version is http/1.0
            // Because in Http 1.0 server can return a response without content-length (expectation being client would read until end of stream)
            if (KeepAlive && HttpVersion == HttpHeader.Version10) return true;

            return false;
        }
    }

    /// <summary>
    ///     Keep the connection alive?
    /// </summary>
    public bool KeepAlive
    {
        get
        {
            var headerValue = Headers.GetHeaderValueOrNull(KnownHeaders.Connection);

            // HTTP/1.0 is non-persistent by default: the connection is only reusable when the
            // response explicitly opts in with "Connection: keep-alive". Treating a plain HTTP/1.0
            // response as keep-alive would let us pool a connection the server is about to close.
            if (HttpVersion == HttpHeader.Version10)
                return headerValue != null &&
                       headerValue.EqualsIgnoreCase(KnownHeaders.ConnectionKeepAlive.String);

            // HTTP/1.1 (and HTTP/2) are persistent by default unless the response asks to close.
            if (headerValue != null && headerValue.EqualsIgnoreCase(KnownHeaders.ConnectionClose.String))
                return false;

            return true;
        }
    }

    /// <summary>
    ///     Gets the header text.
    /// </summary>
    public override string HeaderText
    {
        get
        {
            var headerBuilder = new HeaderBuilder();
            headerBuilder.WriteResponseLine(HttpVersion, StatusCode, StatusDescription);
            headerBuilder.WriteHeaders(Headers);
            return headerBuilder.GetString(HttpHeader.Encoding);
        }
    }

    internal override void EnsureBodyAvailable(bool throwWhenNotReadYet = true)
    {
        if (BodyInternal != null) return;

        if (!HasBody) throw new BodyNotFoundException("Response don't have a body.");

        if (!IsBodyRead && throwWhenNotReadYet)
            throw new Exception("Response body is not read yet. " +
                                "Use SessionEventArgs.GetResponseBody() or SessionEventArgs.GetResponseBodyAsString() " +
                                "method to read the response body.");
    }

    internal static void ParseResponseLine(string httpStatus, out Version version, out int statusCode,
        out string statusDescription)
    {
        var firstSpace = httpStatus.IndexOf(' ');
        if (firstSpace == -1) throw new Exception("Invalid HTTP status line: " + httpStatus);

        var httpVersion = httpStatus.AsSpan(0, firstSpace);

        version = HttpHeader.Version11;
        if (httpVersion.EqualsIgnoreCase("HTTP/1.0".AsSpan())) version = HttpHeader.Version10;

        var secondSpace = httpStatus.IndexOf(' ', firstSpace + 1);
        if (secondSpace != -1)
        {
#if NET6_0_OR_GREATER
            statusCode = int.Parse(httpStatus.AsSpan(firstSpace + 1, secondSpace - firstSpace - 1));
#else
            statusCode = int.Parse(httpStatus.AsSpan(firstSpace + 1, secondSpace - firstSpace - 1).ToString());
#endif
            statusDescription = httpStatus.AsSpan(secondSpace + 1).ToString();
        }
        else
        {
#if NET6_0_OR_GREATER
            statusCode = int.Parse(httpStatus.AsSpan(firstSpace + 1));
#else
            statusCode = int.Parse(httpStatus.AsSpan(firstSpace + 1).ToString());
#endif
            statusDescription = string.Empty;
        }
    }
}ParseOptions.0.json›
cD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http\Responses\GenericResponse.cs‡using System.Net;

namespace Titanium.Web.Proxy.Http.Responses;

/// <summary>
///     Anything¬†other¬†than¬†a¬†200¬†or¬†302 http¬†response.
/// </summary>
public class GenericResponse : Response
{
    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="status"></param>
    public GenericResponse(HttpStatusCode status)
    {
        StatusCode = (int)status;
        StatusDescription = Get(StatusCode) ?? string.Empty;
    }

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="statusCode"></param>
    /// <param name="statusDescription"></param>
    public GenericResponse(int statusCode, string statusDescription)
    {
        StatusCode = statusCode;
        StatusDescription = statusDescription;
    }

    internal static string? Get(int code)
    {
        switch (code)
        {
            case 100: return "Continue";
            case 101: return "Switching Protocols";
            case 102: return "Processing";
            case 103: return "Early Hints";

            case 200: return "OK";
            case 201: return "Created";
            case 202: return "Accepted";
            case 203: return "Non-Authoritative Information";
            case 204: return "No Content";
            case 205: return "Reset Content";
            case 206: return "Partial Content";
            case 207: return "Multi-Status";
            case 208: return "Already Reported";
            case 226: return "IM Used";

            case 300: return "Multiple Choices";
            case 301: return "Moved Permanently";
            case 302: return "Found";
            case 303: return "See Other";
            case 304: return "Not Modified";
            case 305: return "Use Proxy";
            case 307: return "Temporary Redirect";
            case 308: return "Permanent Redirect";

            case 400: return "Bad Request";
            case 401: return "Unauthorized";
            case 402: return "Payment Required";
            case 403: return "Forbidden";
            case 404: return "Not Found";
            case 405: return "Method Not Allowed";
            case 406: return "Not Acceptable";
            case 407: return "Proxy Authentication Required";
            case 408: return "Request Timeout";
            case 409: return "Conflict";
            case 410: return "Gone";
            case 411: return "Length Required";
            case 412: return "Precondition Failed";
            case 413: return "Request Entity Too Large";
            case 414: return "Request-Uri Too Long";
            case 415: return "Unsupported Media Type";
            case 416: return "Requested Range Not Satisfiable";
            case 417: return "Expectation Failed";
            case 421: return "Misdirected Request";
            case 422: return "Unprocessable Entity";
            case 423: return "Locked";
            case 424: return "Failed Dependency";
            case 426: return "Upgrade Required"; // RFC 2817
            case 428: return "Precondition Required";
            case 429: return "Too Many Requests";
            case 431: return "Request Header Fields Too Large";
            case 451: return "Unavailable For Legal Reasons";

            case 500: return "Internal Server Error";
            case 501: return "Not Implemented";
            case 502: return "Bad Gateway";
            case 503: return "Service Unavailable";
            case 504: return "Gateway Timeout";
            case 505: return "Http Version Not Supported";
            case 506: return "Variant Also Negotiates";
            case 507: return "Insufficient Storage";
            case 508: return "Loop Detected";
            case 510: return "Not Extended";
            case 511: return "Network Authentication Required";
        }

        return null;
    }
}ParseOptions.0.json˚
^D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http\Responses\OkResponse.csÉusing System.Net;

namespace Titanium.Web.Proxy.Http.Responses;

/// <summary>
///     The http 200 Ok response.
/// </summary>
public sealed class OkResponse : Response
{
    /// <summary>
    ///     Constructor.
    /// </summary>
    public OkResponse()
    {
        StatusCode = (int)HttpStatusCode.OK;
        StatusDescription = "OK";
    }

    /// <summary>
    ///     Constructor.
    /// </summary>
    public OkResponse(byte[] body) : this()
    {
        Body = body;
    }
}ParseOptions.0.jsonΩ
dD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http\Responses\RedirectResponse.csøusing System.Net;

namespace Titanium.Web.Proxy.Http.Responses;

/// <summary>
///     The http redirect response.
/// </summary>
public sealed class RedirectResponse : Response
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="RedirectResponse" /> class.
    /// </summary>
    public RedirectResponse()
    {
        StatusCode = (int)HttpStatusCode.Found;
        StatusDescription = "Found";
    }
}ParseOptions.0.json‰
TD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Http\TunnelType.cswnamespace Titanium.Web.Proxy.Http;

public enum TunnelType
{
    Unknown,
    Https,
    Websocket,
    Http2
}ParseOptions.0.jsonî
VD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Models\ByteString.cs§using System;
using System.Text;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy.Models;

internal struct ByteString : IEquatable<ByteString>
{
    public static ByteString Empty = new(ReadOnlyMemory<byte>.Empty);

    public ReadOnlyMemory<byte> Data { get; }

    public ReadOnlySpan<byte> Span => Data.Span;

    public int Length => Data.Length;

    public ByteString(ReadOnlyMemory<byte> data)
    {
        Data = data;
    }

    public override bool Equals(object? obj)
    {
        return obj is ByteString other && Equals(other);
    }

    public bool Equals(ByteString other)
    {
        return Data.Span.SequenceEqual(other.Data.Span);
    }

    public int IndexOf(byte value)
    {
        return Span.IndexOf(value);
    }

    public ByteString Slice(int start)
    {
        return Data.Slice(start);
    }

    public ByteString Slice(int start, int length)
    {
        return Data.Slice(start, length);
    }

    public override int GetHashCode()
    {
        return Data.GetHashCode();
    }

    public override string ToString()
    {
        return this.GetString();
    }

    public static explicit operator ByteString(string str)
    {
        return new(Encoding.ASCII.GetBytes(str));
    }

    public static implicit operator ByteString(byte[] data)
    {
        return new(data);
    }

    public static implicit operator ByteString(ReadOnlyMemory<byte> data)
    {
        return new(data);
    }

    public byte this[int i] => Span[i];
}ParseOptions.0.json€
aD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Models\ExplicitProxyEndPoint.cs‡using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy.Models;

/// <summary>
///     A proxy endpoint that the client is aware of.
///     So client application know that it is communicating with a proxy server.
/// </summary>
[DebuggerDisplay("Explicit: {IpAddress}:{Port}")]
public class ExplicitProxyEndPoint : ProxyEndPoint
{
    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="ipAddress">Listening IP address.</param>
    /// <param name="port">Listening port.</param>
    /// <param name="decryptSsl">Should we decrypt ssl?</param>
    public ExplicitProxyEndPoint(IPAddress ipAddress, int port, bool decryptSsl = true) : base(ipAddress, port,
        decryptSsl)
    {
    }

    internal bool IsSystemHttpProxy { get; set; }

    internal bool IsSystemHttpsProxy { get; set; }

    /// <summary>
    ///     Intercept tunnel connect request.
    ///     Valid only for explicit endpoints.
    ///     Set the <see cref="TunnelConnectSessionEventArgs.DecryptSsl" /> property to false if this HTTP connect request
    ///     shouldn't be decrypted and instead be relayed.
    /// </summary>
    public event AsyncEventHandler<TunnelConnectSessionEventArgs>? BeforeTunnelConnectRequest;

    /// <summary>
    ///     Intercept tunnel connect response.
    ///     Valid only for explicit endpoints.
    /// </summary>
    public event AsyncEventHandler<TunnelConnectSessionEventArgs>? BeforeTunnelConnectResponse;

    internal async Task InvokeBeforeTunnelConnectRequest(ProxyServer proxyServer,
        TunnelConnectSessionEventArgs connectArgs, ExceptionHandler? exceptionFunc)
    {
        if (BeforeTunnelConnectRequest != null)
            await BeforeTunnelConnectRequest.InvokeAsync(proxyServer, connectArgs, exceptionFunc);
    }

    internal async Task InvokeBeforeTunnelConnectResponse(ProxyServer proxyServer,
        TunnelConnectSessionEventArgs connectArgs, ExceptionHandler? exceptionFunc, bool isClientHello = false)
    {
        if (BeforeTunnelConnectResponse != null)
        {
            connectArgs.IsHttpsConnect = isClientHello;
            await BeforeTunnelConnectResponse.InvokeAsync(proxyServer, connectArgs, exceptionFunc);
        }
    }
}ParseOptions.0.jsonß
YD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Models\ExternalProxy.cs¥using System;
using System.Net;

namespace Titanium.Web.Proxy.Models;

/// <summary>
///     An upstream proxy this proxy uses if any.
/// </summary>
public class ExternalProxy : IExternalProxy
{
    private static readonly Lazy<NetworkCredential> defaultCredentials =
        new(() => CredentialCache.DefaultNetworkCredentials);

    private string? password;

    private string? userName;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ExternalProxy" /> class.
    /// </summary>
    public ExternalProxy()
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="ExternalProxy" /> class.
    /// </summary>
    /// <param name="hostName">Name of the host.</param>
    /// <param name="port">The port.</param>
    public ExternalProxy(string hostName, int port)
    {
        HostName = hostName;
        Port = port;
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="ExternalProxy" /> class.
    /// </summary>
    /// <param name="hostName">Name of the host.</param>
    /// <param name="port">The port.</param>
    /// <param name="userName">Name of the user.</param>
    /// <param name="password">The password.</param>
    public ExternalProxy(string hostName, int port, string userName, string password)
    {
        HostName = hostName;
        Port = port;
        UserName = userName;
        Password = password;
    }

    /// <summary>
    ///     Use default windows credentials?
    /// </summary>
    public bool UseDefaultCredentials { get; set; }

    /// <summary>
    ///     Bypass this proxy for connections to localhost?
    /// </summary>
    public bool BypassLocalhost { get; set; }

    public ExternalProxyType ProxyType { get; set; }

    public bool ProxyDnsRequests { get; set; }

    /// <summary>
    ///     Username.
    /// </summary>
    public string? UserName
    {
        get => UseDefaultCredentials ? defaultCredentials.Value.UserName : userName;
        set
        {
            userName = value;

            if (defaultCredentials.Value.UserName != userName) UseDefaultCredentials = false;
        }
    }

    /// <summary>
    ///     Password.
    /// </summary>
    public string? Password
    {
        get => UseDefaultCredentials ? defaultCredentials.Value.Password : password;
        set
        {
            password = value;

            if (defaultCredentials.Value.Password != password) UseDefaultCredentials = false;
        }
    }

    /// <summary>
    ///     Host name.
    /// </summary>
    public string HostName { get; set; } = string.Empty;

    /// <summary>
    ///     Port.
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    ///     returns data in Hostname:port format.
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return $"{HostName}:{Port}";
    }
}

public enum ExternalProxyType
{
    /// <summary>A HTTP/HTTPS proxy server.</summary>
    Http,

    /// <summary>A SOCKS4[A] proxy server.</summary>
    Socks4,

    /// <summary>A SOCKS5 proxy server.</summary>
    Socks5
}ParseOptions.0.json¸
[D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Models\HttpCompression.csánamespace Titanium.Web.Proxy.Compression;

internal enum HttpCompression
{
    Unsupported,
    Gzip,
    Deflate,
    Brotli
}ParseOptions.0.json°!
VD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Models\HttpHeader.cs± using System;
using System.Net;
using System.Text;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Models;

/// <summary>
///     Http Header object used by proxy
/// </summary>
public class HttpHeader
{
    /// <summary>
    ///     HPACK: Header Compression for HTTP/2
    ///     Section 4.1. Calculating Table Size
    ///     The additional 32 octets account for an estimated overhead associated with an entry.
    /// </summary>
    public const int HttpHeaderOverhead = 32;

#if NET6_0_OR_GREATER
    internal static Version VersionUnknown => HttpVersion.Unknown;
#else
    internal static Version VersionUnknown { get; } = new(0, 0);
#endif

    internal static Version Version10 => HttpVersion.Version10;

    internal static Version Version11 => HttpVersion.Version11;

#if NET6_0_OR_GREATER
    internal static Version Version20 => HttpVersion.Version20;
#else
    internal static Version Version20 { get; } = new(2, 0);
#endif

    internal static readonly Encoding DefaultEncoding = Encoding.GetEncoding("ISO-8859-1");

    public static Encoding Encoding => DefaultEncoding;

    internal static readonly HttpHeader ProxyConnectionKeepAlive = new("Proxy-Connection", "keep-alive");

    private string? nameString;

    private string? valueString;

    /// <summary>
    ///     Initialize a new instance.
    /// </summary>
    /// <param name="name">Header name.</param>
    /// <param name="value">Header value.</param>
    public HttpHeader(string name, string value)
    {
        if (string.IsNullOrEmpty(name)) throw new Exception("Name cannot be null or empty");

        nameString = name.Trim();
        NameData = nameString.GetByteString();

        valueString = value.Trim();
        ValueData = valueString.GetByteString();
    }

    internal HttpHeader(KnownHeader name, string value)
    {
        nameString = name.String;
        NameData = name.String8;

        valueString = value.Trim();
        ValueData = valueString.GetByteString();
    }

    internal HttpHeader(KnownHeader name, KnownHeader value)
    {
        nameString = name.String;
        NameData = name.String8;

        valueString = value.String;
        ValueData = value.String8;
    }

    internal HttpHeader(ByteString name, ByteString value)
    {
        if (name.Length == 0) throw new Exception("Name cannot be empty");

        NameData = name;
        ValueData = value;
    }

    private protected HttpHeader(ByteString name, ByteString value, bool headerEntry)
    {
        // special header entry created in inherited class with empty name
        NameData = name;
        ValueData = value;
    }

    /// <summary>
    ///     Header Name.
    /// </summary>
    public string Name => nameString ??= NameData.GetString();

    internal ByteString NameData { get; }

    /// <summary>
    ///     Header Value.
    /// </summary>
    public string Value => valueString ??= ValueData.GetString();

    internal ByteString ValueData { get; private set; }

    public int Size => Name.Length + Value.Length + HttpHeaderOverhead;

    internal static int SizeOf(ByteString name, ByteString value)
    {
        return name.Length + value.Length + HttpHeaderOverhead;
    }

    internal void SetValue(string value)
    {
        valueString = value;
        ValueData = value.GetByteString();
    }

    internal void SetValue(KnownHeader value)
    {
        valueString = value.String;
        ValueData = value.String8;
    }

    /// <summary>
    ///     Returns header as a valid header string.
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return $"{Name}: {Value}";
    }

    internal static HttpHeader GetProxyAuthorizationHeader(string? userName, string? password)
    {
        var result = new HttpHeader(KnownHeaders.ProxyAuthorization,
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}")));
        return result;
    }
}ParseOptions.0.jsonÃ
ZD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Models\IExternalProxy.csÿnamespace Titanium.Web.Proxy.Models;

public interface IExternalProxy
{
    /// <summary>
    ///     Use default windows credentials?
    /// </summary>
    bool UseDefaultCredentials { get; set; }

    /// <summary>
    ///     Bypass this proxy for connections to localhost?
    /// </summary>
    bool BypassLocalhost { get; set; }

    ExternalProxyType ProxyType { get; set; }

    bool ProxyDnsRequests { get; set; }

    /// <summary>
    ///     Username.
    /// </summary>
    string? UserName { get; set; }

    /// <summary>
    ///     Password.
    /// </summary>
    string? Password { get; set; }

    /// <summary>
    ///     Host name.
    /// </summary>
    string HostName { get; set; }

    /// <summary>
    ///     Port.
    /// </summary>
    int Port { get; set; }

    string ToString();
}ParseOptions.0.json∑
WD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Models\KnownMethod.cs∆namespace Titanium.Web.Proxy.Helpers;

internal enum KnownMethod
{
    Unknown,
    Invalid,

    // RFC 7231: Hypertext Transfer Protocol (HTTP/1.1): Semantics and Content
    Connect,
    Delete,
    Get,
    Head,
    Options,
    Post,
    Put,
    Trace,

    // RFC 7540: Hypertext Transfer Protocol Version 2
    Pri,

    // RFC 5789: PATCH Method for HTTP
    Patch,

    // RFC 3744: Web Distributed Authoring and Versioning (WebDAV) Access Control Protocol
    Acl,

    // RFC 3253: Versioning Extensions to WebDAV (Web Distributed Authoring and Versioning)
    BaselineControl,
    Checkin,
    Checkout,
    Label,
    Merge,
    Mkactivity,
    Mkworkspace,
    Report,
    Unckeckout,
    Update,
    VersionControl,

    // RFC 3648: Web Distributed Authoring and Versioning (WebDAV) Ordered Collections Protocol
    Orderpatch,

    // RFC 4437: Web Distributed Authoring and Versioning (WebDAV): Redirect Reference Resources
    Mkredirectref,
    Updateredirectref,

    // RFC 4791: Calendaring Extensions to WebDAV (CalDAV)
    Mkcalendar,

    // RFC 4918: HTTP Extensions for Web Distributed Authoring and Versioning (WebDAV)
    Copy,
    Lock,
    Mkcol,
    Move,
    Propfind,
    Proppatch,
    Unlock,

    // RFC 5323: Web Distributed Authoring and Versioning (WebDAV) SEARCH
    Search,

    // 	RFC 5842: Binding Extensions to Web Distributed Authoring and Versioning (WebDAV)
    Bind,
    Rebind,
    Unbind,

    // Internet Draft snell-link-method: HTTP Link and Unlink Methods
    Link,
    Unlink
}ParseOptions.0.json¢
fD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Models\ProxyAuthenticationContext.cs¢namespace Titanium.Web.Proxy.Models;

public enum ProxyAuthenticationResult
{
    /// <summary>
    ///     Indicates the authentication request was successful
    /// </summary>
    Success,

    /// <summary>
    ///     Indicates the authentication request failed
    /// </summary>
    Failure,

    /// <summary>
    ///     Indicates that this stage of the authentication request succeeded
    ///     And a second pass of the handshake needs to occur
    /// </summary>
    ContinuationNeeded
}

/// <summary>
///     A context container for authentication flows
/// </summary>
public class ProxyAuthenticationContext
{
    /// <summary>
    ///     The result of the current authentication request
    /// </summary>
    public ProxyAuthenticationResult Result { get; set; }

    /// <summary>
    ///     An optional continuation token to return to the caller if set
    /// </summary>
    public string? Continuation { get; set; }

    public static ProxyAuthenticationContext Failed()
    {
        return new ProxyAuthenticationContext
        {
            Result = ProxyAuthenticationResult.Failure,
            Continuation = null
        };
    }

    public static ProxyAuthenticationContext Succeeded()
    {
        return new ProxyAuthenticationContext
        {
            Result = ProxyAuthenticationResult.Success,
            Continuation = null
        };
    }
}ParseOptions.0.jsonÙ

YD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Models\ProxyEndPoint.csÅ
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.Models;

/// <summary>
///     An abstract endpoint where the proxy listens
/// </summary>
public abstract class ProxyEndPoint
{
    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="ipAddress"></param>
    /// <param name="port"></param>
    /// <param name="decryptSsl"></param>
    protected ProxyEndPoint(IPAddress ipAddress, int port, bool decryptSsl)
    {
        IpAddress = ipAddress;
        Port = port;
        DecryptSsl = decryptSsl;
    }

    /// <summary>
    ///     underlying TCP Listener object
    /// </summary>
    internal TcpListener? Listener { get; set; }

    /// <summary>
    ///     Ip Address we are listening.
    /// </summary>
    public IPAddress IpAddress { get; }

    /// <summary>
    ///     Port we are listening.
    /// </summary>
    public int Port { get; internal set; }

    /// <summary>
    ///     Enable SSL?
    /// </summary>
    public bool DecryptSsl { get; }

    /// <summary>
    ///     Generic certificate to use for SSL decryption.
    /// </summary>
    public X509Certificate2? GenericCertificate { get; set; }
}ParseOptions.0.json§
]D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Models\ProxyProtocolType.cs≠using System;

namespace Titanium.Web.Proxy.Models;

[Flags]
public enum ProxyProtocolType
{
    /// <summary>
    ///     The none
    /// </summary>
    None = 0,

    /// <summary>
    ///     HTTP
    /// </summary>
    Http = 1,

    /// <summary>
    ///     HTTPS
    /// </summary>
    Https = 2,

    /// <summary>
    ///     Both HTTP and HTTPS
    /// </summary>
    AllHttp = Http | Https
}ParseOptions.0.json¯
]D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Models\RequestStatusInfo.csÅusing System;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Helpers;

internal struct RequestStatusInfo
{
    public string Method { get; set; }

    public ByteString RequestUri { get; set; }

    public Version Version { get; set; }

    public bool IsEmpty()
    {
        return Method == null && RequestUri.Length == 0 && Version == null;
    }
}ParseOptions.0.jsonﬁ
^D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Models\ResponseStatusInfo.csÊusing System;

namespace Titanium.Web.Proxy.Helpers;

internal struct ResponseStatusInfo
{
    public Version Version { get; set; }

    public int StatusCode { get; set; }

    public string Description { get; set; }
}ParseOptions.0.json–
^D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Models\SocksProxyEndPoint.csÿusing System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy.Models;

/// <summary>
///     A proxy end point client is not aware of.
///     Useful when requests are redirected to this proxy end point through port forwarding via router.
/// </summary>
[DebuggerDisplay("SOCKS: {IpAddress}:{Port}")]
public class SocksProxyEndPoint : TransparentBaseProxyEndPoint
{
    /// <summary>
    ///     Initialize a new instance.
    /// </summary>
    /// <param name="ipAddress">Listening Ip address.</param>
    /// <param name="port">Listening port.</param>
    /// <param name="decryptSsl">Should we decrypt ssl?</param>
    public SocksProxyEndPoint(IPAddress ipAddress, int port, bool decryptSsl = true) : base(ipAddress, port,
        decryptSsl)
    {
        GenericCertificateName = "localhost";
    }

    /// <summary>
    ///     Name of the Certificate need to be sent (same as the hostname we want to proxy).
    ///     This is valid only when UseServerNameIndication is set to false.
    /// </summary>
    public override string GenericCertificateName { get; set; }

    /// <summary>
    ///     Before Ssl authentication this event is fired.
    /// </summary>
    public event AsyncEventHandler<BeforeSslAuthenticateEventArgs>? BeforeSslAuthenticate;

    internal override async Task InvokeBeforeSslAuthenticate(ProxyServer proxyServer,
        BeforeSslAuthenticateEventArgs connectArgs, ExceptionHandler? exceptionFunc)
    {
        if (BeforeSslAuthenticate != null)
            await BeforeSslAuthenticate.InvokeAsync(proxyServer, connectArgs, exceptionFunc);
    }
}ParseOptions.0.jsonŸ
hD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Models\TransparentBaseProxyEndPoint.cs◊
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Models;

public abstract class TransparentBaseProxyEndPoint : ProxyEndPoint
{
    protected TransparentBaseProxyEndPoint(IPAddress ipAddress, int port, bool decryptSsl) : base(ipAddress, port,
        decryptSsl)
    {
    }

    /// <summary>
    ///     The hostname of the generic certificate to negotiate SSL.
    ///     This will be only used when Sever Name Indication (SNI) is not supported by client,
    ///     or when it does not indicate any host name.
    /// </summary>
    public abstract string GenericCertificateName { get; set; }

    /// <summary>
    ///     Optional fixed upstream server to forward all traffic on this endpoint to.
    ///     Only the TCP connection target is changed; the original host is still used
    ///     for TLS SNI/certificate validation and the HTTP Host header.
    /// </summary>
    public string? ForwardHost { get; set; }

    /// <summary>
    ///     Optional fixed upstream port. When null the original request port is used.
    /// </summary>
    public int? ForwardPort { get; set; }

    internal abstract Task InvokeBeforeSslAuthenticate(ProxyServer proxyServer,
        BeforeSslAuthenticateEventArgs connectArgs, ExceptionHandler? exceptionFunc);
}ParseOptions.0.jsonË
dD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Models\TransparentProxyEndPoint.csÍusing System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy.Models;

/// <summary>
///     A proxy end point client is not aware of.
///     Useful when requests are redirected to this proxy end point through port forwarding via router.
/// </summary>
[DebuggerDisplay("Transparent: {IpAddress}:{Port}")]
public class TransparentProxyEndPoint : TransparentBaseProxyEndPoint
{
    /// <summary>
    ///     Initialize a new instance.
    /// </summary>
    /// <param name="ipAddress">Listening Ip address.</param>
    /// <param name="port">Listening port.</param>
    /// <param name="decryptSsl">Should we decrypt ssl?</param>
    public TransparentProxyEndPoint(IPAddress ipAddress, int port, bool decryptSsl = true) : base(ipAddress, port,
        decryptSsl)
    {
        GenericCertificateName = "localhost";
    }

    /// <summary>
    ///     Name of the Certificate need to be sent (same as the hostname we want to proxy).
    ///     This is valid only when UseServerNameIndication is set to false.
    /// </summary>
    public override string GenericCertificateName { get; set; }

    /// <summary>
    ///     Before Ssl authentication this event is fired.
    /// </summary>
    public event AsyncEventHandler<BeforeSslAuthenticateEventArgs>? BeforeSslAuthenticate;

    internal override async Task InvokeBeforeSslAuthenticate(ProxyServer proxyServer,
        BeforeSslAuthenticateEventArgs connectArgs, ExceptionHandler? exceptionFunc)
    {
        if (BeforeSslAuthenticate != null)
            await BeforeSslAuthenticate.InvokeAsync(proxyServer, connectArgs, exceptionFunc);
    }
}ParseOptions.0.jsonµ
iD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\BufferPool\DefaultBufferPool.cs≤using System.Buffers;

namespace Titanium.Web.Proxy.StreamExtended.BufferPool;

/// <summary>
///     A concrete IBufferPool implementation backed by the shared <see cref="System.Buffers.ArrayPool{T}" />.
///     It is thread-safe and handles both fixed and variable size buffer requests.
///     Note: rented buffers may be larger than the requested size (ArrayPool bucketing) and are not
///     cleared on return, so callers must not assume the buffer length equals the requested size.
/// </summary>
internal class DefaultBufferPool : IBufferPool
{
    /// <summary>
    ///     Buffer size in bytes used throughout this proxy.
    ///     Default value is 8192 bytes.
    /// </summary>
    public int BufferSize { get; set; } = 8192;

    /// <summary>
    ///     Gets a buffer with a default size.
    /// </summary>
    /// <returns></returns>
    public byte[] GetBuffer()
    {
        return ArrayPool<byte>.Shared.Rent(BufferSize);
    }

    /// <summary>
    ///     Gets a buffer.
    /// </summary>
    /// <param name="bufferSize">Size of the buffer.</param>
    /// <returns></returns>
    public byte[] GetBuffer(int bufferSize)
    {
        return ArrayPool<byte>.Shared.Rent(bufferSize);
    }

    /// <summary>
    ///     Returns the buffer.
    /// </summary>
    /// <param name="buffer">The buffer.</param>
    public void ReturnBuffer(byte[] buffer)
    {
        ArrayPool<byte>.Shared.Return(buffer);
    }

    public void Dispose()
    {
        //Nothing to dispose. But need for the interface
    }
}ParseOptions.0.json≥
cD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\BufferPool\IBufferPool.cs∂using System;

namespace Titanium.Web.Proxy.StreamExtended.BufferPool;

/// <summary>
///     Use this interface to implement custom buffer pool.
///     To use the default buffer pool implementation use DefaultBufferPool class.
/// </summary>
public interface IBufferPool : IDisposable
{
    int BufferSize { get; }

    byte[] GetBuffer();

    byte[] GetBuffer(int bufferSize);

    void ReturnBuffer(byte[] buffer);
}ParseOptions.0.jsonµ2
ZD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\HttpWebClient.cs¡1using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     Used to communicate with the server over HTTP(S)
/// </summary>
public class HttpWebClient
{
    private TcpServerConnection? connection;

    internal HttpWebClient(ConnectRequest? connectRequest, Request request, Lazy<int> processIdFunc)
    {
        ConnectRequest = connectRequest;
        Request = request;
        Response = new Response();
        ProcessId = processIdFunc;
    }

    /// <summary>
    ///     Connection to server
    /// </summary>
    internal TcpServerConnection Connection
    {
        get
        {
            if (connection == null) throw new Exception("Connection is null");

            return connection;
        }
    }

    internal bool HasConnection => connection != null;

    /// <summary>
    ///     Should we close the server connection at the end of this HTTP request/response session.
    /// </summary>
    internal bool CloseServerConnection { get; set; }

    /// <summary>
    ///     Stores internal data for the session.
    /// </summary>
    internal InternalDataStore Data { get; } = new();

    /// <summary>
    ///     Gets or sets the user data.
    /// </summary>
    public object? UserData { get; set; }

    /// <summary>
    ///     Override UpStreamEndPoint for this request; Local NIC via request is made
    /// </summary>
    public IPEndPoint? UpStreamEndPoint { get; set; }

    /// <summary>
    ///     Headers passed with Connect.
    /// </summary>
    public ConnectRequest? ConnectRequest { get; }

    /// <summary>
    ///     Web Request.
    /// </summary>
    public Request Request { get; }

    /// <summary>
    ///     Web Response.
    /// </summary>
    public Response Response { get; internal set; }

    /// <summary>
    ///     PID of the process that is created the current session when client is running in this machine
    ///     If client is remote then this will return
    /// </summary>
    public Lazy<int> ProcessId { get; internal set; }

    /// <summary>
    ///     Is Https?
    /// </summary>
    public bool IsHttps => Request.IsHttps;

    /// <summary>
    ///     Set the tcp connection to server used by this webclient
    /// </summary>
    /// <param name="serverConnection">Instance of <see cref="TcpServerConnection" /></param>
    internal void SetConnection(TcpServerConnection serverConnection)
    {
        serverConnection.LastAccess = DateTime.UtcNow;
        connection = serverConnection;
    }

    /// <summary>
    ///     Prepare and send the http(s) request
    /// </summary>
    /// <returns></returns>
    internal async Task SendRequest(bool enable100ContinueBehaviour, bool isTransparent,
        CancellationToken cancellationToken)
    {
        var upstreamProxy = Connection.UpStreamProxy;

        var useUpstreamProxy = upstreamProxy != null && upstreamProxy.ProxyType == ExternalProxyType.Http &&
                               !Connection.IsHttps;

        var serverStream = Connection.Stream;

        string? upstreamProxyUserName = null;
        string? upstreamProxyPassword = null;

        string url;
        if (isTransparent)
        {
            url = Request.RequestUriString;
        }
        else if (!useUpstreamProxy)
        {
            if (UriExtensions.GetScheme(Request.RequestUriString8).Length == 0)
                url = Request.RequestUriString;
            else
                url = Request.RequestUri.GetOriginalPathAndQuery();
        }
        else
        {
            url = Request.RequestUri.ToString();

            // Send Authentication to Upstream proxy if needed
            if (!upstreamProxy!.UseDefaultCredentials &&
                !string.IsNullOrEmpty(upstreamProxy.UserName) && upstreamProxy.Password != null)
            {
                upstreamProxyUserName = upstreamProxy.UserName;
                upstreamProxyPassword = upstreamProxy.Password;
            }
        }

        if (url == string.Empty) url = "/";

        // prepare the request & headers
        var headerBuilder = new HeaderBuilder();
        headerBuilder.WriteRequestLine(Request.Method, url, Request.HttpVersion);
        headerBuilder.WriteHeaders(Request.Headers, !isTransparent, upstreamProxyUserName, upstreamProxyPassword);

        // write request headers
        await serverStream.WriteHeadersAsync(headerBuilder, cancellationToken);

        if (enable100ContinueBehaviour && Request.ExpectContinue)
        {
            // wait for expectation response from server
            await ReceiveResponse(cancellationToken);

            if (Response.StatusCode == (int)HttpStatusCode.Continue)
                Request.ExpectationSucceeded = true;
            else
                Request.ExpectationFailed = true;
        }
    }

    /// <summary>
    ///     Receive and parse the http response from server
    /// </summary>
    /// <returns></returns>
    internal async Task ReceiveResponse(CancellationToken cancellationToken)
    {
        // return if this is already read
        if (Response.StatusCode != 0) return;

        Response.RequestMethod = Request.Method;

        var httpStatus = await Connection.Stream.ReadResponseStatus(cancellationToken);
        Response.HttpVersion = httpStatus.Version;
        Response.StatusCode = httpStatus.StatusCode;
        Response.StatusDescription = httpStatus.Description;

        // Read the response headers in to unique and non-unique header collections
        await HeaderParser.ReadHeaders(Connection.Stream, Response.Headers, cancellationToken);
    }

    /// <summary>
    ///     Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
    /// </summary>
    internal void FinishSession()
    {
        connection = null;

        ConnectRequest?.FinishSession();
        Request?.FinishSession();
        Response?.FinishSession();

        Data.Clear();
        UserData = null;
    }
}ParseOptions.0.jsonö¬
^D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\Models\SslCiphers.cs°¡using System.Collections.Generic;

namespace Titanium.Web.Proxy.StreamExtended.Models;

internal static class SslCiphers
{
    internal static readonly Dictionary<int, string> Ciphers = new()
    {
        { 0x0000, "TLS_NULL_WITH_NULL_NULL" },
        { 0x0001, "TLS_RSA_WITH_NULL_MD5" },
        { 0x0002, "TLS_RSA_WITH_NULL_SHA" },
        { 0x0003, "TLS_RSA_EXPORT_WITH_RC4_40_MD5" },
        { 0x0004, "TLS_RSA_WITH_RC4_128_MD5" },
        { 0x0005, "TLS_RSA_WITH_RC4_128_SHA" },
        { 0x0006, "TLS_RSA_EXPORT_WITH_RC2_CBC_40_MD5" },
        { 0x0007, "TLS_RSA_WITH_IDEA_CBC_SHA" },
        { 0x0008, "TLS_RSA_EXPORT_WITH_DES40_CBC_SHA" },
        { 0x0009, "TLS_RSA_WITH_DES_CBC_SHA" },
        { 0x000A, "TLS_RSA_WITH_3DES_EDE_CBC_SHA" },
        { 0x000B, "TLS_DH_DSS_EXPORT_WITH_DES40_CBC_SHA" },
        { 0x000C, "TLS_DH_DSS_WITH_DES_CBC_SHA" },
        { 0x000D, "TLS_DH_DSS_WITH_3DES_EDE_CBC_SHA" },
        { 0x000E, "TLS_DH_RSA_EXPORT_WITH_DES40_CBC_SHA" },
        { 0x000F, "TLS_DH_RSA_WITH_DES_CBC_SHA" },
        { 0x0010, "TLS_DH_RSA_WITH_3DES_EDE_CBC_SHA" },
        { 0x0011, "TLS_DHE_DSS_EXPORT_WITH_DES40_CBC_SHA" },
        { 0x0012, "TLS_DHE_DSS_WITH_DES_CBC_SHA" },
        { 0x0013, "TLS_DHE_DSS_WITH_3DES_EDE_CBC_SHA" },
        { 0x0014, "TLS_DHE_RSA_EXPORT_WITH_DES40_CBC_SHA" },
        { 0x0015, "TLS_DHE_RSA_WITH_DES_CBC_SHA" },
        { 0x0016, "TLS_DHE_RSA_WITH_3DES_EDE_CBC_SHA" },
        { 0x0017, "TLS_DH_anon_EXPORT_WITH_RC4_40_MD5" },
        { 0x0018, "TLS_DH_anon_WITH_RC4_128_MD5" },
        { 0x0019, "TLS_DH_anon_EXPORT_WITH_DES40_CBC_SHA" },
        { 0x001A, "TLS_DH_anon_WITH_DES_CBC_SHA" },
        { 0x001B, "TLS_DH_anon_WITH_3DES_EDE_CBC_SHA" },
        { 0x001C, "SSL_FORTEZZA_KEA_WITH_NULL_SHA" },
        { 0x001D, "SSL_FORTEZZA_KEA_WITH_FORTEZZA_CBC_SHA" },
        //{ 0x001E, "SSL_FORTEZZA_KEA_WITH_RC4_128_SHA" },
        // RFC 2712
        { 0x001E, "TLS_KRB5_WITH_DES_CBC_SHA" },
        { 0x001F, "TLS_KRB5_WITH_3DES_EDE_CBC_SHA" },
        { 0x0020, "TLS_KRB5_WITH_RC4_128_SHA" },
        { 0x0021, "TLS_KRB5_WITH_IDEA_CBC_SHA" },
        { 0x0022, "TLS_KRB5_WITH_DES_CBC_MD5" },
        { 0x0023, "TLS_KRB5_WITH_3DES_EDE_CBC_MD5" },
        { 0x0024, "TLS_KRB5_WITH_RC4_128_MD5" },
        { 0x0025, "TLS_KRB5_WITH_IDEA_CBC_MD5" },
        { 0x0026, "TLS_KRB5_EXPORT_WITH_DES_CBC_40_SHA" },
        { 0x0027, "TLS_KRB5_EXPORT_WITH_RC2_CBC_40_SHA" },
        { 0x0028, "TLS_KRB5_EXPORT_WITH_RC4_40_SHA" },
        { 0x0029, "TLS_KRB5_EXPORT_WITH_DES_CBC_40_MD5" },
        { 0x002A, "TLS_KRB5_EXPORT_WITH_RC2_CBC_40_MD5" },
        { 0x002B, "TLS_KRB5_EXPORT_WITH_RC4_40_MD5" },
        // RFC 4785
        { 0x002C, "TLS_PSK_WITH_NULL_SHA" },
        { 0x002D, "TLS_DHE_PSK_WITH_NULL_SHA" },
        { 0x002E, "TLS_RSA_PSK_WITH_NULL_SHA" },
        // RFC 5246
        { 0x002F, "TLS_RSA_WITH_AES_128_CBC_SHA" },
        { 0x0030, "TLS_DH_DSS_WITH_AES_128_CBC_SHA" },
        { 0x0031, "TLS_DH_RSA_WITH_AES_128_CBC_SHA" },
        { 0x0032, "TLS_DHE_DSS_WITH_AES_128_CBC_SHA" },
        { 0x0033, "TLS_DHE_RSA_WITH_AES_128_CBC_SHA" },
        { 0x0034, "TLS_DH_anon_WITH_AES_128_CBC_SHA" },
        { 0x0035, "TLS_RSA_WITH_AES_256_CBC_SHA" },
        { 0x0036, "TLS_DH_DSS_WITH_AES_256_CBC_SHA" },
        { 0x0037, "TLS_DH_RSA_WITH_AES_256_CBC_SHA" },
        { 0x0038, "TLS_DHE_DSS_WITH_AES_256_CBC_SHA" },
        { 0x0039, "TLS_DHE_RSA_WITH_AES_256_CBC_SHA" },
        { 0x003A, "TLS_DH_anon_WITH_AES_256_CBC_SHA" },
        { 0x003B, "TLS_RSA_WITH_NULL_SHA256" },
        { 0x003C, "TLS_RSA_WITH_AES_128_CBC_SHA256" },
        { 0x003D, "TLS_RSA_WITH_AES_256_CBC_SHA256" },
        { 0x003E, "TLS_DH_DSS_WITH_AES_128_CBC_SHA256" },
        { 0x003F, "TLS_DH_RSA_WITH_AES_128_CBC_SHA256" },
        { 0x0040, "TLS_DHE_DSS_WITH_AES_128_CBC_SHA256" },
        { 0x0041, "TLS_RSA_WITH_CAMELLIA_128_CBC_SHA" },
        { 0x0042, "TLS_DH_DSS_WITH_CAMELLIA_128_CBC_SHA" },
        { 0x0043, "TLS_DH_RSA_WITH_CAMELLIA_128_CBC_SHA" },
        { 0x0044, "TLS_DHE_DSS_WITH_CAMELLIA_128_CBC_SHA" },
        { 0x0045, "TLS_DHE_RSA_WITH_CAMELLIA_128_CBC_SHA" },
        { 0x0046, "TLS_DH_anon_WITH_CAMELLIA_128_CBC_SHA" },
        { 0x0047, "TLS_ECDH_ECDSA_WITH_NULL_SHA" },
        { 0x0048, "TLS_ECDH_ECDSA_WITH_RC4_128_SHA" },
        { 0x0049, "TLS_ECDH_ECDSA_WITH_DES_CBC_SHA" },
        { 0x004A, "TLS_ECDH_ECDSA_WITH_3DES_EDE_CBC_SHA" },
        { 0x004B, "TLS_ECDH_ECDSA_WITH_AES_128_CBC_SHA" },
        { 0x004C, "TLS_ECDH_ECDSA_WITH_AES_256_CBC_SHA" },
        { 0x0060, "TLS_RSA_EXPORT1024_WITH_RC4_56_MD5" },
        { 0x0061, "TLS_RSA_EXPORT1024_WITH_RC2_CBC_56_MD5" },
        { 0x0062, "TLS_RSA_EXPORT1024_WITH_DES_CBC_SHA" },
        { 0x0063, "TLS_DHE_DSS_EXPORT1024_WITH_DES_CBC_SHA" },
        { 0x0064, "TLS_RSA_EXPORT1024_WITH_RC4_56_SHA" },
        { 0x0065, "TLS_DHE_DSS_EXPORT1024_WITH_RC4_56_SHA" },
        { 0x0066, "TLS_DHE_DSS_WITH_RC4_128_SHA" },
        { 0x0067, "TLS_DHE_RSA_WITH_AES_128_CBC_SHA256" },
        { 0x0068, "TLS_DH_DSS_WITH_AES_256_CBC_SHA256" },
        { 0x0069, "TLS_DH_RSA_WITH_AES_256_CBC_SHA256" },
        { 0x006A, "TLS_DHE_DSS_WITH_AES_256_CBC_SHA256" },
        { 0x006B, "TLS_DHE_RSA_WITH_AES_256_CBC_SHA256" },
        { 0x006C, "TLS_DH_anon_WITH_AES_128_CBC_SHA256" },
        { 0x006D, "TLS_DH_anon_WITH_AES_256_CBC_SHA256" },
        { 0x0084, "TLS_RSA_WITH_CAMELLIA_256_CBC_SHA" },
        { 0x0085, "TLS_DH_DSS_WITH_CAMELLIA_256_CBC_SHA" },
        { 0x0086, "TLS_DH_RSA_WITH_CAMELLIA_256_CBC_SHA" },
        { 0x0087, "TLS_DHE_DSS_WITH_CAMELLIA_256_CBC_SHA" },
        { 0x0088, "TLS_DHE_RSA_WITH_CAMELLIA_256_CBC_SHA" },
        { 0x0089, "TLS_DH_anon_WITH_CAMELLIA_256_CBC_SHA" },
        // RFC 4279
        { 0x008A, "TLS_PSK_WITH_RC4_128_SHA" },
        { 0x008B, "TLS_PSK_WITH_3DES_EDE_CBC_SHA" },
        { 0x008C, "TLS_PSK_WITH_AES_128_CBC_SHA" },
        { 0x008D, "TLS_PSK_WITH_AES_256_CBC_SHA" },
        { 0x008E, "TLS_DHE_PSK_WITH_RC4_128_SHA" },
        { 0x008F, "TLS_DHE_PSK_WITH_3DES_EDE_CBC_SHA" },
        { 0x0090, "TLS_DHE_PSK_WITH_AES_128_CBC_SHA" },
        { 0x0091, "TLS_DHE_PSK_WITH_AES_256_CBC_SHA" },
        { 0x0092, "TLS_RSA_PSK_WITH_RC4_128_SHA" },
        { 0x0093, "TLS_RSA_PSK_WITH_3DES_EDE_CBC_SHA" },
        { 0x0094, "TLS_RSA_PSK_WITH_AES_128_CBC_SHA" },
        { 0x0095, "TLS_RSA_PSK_WITH_AES_256_CBC_SHA" },
        // RFC 4162
        { 0x0096, "TLS_RSA_WITH_SEED_CBC_SHA" },
        { 0x0097, "TLS_DH_DSS_WITH_SEED_CBC_SHA" },
        { 0x0098, "TLS_DH_RSA_WITH_SEED_CBC_SHA" },
        { 0x0099, "TLS_DHE_DSS_WITH_SEED_CBC_SHA" },
        { 0x009A, "TLS_DHE_RSA_WITH_SEED_CBC_SHA" },
        { 0x009B, "TLS_DH_anon_WITH_SEED_CBC_SHA" },
        // RFC 5288
        { 0x009C, "TLS_RSA_WITH_AES_128_GCM_SHA256" },
        { 0x009D, "TLS_RSA_WITH_AES_256_GCM_SHA384" },
        { 0x009E, "TLS_DHE_RSA_WITH_AES_128_GCM_SHA256" },
        { 0x009F, "TLS_DHE_RSA_WITH_AES_256_GCM_SHA384" },
        { 0x00A0, "TLS_DH_RSA_WITH_AES_128_GCM_SHA256" },
        { 0x00A1, "TLS_DH_RSA_WITH_AES_256_GCM_SHA384" },
        { 0x00A2, "TLS_DHE_DSS_WITH_AES_128_GCM_SHA256" },
        { 0x00A3, "TLS_DHE_DSS_WITH_AES_256_GCM_SHA384" },
        { 0x00A4, "TLS_DH_DSS_WITH_AES_128_GCM_SHA256" },
        { 0x00A5, "TLS_DH_DSS_WITH_AES_256_GCM_SHA384" },
        { 0x00A6, "TLS_DH_anon_WITH_AES_128_GCM_SHA256" },
        { 0x00A7, "TLS_DH_anon_WITH_AES_256_GCM_SHA384" },
        // RFC 5487
        { 0x00A8, "TLS_PSK_WITH_AES_128_GCM_SHA256" },
        { 0x00A9, "TLS_PSK_WITH_AES_256_GCM_SHA384" },
        { 0x00AA, "TLS_DHE_PSK_WITH_AES_128_GCM_SHA256" },
        { 0x00AB, "TLS_DHE_PSK_WITH_AES_256_GCM_SHA384" },
        { 0x00AC, "TLS_RSA_PSK_WITH_AES_128_GCM_SHA256" },
        { 0x00AD, "TLS_RSA_PSK_WITH_AES_256_GCM_SHA384" },
        { 0x00AE, "TLS_PSK_WITH_AES_128_CBC_SHA256" },
        { 0x00AF, "TLS_PSK_WITH_AES_256_CBC_SHA384" },
        { 0x00B0, "TLS_PSK_WITH_NULL_SHA256" },
        { 0x00B1, "TLS_PSK_WITH_NULL_SHA384" },
        { 0x00B2, "TLS_DHE_PSK_WITH_AES_128_CBC_SHA256" },
        { 0x00B3, "TLS_DHE_PSK_WITH_AES_256_CBC_SHA384" },
        { 0x00B4, "TLS_DHE_PSK_WITH_NULL_SHA256" },
        { 0x00B5, "TLS_DHE_PSK_WITH_NULL_SHA384" },
        { 0x00B6, "TLS_RSA_PSK_WITH_AES_128_CBC_SHA256" },
        { 0x00B7, "TLS_RSA_PSK_WITH_AES_256_CBC_SHA384" },
        { 0x00B8, "TLS_RSA_PSK_WITH_NULL_SHA256" },
        { 0x00B9, "TLS_RSA_PSK_WITH_NULL_SHA384" },
        // RFC 5932
        { 0x00BA, "TLS_RSA_WITH_CAMELLIA_128_CBC_SHA256" },
        { 0x00BB, "TLS_DH_DSS_WITH_CAMELLIA_128_CBC_SHA256" },
        { 0x00BC, "TLS_DH_RSA_WITH_CAMELLIA_128_CBC_SHA256" },
        { 0x00BD, "TLS_DHE_DSS_WITH_CAMELLIA_128_CBC_SHA256" },
        { 0x00BE, "TLS_DHE_RSA_WITH_CAMELLIA_128_CBC_SHA256" },
        { 0x00BF, "TLS_DH_anon_WITH_CAMELLIA_128_CBC_SHA256" },
        { 0x00C0, "TLS_RSA_WITH_CAMELLIA_256_CBC_SHA256" },
        { 0x00C1, "TLS_DH_DSS_WITH_CAMELLIA_256_CBC_SHA256" },
        { 0x00C2, "TLS_DH_RSA_WITH_CAMELLIA_256_CBC_SHA256" },
        { 0x00C3, "TLS_DHE_DSS_WITH_CAMELLIA_256_CBC_SHA256" },
        { 0x00C4, "TLS_DHE_RSA_WITH_CAMELLIA_256_CBC_SHA256" },
        { 0x00C5, "TLS_DH_anon_WITH_CAMELLIA_256_CBC_SHA256" },
        { 0x00FF, "TLS_EMPTY_RENEGOTIATION_INFO_SCSV" },
        // RFC 8446
        { 0x1301, "TLS_AES_128_GCM_SHA256" },
        { 0x1302, "TLS_AES_256_GCM_SHA384" },
        { 0x1303, "TLS_CHACHA20_POLY1305_SHA256" },
        { 0x1304, "TLS_AES_128_CCM_SHA256" },
        { 0x1305, "TLS_AES_128_CCM_8_SHA256" },
        { 0x5600, "TLS_FALLBACK_SCSV" },
        // RFC 4492
        { 0xC001, "TLS_ECDH_ECDSA_WITH_NULL_SHA" },
        { 0xC002, "TLS_ECDH_ECDSA_WITH_RC4_128_SHA" },
        { 0xC003, "TLS_ECDH_ECDSA_WITH_3DES_EDE_CBC_SHA" },
        { 0xC004, "TLS_ECDH_ECDSA_WITH_AES_128_CBC_SHA" },
        { 0xC005, "TLS_ECDH_ECDSA_WITH_AES_256_CBC_SHA" },
        { 0xC006, "TLS_ECDHE_ECDSA_WITH_NULL_SHA" },
        { 0xC007, "TLS_ECDHE_ECDSA_WITH_RC4_128_SHA" },
        { 0xC008, "TLS_ECDHE_ECDSA_WITH_3DES_EDE_CBC_SHA" },
        { 0xC009, "TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA" },
        { 0xC00A, "TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA" },
        { 0xC00B, "TLS_ECDH_RSA_WITH_NULL_SHA" },
        { 0xC00C, "TLS_ECDH_RSA_WITH_RC4_128_SHA" },
        { 0xC00D, "TLS_ECDH_RSA_WITH_3DES_EDE_CBC_SHA" },
        { 0xC00E, "TLS_ECDH_RSA_WITH_AES_128_CBC_SHA" },
        { 0xC00F, "TLS_ECDH_RSA_WITH_AES_256_CBC_SHA" },
        { 0xC010, "TLS_ECDHE_RSA_WITH_NULL_SHA" },
        { 0xC011, "TLS_ECDHE_RSA_WITH_RC4_128_SHA" },
        { 0xC012, "TLS_ECDHE_RSA_WITH_3DES_EDE_CBC_SHA" },
        { 0xC013, "TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA" },
        { 0xC014, "TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA" },
        { 0xC015, "TLS_ECDH_anon_WITH_NULL_SHA" },
        { 0xC016, "TLS_ECDH_anon_WITH_RC4_128_SHA" },
        { 0xC017, "TLS_ECDH_anon_WITH_3DES_EDE_CBC_SHA" },
        { 0xC018, "TLS_ECDH_anon_WITH_AES_128_CBC_SHA" },
        { 0xC019, "TLS_ECDH_anon_WITH_AES_256_CBC_SHA" },
        // RFC 5054
        { 0xC01A, "TLS_SRP_SHA_WITH_3DES_EDE_CBC_SHA" },
        { 0xC01B, "TLS_SRP_SHA_RSA_WITH_3DES_EDE_CBC_SHA" },
        { 0xC01C, "TLS_SRP_SHA_DSS_WITH_3DES_EDE_CBC_SHA" },
        { 0xC01D, "TLS_SRP_SHA_WITH_AES_128_CBC_SHA" },
        { 0xC01E, "TLS_SRP_SHA_RSA_WITH_AES_128_CBC_SHA" },
        { 0xC01F, "TLS_SRP_SHA_DSS_WITH_AES_128_CBC_SHA" },
        { 0xC020, "TLS_SRP_SHA_WITH_AES_256_CBC_SHA" },
        { 0xC021, "TLS_SRP_SHA_RSA_WITH_AES_256_CBC_SHA" },
        { 0xC022, "TLS_SRP_SHA_DSS_WITH_AES_256_CBC_SHA" },
        // RFC 5589
        { 0xC023, "TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256" },
        { 0xC024, "TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA384" },
        { 0xC025, "TLS_ECDH_ECDSA_WITH_AES_128_CBC_SHA256" },
        { 0xC026, "TLS_ECDH_ECDSA_WITH_AES_256_CBC_SHA384" },
        { 0xC027, "TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA256" },
        { 0xC028, "TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA384" },
        { 0xC029, "TLS_ECDH_RSA_WITH_AES_128_CBC_SHA256" },
        { 0xC02A, "TLS_ECDH_RSA_WITH_AES_256_CBC_SHA384" },
        { 0xC02B, "TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256" },
        { 0xC02C, "TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384" },
        { 0xC02D, "TLS_ECDH_ECDSA_WITH_AES_128_GCM_SHA256" },
        { 0xC02E, "TLS_ECDH_ECDSA_WITH_AES_256_GCM_SHA384" },
        { 0xC02F, "TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256" },
        { 0xC030, "TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384" },
        { 0xC031, "TLS_ECDH_RSA_WITH_AES_128_GCM_SHA256" },
        { 0xC032, "TLS_ECDH_RSA_WITH_AES_256_GCM_SHA384" },
        // RFC 5489
        { 0xC033, "TLS_ECDHE_PSK_WITH_RC4_128_SHA" },
        { 0xC034, "TLS_ECDHE_PSK_WITH_3DES_EDE_CBC_SHA" },
        { 0xC035, "TLS_ECDHE_PSK_WITH_AES_128_CBC_SHA" },
        { 0xC036, "TLS_ECDHE_PSK_WITH_AES_256_CBC_SHA" },
        { 0xC037, "TLS_ECDHE_PSK_WITH_AES_128_CBC_SHA256" },
        { 0xC038, "TLS_ECDHE_PSK_WITH_AES_256_CBC_SHA384" },
        { 0xC039, "TLS_ECDHE_PSK_WITH_NULL_SHA" },
        { 0xC03A, "TLS_ECDHE_PSK_WITH_NULL_SHA256" },
        { 0xC03B, "TLS_ECDHE_PSK_WITH_NULL_SHA384" },
        { 0xC03C, "TLS_RSA_WITH_ARIA_128_CBC_SHA256" },
        { 0xC03D, "TLS_RSA_WITH_ARIA_256_CBC_SHA384" },
        { 0xC03E, "TLS_DH_DSS_WITH_ARIA_128_CBC_SHA256" },
        { 0xC03F, "TLS_DH_DSS_WITH_ARIA_256_CBC_SHA384" },
        { 0xC040, "TLS_DH_RSA_WITH_ARIA_128_CBC_SHA256" },
        { 0xC041, "TLS_DH_RSA_WITH_ARIA_256_CBC_SHA384" },
        { 0xC042, "TLS_DHE_DSS_WITH_ARIA_128_CBC_SHA256" },
        { 0xC043, "TLS_DHE_DSS_WITH_ARIA_256_CBC_SHA384" },
        { 0xC044, "TLS_DHE_RSA_WITH_ARIA_128_CBC_SHA256" },
        { 0xC045, "TLS_DHE_RSA_WITH_ARIA_256_CBC_SHA384" },
        { 0xC046, "TLS_DH_anon_WITH_ARIA_128_CBC_SHA256" },
        { 0xC047, "TLS_DH_anon_WITH_ARIA_256_CBC_SHA384" },
        { 0xC048, "TLS_ECDHE_ECDSA_WITH_ARIA_128_CBC_SHA256" },
        { 0xC049, "TLS_ECDHE_ECDSA_WITH_ARIA_256_CBC_SHA384" },
        { 0xC04A, "TLS_ECDH_ECDSA_WITH_ARIA_128_CBC_SHA256" },
        { 0xC04B, "TLS_ECDH_ECDSA_WITH_ARIA_256_CBC_SHA384" },
        { 0xC04C, "TLS_ECDHE_RSA_WITH_ARIA_128_CBC_SHA256" },
        { 0xC04D, "TLS_ECDHE_RSA_WITH_ARIA_256_CBC_SHA384" },
        { 0xC04E, "TLS_ECDH_RSA_WITH_ARIA_128_CBC_SHA256" },
        { 0xC04F, "TLS_ECDH_RSA_WITH_ARIA_256_CBC_SHA384" },
        { 0xC050, "TLS_RSA_WITH_ARIA_128_GCM_SHA256" },
        { 0xC051, "TLS_RSA_WITH_ARIA_256_GCM_SHA384" },
        { 0xC052, "TLS_DHE_RSA_WITH_ARIA_128_GCM_SHA256" },
        { 0xC053, "TLS_DHE_RSA_WITH_ARIA_256_GCM_SHA384" },
        { 0xC054, "TLS_DH_RSA_WITH_ARIA_128_GCM_SHA256" },
        { 0xC055, "TLS_DH_RSA_WITH_ARIA_256_GCM_SHA384" },
        { 0xC056, "TLS_DHE_DSS_WITH_ARIA_128_GCM_SHA256" },
        { 0xC057, "TLS_DHE_DSS_WITH_ARIA_256_GCM_SHA384" },
        { 0xC058, "TLS_DH_DSS_WITH_ARIA_128_GCM_SHA256" },
        { 0xC059, "TLS_DH_DSS_WITH_ARIA_256_GCM_SHA384" },
        { 0xC05A, "TLS_DH_anon_WITH_ARIA_128_GCM_SHA256" },
        { 0xC05B, "TLS_DH_anon_WITH_ARIA_256_GCM_SHA384" },
        { 0xC05C, "TLS_ECDHE_ECDSA_WITH_ARIA_128_GCM_SHA256" },
        { 0xC05D, "TLS_ECDHE_ECDSA_WITH_ARIA_256_GCM_SHA384" },
        { 0xC05E, "TLS_ECDH_ECDSA_WITH_ARIA_128_GCM_SHA256" },
        { 0xC05F, "TLS_ECDH_ECDSA_WITH_ARIA_256_GCM_SHA384" },
        { 0xC060, "TLS_ECDHE_RSA_WITH_ARIA_128_GCM_SHA256" },
        { 0xC061, "TLS_ECDHE_RSA_WITH_ARIA_256_GCM_SHA384" },
        { 0xC062, "TLS_ECDH_RSA_WITH_ARIA_128_GCM_SHA256" },
        { 0xC063, "TLS_ECDH_RSA_WITH_ARIA_256_GCM_SHA384" },
        { 0xC064, "TLS_PSK_WITH_ARIA_128_CBC_SHA256" },
        { 0xC065, "TLS_PSK_WITH_ARIA_256_CBC_SHA384" },
        { 0xC066, "TLS_DHE_PSK_WITH_ARIA_128_CBC_SHA256" },
        { 0xC067, "TLS_DHE_PSK_WITH_ARIA_256_CBC_SHA384" },
        { 0xC068, "TLS_RSA_PSK_WITH_ARIA_128_CBC_SHA256" },
        { 0xC069, "TLS_RSA_PSK_WITH_ARIA_256_CBC_SHA384" },
        { 0xC06A, "TLS_PSK_WITH_ARIA_128_GCM_SHA256" },
        { 0xC06B, "TLS_PSK_WITH_ARIA_256_GCM_SHA384" },
        { 0xC06C, "TLS_DHE_PSK_WITH_ARIA_128_GCM_SHA256" },
        { 0xC06D, "TLS_DHE_PSK_WITH_ARIA_256_GCM_SHA384" },
        { 0xC06E, "TLS_RSA_PSK_WITH_ARIA_128_GCM_SHA256" },
        { 0xC06F, "TLS_RSA_PSK_WITH_ARIA_256_GCM_SHA384" },
        { 0xC070, "TLS_ECDHE_PSK_WITH_ARIA_128_CBC_SHA256" },
        { 0xC071, "TLS_ECDHE_PSK_WITH_ARIA_256_CBC_SHA384" },
        { 0xC072, "TLS_ECDHE_ECDSA_WITH_CAMELLIA_128_CBC_SHA256" },
        { 0xC073, "TLS_ECDHE_ECDSA_WITH_CAMELLIA_256_CBC_SHA384" },
        { 0xC074, "TLS_ECDH_ECDSA_WITH_CAMELLIA_128_CBC_SHA256" },
        { 0xC075, "TLS_ECDH_ECDSA_WITH_CAMELLIA_256_CBC_SHA384" },
        { 0xC076, "TLS_ECDHE_RSA_WITH_CAMELLIA_128_CBC_SHA256" },
        { 0xC077, "TLS_ECDHE_RSA_WITH_CAMELLIA_256_CBC_SHA384" },
        { 0xC078, "TLS_ECDH_RSA_WITH_CAMELLIA_128_CBC_SHA256" },
        { 0xC079, "TLS_ECDH_RSA_WITH_CAMELLIA_256_CBC_SHA384" },
        { 0xC07A, "TLS_RSA_WITH_CAMELLIA_128_GCM_SHA256" },
        { 0xC07B, "TLS_RSA_WITH_CAMELLIA_256_GCM_SHA384" },
        { 0xC07C, "TLS_DHE_RSA_WITH_CAMELLIA_128_GCM_SHA256" },
        { 0xC07D, "TLS_DHE_RSA_WITH_CAMELLIA_256_GCM_SHA384" },
        { 0xC07E, "TLS_DH_RSA_WITH_CAMELLIA_128_GCM_SHA256" },
        { 0xC07F, "TLS_DH_RSA_WITH_CAMELLIA_256_GCM_SHA384" },
        { 0xC080, "TLS_DHE_DSS_WITH_CAMELLIA_128_GCM_SHA256" },
        { 0xC081, "TLS_DHE_DSS_WITH_CAMELLIA_256_GCM_SHA384" },
        { 0xC082, "TLS_DH_DSS_WITH_CAMELLIA_128_GCM_SHA256" },
        { 0xC083, "TLS_DH_DSS_WITH_CAMELLIA_256_GCM_SHA384" },
        { 0xC084, "TLS_DH_anon_WITH_CAMELLIA_128_GCM_SHA256" },
        { 0xC085, "TLS_DH_anon_WITH_CAMELLIA_256_GCM_SHA384" },
        { 0xC086, "TLS_ECDHE_ECDSA_WITH_CAMELLIA_128_GCM_SHA256" },
        { 0xC087, "TLS_ECDHE_ECDSA_WITH_CAMELLIA_256_GCM_SHA384" },
        { 0xC088, "TLS_ECDH_ECDSA_WITH_CAMELLIA_128_GCM_SHA256" },
        { 0xC089, "TLS_ECDH_ECDSA_WITH_CAMELLIA_256_GCM_SHA384" },
        { 0xC08A, "TLS_ECDHE_RSA_WITH_CAMELLIA_128_GCM_SHA256" },
        { 0xC08B, "TLS_ECDHE_RSA_WITH_CAMELLIA_256_GCM_SHA384" },
        { 0xC08C, "TLS_ECDH_RSA_WITH_CAMELLIA_128_GCM_SHA256" },
        { 0xC08D, "TLS_ECDH_RSA_WITH_CAMELLIA_256_GCM_SHA384" },
        { 0xC08E, "TLS_PSK_WITH_CAMELLIA_128_GCM_SHA256" },
        { 0xC08F, "TLS_PSK_WITH_CAMELLIA_256_GCM_SHA384" },
        { 0xC090, "TLS_DHE_PSK_WITH_CAMELLIA_128_GCM_SHA256" },
        { 0xC091, "TLS_DHE_PSK_WITH_CAMELLIA_256_GCM_SHA384" },
        { 0xC092, "TLS_RSA_PSK_WITH_CAMELLIA_128_GCM_SHA256" },
        { 0xC093, "TLS_RSA_PSK_WITH_CAMELLIA_256_GCM_SHA384" },
        { 0xC094, "TLS_PSK_WITH_CAMELLIA_128_CBC_SHA256" },
        { 0xC095, "TLS_PSK_WITH_CAMELLIA_256_CBC_SHA384" },
        { 0xC096, "TLS_DHE_PSK_WITH_CAMELLIA_128_CBC_SHA256" },
        { 0xC097, "TLS_DHE_PSK_WITH_CAMELLIA_256_CBC_SHA384" },
        { 0xC098, "TLS_RSA_PSK_WITH_CAMELLIA_128_CBC_SHA256" },
        { 0xC099, "TLS_RSA_PSK_WITH_CAMELLIA_256_CBC_SHA384" },
        { 0xC09A, "TLS_ECDHE_PSK_WITH_CAMELLIA_128_CBC_SHA256" },
        { 0xC09B, "TLS_ECDHE_PSK_WITH_CAMELLIA_256_CBC_SHA384" },
        { 0xC09C, "TLS_RSA_WITH_AES_128_CCM" },
        { 0xC09D, "TLS_RSA_WITH_AES_256_CCM" },
        { 0xC09E, "TLS_DHE_RSA_WITH_AES_128_CCM" },
        { 0xC09F, "TLS_DHE_RSA_WITH_AES_256_CCM" },
        { 0xC0A0, "TLS_RSA_WITH_AES_128_CCM_8" },
        { 0xC0A1, "TLS_RSA_WITH_AES_256_CCM_8" },
        { 0xC0A2, "TLS_DHE_RSA_WITH_AES_128_CCM_8" },
        { 0xC0A3, "TLS_DHE_RSA_WITH_AES_256_CCM_8" },
        { 0xC0A4, "TLS_PSK_WITH_AES_128_CCM" },
        { 0xC0A5, "TLS_PSK_WITH_AES_256_CCM" },
        { 0xC0A6, "TLS_DHE_PSK_WITH_AES_128_CCM" },
        { 0xC0A7, "TLS_DHE_PSK_WITH_AES_256_CCM" },
        { 0xC0A8, "TLS_PSK_WITH_AES_128_CCM_8" },
        { 0xC0A9, "TLS_PSK_WITH_AES_256_CCM_8" },
        { 0xC0AA, "TLS_PSK_DHE_WITH_AES_128_CCM_8" },
        { 0xC0AB, "TLS_PSK_DHE_WITH_AES_256_CCM_8" },
        { 0xC0AC, "TLS_ECDHE_ECDSA_WITH_AES_128_CCM" },
        { 0xC0AD, "TLS_ECDHE_ECDSA_WITH_AES_256_CCM" },
        { 0xC0AE, "TLS_ECDHE_ECDSA_WITH_AES_128_CCM_8" },
        { 0xC0AF, "TLS_ECDHE_ECDSA_WITH_AES_256_CCM_8" },
        { 0xC0B0, "TLS_ECCPWD_WITH_AES_128_GCM_SHA256" },
        { 0xC0B1, "TLS_ECCPWD_WITH_AES_256_GCM_SHA384" },
        { 0xC0B2, "TLS_ECCPWD_WITH_AES_128_CCM_SHA256" },
        { 0xC0B3, "TLS_ECCPWD_WITH_AES_256_CCM_SHA384" },
        { 0xC0B4, "TLS_SHA256_SHA256" },
        { 0xC0B5, "TLS_SHA384_SHA384" },
        { 0xC100, "TLS_GOSTR341112_256_WITH_KUZNYECHIK_CTR_OMAC" },
        { 0xC101, "TLS_GOSTR341112_256_WITH_MAGMA_CTR_OMAC" },
        { 0xC102, "TLS_GOSTR341112_256_WITH_28147_CNT_IMIT" },
        // old numbers used in the beginning http://tools.ietf.org/html/draft-agl-tls-chacha20poly1305
        { 0xCC13, "TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256" },
        { 0xCC14, "TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256" },
        { 0xCC15, "TLS_DHE_RSA_WITH_CHACHA20_POLY1305_SHA256" },
        // http://tools.ietf.org/html/draft-ietf-tls-chacha20-poly1305
        { 0xCCA8, "TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256" },
        { 0xCCA9, "TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256" },
        { 0xCCAA, "TLS_DHE_RSA_WITH_CHACHA20_POLY1305_SHA256" },
        { 0xCCAB, "TLS_PSK_WITH_CHACHA20_POLY1305_SHA256" },
        { 0xCCAC, "TLS_ECDHE_PSK_WITH_CHACHA20_POLY1305_SHA256" },
        { 0xCCAD, "TLS_DHE_PSK_WITH_CHACHA20_POLY1305_SHA256" },
        { 0xCCAE, "TLS_RSA_PSK_WITH_CHACHA20_POLY1305_SHA256" },
        // RFC 8442
        { 0xD001, "TLS_ECDHE_PSK_WITH_AES_128_GCM_SHA256" },
        { 0xD002, "TLS_ECDHE_PSK_WITH_AES_256_GCM_SHA384" },
        { 0xD003, "TLS_ECDHE_PSK_WITH_AES_128_CCM_8_SHA256" },
        { 0xD005, "TLS_ECDHE_PSK_WITH_AES_128_CCM_SHA256" },
        // http://tools.ietf.org/html/draft-josefsson-salsa20-tls
        { 0xE410, "TLS_RSA_WITH_ESTREAM_SALSA20_SHA1" },
        { 0xE411, "TLS_RSA_WITH_SALSA20_SHA1" },
        { 0xE412, "TLS_ECDHE_RSA_WITH_ESTREAM_SALSA20_SHA1" },
        { 0xE413, "TLS_ECDHE_RSA_WITH_SALSA20_SHA1" },
        { 0xE414, "TLS_ECDHE_ECDSA_WITH_ESTREAM_SALSA20_SHA1" },
        { 0xE415, "TLS_ECDHE_ECDSA_WITH_SALSA20_SHA1" },
        { 0xE416, "TLS_PSK_WITH_ESTREAM_SALSA20_SHA1" },
        { 0xE417, "TLS_PSK_WITH_SALSA20_SHA1" },
        { 0xE418, "TLS_ECDHE_PSK_WITH_ESTREAM_SALSA20_SHA1" },
        { 0xE419, "TLS_ECDHE_PSK_WITH_SALSA20_SHA1" },
        { 0xE41A, "TLS_RSA_PSK_WITH_ESTREAM_SALSA20_SHA1" },
        { 0xE41B, "TLS_RSA_PSK_WITH_SALSA20_SHA1" },
        { 0xE41C, "TLS_DHE_PSK_WITH_ESTREAM_SALSA20_SHA1" },
        { 0xE41D, "TLS_DHE_PSK_WITH_SALSA20_SHA1" },
        { 0xE41E, "TLS_DHE_RSA_WITH_ESTREAM_SALSA20_SHA1" },
        { 0xE41F, "TLS_DHE_RSA_WITH_SALSA20_SHA1" },
        // these from http://www.mozilla.org/projects/security/pki/nss/ssl/fips-ssl-ciphersuites.html
        { 0xFEFE, "SSL_RSA_FIPS_WITH_DES_CBC_SHA" },
        { 0xFEFF, "SSL_RSA_FIPS_WITH_3DES_EDE_CBC_SHA" },
        { 0xFFE0, "SSL_RSA_FIPS_WITH_3DES_EDE_CBC_SHA" },
        { 0xFFE1, "SSL_RSA_FIPS_WITH_DES_CBC_SHA" },
        // note that ciphersuites of {0x00????} are TLS cipher suites in
        // a sslv2 client hello message; the ???? above is the two-byte
        // tls cipher suite id
        { 0x010080, "SSL2_RC4_128_WITH_MD5" },
        { 0x020080, "SSL2_RC4_128_EXPORT40_WITH_MD5" },
        { 0x030080, "SSL2_RC2_128_CBC_WITH_MD5" },
        { 0x040080, "SSL2_RC2_128_CBC_EXPORT40_WITH_MD5" },
        { 0x050080, "SSL2_IDEA_128_CBC_WITH_MD5" },
        { 0x060040, "SSL2_DES_64_CBC_WITH_MD5" },
        { 0x0700C0, "SSL2_DES_192_EDE3_CBC_WITH_MD5" },
        { 0x080080, "SSL2_RC4_64_WITH_MD5" },
        // Microsoft's old PCT protocol. These are from Eric Rescorla's book "SSL and TLS"
        { 0x800001, "PCT_SSL_CERT_TYPE | PCT1_CERT_X509" },
        { 0x800003, "PCT_SSL_CERT_TYPE | PCT1_CERT_X509_CHAIN" },
        { 0x810001, "PCT_SSL_HASH_TYPE | PCT1_HASH_MD5" },
        { 0x810003, "PCT_SSL_HASH_TYPE | PCT1_HASH_SHA" },
        { 0x820001, "PCT_SSL_EXCH_TYPE | PCT1_EXCH_RSA_PKCS1" },
        { 0x830004, "PCT_SSL_CIPHER_TYPE_1ST_HALF | PCT1_CIPHER_RC4" },
        { 0x842840, "PCT_SSL_CIPHER_TYPE_2ND_HALF | PCT1_ENC_BITS_40 | PCT1_MAC_BITS_128" },
        { 0x848040, "PCT_SSL_CIPHER_TYPE_2ND_HALF | PCT1_ENC_BITS_128 | PCT1_MAC_BITS_128" },
        { 0x8F8001, "PCT_SSL_COMPAT | PCT_VERSION_1" }
    };
}ParseOptions.0.json·≤
`D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\Models\SslExtension.csÊ±using System.Collections.Generic;
using System.Text;
using System;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using Titanium.Web.Proxy.Extensions;
using System.Xml.Linq;

namespace Titanium.Web.Proxy.StreamExtended.Models;

/// <summary>
///     The SSL extension information.
/// </summary>
public class SslExtension
{
    internal static readonly byte[] Http11Utf8 = new byte[] { 0x68, 0x74, 0x74, 0x70, 0x2f, 0x31, 0x2e, 0x31 }; // "http/1.1"
    internal static readonly byte[] Http2Utf8 = new byte[] { 0x68, 0x32 }; // "h2"
    internal static readonly byte[] Http3Utf8 = new byte[] { 0x68, 0x33 }; // "h3"

    /// <summary>
    ///     Initializes a new instance of the <see cref="SslExtension" /> class.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="data">The data.</param>
    /// <param name="position">The position.</param>
    public SslExtension(int value, ReadOnlyMemory<byte> data, int position)
    {
        Value = value;
        this.data = data;
        Name = GetExtensionName(value);
        Position = position;
    }

    private ReadOnlyMemory<byte> data;

    /// <summary>
    ///     Gets the value.
    /// </summary>
    /// <value>
    ///     The value.
    /// </value>
    public int Value { get; }

    /// <summary>
    ///     Gets the name.
    /// </summary>
    /// <value>
    ///     The name.
    /// </value>
    public string Name { get; }

    /// <summary>
    ///     Gets the data.
    /// </summary>
    /// <value>
    ///     The data.
    /// </value>
    public string Data => GetExtensionData(Value, data.Span);

    internal List<SslApplicationProtocol> Alpns => GetApplicationLayerProtocolNegotiation(data.Span);
    
    internal List<string> Protocols => GetSupportedVersions(data.Span);

    /// <summary>
    ///     Gets the position.
    /// </summary>
    /// <value>
    ///     The position.
    /// </value>
    public int Position { get; }

    private static unsafe string GetExtensionData(int value, ReadOnlySpan<byte> data)
    {
        // https://www.iana.org/assignments/tls-extensiontype-values/tls-extensiontype-values.xhtml
        switch (value)
        {
            case 0:
                var stringBuilder = new StringBuilder();
                var index = 2;
                while (index < data.Length)
                {
                    int nameType = data[index];
                    var count = (data[index + 1] << 8) + data[index + 2];
#if NET6_0_OR_GREATER
                    var str = Encoding.ASCII.GetString(data.Slice(index + 3, count));
#else
                    string str;
                    fixed (byte* bp = data.Slice(index + 3))
                    {
                        str = Encoding.ASCII.GetString(bp, count);
                    }
#endif
                    if (nameType == 0)
                    {
                        if (stringBuilder.Length > 0)
                        {
                            stringBuilder.Append("; ");
                            stringBuilder.Append(str);
                        }
                        else
                        {
                            stringBuilder.Append(str);
                        }
                    }

                    index += 3 + count;
                }

                return stringBuilder.ToString();
            case 5:
                if (data.Length == 5 && data[0] == 1 && data[1] == 0 && data[2] == 0 && data[3] == 0 && data[4] == 0)
                    return "OCSP - Implicit Responder";

                return data.ByteArrayToHexString();
            case 10:
                return GetSupportedGroup(data);
            case 11:
                return GetEcPointFormats(data);
            case 13:
                return GetSignatureAlgorithms(data);
            case 16:
                var protocols = GetApplicationLayerProtocolNegotiation(data);
#if NET6_0_OR_GREATER
                return string.Join(", ", protocols.Select(x => Encoding.UTF8.GetString(x.Protocol.Span)));
#else
                return string.Join(", ", protocols.Select(x => x.ToString()));
#endif
            case 21:
                for (int i = 0; i < data.Length; i++)
                {
                    if (data[i] != 0)
                    {
                        return data.ByteArrayToHexString();
                    }
                }

                return $"{data.Length:N0} null bytes";
            case 43:
                return string.Join(", ", GetSupportedVersions(data));
            case 50:
                return GetSignatureAlgorithms(data);
            case 35655:
                return $"{data.Length} bytes";
            default:
                return data.ByteArrayToHexString();
        }
    }

    private static string GetSupportedGroup(ReadOnlySpan<byte> data)
    {
        // https://datatracker.ietf.org/doc/draft-ietf-tls-rfc4492bis/?include_text=1
        var list = new List<string>();
        if (data.Length < 2) return string.Empty;

        var i = 2;
        while (i < data.Length - 1)
        {
            var namedCurve = (data[i] << 8) + data[i + 1];
            switch (namedCurve)
            {
                case 1:
                    list.Add("sect163k1 [0x1]"); // deprecated
                    break;
                case 2:
                    list.Add("sect163r1 [0x2]"); // deprecated
                    break;
                case 3:
                    list.Add("sect163r2 [0x3]"); // deprecated
                    break;
                case 4:
                    list.Add("sect193r1 [0x4]"); // deprecated
                    break;
                case 5:
                    list.Add("sect193r2 [0x5]"); // deprecated
                    break;
                case 6:
                    list.Add("sect233k1 [0x6]"); // deprecated
                    break;
                case 7:
                    list.Add("sect233r1 [0x7]"); // deprecated
                    break;
                case 8:
                    list.Add("sect239k1 [0x8]"); // deprecated
                    break;
                case 9:
                    list.Add("sect283k1 [0x9]"); // deprecated
                    break;
                case 10:
                    list.Add("sect283r1 [0xA]"); // deprecated
                    break;
                case 11:
                    list.Add("sect409k1 [0xB]"); // deprecated
                    break;
                case 12:
                    list.Add("sect409r1 [0xC]"); // deprecated
                    break;
                case 13:
                    list.Add("sect571k1 [0xD]"); // deprecated
                    break;
                case 14:
                    list.Add("sect571r1 [0xE]"); // deprecated
                    break;
                case 15:
                    list.Add("secp160k1 [0xF]"); // deprecated
                    break;
                case 16:
                    list.Add("secp160r1 [0x10]"); // deprecated
                    break;
                case 17:
                    list.Add("secp160r2 [0x11]"); // deprecated
                    break;
                case 18:
                    list.Add("secp192k1 [0x12]"); // deprecated
                    break;
                case 19:
                    list.Add("secp192r1 [0x13]"); // deprecated
                    break;
                case 20:
                    list.Add("secp224k1 [0x14]"); // deprecated
                    break;
                case 21:
                    list.Add("secp224r1 [0x15]"); // deprecated
                    break;
                case 22:
                    list.Add("secp256k1 [0x16]"); // deprecated
                    break;
                case 23:
                    list.Add("secp256r1 [0x17]");
                    break;
                case 24:
                    list.Add("secp384r1 [0x18]");
                    break;
                case 25:
                    list.Add("secp521r1 [0x19]");
                    break;
                case 26:
                    list.Add("brainpoolP256r1 [0x1A]");
                    break;
                case 27:
                    list.Add("brainpoolP384r1 [0x1B]");
                    break;
                case 28:
                    list.Add("brainpoolP512r1 [0x1C]");
                    break;
                case 29:
                    list.Add("x25519 [0x1D]");
                    break;
                case 30:
                    list.Add("x448 [0x1E]");
                    break;
                case 256:
                    list.Add("ffdhe2048	[0x0100]");
                    break;
                case 257:
                    list.Add("ffdhe3072 [0x0101]");
                    break;
                case 258:
                    list.Add("ffdhe4096 [0x0102]");
                    break;
                case 259:
                    list.Add("ffdhe6144 [0x0103]");
                    break;
                case 260:
                    list.Add("ffdhe8192 [0x0104]");
                    break;
                case 65281:
                    list.Add("arbitrary_explicit_prime_curves [0xFF01]"); // deprecated
                    break;
                case 65282:
                    list.Add("arbitrary_explicit_char2_curves [0xFF02]"); // deprecated
                    break;
                default:
                    list.Add($"unknown [0x{namedCurve:X4}]");
                    break;
            }

            i += 2;
        }

        return string.Join(", ", list.ToArray());
    }

    private static string GetEcPointFormats(ReadOnlySpan<byte> data)
    {
        var list = new List<string>();
        if (data.Length < 1) return string.Empty;

        var i = 1;
        while (i < data.Length)
        {
            switch (data[i])
            {
                case 0:
                    list.Add("uncompressed [0x0]");
                    break;
                case 1:
                    list.Add("ansiX962_compressed_prime [0x1]");
                    break;
                case 2:
                    list.Add("ansiX962_compressed_char2 [0x2]");
                    break;
                default:
                    list.Add($"unknown [0x{data[i]:X2}]");
                    break;
            }

            i += 2;
        }

        return string.Join(", ", list.ToArray());
    }

    private static List<string> GetSupportedVersions(ReadOnlySpan<byte> data)
    {
        var list = new List<string>();
        if (data.Length < 2)
        {
            return list;
        }

        int i = 0;
        if (data.Length > 2)
        {
            // client hello contains a list
            i = 1;
        }

        for (; i < data.Length; i += 2)
        {
            int val = (data[i] << 8) | data[i + 1];
            switch (val)
            {
                case 0x300:
                    list.Add("Ssl3.0");
                    continue;
                case 0x301:
                    list.Add("Tls1.0");
                    continue;
                case 0x302:
                    list.Add("Tls1.1");
                    continue;
                case 0x303:
                    list.Add("Tls1.2");
                    continue;
                case 0x304:
                    list.Add("Tls1.3");
                    continue;
            }

            string arg = "unknown";

            if ((val & 0x0A0A) == 0x0A0A && (val >> 8) == (val & 0xFF))
            {
                arg = "grease";
            }
            else if ((val & 0x7F00) == 32512)
            {
                arg = "Tls1.3_draft" + (val & 0xFF);
            }

            list.Add($"{arg} [0x{val:x}]");
        }

        return list;
    }

    private static string GetSignatureAlgorithms(ReadOnlySpan<byte> data)
    {
        // https://www.iana.org/assignments/tls-parameters/tls-parameters.xhtml
        var num = (data[0] << 8) + data[1];
        var sb = new StringBuilder();
        var index = 2;
        while (index < num + 2)
        {
            int val0 = data[index];
            int val1 = data[index + 1];
            int val = (val0 << 8) + val1;
            switch (val)
            {
                /* RSASSA-PKCS1-v1_5 algorithms */
                case 0x401:
                    sb.Append("rsa_pkcs1_sha256");
                    break;
                case 0x501:
                    sb.Append("rsa_pkcs1_sha384");
                    break;
                case 0x601:
                    sb.Append("rsa_pkcs1_sha512");
                    break;

                /* ECDSA algorithms */
                case 0x403:
                    sb.Append("ecdsa_secp256r1_sha256");
                    break;
                case 0x503:
                    sb.Append("ecdsa_secp384r1_sha384");
                    break;
                case 0x603:
                    sb.Append("ecdsa_secp521r1_sha512");
                    break;

                /* RSASSA-PSS algorithms with public key OID rsaEncryption */
                case 0x804:
                    sb.Append("rsa_pss_rsae_sha256");
                    break;
                case 0x805:
                    sb.Append("rsa_pss_rsae_sha384");
                    break;
                case 0x806:
                    sb.Append("rsa_pss_rsae_sha512");
                    break;

                /* EdDSA algorithms */
                case 0x807:
                    sb.Append("ed25519");
                    break;
                case 0x808:
                    sb.Append("ed448");
                    break;

                /* RSASSA-PSS algorithms with public key OID RSASSA-PSS */
                case 0x809:
                    sb.Append("rsa_pss_pss_sha256");
                    break;
                case 0x80A:
                    sb.Append("rsa_pss_pss_sha384");
                    break;
                case 0x80B:
                    sb.Append("rsa_pss_pss_sha512");
                    break;

                /* Legacy algorithms */
                case 0x201:
                    sb.Append("rsa_pkcs1_sha1");
                    break;
                case 0x203:
                    sb.Append("ecdsa_sha1");
                    break;

                default:
                    switch (val1)
                    {
                        case 0:
                            sb.Append("anonymous");
                            break;
                        case 1:
                            sb.Append("rsa");
                            break;
                        case 2:
                            sb.Append("dsa");
                            break;
                        case 3:
                            sb.Append("ecdsa");
                            break;
                        case 7:
                            sb.Append("ed25519");
                            break;
                        case 8:
                            sb.Append("ed448");
                            break;
                        case 64:
                            sb.Append("gostr34102012_256");
                            break;
                        case 65:
                            sb.Append("gostr34102012_512");
                            break;
                        default:
                            sb.AppendFormat(val1 >= 224 ? "Reserved for Private Use[0x{0:X2}]" : "Reserved[0x{0:X2}]",
                                val1);
                            break;
                    }

                    sb.AppendFormat("_");

                    switch (val0)
                    {
                        case 0:
                            sb.Append("none");
                            break;
                        case 1:
                            sb.Append("md5");
                            break;
                        case 2:
                            sb.Append("sha1");
                            break;
                        case 3:
                            sb.Append("sha224");
                            break;
                        case 4:
                            sb.Append("sha256");
                            break;
                        case 5:
                            sb.Append("sha384");
                            break;
                        case 6:
                            sb.Append("sha512");
                            break;
                        case 8:
                            sb.Append("Intrinsic");
                            break;
                        default:
                            sb.AppendFormat(val0 >= 224 ? "Reserved for Private Use[0x{0:X2}]" : "Reserved[0x{0:X2}]",
                                val0);
                            break;
                    }

                    break;
            }

            sb.AppendFormat(", ");
            index += 2;
        }

        if (sb.Length > 1)
            sb.Length -= 2;

        return sb.ToString();
    }

    private static List<SslApplicationProtocol> GetApplicationLayerProtocolNegotiation(ReadOnlySpan<byte> data)
    {
        var list = new List<SslApplicationProtocol>();
        var index = 2;
        while (index < data.Length)
        {
            int count = data[index];
            var protocol = data.Slice(index + 1, count);
            if (Http11Utf8.AsSpan().SequenceEqual(protocol))
            {
                list.Add(SslApplicationProtocol.Http11);
            }
            else if (Http2Utf8.AsSpan().SequenceEqual(protocol))
            {
                list.Add(SslApplicationProtocol.Http2);
            }
            else if (Http3Utf8.AsSpan().SequenceEqual(protocol))
            {
                list.Add(SslApplicationProtocol.Http3);
            }
            else
            {
                list.Add(new SslApplicationProtocol(protocol.ToArray()));
            }

            index += 1 + count;
        }

        return list;
    }

    private static string GetExtensionName(int value)
    {
        // https://www.iana.org/assignments/tls-extensiontype-values/tls-extensiontype-values.xhtml
        switch (value)
        {
            case 0:
                return "server_name";
            case 1:
                return "max_fragment_length";
            case 2:
                return "client_certificate_url";
            case 3:
                return "trusted_ca_keys";
            case 4:
                return "truncated_hmac";
            case 5:
                return "status_request";
            case 6:
                return "user_mapping";
            case 7:
                return "client_authz";
            case 8:
                return "server_authz";
            case 9:
                return "cert_type";
            case 10:
                return "supported_groups"; // renamed from "elliptic_curves" (RFC 7919 / TLS 1.3)
            case 11:
                return "ec_point_formats";
            case 12:
                return "srp";
            case 13:
                return "signature_algorithms";
            case 14:
                return "use_srtp";
            case 15:
                return "heartbeat";
            case 16:
                return "ALPN"; // application_layer_protocol_negotiation
            case 17:
                return "status_request_v2";
            case 18:
                return "signed_certificate_timestamp";
            case 19:
                return "client_certificate_type";
            case 20:
                return "server_certificate_type";
            case 21:
                return "padding";
            case 22:
                return "encrypt_then_mac";
            case 23:
                return "extended_master_secret";
            case 24:
                return
                    "token_binding"; // TEMPORARY - registered 2016-02-04, extension registered 2017-01-12, expires 2018-02-04
            case 25:
                return "cached_info";
            case 26:
                return "quic_transports_parameters"; // Not yet assigned by IANA (QUIC-TLS Draft04)
            case 35:
                return "SessionTicket TLS";
            // TLS 1.3 draft: https://tools.ietf.org/html/draft-ietf-tls-tls13
            case 40:
                return "key_share";
            case 41:
                return "pre_shared_key";
            case 42:
                return "early_data";
            case 43:
                return "supported_versions";
            case 44:
                return "cookie";
            case 45:
                return "psk_key_exchange_modes";
            case 46:
                return "ticket_early_data_info";
            case 47:
                return "certificate_authorities";
            case 48:
                return "oid_filters";
            case 49:
                return "post_handshake_auth";
            case 2570: // 0a0a
            case 6682: // 1a1a
            case 10794: // 2a2a
            case 14906: // 3a3a
            case 19018: // 4a4a
            case 23130: // 5a5a
            case 27242: // 6a6a
            case 31354: // 7a7a
            case 35466: // 8a8a
            case 39578: // 9a9a
            case 43690: // aaaa
            case 47802: // baba
            case 51914: // caca
            case 56026: // dada
            case 60138: // eaea
            case 64250: // fafa
                return "Reserved (GREASE)";
            case 13172:
                return "next_protocol_negotiation";
            case 30031:
                return "channel_id_old"; // Google
            case 30032:
                return "channel_id"; // Google
            case 35655:
                return "draft-agl-tls-padding";
            case 65281:
                return "renegotiation_info";
            case 65282:
                return
                    "Draft version of TLS 1.3"; // for experimentation only  https://www.ietf.org/mail-archive/web/tls/current/msg20853.html
            default:
                return $"unknown_{value:x2}";
        }
    }
}ParseOptions.0.jsonÎ
^D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\Models\TaskResult.csÛ
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.StreamExtended.Network;

/// <summary>
///     Mimic a Task but you can set AsyncState
/// </summary>
public class TaskResult : IAsyncResult
{
    private readonly Task task;

    public TaskResult(Task pTask, object? state)
    {
        task = pTask;
        AsyncState = state;
    }

    public object? AsyncState { get; }

    public WaitHandle AsyncWaitHandle => ((IAsyncResult)task).AsyncWaitHandle;

    public bool CompletedSynchronously => ((IAsyncResult)task).CompletedSynchronously;

    public bool IsCompleted => task.IsCompleted;

    public void GetResult()
    {
        task.GetAwaiter().GetResult();
    }
}

/// <summary>
///     Mimic a Task&lt;T&gt; but you can set AsyncState
/// </summary>
/// <typeparam name="T"></typeparam>
public class TaskResult<T> : IAsyncResult
{
    private readonly Task<T> task;

    public TaskResult(Task<T> pTask, object? state)
    {
        task = pTask;
        AsyncState = state;
    }

    public T Result => task.Result;

    public object? AsyncState { get; }

    public WaitHandle AsyncWaitHandle => ((IAsyncResult)task).AsyncWaitHandle;

    public bool CompletedSynchronously => ((IAsyncResult)task).CompletedSynchronously;

    public bool IsCompleted => task.IsCompleted;
}ParseOptions.0.jsonú
fD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\Readers\IHttpStreamReader.csúusing System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.StreamExtended.Network;

public interface IHttpStreamReader : ILineStream
{
    int Read(byte[] buffer, int offset, int count);

    Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);

    Task CopyBodyAsync(IHttpStreamWriter writer, bool isChunked, long contentLength,
        bool isRequest, SessionEventArgs args, CancellationToken cancellationToken);
}ParseOptions.0.jsonˇ
eD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\Readers\PeekStreamReader.csÄusing System;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.StreamExtended.Network;

internal class PeekStreamReader
{
    private readonly IPeekStream baseStream;

    public PeekStreamReader(IPeekStream baseStream, int startPosition = 0)
    {
        this.baseStream = baseStream;
        Position = startPosition;
    }

    public int Position { get; private set; }

    public async ValueTask<bool> EnsureBufferLength(int length, CancellationToken cancellationToken)
    {
        var val = await baseStream.PeekByteAsync(Position + length - 1, cancellationToken);
        return val != -1;
    }

    public byte ReadByte()
    {
        return baseStream.PeekByteFromBuffer(Position++);
    }

    public int ReadInt16()
    {
        int i1 = ReadByte();
        int i2 = ReadByte();
        return (i1 << 8) + i2;
    }

    public int ReadInt24()
    {
        int i1 = ReadByte();
        int i2 = ReadByte();
        int i3 = ReadByte();
        return (i1 << 16) + (i2 << 8) + i3;
    }

    public byte[] ReadBytes(int length)
    {
        var buffer = new byte[length];
        for (var i = 0; i < buffer.Length; i++) buffer[i] = ReadByte();

        return buffer;
    }

    public void ReadBytes(Span<byte> data)
    {
        for (var i = 0; i < data.Length; i++) data[i] = ReadByte();
    }
}ParseOptions.0.jsonÁ
XD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\RetryPolicy.csıusing System;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.Network;

internal class RetryPolicy<T> where T : Exception
{
    private readonly int retries;
    private readonly TcpConnectionFactory tcpConnectionFactory;

    private TcpServerConnection? currentConnection;

    internal RetryPolicy(int retries, TcpConnectionFactory tcpConnectionFactory)
    {
        this.retries = retries;
        this.tcpConnectionFactory = tcpConnectionFactory;
    }

    /// <summary>
    ///     Execute and retry the given action until retry number of times.
    /// </summary>
    /// <param name="action">The action to retry with return value specifying whether caller should continue execution.</param>
    /// <param name="generator">The Tcp connection generator to be invoked to get new connection for retry.</param>
    /// <param name="initialConnection">Initial Tcp connection to use.</param>
    /// <returns>Returns the latest connection used and the latest exception if any.</returns>
    internal async Task<RetryResult> ExecuteAsync(Func<TcpServerConnection, Task<bool>> action,
        Func<Task<TcpServerConnection>> generator, TcpServerConnection? initialConnection)
    {
        currentConnection = initialConnection;
        var @continue = true;
        Exception? exception = null;

        var attempts = retries;

        while (true)
        {
            // setup connection
            currentConnection ??= await generator();

            try
            {
                @continue = await action(currentConnection);
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            attempts--;

            if (attempts < 0 || exception == null || !(exception is T)) break;

            exception = null;

            // before retry clear connection
            await tcpConnectionFactory.Release(currentConnection, true);
            currentConnection = null;
        }

        return new RetryResult(currentConnection, exception, @continue);
    }
}

internal class RetryResult
{
    internal RetryResult(TcpServerConnection? lastConnection, Exception? exception, bool @continue)
    {
        LatestConnection = lastConnection;
        Exception = exception;
        Continue = @continue;
    }

    internal TcpServerConnection? LatestConnection { get; }

    internal Exception? Exception { get; }

    internal bool Continue { get; }
}ParseOptions.0.jsonƒ,
`D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\Ssl\ClientHelloInfo.cs +using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.StreamExtended.Models;

namespace Titanium.Web.Proxy.StreamExtended;

/// <summary>
///     Wraps up the client SSL hello information.
/// </summary>
public class ClientHelloInfo
{
    private static readonly string[] compressions =
    {
        "null",
        "DEFLATE"
    };

    internal ClientHelloInfo(int handshakeVersion, int majorVersion, int minorVersion, byte[] random, byte[] sessionId,
        int[] ciphers, int clientHelloLength)
    {
        HandshakeVersion = handshakeVersion;
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        Random = random;
        SessionId = sessionId;
        Ciphers = ciphers;
        ClientHelloLength = clientHelloLength;
    }

    public int HandshakeVersion { get; }

    public int MajorVersion { get; }

    public int MinorVersion { get; }

    public byte[] Random { get; }

    public DateTime Time
    {
        get
        {
            var time = DateTime.MinValue;
            if (Random.Length > 3)
                time = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)
                    .AddSeconds(((uint)Random[3] << 24) + ((uint)Random[2] << 16) + ((uint)Random[1] << 8) + Random[0])
                    .ToLocalTime();

            return time;
        }
    }

    public byte[] SessionId { get; }

    public int[] Ciphers { get; }

    public byte[]? CompressionData { get; internal set; }

    internal int ClientHelloLength { get; }

    internal int ExtensionsStartPosition { get; set; }

    public Dictionary<string, SslExtension>? Extensions { get; set; }

    public SslProtocols SslProtocol
    {
        get
        {
            var major = MajorVersion;
            var minor = MinorVersion;
            if (major == 3 && minor == 3)
            {
#if NET6_0_OR_GREATER
                var protocols = this.GetSslProtocols();
                if (protocols != null)
                {
                    if (protocols.Contains("Tls1.3"))
                    {
                        return SslProtocols.Tls12 | SslProtocols.Tls13;
                    }
                }
#endif

                return SslProtocols.Tls12;
            }

            if (major == 3 && minor == 2)
#pragma warning disable SYSLIB0039 // Report the legacy protocol advertised by this ClientHello.
                return SslProtocols.Tls11;
#pragma warning restore SYSLIB0039

            if (major == 3 && minor == 1)
#pragma warning disable SYSLIB0039 // Report the legacy protocol advertised by this ClientHello.
                return SslProtocols.Tls;
#pragma warning restore SYSLIB0039

#pragma warning disable 618
            if (major == 3 && minor == 0)
                return SslProtocols.Ssl3;

            if (major == 2 && minor == 0)
                return SslProtocols.Ssl2;
#pragma warning restore 618

            return SslProtocols.None;
        }
    }

    private static string SslVersionToString(int major, int minor)
    {
        var str = "Unknown";
        if (major == 3 && minor == 3)
            str = "TLS/1.2";
        else if (major == 3 && minor == 2)
            str = "TLS/1.1";
        else if (major == 3 && minor == 1)
            str = "TLS/1.0";
        else if (major == 3 && minor == 0)
            str = "SSL/3.0";
        else if (major == 2 && minor == 0)
            str = "SSL/2.0";

        return $"{major}.{minor} ({str})";
    }

    /// <summary>
    ///     Returns a <see cref="System.String" /> that represents this instance.
    /// </summary>
    /// <returns>
    ///     A <see cref="System.String" /> that represents this instance.
    /// </returns>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            $"A SSLv{HandshakeVersion}-compatible ClientHello handshake was found. Titanium extracted the parameters below.");
        sb.AppendLine();
        sb.AppendLine($"Version: {SslVersionToString(MajorVersion, MinorVersion)}");
        sb.AppendLine($"Random: {StringExtensions.ByteArrayToHexString(Random)}");
        sb.AppendLine($"\"Time\": {Time}");
        sb.AppendLine($"SessionID: {StringExtensions.ByteArrayToHexString(SessionId)}");

        if (Extensions != null)
        {
            sb.AppendLine("Extensions:");
            foreach (var extension in Extensions.Values.OrderBy(x => x.Position))
                sb.AppendLine($"{extension.Name}: {extension.Data}");
        }

        if (CompressionData != null && CompressionData.Length > 0)
        {
            int compressionMethod = CompressionData[0];
            var compression = compressions.Length > compressionMethod
                ? compressions[compressionMethod]
                : $"unknown [0x{compressionMethod:X2}]";
            sb.AppendLine($"Compression: {compression}");
        }

        if (Ciphers.Length > 0)
        {
            sb.AppendLine("Ciphers:");
            foreach (var cipherSuite in Ciphers)
            {
                if (!SslCiphers.Ciphers.TryGetValue(cipherSuite, out var cipherStr)) cipherStr = "unknown";

                sb.AppendLine($"[0x{cipherSuite:X4}] {cipherStr}");
            }
        }

        return sb.ToString();
    }
}ParseOptions.0.jsonÛ
`D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\Ssl\ServerHelloInfo.cs˘using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.StreamExtended.Models;

namespace Titanium.Web.Proxy.StreamExtended;

/// <summary>
///     Wraps up the server SSL hello information.
/// </summary>
public class ServerHelloInfo
{
    private static readonly string[] compressions =
    {
        "null",
        "DEFLATE"
    };

    public ServerHelloInfo(int handshakeVersion, int majorVersion, int minorVersion, byte[] random,
        byte[] sessionId, int cipherSuite, int serverHelloLength)
    {
        HandshakeVersion = handshakeVersion;
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        Random = random;
        SessionId = sessionId;
        CipherSuite = cipherSuite;
        ServerHelloLength = serverHelloLength;
    }

    public int HandshakeVersion { get; }

    public int MajorVersion { get; }

    public int MinorVersion { get; }

    public byte[] Random { get; }

    public DateTime Time
    {
        get
        {
            var time = DateTime.MinValue;
            if (Random.Length > 3)
                time = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)
                    .AddSeconds(((uint)Random[3] << 24) + ((uint)Random[2] << 16) + ((uint)Random[1] << 8) + Random[0])
                    .ToLocalTime();

            return time;
        }
    }

    public byte[] SessionId { get; }

    public int CipherSuite { get; }

    public byte CompressionMethod { get; set; }

    internal int ServerHelloLength { get; }

    internal int EntensionsStartPosition { get; set; }

    public Dictionary<string, SslExtension>? Extensions { get; set; }

    private static string SslVersionToString(int major, int minor)
    {
        var str = "Unknown";
        if (major == 3 && minor == 3)
            str = "TLS/1.2";
        else if (major == 3 && minor == 2)
            str = "TLS/1.1";
        else if (major == 3 && minor == 1)
            str = "TLS/1.0";
        else if (major == 3 && minor == 0)
            str = "SSL/3.0";
        else if (major == 2 && minor == 0)
            str = "SSL/2.0";

        return $"{major}.{minor} ({str})";
    }

    /// <summary>
    ///     Returns a <see cref="System.String" /> that represents this instance.
    /// </summary>
    /// <returns>
    ///     A <see cref="System.String" /> that represents this instance.
    /// </returns>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            $"A SSLv{HandshakeVersion}-compatible ServerHello handshake was found. Titanium extracted the parameters below.");
        sb.AppendLine();
        sb.AppendLine($"Version: {SslVersionToString(MajorVersion, MinorVersion)}");
        sb.AppendLine($"Random: {StringExtensions.ByteArrayToHexString(Random)}");
        sb.AppendLine($"\"Time\": {Time}");
        sb.AppendLine($"SessionID: {StringExtensions.ByteArrayToHexString(SessionId)}");

        if (Extensions != null)
        {
            sb.AppendLine("Extensions:");
            foreach (var extension in Extensions.Values.OrderBy(x => x.Position))
                sb.AppendLine($"{extension.Name}: {extension.Data}");
        }

        var compression = compressions.Length > CompressionMethod
            ? compressions[CompressionMethod]
            : $"unknown [0x{CompressionMethod:X2}]";
        sb.AppendLine($"Compression: {compression}");

        sb.Append("Cipher:");
        if (!SslCiphers.Ciphers.TryGetValue(CipherSuite, out var cipherStr)) cipherStr = "unknown";

        sb.AppendLine($"[0x{CipherSuite:X4}] {cipherStr}");

        return sb.ToString();
    }
}ParseOptions.0.json‡Z
YD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\Ssl\SslTools.csÌYusing System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.StreamExtended.BufferPool;
using Titanium.Web.Proxy.StreamExtended.Models;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.StreamExtended;

/// <summary>
///     Use this class to peek SSL client/server hello information.
/// </summary>
internal class SslTools
{
    /// <summary>
    ///     Peek the SSL client hello information.
    /// </summary>
    /// <param name="clientStream"></param>
    /// <param name="bufferPool"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async Task<ClientHelloInfo?> PeekClientHello(IPeekStream clientStream, IBufferPool bufferPool,
        CancellationToken cancellationToken = default)
    {
        // detects the HTTPS ClientHello message as it is described in the following url:
        // https://stackoverflow.com/questions/3897883/how-to-detect-an-incoming-ssl-https-handshake-ssl-wire-format

        var recordType = await clientStream.PeekByteAsync(0, cancellationToken);
        if (recordType == -1) return null;

        if ((recordType & 0x80) == 0x80)
        {
            // SSL 2
            var peekStream = new PeekStreamReader(clientStream, 1);

            // length value + minimum length
            if (!await peekStream.EnsureBufferLength(10, cancellationToken)) return null;

            var recordLength = ((recordType & 0x7f) << 8) + peekStream.ReadByte();
            if (recordLength < 9)
                // Message body too short.
                return null;

            if (peekStream.ReadByte() != 0x01)
                // should be ClientHello
                return null;

            int majorVersion = peekStream.ReadByte();
            int minorVersion = peekStream.ReadByte();

            var ciphersCount = peekStream.ReadInt16() / 3;
            var sessionIdLength = peekStream.ReadInt16();
            var randomLength = peekStream.ReadInt16();

            if (!await peekStream.EnsureBufferLength(ciphersCount * 3 + sessionIdLength + randomLength,
                    cancellationToken)) return null;

            var ciphers = new int[ciphersCount];
            for (var i = 0; i < ciphers.Length; i++)
                ciphers[i] = (peekStream.ReadByte() << 16) + (peekStream.ReadByte() << 8) + peekStream.ReadByte();

            var sessionId = peekStream.ReadBytes(sessionIdLength);
            var random = peekStream.ReadBytes(randomLength);

            var clientHelloInfo = new ClientHelloInfo(2, majorVersion, minorVersion, random, sessionId, ciphers,
                peekStream.Position);

            return clientHelloInfo;
        }

        if (recordType == 0x16)
        {
            var peekStream = new PeekStreamReader(clientStream, 1);

            // should contain at least 43 bytes
            // 2 version + 2 length + 1 type + 3 length(?) + 2 version +  32 random + 1 sessionid length
            if (!await peekStream.EnsureBufferLength(43, cancellationToken)) return null;

            // SSL 3.0 or TLS 1.0, 1.1 and 1.2
            int majorVersion = peekStream.ReadByte();
            int minorVersion = peekStream.ReadByte();

            var recordLength = peekStream.ReadInt16();

            if (peekStream.ReadByte() != 0x01)
                // should be ClientHello
                return null;

            var length = peekStream.ReadInt24();

            majorVersion = peekStream.ReadByte();
            minorVersion = peekStream.ReadByte();

            var random = peekStream.ReadBytes(32);
            length = peekStream.ReadByte();

            // sessionid + 2 ciphersData length
            if (!await peekStream.EnsureBufferLength(length + 2, cancellationToken)) return null;

            var sessionId = peekStream.ReadBytes(length);

            length = peekStream.ReadInt16();

            // ciphersData + compressionData length
            if (!await peekStream.EnsureBufferLength(length + 1, cancellationToken)) return null;

            var ciphers = new int[length / 2];
            for (var i = 0; i < ciphers.Length; i++) ciphers[i] = peekStream.ReadInt16();

            length = peekStream.ReadByte();
            if (length < 1) return null;

            // compressionData
            if (!await peekStream.EnsureBufferLength(length, cancellationToken)) return null;

            var compressionData = peekStream.ReadBytes(length);

            var extensionsStartPosition = peekStream.Position;

            Dictionary<string, SslExtension>? extensions = null;

            if (extensionsStartPosition < recordLength + 5)
                extensions = await ReadExtensions(majorVersion, minorVersion, peekStream, cancellationToken);

            var clientHelloInfo = new ClientHelloInfo(3, majorVersion, minorVersion, random, sessionId, ciphers,
                peekStream.Position)
            {
                ExtensionsStartPosition = extensionsStartPosition,
                CompressionData = compressionData,
                Extensions = extensions
            };

            return clientHelloInfo;
        }

        return null;
    }


    /// <summary>
    ///     Is the given stream starts with an SSL client hello?
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="bufferPool"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async Task<bool> IsServerHello(IPeekStream stream, IBufferPool bufferPool,
        CancellationToken cancellationToken)
    {
        var serverHello = await PeekServerHello(stream, bufferPool, cancellationToken);
        return serverHello != null;
    }

    /// <summary>
    ///     Peek the SSL client hello information.
    /// </summary>
    /// <param name="serverStream"></param>
    /// <param name="bufferPool"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async Task<ServerHelloInfo?> PeekServerHello(IPeekStream serverStream, IBufferPool bufferPool,
        CancellationToken cancellationToken = default)
    {
        // detects the HTTPS ClientHello message as it is described in the following url:
        // https://stackoverflow.com/questions/3897883/how-to-detect-an-incoming-ssl-https-handshake-ssl-wire-format

        var recordType = await serverStream.PeekByteAsync(0, cancellationToken);
        if (recordType == -1) return null;

        if ((recordType & 0x80) == 0x80)
        {
            // SSL 2
            // not tested. SSL2 is deprecated
            var peekStream = new PeekStreamReader(serverStream, 1);

            // length value + minimum length
            if (!await peekStream.EnsureBufferLength(39, cancellationToken)) return null;

            var recordLength = ((recordType & 0x7f) << 8) + peekStream.ReadByte();
            if (recordLength < 38)
                // Message body too short.
                return null;

            if (peekStream.ReadByte() != 0x04)
                // should be ServerHello
                return null;

            int majorVersion = peekStream.ReadByte();
            int minorVersion = peekStream.ReadByte();

            // 32 bytes random + 1 byte sessionId + 2 bytes cipherSuite
            if (!await peekStream.EnsureBufferLength(35, cancellationToken)) return null;

            var random = peekStream.ReadBytes(32);
            var sessionId = peekStream.ReadBytes(1);
            var cipherSuite = peekStream.ReadInt16();

            var serverHelloInfo = new ServerHelloInfo(2, majorVersion, minorVersion, random, sessionId, cipherSuite,
                peekStream.Position);

            return serverHelloInfo;
        }

        if (recordType == 0x16)
        {
            var peekStream = new PeekStreamReader(serverStream, 1);

            // should contain at least 43 bytes
            // 2 version + 2 length + 1 type + 3 length(?) + 2 version +  32 random + 1 sessionid length
            if (!await peekStream.EnsureBufferLength(43, cancellationToken)) return null;

            // SSL 3.0 or TLS 1.0, 1.1 and 1.2
            int majorVersion = peekStream.ReadByte();
            int minorVersion = peekStream.ReadByte();

            var recordLength = peekStream.ReadInt16();

            if (peekStream.ReadByte() != 0x02)
                // should be ServerHello
                return null;

            var length = peekStream.ReadInt24();

            majorVersion = peekStream.ReadByte();
            minorVersion = peekStream.ReadByte();

            var random = peekStream.ReadBytes(32);
            length = peekStream.ReadByte();

            // sessionid + cipherSuite + compressionMethod
            if (!await peekStream.EnsureBufferLength(length + 2 + 1, cancellationToken)) return null;

            var sessionId = peekStream.ReadBytes(length);

            var cipherSuite = peekStream.ReadInt16();
            var compressionMethod = peekStream.ReadByte();

            var extensionsStartPosition = peekStream.Position;

            Dictionary<string, SslExtension>? extensions = null;

            if (extensionsStartPosition < recordLength + 5)
                extensions = await ReadExtensions(majorVersion, minorVersion, peekStream, cancellationToken);

            var serverHelloInfo = new ServerHelloInfo(3, majorVersion, minorVersion, random, sessionId, cipherSuite,
                peekStream.Position)
            {
                CompressionMethod = compressionMethod,
                EntensionsStartPosition = extensionsStartPosition,
                Extensions = extensions
            };

            return serverHelloInfo;
        }

        return null;
    }

    private static async Task<Dictionary<string, SslExtension>?> ReadExtensions(int majorVersion, int minorVersion,
        PeekStreamReader peekStreamReader, CancellationToken cancellationToken)
    {
        Dictionary<string, SslExtension>? extensions = null;
        if (majorVersion > 3 || majorVersion == 3 && minorVersion >= 1)
            if (await peekStreamReader.EnsureBufferLength(2, cancellationToken))
            {
                var extensionsLength = peekStreamReader.ReadInt16();

                if (await peekStreamReader.EnsureBufferLength(extensionsLength, cancellationToken))
                {
                    var extensionsData = peekStreamReader.ReadBytes(extensionsLength).AsMemory();
                    extensions = new Dictionary<string, SslExtension>();
                    var idx = 0;
                    while (extensionsData.Length > 3)
                    {
                        var id = BinaryPrimitives.ReadInt16BigEndian(extensionsData.Span);
                        var length = BinaryPrimitives.ReadInt16BigEndian(extensionsData.Span.Slice(2));
                        var extension = new SslExtension(id, extensionsData.Slice(4, length), idx++);
                        extensions[extension.Name] = extension;
                        extensionsData = extensionsData.Slice(4 + length);
                    }
                }
            }

        return extensions;
    }
}ParseOptions.0.jsonÏ
eD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\Streams\BodyStreamWriter.csÌusing System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     A write-only stream handed to consumers of <see cref="EventArguments.SessionEventArgs.RespondStreaming" />
///     so they can push a response body to the client without buffering it in memory.
///     In chunked mode each write is emitted as an HTTP/1.1 chunk; in fixed-length mode the bytes are written
///     raw (the caller is responsible for writing exactly the declared Content-Length number of bytes).
/// </summary>
internal sealed class BodyStreamWriter : Stream
{
    private readonly IHttpStreamWriter writer;
    private readonly bool isChunked;
    private bool completed;

    internal BodyStreamWriter(IHttpStreamWriter writer, bool isChunked)
    {
        this.writer = writer;
        this.isChunked = isChunked;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (count == 0) return;

        if (isChunked)
        {
            await writer.WriteLineAsync(count.ToString("x"), cancellationToken);
            await writer.WriteAsync(buffer, offset, count, cancellationToken);
            await writer.WriteLineAsync(cancellationToken);
        }
        else
        {
            await writer.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }

#if NET6_0_OR_GREATER
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(buffer, out var segment) && segment.Array != null)
        {
            await WriteAsync(segment.Array, segment.Offset, segment.Count, cancellationToken);
        }
        else
        {
            var array = buffer.ToArray();
            await WriteAsync(array, 0, array.Length, cancellationToken);
        }
    }
#endif

    /// <summary>
    ///     Writes the terminating chunk when in chunked mode. Must be called once the consumer's write delegate
    ///     has completed. No-op for fixed-length mode.
    /// </summary>
    internal async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (completed) return;
        completed = true;

        if (isChunked)
        {
            await writer.WriteLineAsync("0", cancellationToken);
            await writer.WriteLineAsync(cancellationToken);
        }
    }
}
ParseOptions.0.json‹
_D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\Streams\CopyStream.cs„using System;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.StreamExtended.Network;

/// <summary>
///     Copies the source stream to destination stream.
///     But this let users to peek and read the copying process.
/// </summary>
internal class CopyStream : ILineStream, IDisposable
{
    private readonly byte[] buffer;

    private readonly IBufferPool bufferPool;
    private readonly IHttpStreamReader reader;

    private readonly IHttpStreamWriter writer;

    private int bufferLength;

    private bool disposed;

    public CopyStream(IHttpStreamReader reader, IHttpStreamWriter writer, IBufferPool bufferPool)
    {
        this.reader = reader;
        this.writer = writer;
        buffer = bufferPool.GetBuffer();
        this.bufferPool = bufferPool;
    }

    public long ReadBytes { get; private set; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public bool DataAvailable => reader.DataAvailable;

    public async ValueTask<bool> FillBufferAsync(CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken);
        return await reader.FillBufferAsync(cancellationToken);
    }

    public byte ReadByteFromBuffer()
    {
        var b = reader.ReadByteFromBuffer();
        buffer[bufferLength++] = b;
        ReadBytes++;
        return b;
    }

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        return HttpStream.ReadLineInternalAsync(this, bufferPool, cancellationToken);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        // send out the current data from from the buffer
        if (bufferLength > 0)
        {
            await writer.WriteAsync(buffer, 0, bufferLength, cancellationToken);
            bufferLength = 0;
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposed) return;

        // Return the pooled buffer on both the normal Dispose and the finalizer path.
        // ArrayPool.Return is thread-safe, and the buffer/bufferPool references remain
        // reachable via this instance until it is collected, so this is safe from a finalizer.
        // This prevents leaking a rented buffer if the stream is ever finalized without disposal.
        bufferPool.ReturnBuffer(buffer);

        disposed = true;
    }

    ~CopyStream()
    {
#if DEBUG
            // Finalizer should not be called
            System.Diagnostics.Debugger.Break();
#endif

        Dispose(false);
    }
}ParseOptions.0.jsonõ
eD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\Streams\HttpClientStream.csúusing System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.Helpers;

internal sealed class HttpClientStream : HttpStream
{
    internal HttpClientStream(ProxyServer server, TcpClientConnection connection, Stream stream, IBufferPool bufferPool,
        CancellationToken cancellationToken)
        : base(server, stream, bufferPool, cancellationToken)
    {
        Connection = connection;
    }

    public TcpClientConnection Connection { get; }

    /// <summary>
    ///     Writes the response.
    /// </summary>
    /// <param name="response">The response object.</param>
    /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
    /// <returns>The Task.</returns>
    internal async ValueTask WriteResponseAsync(Response response, CancellationToken cancellationToken = default)
    {
        var headerBuilder = new HeaderBuilder();

        // Write back response status to client
        headerBuilder.WriteResponseLine(response.HttpVersion, response.StatusCode, response.StatusDescription);

        await WriteAsync(response, headerBuilder, cancellationToken);
    }

    internal async ValueTask<RequestStatusInfo> ReadRequestLine(CancellationToken cancellationToken = default)
    {
        // read the first line HTTP command
        var httpCmd = await ReadLineAsync(cancellationToken);
        if (string.IsNullOrEmpty(httpCmd)) return default;

        Request.ParseRequestLine(httpCmd!, out var method, out var requestUri, out var version);

        return new RequestStatusInfo { Method = method, RequestUri = requestUri, Version = version };
    }
}ParseOptions.0.jsonî
eD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\Streams\HttpServerStream.csïusing System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.Helpers;

internal sealed class HttpServerStream : HttpStream
{
    internal HttpServerStream(ProxyServer server, Stream stream, IBufferPool bufferPool,
        CancellationToken cancellationToken)
        : base(server, stream, bufferPool, cancellationToken)
    {
    }

    /// <summary>
    ///     Writes the request.
    /// </summary>
    /// <param name="request">The request object.</param>
    /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
    /// <returns></returns>
    internal async ValueTask WriteRequestAsync(Request request, CancellationToken cancellationToken = default)
    {
        var headerBuilder = new HeaderBuilder();
        headerBuilder.WriteRequestLine(request.Method, request.RequestUriString, request.HttpVersion);
        await WriteAsync(request, headerBuilder, cancellationToken);
    }

    internal async ValueTask<ResponseStatusInfo> ReadResponseStatus(CancellationToken cancellationToken = default)
    {
        var httpStatus = await ReadLineAsync(cancellationToken) ??
                         throw new IOException("Invalid http status code.");

        if (httpStatus == string.Empty)
            // is this really possible?
            httpStatus = await ReadLineAsync(cancellationToken) ??
                         throw new IOException("Response status is empty.");

        Response.ParseResponseLine(httpStatus, out var version, out var statusCode, out var description);
        return new ResponseStatusInfo { Version = version, StatusCode = statusCode, Description = description };
    }
}ParseOptions.0.jsonÊä
_D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\Streams\HttpStream.csÏâusing System;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Shared;
using Titanium.Web.Proxy.StreamExtended.BufferPool;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Helpers;

internal class HttpStream : Stream, IHttpStreamWriter, IHttpStreamReader, IPeekStream
{
    private readonly bool leaveOpen;
    private readonly byte[] streamBuffer;

    private static Encoding Encoding => HttpHeader.Encoding;

    // On .NET Framework, NetworkStream does not override the cancellable ReadAsync/WriteAsync
    // overloads (they fall back to Stream's sync-over-async), so we route Begin/End Read/Write
    // through our own Task-based async methods. Modern .NET implements true async socket I/O, so
    // this stays false there and the base Stream implementation is used directly.
#if NETFRAMEWORK
    private static readonly bool networkStreamHack;
#else
    private static readonly bool networkStreamHack = false;
#endif

    private int bufferPos;

    private bool disposed;

    private bool closedWrite;

    private readonly IBufferPool bufferPool;
    private readonly CancellationToken cancellationToken;

    public bool IsNetworkStream { get; }

    public event EventHandler<DataEventArgs>? DataRead;

    public event EventHandler<DataEventArgs>? DataWrite;

    private Stream BaseStream { get; }

    public bool IsClosed { get; private set; }

#if NETFRAMEWORK
    static HttpStream()
    {
        // Detect whether NetworkStream provides its own cancellable ReadAsync. If it only inherits
        // Stream's implementation (as on .NET Framework), enable the async routing hack below.
        try
        {
            var method = typeof(NetworkStream).GetMethod(nameof(Stream.ReadAsync),
                new[] { typeof(byte[]), typeof(int), typeof(int), typeof(CancellationToken) });
            if (method == null || method.DeclaringType == typeof(Stream)) networkStreamHack = true;
        }
        catch
        {
            networkStreamHack = true;
        }
    }
#endif

    private static readonly byte[] newLine = ProxyConstants.NewLineBytes;
    private readonly ProxyServer server;

    /// <summary>
    ///     Initializes a new instance of the <see cref="HttpStream" /> class.
    /// </summary>
    /// <param name="baseStream">The base stream.</param>
    /// <param name="bufferPool">Bufferpool.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="leaveOpen">
    ///     <see langword="true" /> to leave the stream open after disposing the
    ///     <see cref="T:CustomBufferedStream" /> object; otherwise, <see langword="false" />.
    /// </param>
    internal HttpStream(ProxyServer server, Stream baseStream, IBufferPool bufferPool,
        CancellationToken cancellationToken, bool leaveOpen = false)
    {
        this.server = server;

        if (baseStream is NetworkStream) IsNetworkStream = true;

        BaseStream = baseStream;
        this.leaveOpen = leaveOpen;
        streamBuffer = bufferPool.GetBuffer();
        this.bufferPool = bufferPool;
        this.cancellationToken = cancellationToken;
    }

    /// <summary>
    ///     When overridden in a derived class, clears all buffers for this stream and causes any buffered data to be written
    ///     to the underlying device.
    /// </summary>
    public override void Flush()
    {
        if (closedWrite) return;

        try
        {
            BaseStream.Flush();
        }
        catch
        {
            closedWrite = true;
            if (!IsNetworkStream)
                throw;
        }
    }

    /// <summary>
    ///     When overridden in a derived class, sets the position within the current stream.
    /// </summary>
    /// <param name="offset">A byte offset relative to the <paramref name="origin" /> parameter.</param>
    /// <param name="origin">
    ///     A value of type <see cref="T:System.IO.SeekOrigin" /> indicating the reference point used to
    ///     obtain the new position.
    /// </param>
    /// <returns>
    ///     The new position within the current stream.
    /// </returns>
    public override long Seek(long offset, SeekOrigin origin)
    {
        Available = 0;
        bufferPos = 0;
        return BaseStream.Seek(offset, origin);
    }

    /// <summary>
    ///     When overridden in a derived class, sets the length of the current stream.
    /// </summary>
    /// <param name="value">The desired length of the current stream in bytes.</param>
    public override void SetLength(long value)
    {
        BaseStream.SetLength(value);
    }

    /// <summary>
    ///     When overridden in a derived class, reads a sequence of bytes from the current stream and advances the position
    ///     within the stream by the number of bytes read.
    /// </summary>
    /// <param name="buffer">
    ///     An array of bytes. When this method returns, the buffer contains the specified byte array with the
    ///     values between <paramref name="offset" /> and (<paramref name="offset" /> + <paramref name="count" /> - 1) replaced
    ///     by the bytes read from the current source.
    /// </param>
    /// <param name="offset">
    ///     The zero-based byte offset in <paramref name="buffer" /> at which to begin storing the data read
    ///     from the current stream.
    /// </param>
    /// <param name="count">The maximum number of bytes to be read from the current stream.</param>
    /// <returns>
    ///     The total number of bytes read into the buffer. This can be less than the number of bytes requested if that many
    ///     bytes are not currently available, or zero (0) if the end of the stream has been reached.
    /// </returns>
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (Available == 0) FillBuffer();

        var available = Math.Min(Available, count);
        if (available > 0)
        {
            Buffer.BlockCopy(streamBuffer, bufferPos, buffer, offset, available);
            bufferPos += available;
            Available -= available;
        }

        return available;
    }

    /// <summary>
    ///     When overridden in a derived class, writes a sequence of bytes to the current stream and advances the current
    ///     position within this stream by the number of bytes written.
    /// </summary>
    /// <param name="buffer">An array of bytes. This method copies count bytes from buffer to the current stream.</param>
    /// <param name="offset">The zero-based byte offset in buffer at which to begin copying bytes to the current stream.</param>
    /// <param name="count">The number of bytes to be written to the current stream.</param>
    [DebuggerStepThrough]
    public override void Write(byte[] buffer, int offset, int count)
    {
        OnDataWrite(buffer, offset, count);

        if (closedWrite) return;

        try
        {
            BaseStream.Write(buffer, offset, count);
        }
        catch
        {
            closedWrite = true;
            if (!IsNetworkStream)
                throw;
        }
    }

    /// <summary>
    ///     Asynchronously reads the bytes from the current stream and writes them to another stream, using a specified buffer
    ///     size and cancellation token.
    /// </summary>
    /// <param name="destination">The stream to which the contents of the current stream will be copied.</param>
    /// <param name="bufferSize">
    ///     The size, in bytes, of the buffer. This value must be greater than zero. The default size is
    ///     81920.
    /// </param>
    /// <param name="cancellationToken">
    ///     The token to monitor for cancellation requests. The default value is
    ///     <see cref="P:System.Threading.CancellationToken.None" />.
    /// </param>
    /// <returns>
    ///     A task that represents the asynchronous copy operation.
    /// </returns>
    public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        if (Available > 0)
        {
            await destination.WriteAsync(streamBuffer, bufferPos, Available, cancellationToken);

            Available = 0;
        }

        await base.CopyToAsync(destination, bufferSize, cancellationToken);
    }

    /// <summary>
    ///     Asynchronously clears all buffers for this stream, causes any buffered data to be written to the underlying device,
    ///     and monitors cancellation requests.
    /// </summary>
    /// <param name="cancellationToken">
    ///     The token to monitor for cancellation requests. The default value is
    ///     <see cref="P:System.Threading.CancellationToken.None" />.
    /// </param>
    /// <returns>
    ///     A task that represents the asynchronous flush operation.
    /// </returns>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (closedWrite) return;

        try
        {
            await BaseStream.FlushAsync(cancellationToken);
        }
        catch
        {
            closedWrite = true;
            if (!IsNetworkStream)
                throw;
        }
    }

    /// <summary>
    ///     Asynchronously reads a sequence of bytes from the current stream,
    ///     advances the position within the stream by the number of bytes read,
    ///     and monitors cancellation requests.
    /// </summary>
    /// <param name="buffer">The buffer to write the data into.</param>
    /// <param name="offset">
    ///     The byte offset in <paramref name="buffer" /> at which
    ///     to begin writing data from the stream.
    /// </param>
    /// <param name="count">The maximum number of bytes to read.</param>
    /// <param name="cancellationToken">
    ///     The token to monitor for cancellation requests.
    ///     The default value is <see cref="P:System.Threading.CancellationToken.None" />.
    /// </param>
    /// <returns>
    ///     A task that represents the asynchronous read operation.
    ///     The value of the parameter contains the total
    ///     number of bytes read into the buffer.
    ///     The result value can be less than the number of bytes
    ///     requested if the number of bytes currently available is
    ///     less than the requested number, or it can be 0 (zero)
    ///     if the end of the stream has been reached.
    /// </returns>
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (Available == 0) await FillBufferAsync(cancellationToken);

        var available = Math.Min(Available, count);
        if (available > 0)
        {
            Buffer.BlockCopy(streamBuffer, bufferPos, buffer, offset, available);
            bufferPos += available;
            Available -= available;
        }

        return available;
    }

    /// <summary>
    ///     Asynchronously reads a sequence of bytes from the current stream,
    ///     advances the position within the stream by the number of bytes read,
    ///     and monitors cancellation requests.
    /// </summary>
    /// <param name="buffer">The buffer to write the data into.</param>
    /// <param name="cancellationToken">
    ///     The token to monitor for cancellation requests.
    ///     The default value is <see cref="P:System.Threading.CancellationToken.None" />.
    /// </param>
    /// <returns>
    ///     A task that represents the asynchronous read operation.
    ///     The value of the parameter contains the total
    ///     number of bytes read into the buffer.
    ///     The result value can be less than the number of bytes
    ///     requested if the number of bytes currently available is
    ///     less than the requested number, or it can be 0 (zero)
    ///     if the end of the stream has been reached.
    /// </returns>
#if NET6_0_OR_GREATER
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken =
 default)
#else
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
#endif
    {
        if (Available == 0) await FillBufferAsync(cancellationToken);

        var available = Math.Min(Available, buffer.Length);
        if (available > 0)
        {
            new Span<byte>(streamBuffer, bufferPos, available).CopyTo(buffer.Span);
            bufferPos += available;
            Available -= available;
        }

        return available;
    }

    /// <summary>
    ///     Reads a byte from the stream and advances the position within the stream by one byte, or returns -1 if at the end
    ///     of the stream.
    /// </summary>
    /// <returns>
    ///     The unsigned byte cast to an Int32, or -1 if at the end of the stream.
    /// </returns>
    public override int ReadByte()
    {
        if (Available == 0) FillBuffer();

        if (Available == 0) return -1;

        Available--;
        return streamBuffer[bufferPos++];
    }

    /// <summary>
    ///     Peeks a byte asynchronous.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    public async ValueTask<int> PeekByteAsync(int index, CancellationToken cancellationToken = default)
    {
        // When index is greater than the buffer size
        if (streamBuffer.Length <= index)
            throw new Exception("Requested Peek index exceeds the buffer size. Consider increasing the buffer size.");

        while (Available <= index)
        {
            // When index is greater than the buffer size
            var fillResult = await FillBufferAsync(cancellationToken);
            if (!fillResult) return -1;
        }

        return streamBuffer[bufferPos + index];
    }

    /// <summary>
    ///     Peeks bytes asynchronous.
    /// </summary>
    /// <param name="buffer">The buffer to copy.</param>
    /// <param name="offset">The offset where copying.</param>
    /// <param name="index">The index.</param>
    /// <param name="count">The count.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    public async ValueTask<int> PeekBytesAsync(byte[] buffer, int offset, int index, int count,
        CancellationToken cancellationToken = default)
    {
        // When index is greater than the buffer size
        if (streamBuffer.Length <= index + count)
            throw new Exception(
                "Requested Peek index and size exceeds the buffer size. Consider increasing the buffer size.");

        while (Available <= index)
        {
            var fillResult = await FillBufferAsync(cancellationToken);
            if (!fillResult) return 0;
        }

        if (Available - index < count) count = Available - index;

        Buffer.BlockCopy(streamBuffer, index, buffer, offset, count);
        return count;
    }

    /// <summary>
    ///     Peeks a byte from buffer.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <returns></returns>
    /// <exception cref="Exception">Index is out of buffer size</exception>
    public byte PeekByteFromBuffer(int index)
    {
        if (Available <= index) throw new Exception("Index is out of buffer size");

        return streamBuffer[bufferPos + index];
    }

    /// <summary>
    ///     Reads a byte from buffer.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="Exception">Buffer is empty</exception>
    public byte ReadByteFromBuffer()
    {
        if (Available == 0) throw new Exception("Buffer is empty");

        Available--;
        return streamBuffer[bufferPos++];
    }

    /// <summary>
    ///     Asynchronously writes a sequence of bytes to the current stream, advances the current position within this stream
    ///     by the number of bytes written, and monitors cancellation requests.
    /// </summary>
    /// <param name="buffer">The buffer to write data from.</param>
    /// <param name="offset">The zero-based byte offset in buffer from which to begin copying bytes to the stream.</param>
    /// <param name="count">The maximum number of bytes to write.</param>
    /// <param name="cancellationToken">
    ///     The token to monitor for cancellation requests. The default value is
    ///     <see cref="P:System.Threading.CancellationToken.None"></see>.
    /// </param>
    [DebuggerStepThrough]
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        OnDataWrite(buffer, offset, count);

        if (closedWrite) return;

        try
        {
            await BaseStream.WriteAsync(buffer, offset, count, cancellationToken);
        }
        catch
        {
            closedWrite = true;
            if (!IsNetworkStream)
                throw;
        }
    }

    /// <summary>
    ///     Writes a byte to the current position in the stream and advances the position within the stream by one byte.
    /// </summary>
    /// <param name="value">The byte to write to the stream.</param>
    public override void WriteByte(byte value)
    {
        if (closedWrite) return;

        var buffer = bufferPool.GetBuffer();
        try
        {
            buffer[0] = value;
            OnDataWrite(buffer, 0, 1);
            BaseStream.Write(buffer, 0, 1);
        }
        catch
        {
            closedWrite = true;
            if (!IsNetworkStream)
                throw;
        }
        finally
        {
            bufferPool.ReturnBuffer(buffer);
        }
    }

    protected virtual void OnDataWrite(byte[] buffer, int offset, int count)
    {
        DataWrite?.Invoke(this, new DataEventArgs(buffer, offset, count));
    }

    protected virtual void OnDataRead(byte[] buffer, int offset, int count)
    {
        DataRead?.Invoke(this, new DataEventArgs(buffer, offset, count));
    }

    /// <summary>
    ///     Releases the unmanaged resources used by the <see cref="T:System.IO.Stream" /> and optionally releases the managed
    ///     resources.
    /// </summary>
    /// <param name="disposing">
    ///     true to release both managed and unmanaged resources; false to release only unmanaged
    ///     resources.
    /// </param>
    protected override void Dispose(bool disposing)
    {
        if (!disposed)
        {
            disposed = true;
            IsClosed = true;
            closedWrite = true;

            if (disposing)
            {
                if (!leaveOpen) BaseStream.Dispose();

                bufferPool.ReturnBuffer(streamBuffer);
            }
        }
    }

    /// <summary>
    ///     When overridden in a derived class, gets a value indicating whether the current stream supports reading.
    /// </summary>
    public override bool CanRead => BaseStream.CanRead;

    /// <summary>
    ///     When overridden in a derived class, gets a value indicating whether the current stream supports seeking.
    /// </summary>
    public override bool CanSeek => BaseStream.CanSeek;

    /// <summary>
    ///     When overridden in a derived class, gets a value indicating whether the current stream supports writing.
    /// </summary>
    public override bool CanWrite => BaseStream.CanWrite;

    /// <summary>
    ///     Gets a value that determines whether the current stream can time out.
    /// </summary>
    public override bool CanTimeout => BaseStream.CanTimeout;

    /// <summary>
    ///     When overridden in a derived class, gets the length in bytes of the stream.
    /// </summary>
    public override long Length => BaseStream.Length;

    /// <summary>
    ///     Gets a value indicating whether data is available.
    /// </summary>
    public bool DataAvailable => Available > 0;

    /// <summary>
    ///     Gets the available data size.
    /// </summary>
    public int Available { get; private set; }

    /// <summary>
    ///     When overridden in a derived class, gets or sets the position within the current stream.
    /// </summary>
    public override long Position
    {
        get => BaseStream.Position;
        set => BaseStream.Position = value;
    }

    /// <summary>
    ///     Gets or sets a value, in miliseconds, that determines how long the stream will attempt to read before timing out.
    /// </summary>
    public override int ReadTimeout
    {
        get => BaseStream.ReadTimeout;
        set => BaseStream.ReadTimeout = value;
    }

    /// <summary>
    ///     Gets or sets a value, in miliseconds, that determines how long the stream will attempt to write before timing out.
    /// </summary>
    public override int WriteTimeout
    {
        get => BaseStream.WriteTimeout;
        set => BaseStream.WriteTimeout = value;
    }

    /// <summary>
    ///     Fills the buffer.
    /// </summary>
    public bool FillBuffer()
    {
        if (IsClosed) throw new Exception("Stream is already closed");

        if (Available > 0)
            // normally we fill the buffer only when it is empty, but sometimes we need more data
            // move the remaining data to the beginning of the buffer 
            Buffer.BlockCopy(streamBuffer, bufferPos, streamBuffer, 0, Available);

        bufferPos = 0;

        var result = false;
        try
        {
            var readBytes = BaseStream.Read(streamBuffer, Available, streamBuffer.Length - Available);
            result = readBytes > 0;
            if (result)
            {
                OnDataRead(streamBuffer, Available, readBytes);
                Available += readBytes;
            }
        }
        catch
        {
            if (!IsNetworkStream)
                throw;
        }
        finally
        {
            if (!result)
            {
                IsClosed = true;
                closedWrite = true;
            }
        }

        return result;
    }

    /// <summary>
    ///     Fills the buffer asynchronous.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    public async ValueTask<bool> FillBufferAsync(CancellationToken cancellationToken = default)
    {
        if (IsClosed) throw new Exception("Stream is already closed");

        var bytesToRead = streamBuffer.Length - Available;
        if (bytesToRead == 0) return false;

        if (Available > 0)
            // normally we fill the buffer only when it is empty, but sometimes we need more data
            // move the remaining data to the beginning of the buffer 
            Buffer.BlockCopy(streamBuffer, bufferPos, streamBuffer, 0, Available);

        bufferPos = 0;

        var result = false;
        try
        {
            var readTask = BaseStream.ReadAsync(streamBuffer, Available, bytesToRead, cancellationToken);
            if (IsNetworkStream) readTask = readTask.WithCancellation(cancellationToken);

            var readBytes = await readTask;
            result = readBytes > 0;
            if (result)
            {
                OnDataRead(streamBuffer, Available, readBytes);
                Available += readBytes;
            }
        }
        catch
        {
            if (!IsNetworkStream)
                throw;
        }
        finally
        {
            if (!result)
            {
                IsClosed = true;
                closedWrite = true;
            }
        }

        return result;
    }

    /// <summary>
    ///     Read a line from the byte stream
    /// </summary>
    /// <returns></returns>
    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        return ReadLineInternalAsync(this, bufferPool, cancellationToken);
    }

    /// <summary>
    ///     Read a line from the byte stream
    /// </summary>
    /// <returns></returns>
    internal static async ValueTask<string?> ReadLineInternalAsync(ILineStream reader, IBufferPool bufferPool,
        CancellationToken cancellationToken = default)
    {
        byte lastChar = default;

        var bufferDataLength = 0;

        // try to use buffer from the buffer pool, usually it is enough
        var bufferPoolBuffer = bufferPool.GetBuffer();
        var buffer = bufferPoolBuffer;

        try
        {
            while (reader.DataAvailable || await reader.FillBufferAsync(cancellationToken))
            {
                var newChar = reader.ReadByteFromBuffer();
                buffer[bufferDataLength] = newChar;

                // if new line
                if (newChar == '\n')
                {
                    if (lastChar == '\r') return Encoding.GetString(buffer, 0, bufferDataLength - 1);

                    return Encoding.GetString(buffer, 0, bufferDataLength);
                }

                bufferDataLength++;

                // store last char for new line comparison
                lastChar = newChar;

                if (bufferDataLength == buffer.Length) Array.Resize(ref buffer, bufferDataLength * 2);
            }

            // reached end of stream without a trailing '\n'.
            // build the result string here, while the pooled buffer is still valid,
            // before it is returned in the finally block below.
            if (bufferDataLength == 0) return null;

            return Encoding.GetString(buffer, 0, bufferDataLength);
        }
        finally
        {
            bufferPool.ReturnBuffer(bufferPoolBuffer);
        }
    }

    /// <summary>
    ///     Base Stream.BeginRead will call this.Read and block thread (we don't want this, Network stream handles async)
    ///     In order to really async Reading Launch this.ReadAsync as Task will fire NetworkStream.ReadAsync
    ///     See Threads here :
    ///     https://github.com/justcoding121/Stream-Extended/pull/43
    ///     https://github.com/justcoding121/Titanium-Web-Proxy/issues/575
    /// </summary>
    /// <returns></returns>
    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        if (!networkStreamHack) return base.BeginRead(buffer, offset, count, callback, state);

        var vAsyncResult = ReadAsync(buffer, offset, count, cancellationToken);
        if (IsNetworkStream) vAsyncResult = vAsyncResult.WithCancellation(cancellationToken);

        vAsyncResult.ContinueWith(pAsyncResult =>
        {
            // use TaskExtended to pass State as AsyncObject
            // callback will call EndRead (otherwise, it will block)
            callback?.Invoke(new TaskResult<int>(pAsyncResult, state));
        }, cancellationToken);

        return vAsyncResult;
    }

    /// <summary>
    ///     override EndRead to handle async Reading (see BeginRead comment)
    /// </summary>
    /// <returns></returns>
    public override int EndRead(IAsyncResult asyncResult)
    {
        if (!networkStreamHack) return base.EndRead(asyncResult);

        return ((TaskResult<int>)asyncResult).Result;
    }

    /// <summary>
    ///     Fix the .net bug with SslStream slow WriteAsync
    ///     https://github.com/justcoding121/Titanium-Web-Proxy/issues/495
    ///     Stream.BeginWrite + Stream.BeginRead uses the same SemaphoreSlim(1)
    ///     That's why we need to call NetworkStream.BeginWrite only (while read is waiting SemaphoreSlim)
    /// </summary>
    /// <returns></returns>
    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        if (!networkStreamHack) return base.BeginWrite(buffer, offset, count, callback, state);

        var vAsyncResult = WriteAsync(buffer, offset, count, cancellationToken);

        vAsyncResult.ContinueWith(pAsyncResult => { callback?.Invoke(new TaskResult(pAsyncResult, state)); },
            cancellationToken);

        return vAsyncResult;
    }

    public override void EndWrite(IAsyncResult asyncResult)
    {
        if (!networkStreamHack)
        {
            base.EndWrite(asyncResult);
            return;
        }

        ((TaskResult)asyncResult).GetResult();
    }

    /// <summary>
    ///     Writes a line async
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
    /// <returns></returns>
    public ValueTask WriteLineAsync(CancellationToken cancellationToken = default)
    {
        return WriteAsync(newLine, cancellationToken: cancellationToken);
    }

    private async ValueTask WriteAsyncInternal(string value, bool addNewLine, CancellationToken cancellationToken)
    {
        if (closedWrite) return;

        var newLineChars = addNewLine ? newLine.Length : 0;
        var charCount = value.Length;
        if (charCount < bufferPool.BufferSize - newLineChars)
        {
            var buffer = bufferPool.GetBuffer();
            try
            {
                var idx = Encoding.GetBytes(value, 0, charCount, buffer, 0);
                if (newLineChars > 0)
                {
                    Buffer.BlockCopy(newLine, 0, buffer, idx, newLineChars);
                    idx += newLineChars;
                }

                await BaseStream.WriteAsync(buffer, 0, idx, cancellationToken);
            }
            catch
            {
                closedWrite = true;
                if (!IsNetworkStream)
                    throw;
            }
            finally
            {
                bufferPool.ReturnBuffer(buffer);
            }
        }
        else
        {
            var buffer = new byte[charCount + newLineChars + 1];
            var idx = Encoding.GetBytes(value, 0, charCount, buffer, 0);
            if (newLineChars > 0)
            {
                Buffer.BlockCopy(newLine, 0, buffer, idx, newLineChars);
                idx += newLineChars;
            }

            try
            {
                await BaseStream.WriteAsync(buffer, 0, idx, cancellationToken);
            }
            catch
            {
                closedWrite = true;
                if (!IsNetworkStream)
                    throw;
            }
        }
    }

    public ValueTask WriteLineAsync(string value, CancellationToken cancellationToken = default)
    {
        return WriteAsyncInternal(value, true, cancellationToken);
    }

    /// <summary>
    ///     Write the headers to client
    /// </summary>
    /// <param name="headerBuilder"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    internal async Task WriteHeadersAsync(HeaderBuilder headerBuilder, CancellationToken cancellationToken = default)
    {
        var buffer = headerBuilder.GetBuffer();
        var array = buffer.Array ??
                    throw new InvalidOperationException("The header buffer has no backing array.");

        try
        {
            await WriteAsync(array, buffer.Offset, buffer.Count, true, cancellationToken);
        }
        catch (IOException e)
        {
            //throw this as ServerConnectionException so that RetryPolicy can retry with a new server connection.
            if (this is HttpServerStream)
                throw new RetryableServerConnectionException(
                    "Server connection was closed. Exception while sending request line and headers.", e);

            throw;
        }
    }

    /// <summary>
    ///     Writes the data to the stream.
    /// </summary>
    /// <param name="data">The data.</param>
    /// <param name="flush">Should we flush after write?</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    internal async ValueTask WriteAsync(byte[] data, bool flush = false, CancellationToken cancellationToken = default)
    {
        if (closedWrite) return;

        try
        {
            await BaseStream.WriteAsync(data, 0, data.Length, cancellationToken);
            if (flush) await BaseStream.FlushAsync(cancellationToken);
        }
        catch
        {
            closedWrite = true;
            if (!IsNetworkStream)
                throw;
        }
    }

    internal async Task WriteAsync(byte[] data, int offset, int count, bool flush,
        CancellationToken cancellationToken = default)
    {
        if (closedWrite) return;

        try
        {
            await BaseStream.WriteAsync(data, offset, count, cancellationToken);
            if (flush) await BaseStream.FlushAsync(cancellationToken);
        }
        catch
        {
            closedWrite = true;
            if (!IsNetworkStream)
                throw;
        }
    }

    /// <summary>
    ///     Writes the byte array body to the stream; optionally chunked
    /// </summary>
    /// <param name="data"></param>
    /// <param name="isChunked"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    internal ValueTask WriteBodyAsync(byte[] data, bool isChunked, CancellationToken cancellationToken)
    {
        if (isChunked) return WriteBodyChunkedAsync(data, cancellationToken);

        return WriteAsync(data, cancellationToken: cancellationToken);
    }

    public async Task CopyBodyAsync(RequestResponseBase requestResponse, bool useOriginalHeaderValues,
        IHttpStreamWriter writer, TransformationMode transformation, bool isRequest, SessionEventArgs args,
        CancellationToken cancellationToken)
    {
        var isChunked = useOriginalHeaderValues ? requestResponse.OriginalIsChunked : requestResponse.IsChunked;
        var contentLength = useOriginalHeaderValues
            ? requestResponse.OriginalContentLength
            : requestResponse.ContentLength;

        if (transformation == TransformationMode.None)
        {
            await CopyBodyAsync(writer, isChunked, contentLength, isRequest, args, cancellationToken);
            return;
        }

        LimitedStream limitedStream;
        Stream? decompressStream = null;

        var contentEncoding = useOriginalHeaderValues
            ? requestResponse.OriginalContentEncoding
            : requestResponse.ContentEncoding;

        Stream s = limitedStream = new LimitedStream(this, bufferPool, isChunked, contentLength);

        if (transformation == TransformationMode.Uncompress && contentEncoding != null)
            s = decompressStream =
                DecompressionFactory.Create(CompressionUtil.CompressionNameToEnum(contentEncoding), s);

        // leaveOpen: true so disposing the wrapper returns its pooled buffer without
        // disposing the underlying limited/decompression stream (handled in finally).
        var http = new HttpStream(server, s, bufferPool, cancellationToken, true);
        try
        {
            await http.CopyBodyAsync(writer, false, -1, isRequest, args, cancellationToken);
        }
        finally
        {
            http.Dispose();

            decompressStream?.Dispose();

            await limitedStream.Finish();
            limitedStream.Dispose();
        }
    }

    /// <summary>
    ///     Copies the specified content length number of bytes to the output stream from the given inputs stream
    ///     optionally chunked
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="isChunked"></param>
    /// <param name="contentLength"></param>
    /// <param name="onCopy"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task CopyBodyAsync(IHttpStreamWriter writer, bool isChunked, long contentLength,
        bool isRequest,
        SessionEventArgs args, CancellationToken cancellationToken)
    {
        var isResponse = !isRequest;

        if (IsNetworkStream && writer.IsNetworkStream &&
            ((isRequest && args.HttpClient.Request.OriginalHasBody && !args.HttpClient.Request.IsBodyRead && server.ShouldCallBeforeRequestBodyWrite()) ||
             (isResponse && args.HttpClient.Response.OriginalHasBody && !args.HttpClient.Response.IsBodyRead && server.ShouldCallBeforeResponseBodyWrite())))
        {
            return HandleBodyWrite(writer, isChunked, contentLength, isRequest, args, cancellationToken);
        }

        // For chunked request we need to read data as they arrive, until we reach a chunk end symbol
        if (isChunked) return CopyBodyChunkedAsync(writer, isRequest, args, cancellationToken);

        // http 1.0 or the stream reader limits the stream
        if (contentLength == -1) contentLength = long.MaxValue;

        // If not chunked then its easy just read the amount of bytes mentioned in content length header
        return CopyBytesToStream(writer, contentLength, isRequest, args, cancellationToken);
    }

    /// <summary>
    ///     Streams the body from this source stream to the target writer, invoking the
    ///     OnRequestBodyWrite / OnResponseBodyWrite handler for each buffer-sized piece so consumers
    ///     can inspect or modify the body chunk-by-chunk without buffering the whole body.
    ///     The bytes are exposed exactly as they arrive on the wire (still content-encoded if the message
    ///     uses Content-Encoding); on-the-fly decompression/recompression is not performed here in order to
    ///     preserve exact framing and length. Reads are bounded by bufferPool.BufferSize to keep memory flat.
    /// </summary>
    private async Task HandleBodyWrite(IHttpStreamWriter writer, bool isChunked, long contentLength,
        bool isRequest, SessionEventArgs args, CancellationToken cancellationToken)
    {
        var originalContentLength = isRequest
            ? args.HttpClient.Request.OriginalContentLength
            : args.HttpClient.Response.OriginalContentLength;
        var originalIsChunked =
            isRequest ? args.HttpClient.Request.OriginalIsChunked : args.HttpClient.Response.OriginalIsChunked;

        async ValueTask writeFramed(byte[] data)
        {
            if (data.Length == 0) return;

            if (isChunked)
            {
                await writer.WriteLineAsync(data.Length.ToString("x"), cancellationToken);
                await writer.WriteAsync(data, 0, data.Length, cancellationToken);
                await writer.WriteLineAsync(cancellationToken);
            }
            else
            {
                await writer.WriteAsync(data, 0, data.Length, cancellationToken);
            }
        }

        async ValueTask writeTerminator()
        {
            if (isChunked)
            {
                await writer.WriteLineAsync("0", cancellationToken);
                await writer.WriteLineAsync(cancellationToken);
            }
        }

        // returns true when writing should stop (either source end reached or handler requested it)
        async Task<bool> emit(byte[] piece, bool isLastPiece)
        {
            var eventArgs = new BeforeBodyWriteEventArgs(args, piece, isChunked, isLastPiece);

            if (isRequest)
                await server.OnBeforeRequestBodyWrite(eventArgs);
            else
                await server.OnBeforeResponseBodyWrite(eventArgs);

            if (eventArgs.BodyBytes is { Length: > 0 }) await writeFramed(eventArgs.BodyBytes);

            return isLastPiece || eventArgs.IsLastChunk;
        }

        var buffer = bufferPool.GetBuffer();

        try
        {
            if (originalIsChunked)
            {
                while (true)
                {
                    var chunkHead = await ReadLineAsync(cancellationToken);
                    if (chunkHead == null) break;

                    var idx = chunkHead.IndexOf(";", StringComparison.Ordinal);
                    if (idx >= 0) chunkHead = chunkHead.Substring(0, idx);

                    if (!int.TryParse(chunkHead, NumberStyles.HexNumber, null, out var chunkSize))
                        throw new ProxyHttpException($"Invalid chunk length: '{chunkHead}'", null, null);

                    if (chunkSize == 0)
                    {
                        // trailer line of the terminating chunk
                        await ReadLineAsync(cancellationToken);
                        await emit(Array.Empty<byte>(), true);
                        break;
                    }

                    var remaining = chunkSize;
                    var stop = false;
                    while (remaining > 0)
                    {
                        var toRead = Math.Min(buffer.Length, remaining);
                        var bytesRead = await ReadAsync(buffer, 0, toRead, cancellationToken);
                        if (bytesRead == 0)
                            throw new ProxyHttpException("Unexpected end of stream while reading chunk body.", null, args);

                        remaining -= bytesRead;

                        if (isRequest) args.OnDataSent(buffer, 0, bytesRead);
                        else args.OnDataReceived(buffer, 0, bytesRead);

                        var piece = new byte[bytesRead];
                        Buffer.BlockCopy(buffer, 0, piece, 0, bytesRead);

                        if (await emit(piece, false))
                        {
                            stop = true;
                            break;
                        }
                    }

                    if (stop) break;

                    // trailing CRLF after chunk data
                    await ReadLineAsync(cancellationToken);
                }

                await writeTerminator();
            }
            else
            {
                var remaining = originalContentLength == -1 ? long.MaxValue : originalContentLength;

                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    var bytesRead = await ReadAsync(buffer, 0, toRead, cancellationToken);
                    if (bytesRead == 0) break;

                    remaining -= bytesRead;

                    if (isRequest) args.OnDataSent(buffer, 0, bytesRead);
                    else args.OnDataReceived(buffer, 0, bytesRead);

                    var piece = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, piece, 0, bytesRead);

                    if (await emit(piece, remaining == 0)) break;
                }

                await writeTerminator();
            }
        }
        finally
        {
            bufferPool.ReturnBuffer(buffer);
        }
    }

    /// <summary>
    ///     Copies the given input bytes to output stream chunked
    /// </summary>
    /// <param name="data"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async ValueTask WriteBodyChunkedAsync(byte[] data, CancellationToken cancellationToken)
    {
        var chunkHead = Encoding.ASCII.GetBytes(data.Length.ToString("x2"));

        await WriteAsync(chunkHead, cancellationToken: cancellationToken);
        await WriteLineAsync(cancellationToken);
        await WriteAsync(data, cancellationToken: cancellationToken);
        await WriteLineAsync(cancellationToken);

        await WriteLineAsync("0", cancellationToken);
        await WriteLineAsync(cancellationToken);
    }

    /// <summary>
    ///     Copies the streams chunked
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="onCopy"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task CopyBodyChunkedAsync(IHttpStreamWriter writer, bool isRequest, SessionEventArgs args,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var chunkHead = await ReadLineAsync(cancellationToken);
            if (chunkHead == null) return;

            var idx = chunkHead.IndexOf(";", StringComparison.Ordinal);
            if (idx >= 0) chunkHead = chunkHead.Substring(0, idx);

            if (!int.TryParse(chunkHead, NumberStyles.HexNumber, null, out var chunkSize))
                throw new ProxyHttpException($"Invalid chunk length: '{chunkHead}'", null, null);

            await writer.WriteLineAsync(chunkHead, cancellationToken);

            if (chunkSize != 0) await CopyBytesToStream(writer, chunkSize, isRequest, args, cancellationToken);

            await writer.WriteLineAsync(cancellationToken);

            // chunk trail
            await ReadLineAsync(cancellationToken);

            if (chunkSize == 0) break;
        }
    }

    /// <summary>
    ///     Copies the specified bytes to the stream from the input stream
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="count"></param>
    /// <param name="onCopy"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task CopyBytesToStream(IHttpStreamWriter writer, long count, bool isRequest, SessionEventArgs args,
        CancellationToken cancellationToken)
    {
        var buffer = bufferPool.GetBuffer();

        try
        {
            var remainingBytes = count;

            while (remainingBytes > 0)
            {
                var bytesToRead = buffer.Length;
                if (remainingBytes < bytesToRead) bytesToRead = (int)remainingBytes;

                var bytesRead = await ReadAsync(buffer, 0, bytesToRead, cancellationToken);
                if (bytesRead == 0) break;

                remainingBytes -= bytesRead;

                await writer.WriteAsync(buffer, 0, bytesRead, cancellationToken);

                if (isRequest)
                    args.OnDataSent(buffer, 0, bytesRead);
                else
                    args.OnDataReceived(buffer, 0, bytesRead);
            }
        }
        finally
        {
            bufferPool.ReturnBuffer(buffer);
        }
    }

    /// <summary>
    ///     Writes the request/response headers and body.
    /// </summary>
    /// <param name="requestResponse"></param>
    /// <param name="headerBuilder"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected async ValueTask WriteAsync(RequestResponseBase requestResponse, HeaderBuilder headerBuilder,
        CancellationToken cancellationToken = default)
    {
        var body = requestResponse.CompressBodyAndUpdateContentLength();
        headerBuilder.WriteHeaders(requestResponse.Headers);
        await WriteHeadersAsync(headerBuilder, cancellationToken);

        if (body != null)
        {
            await WriteBodyAsync(body, requestResponse.IsChunked, cancellationToken);
            requestResponse.IsBodySent = true;
        }
    }

#if NET6_0_OR_GREATER
    /// <summary>
    ///     Asynchronously writes a sequence of bytes to the current stream, advances the current position within this stream by the number of bytes written, and monitors cancellation requests.
    /// </summary>
    /// <param name="buffer">The buffer to write data from.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see cref="P:System.Threading.CancellationToken.None" />.</param>
    /// <returns>A task that represents the asynchronous write operation.</returns>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken =
 default)
        {
            if (closedWrite)
            {
                return;
            }

            try
            {
                await BaseStream.WriteAsync(buffer, cancellationToken);
            }
            catch
            {
                closedWrite = true;
                if (!IsNetworkStream)
                    throw;
            }
        }
#else
    /// <summary>
    ///     Asynchronously writes a sequence of bytes to the current stream, advances the current position within this stream
    ///     by the number of bytes written, and monitors cancellation requests.
    /// </summary>
    /// <param name="buffer">The buffer to write data from.</param>
    /// <param name="cancellationToken">
    ///     The token to monitor for cancellation requests. The default value is
    ///     <see cref="P:System.Threading.CancellationToken.None" />.
    /// </param>
    /// <returns>A task that represents the asynchronous write operation.</returns>
    public async Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        var buf = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.CopyTo(buf);
            await BaseStream.WriteAsync(buf, 0, buffer.Length, cancellationToken);
        }
        catch
        {
            if (!IsNetworkStream)
                throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
#endif
}ParseOptions.0.json‚
`D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\Streams\ILineStream.csËusing System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.StreamExtended.Network;

public interface ILineStream
{
    bool DataAvailable { get; }

    /// <summary>
    ///     Fills the buffer asynchronous.
    /// </summary>
    /// <returns></returns>
    ValueTask<bool> FillBufferAsync(CancellationToken cancellationToken = default);

    byte ReadByteFromBuffer();

    /// <summary>
    ///     Read a line from the byte stream
    /// </summary>
    /// <returns></returns>
    ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default);
}ParseOptions.0.jsonè
`D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\Streams\IPeekStream.csï
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.StreamExtended.Network;

public interface IPeekStream
{
    /// <summary>
    ///     Peeks a byte from buffer.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <returns></returns>
    /// <exception cref="Exception">Index is out of buffer size</exception>
    byte PeekByteFromBuffer(int index);

    /// <summary>
    ///     Peeks a byte asynchronous.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    ValueTask<int> PeekByteAsync(int index, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Peeks bytes asynchronous.
    /// </summary>
    /// <param name="buffer">The buffer to copy.</param>
    /// <param name="offset">The offset where copying.</param>
    /// <param name="index">The index.</param>
    /// <param name="count">The count.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    ValueTask<int> PeekBytesAsync(byte[] buffer, int offset, int index, int count,
        CancellationToken cancellationToken = default);
}ParseOptions.0.jsonƒ-
bD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\Streams\LimitedStream.cs»,using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.StreamExtended.BufferPool;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.EventArguments;

internal class LimitedStream : Stream
{
    private readonly IHttpStreamReader baseReader;
    private readonly IBufferPool bufferPool;
    private readonly bool isChunked;
    private long bytesRemaining;

    private bool readChunkTrail;

    internal LimitedStream(IHttpStreamReader baseStream, IBufferPool bufferPool, bool isChunked,
        long contentLength)
    {
        baseReader = baseStream;
        this.bufferPool = bufferPool;
        this.isChunked = isChunked;
        bytesRemaining = isChunked
            ? 0
            : contentLength == -1
                ? long.MaxValue
                : contentLength;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    private void GetNextChunk()
    {
        if (readChunkTrail)
        {
            // read the chunk trail of the previous chunk
            var s = baseReader.ReadLineAsync().Result;
            if (s == null)
            {
                bytesRemaining = -1;
                return;
            }
        }

        readChunkTrail = true;

        var chunkHead = baseReader.ReadLineAsync().Result;
        if (chunkHead == null)
        {
            bytesRemaining = -1;
            return;
        }

        var idx = chunkHead.IndexOf(";", StringComparison.Ordinal);
        if (idx >= 0) chunkHead = chunkHead.Substring(0, idx);

        if (!int.TryParse(chunkHead, NumberStyles.HexNumber, null, out var chunkSize))
            throw new ProxyHttpException($"Invalid chunk length: '{chunkHead}'", null, null);

        bytesRemaining = chunkSize;

        if (chunkSize == 0)
        {
            bytesRemaining = -1;

            // chunk trail
            var task = baseReader.ReadLineAsync();
            if (!task.IsCompleted)
                task.AsTask().Wait();
        }
    }

    private async Task GetNextChunkAsync()
    {
        if (readChunkTrail)
        {
            // read the chunk trail of the previous chunk
            var s = await baseReader.ReadLineAsync();
            if (s == null)
            {
                bytesRemaining = -1;
                return;
            }
        }

        readChunkTrail = true;

        var chunkHead = await baseReader.ReadLineAsync();
        if (chunkHead == null)
        {
            bytesRemaining = -1;
            return;
        }

        var idx = chunkHead.IndexOf(";", StringComparison.Ordinal);
        if (idx >= 0) chunkHead = chunkHead.Substring(0, idx);

        if (!int.TryParse(chunkHead, NumberStyles.HexNumber, null, out var chunkSize))
            throw new ProxyHttpException($"Invalid chunk length: '{chunkHead}'", null, null);

        bytesRemaining = chunkSize;

        if (chunkSize == 0)
        {
            bytesRemaining = -1;

            // chunk trail
            await baseReader.ReadLineAsync();
        }
    }

    public override void Flush()
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (bytesRemaining == -1) return 0;

        if (bytesRemaining == 0)
        {
            if (isChunked)
                GetNextChunk();
            else
                bytesRemaining = -1;
        }

        if (bytesRemaining == -1) return 0;

        var toRead = (int)Math.Min(count, bytesRemaining);
        var res = baseReader.Read(buffer, offset, toRead);
        bytesRemaining -= res;

        if (res == 0) bytesRemaining = -1;

        return res;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (bytesRemaining == -1) return 0;

        if (bytesRemaining == 0)
        {
            if (isChunked)
                await GetNextChunkAsync();
            else
                bytesRemaining = -1;
        }

        if (bytesRemaining == -1) return 0;

        var toRead = (int)Math.Min(count, bytesRemaining);
        var res = await baseReader.ReadAsync(buffer, offset, toRead, cancellationToken);
        bytesRemaining -= res;

        if (res == 0) bytesRemaining = -1;

        return res;
    }

    public async Task Finish()
    {
        if (bytesRemaining != -1)
        {
            var buffer = bufferPool.GetBuffer();
            try
            {
                var res = await ReadAsync(buffer, 0, buffer.Length);
                if (res != 0) throw new Exception("Data received after stream end");
            }
            finally
            {
                bufferPool.ReturnBuffer(buffer);
            }
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}ParseOptions.0.jsonÁ
nD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\TcpConnection\TcpClientConnection.csﬂusing System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Network.Tcp;

/// <summary>
///     An object that holds TcpConnection to a particular server and port
/// </summary>
internal class TcpClientConnection : IDisposable
{
    private readonly Socket tcpClientSocket;

    private bool disposed;

    private int? processId;

    internal TcpClientConnection(ProxyServer proxyServer, Socket tcpClientSocket)
    {
        this.tcpClientSocket = tcpClientSocket;
        ProxyServer = proxyServer;
        ProxyServer.UpdateClientConnectionCount(true);
    }

    public object? ClientUserData { get; set; }

    private ProxyServer ProxyServer { get; }

    public Guid Id { get; } = Guid.NewGuid();

    public EndPoint LocalEndPoint => tcpClientSocket.LocalEndPoint
                                     ?? throw new InvalidOperationException("Client socket has no local endpoint.");

    public EndPoint RemoteEndPoint => tcpClientSocket.RemoteEndPoint
                                      ?? throw new InvalidOperationException("Client socket has no remote endpoint.");

    internal SslProtocols SslProtocol { get; set; }

    internal SslApplicationProtocol NegotiatedApplicationProtocol { get; set; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public Stream GetStream()
    {
        return new NetworkStream(tcpClientSocket, true);
    }

    public int GetProcessId(ProxyEndPoint endPoint)
    {
        if (processId.HasValue) return processId.Value;

        if (RunTime.IsWindows)
        {
            var remoteEndPoint = (IPEndPoint)RemoteEndPoint;

            // If client is localhost get the process id
            if (NetworkHelper.IsLocalIpAddress(remoteEndPoint.Address))
                processId = TcpHelper.GetProcessIdByLocalPort(endPoint.IpAddress.AddressFamily, remoteEndPoint.Port);
            else
                // can't access process Id of remote request from remote machine
                processId = -1;

            return processId.Value;
        }

        throw new PlatformNotSupportedException();
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposed) return;

        Task.Run(async () =>
        {
            // delay calling tcp connection close()
            // so that client have enough time to call close first.
            // This way we can push tcp Time_Wait to client side when possible.
            await Task.Delay(1000);
            ProxyServer.UpdateClientConnectionCount(false);

            if (disposing)
                try
                {
                    tcpClientSocket.Close();
                }
                catch
                {
                    // ignore
                }
        });

        disposed = true;
    }

    ~TcpClientConnection()
    {
#if DEBUG
            // Finalizer should not be called
            System.Diagnostics.Debugger.Break();
#endif

        Dispose(false);
    }
}ParseOptions.0.json·Ö
oD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\TcpConnection\TcpConnectionFactory.cs◊Ñusing System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.ProxySocket;

namespace Titanium.Web.Proxy.Network.Tcp;

/// <summary>
///     A class that manages Tcp Connection to server used by this proxy server.
/// </summary>
internal class TcpConnectionFactory : IDisposable
{
    private const int MaximumUpstreamProxyAuthenticationAttempts = 5;

    private static readonly string[] UpstreamProxyAuthenticationSchemes = { "Negotiate", "NTLM", "Kerberos" };

    // Tcp server connection pool cache
    private readonly ConcurrentDictionary<string, ConcurrentQueue<TcpServerConnection>> cache = new();

    // Tcp connections waiting to be disposed by cleanup task
    private readonly ConcurrentBag<TcpServerConnection> disposalBag = new();

    // cache object race operations lock
    private readonly SemaphoreSlim @lock = new(1);

    private bool disposed;

    private volatile bool runCleanUpTask = true;

    internal TcpConnectionFactory(ProxyServer server)
    {
        Server = server ?? throw new ArgumentNullException(nameof(server));
        Task.Run(async () => await ClearOutdatedConnections());
    }

    internal ProxyServer Server { get; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    internal string GetConnectionCacheKey(string remoteHostName, int remotePort,
        bool isHttps, List<SslApplicationProtocol>? applicationProtocols,
        IPEndPoint? upStreamEndPoint, IExternalProxy? externalProxy,
        string? connectHost = null, int? connectPort = null)
    {
        // http version is ignored since its an application level decision b/w HTTP 1.0/1.1
        // also when doing connect request MS Edge browser sends http 1.0 but uses 1.1 after server sends 1.1 its response.
        // That can create cache miss for same server connection unnecessarily especially when prefetching with Connect.
        // http version 2 is separated using applicationProtocols below.
        var cacheKeyBuilder = new StringBuilder();
        cacheKeyBuilder.Append(remoteHostName);
        cacheKeyBuilder.Append("-");
        cacheKeyBuilder.Append(remotePort);
        cacheKeyBuilder.Append("-");

        // a fixed forward target changes the actual connection destination while keeping
        // remoteHostName for TLS/identity, so it must be part of the cache key.
        if (!string.IsNullOrEmpty(connectHost))
        {
            cacheKeyBuilder.Append(connectHost);
            cacheKeyBuilder.Append("-");
            cacheKeyBuilder.Append(connectPort ?? remotePort);
            cacheKeyBuilder.Append("-");
        }

        // when creating Tcp client isConnect won't matter
        cacheKeyBuilder.Append(isHttps);

        if (applicationProtocols != null)
            foreach (var protocol in applicationProtocols.OrderBy(x => x))
            {
                cacheKeyBuilder.Append("-");
                cacheKeyBuilder.Append(protocol);
            }

        if (upStreamEndPoint != null)
        {
            cacheKeyBuilder.Append("-");
            cacheKeyBuilder.Append(upStreamEndPoint.Address);
            cacheKeyBuilder.Append("-");
            cacheKeyBuilder.Append(upStreamEndPoint.Port);
        }

        if (externalProxy != null)
        {
            cacheKeyBuilder.Append("-");
            cacheKeyBuilder.Append(externalProxy.HostName);
            cacheKeyBuilder.Append("-");
            cacheKeyBuilder.Append(externalProxy.Port);
            cacheKeyBuilder.Append("-");
            cacheKeyBuilder.Append(externalProxy.ProxyType);

            // SOCKS remote-DNS toggle changes how the connection is established, so it must
            // separate otherwise-identical connections.
            cacheKeyBuilder.Append("-");
            cacheKeyBuilder.Append(externalProxy.ProxyDnsRequests);

            // Different credentials (or default-credential mode) must never share a pooled
            // connection to the same proxy. Include a fingerprint of the credentials, regardless
            // of UseDefaultCredentials, without storing the plaintext password in the key.
            cacheKeyBuilder.Append("-");
            cacheKeyBuilder.Append(externalProxy.UseDefaultCredentials);
            cacheKeyBuilder.Append("-");
            cacheKeyBuilder.Append(GetCredentialFingerprint(externalProxy.UserName, externalProxy.Password));
        }

        return cacheKeyBuilder.ToString();
    }

    /// <summary>
    ///     Produces a short, stable fingerprint of proxy credentials so that connections with
    ///     different credentials do not collide in the pool, without keeping the plaintext
    ///     password inside the long-lived cache key string.
    /// </summary>
    internal static string GetCredentialFingerprint(string? userName, string? password)
    {
        if (string.IsNullOrEmpty(userName) && string.IsNullOrEmpty(password)) return string.Empty;

        // NUL separator cannot appear in the individual parts, avoiding ambiguity between
        // e.g. ("ab", "c") and ("a", "bc").
        var material = (userName ?? string.Empty) + "\0" + (password ?? string.Empty);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    ///     Resolves the upstream proxy actually used for a destination, applying the same
    ///     bypass rules as connection creation (proxy == destination, or BypassLocalhost for
    ///     local addresses). Returns null when the connection is made directly.
    ///     Keeping this in sync with connection creation ensures the cache key reflects the real route.
    /// </summary>
    internal static IExternalProxy? GetEffectiveUpstreamProxy(IExternalProxy? externalProxy, string remoteHostName,
        int remotePort)
    {
        if (externalProxy == null) return null;

        if (externalProxy.HostName == remoteHostName && externalProxy.Port == remotePort) return null;

        if (externalProxy.BypassLocalhost &&
            NetworkHelper.IsLocalIpAddress(remoteHostName, externalProxy.ProxyDnsRequests))
            return null;

        return externalProxy;
    }

    /// <summary>
    ///     Checks that a pooled connection's negotiated ALPN protocol is acceptable for a request
    ///     that asked for the given protocols. Prevents e.g. reusing an HTTP/1.1-negotiated connection
    ///     (that was created while requesting HTTP/2) for a request that requires HTTP/2.
    /// </summary>
    private static bool IsNegotiatedProtocolCompatible(TcpServerConnection connection,
        List<SslApplicationProtocol>? requestedProtocols)
    {
        return IsNegotiatedProtocolCompatible(connection.NegotiatedApplicationProtocol, requestedProtocols);
    }

    internal static bool IsNegotiatedProtocolCompatible(SslApplicationProtocol negotiated,
        List<SslApplicationProtocol>? requestedProtocols)
    {
        if (requestedProtocols == null || requestedProtocols.Count == 0) return true;

        // default => not a TLS/ALPN connection (plain HTTP) or unknown; nothing to verify.
        if (negotiated == default) return true;

        return requestedProtocols.Contains(negotiated);
    }

    /// <summary>
    ///     Gets the connection cache key.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="session">The session event arguments.</param>
    /// <param name="applicationProtocol">The application protocol.</param>
    /// <returns></returns>
    internal async Task<string> GetConnectionCacheKey(ProxyServer server, SessionEventArgsBase session,
        SslApplicationProtocol applicationProtocol)
    {
        List<SslApplicationProtocol>? applicationProtocols = null;
        if (applicationProtocol != default)
            applicationProtocols = new List<SslApplicationProtocol> { applicationProtocol };

        var customUpStreamProxy = session.CustomUpStreamProxy;

        var isHttps = session.IsHttps;
        if (customUpStreamProxy == null && server.GetCustomUpStreamProxyFunc != null)
            customUpStreamProxy = await server.GetCustomUpStreamProxyFunc(session);

        session.CustomUpStreamProxyUsed = customUpStreamProxy;

        var uri = session.HttpClient.Request.RequestUri;
        var upStreamEndPoint = session.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint;
        var upStreamProxy = customUpStreamProxy ?? (isHttps ? server.UpStreamHttpsProxy : server.UpStreamHttpProxy);

        // resolve the effective proxy (post-bypass) so the key matches the connection's actual route
        upStreamProxy = GetEffectiveUpstreamProxy(upStreamProxy, uri.Host, uri.Port);

        return GetConnectionCacheKey(uri.Host, uri.Port, isHttps, applicationProtocols, upStreamEndPoint,
            upStreamProxy);
    }


    /// <summary>
    ///     Create a server connection.
    /// </summary>
    /// <param name="proxyServer">The proxy server.</param>
    /// <param name="session">The session event arguments.</param>
    /// <param name="isConnect">Is this a CONNECT request.</param>
    /// <param name="applicationProtocol"></param>
    /// <param name="noCache">if set to <c>true</c> [no cache].</param>
    /// <param name="cancellationToken">The cancellation token for this async task.</param>
    /// <returns></returns>
    internal Task<TcpServerConnection> GetServerConnection(ProxyServer proxyServer, SessionEventArgsBase session,
        bool isConnect,
        SslApplicationProtocol applicationProtocol, bool noCache, CancellationToken cancellationToken)
    {
        List<SslApplicationProtocol>? applicationProtocols = null;
        if (applicationProtocol != default)
            applicationProtocols = new List<SslApplicationProtocol> { applicationProtocol };

        return GetServerConnection(proxyServer, session, isConnect, applicationProtocols, noCache, false,
            cancellationToken)!;
    }

    /// <summary>
    ///     Create a server connection.
    /// </summary>
    /// <param name="proxyServer">The proxy server.</param>
    /// <param name="session">The session event arguments.</param>
    /// <param name="isConnect">Is this a CONNECT request.</param>
    /// <param name="applicationProtocols"></param>
    /// <param name="noCache">if set to <c>true</c> [no cache].</param>
    /// <param name="prefetch">if set to <c>true</c> [prefetch].</param>
    /// <param name="cancellationToken">The cancellation token for this async task.</param>
    /// <returns></returns>
    internal async Task<TcpServerConnection?> GetServerConnection(ProxyServer proxyServer, SessionEventArgsBase session,
        bool isConnect,
        List<SslApplicationProtocol>? applicationProtocols, bool noCache, bool prefetch,
        CancellationToken cancellationToken)
    {
        var customUpStreamProxy = session.CustomUpStreamProxy;

        var isHttps = session.IsHttps;
        if (customUpStreamProxy == null && proxyServer.GetCustomUpStreamProxyFunc != null)
            customUpStreamProxy = await proxyServer.GetCustomUpStreamProxyFunc(session);

        session.CustomUpStreamProxyUsed = customUpStreamProxy;

        var request = session.HttpClient.Request;
        string host;
        int port;
        if (request.Authority.Length > 0)
        {
            var authority = request.Authority;
            var idx = authority.IndexOf((byte)':');
            if (idx == -1)
            {
                host = authority.GetString();
                port = 80;
            }
            else
            {
                host = authority.Slice(0, idx).GetString();
                port = int.Parse(authority.Slice(idx + 1).GetString());
            }
        }
        else
        {
            var uri = request.RequestUri;
            host = uri.Host;
            port = uri.Port;
        }

        var upStreamEndPoint = session.HttpClient.UpStreamEndPoint ?? proxyServer.UpStreamEndPoint;
        var upStreamProxy = customUpStreamProxy ??
                            (isHttps ? proxyServer.UpStreamHttpsProxy : proxyServer.UpStreamHttpProxy);

        // For transparent endpoints with a fixed forward target, only the TCP connection
        // destination is overridden; host/port stay the original for TLS SNI and Host header.
        string? connectHost = null;
        int? connectPort = null;
        if (session.ProxyEndPoint is TransparentBaseProxyEndPoint transparentEndPoint
            && !string.IsNullOrEmpty(transparentEndPoint.ForwardHost))
        {
            connectHost = transparentEndPoint.ForwardHost;
            connectPort = transparentEndPoint.ForwardPort;
        }

        return await GetServerConnection(proxyServer, host, port, session.HttpClient.Request.HttpVersion, isHttps,
            applicationProtocols, isConnect, session, upStreamEndPoint, upStreamProxy, noCache, prefetch,
            cancellationToken, connectHost, connectPort);
    }

    /// <summary>
    ///     Gets a TCP connection to server from connection pool.
    /// </summary>
    /// <param name="proxyServer">The current ProxyServer instance.</param>
    /// <param name="remoteHostName">The remote hostname.</param>
    /// <param name="remotePort">The remote port.</param>
    /// <param name="httpVersion">The http version to use.</param>
    /// <param name="isHttps">Is this a HTTPS request.</param>
    /// <param name="applicationProtocols">The list of HTTPS application level protocol to negotiate if needed.</param>
    /// <param name="isConnect">Is this a CONNECT request.</param>
    /// <param name="sessionArgs">The session event arguments.</param>
    /// <param name="upStreamEndPoint">The local upstream endpoint to make request via.</param>
    /// <param name="externalProxy">The external proxy to make request via.</param>
    /// <param name="noCache">Not from cache/create new connection.</param>
    /// <param name="prefetch">if set to <c>true</c> [prefetch].</param>
    /// <param name="cancellationToken">The cancellation token for this async task.</param>
    /// <returns></returns>
    internal async Task<TcpServerConnection?> GetServerConnection(ProxyServer proxyServer, string remoteHostName,
        int remotePort,
        Version httpVersion, bool isHttps, List<SslApplicationProtocol>? applicationProtocols, bool isConnect,
        SessionEventArgsBase sessionArgs, IPEndPoint? upStreamEndPoint, IExternalProxy? externalProxy,
        bool noCache, bool prefetch, CancellationToken cancellationToken,
        string? connectHost = null, int? connectPort = null)
    {
        var sslProtocol = sessionArgs.ClientConnection.SslProtocol;

        // resolve the effective proxy (post-bypass) so that direct and proxied connections to the
        // same destination don't collide in the pool, and so the connection's stored key matches.
        externalProxy = GetEffectiveUpstreamProxy(externalProxy, remoteHostName, remotePort);

        var cacheKey = GetConnectionCacheKey(remoteHostName, remotePort,
            isHttps, applicationProtocols, upStreamEndPoint, externalProxy, connectHost, connectPort);

        if (proxyServer.EnableConnectionPool && !noCache)
            if (cache.TryGetValue(cacheKey, out var existingConnections))
                lock (existingConnections)
                {
                    // +3 seconds for potential delay after getting connection
                    var cutOff = DateTime.UtcNow.AddSeconds(-proxyServer.ConnectionTimeOutSeconds + 3);
                    while (existingConnections.Count > 0)
                        if (existingConnections.TryDequeue(out var recentConnection))
                        {
                            if (recentConnection.LastAccess > cutOff
                                && recentConnection.TcpSocket.IsGoodConnection()
                                && IsNegotiatedProtocolCompatible(recentConnection, applicationProtocols))
                                return recentConnection;

                            if (recentConnection.TryScheduleDisposal())
                                disposalBag.Add(recentConnection);
                        }
                }

        var connection = await CreateServerConnection(remoteHostName, remotePort, httpVersion, isHttps, sslProtocol,
            applicationProtocols, isConnect, proxyServer, sessionArgs, upStreamEndPoint, externalProxy, cacheKey,
            prefetch, cancellationToken, connectHost, connectPort);

        return connection;
    }

    /// <summary>
    ///     Creates a TCP connection to server
    /// </summary>
    /// <param name="remoteHostName">The remote hostname.</param>
    /// <param name="remotePort">The remote port.</param>
    /// <param name="httpVersion">The http version to use.</param>
    /// <param name="isHttps">Is this a HTTPS request.</param>
    /// <param name="sslProtocol">The SSL protocol.</param>
    /// <param name="applicationProtocols">The list of HTTPS application level protocol to negotiate if needed.</param>
    /// <param name="isConnect">Is this a CONNECT request.</param>
    /// <param name="proxyServer">The current ProxyServer instance.</param>
    /// <param name="sessionArgs">The http session.</param>
    /// <param name="upStreamEndPoint">The local upstream endpoint to make request via.</param>
    /// <param name="externalProxy">The external proxy to make request via.</param>
    /// <param name="cacheKey">The connection cache key</param>
    /// <param name="prefetch">if set to <c>true</c> [prefetch].</param>
    /// <param name="cancellationToken">The cancellation token for this async task.</param>
    /// <returns></returns>
    private async Task<TcpServerConnection?> CreateServerConnection(string remoteHostName, int remotePort,
        Version httpVersion, bool isHttps, SslProtocols sslProtocol, List<SslApplicationProtocol>? applicationProtocols,
        bool isConnect,
        ProxyServer proxyServer, SessionEventArgsBase sessionArgs, IPEndPoint? upStreamEndPoint,
        IExternalProxy? externalProxy, string cacheKey,
        bool prefetch, CancellationToken cancellationToken,
        string? connectHost = null, int? connectPort = null)
    {
        // The actual destination we open the TCP connection to. When a fixed forward target
        // is configured, this differs from remoteHostName/remotePort which are kept for
        // TLS SNI/certificate validation, the HTTP Host header and connection identity.
        var connectHostName = string.IsNullOrEmpty(connectHost) ? remoteHostName : connectHost!;
        var connectPortNumber = connectPort ?? remotePort;

        // deny connection to proxy end points to avoid infinite connection loop.
        if (Server.ProxyEndPoints.Any(x => x.Port == connectPortNumber)
            && NetworkHelper.IsLocalIpAddress(connectHostName))
            throw new Exception(
                $"A client is making HTTP request to one of the listening ports of this proxy {connectHostName}:{connectPortNumber}");

        if (externalProxy != null)
            if (Server.ProxyEndPoints.Any(x => x.Port == externalProxy.Port)
                && NetworkHelper.IsLocalIpAddress(externalProxy.HostName))
                throw new Exception(
                    $"A client is making HTTP request via external proxy to one of the listening ports of this proxy {remoteHostName}:{remotePort}");

        if (proxyServer.SupportedServerSslProtocols != SslProtocols.None) sslProtocol = proxyServer.SupportedServerSslProtocols;

        if (isHttps && sslProtocol == SslProtocols.None) sslProtocol = proxyServer.SupportedSslProtocols;

        var useUpstreamProxy1 = false;

        // check if external proxy is set for HTTP/HTTPS
        if (externalProxy != null && !(externalProxy.HostName == remoteHostName && externalProxy.Port == remotePort))
        {
            useUpstreamProxy1 = true;

            // check if we need to ByPass
            if (externalProxy.BypassLocalhost &&
                NetworkHelper.IsLocalIpAddress(remoteHostName, externalProxy.ProxyDnsRequests))
                useUpstreamProxy1 = false;
        }

        if (!useUpstreamProxy1) externalProxy = null;

        Socket? tcpServerSocket = null;
        HttpServerStream? stream = null;

        SslApplicationProtocol negotiatedApplicationProtocol = default;
        var upstreamProxyWinAuthenticated = false;
        var usedClientCertificate = false;

        var retry = true;
        var enabledSslProtocols = sslProtocol;

        retry:
        try
        {
            var socks = externalProxy != null && externalProxy.ProxyType != ExternalProxyType.Http;
            var hostname = connectHostName;
            var port = connectPortNumber;

            if (externalProxy != null)
            {
                hostname = externalProxy.HostName;
                port = externalProxy.Port;
            }

            var ipAddresses = await Dns.GetHostAddressesAsync(hostname);
            if (ipAddresses == null || ipAddresses.Length == 0)
            {
                if (prefetch) return null;

                throw new Exception($"Could not resolve the hostname {hostname}");
            }

            if (sessionArgs != null) sessionArgs.TimeLine["Dns Resolved"] = DateTime.UtcNow;

            Array.Sort(ipAddresses, (x, y) => x.AddressFamily.CompareTo(y.AddressFamily));

            Exception? lastException = null;
            for (var i = 0; i < ipAddresses.Length; i++)
                try
                {
                    var ipAddress = ipAddresses[i];
                    var addressFamily = upStreamEndPoint?.AddressFamily ?? ipAddress.AddressFamily;

                    if (socks)
                    {
                        var proxySocket =
                            new ProxySocket.ProxySocket(addressFamily, SocketType.Stream, ProtocolType.Tcp);
                        proxySocket.ProxyType = externalProxy!.ProxyType == ExternalProxyType.Socks4
                            ? ProxyTypes.Socks4
                            : ProxyTypes.Socks5;

                        proxySocket.ProxyEndPoint = new IPEndPoint(ipAddress, port);
                        var proxyUser = externalProxy.UserName;
                        var proxyPassword = externalProxy.Password;

                        // SOCKS4 authenticates with a username only (no password), so do not require a
                        // non-null password to set the user. SOCKS5 user/password auth uses both.
                        if (proxyUser != null && proxyUser.Length > 0)
                        {
                            proxySocket.ProxyUser = proxyUser;
                            if (proxyPassword != null) proxySocket.ProxyPass = proxyPassword;
                        }

                        tcpServerSocket = proxySocket;
                    }
                    else
                    {
                        tcpServerSocket = new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp);
                    }

                    if (upStreamEndPoint != null) tcpServerSocket.Bind(upStreamEndPoint);

                    tcpServerSocket.NoDelay = proxyServer.NoDelay;
                    tcpServerSocket.ReceiveTimeout = proxyServer.ConnectionTimeOutSeconds * 1000;
                    tcpServerSocket.SendTimeout = proxyServer.ConnectionTimeOutSeconds * 1000;
                    tcpServerSocket.LingerState = new LingerOption(true, proxyServer.TcpTimeWaitSeconds);

                    if (proxyServer.ReuseSocket && RunTime.IsSocketReuseAvailable())
                        tcpServerSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                    Task connectTask;

                    if (socks)
                    {
                        if (externalProxy!.ProxyDnsRequests)
                        {
                            connectTask =
                                ProxySocketConnectionTaskFactory.CreateTask((ProxySocket.ProxySocket)tcpServerSocket,
                                    connectHostName, connectPortNumber);
                        }
                        else
                        {
                            var remoteIpAddresses = await Dns.GetHostAddressesAsync(connectHostName);
                            if (remoteIpAddresses == null || remoteIpAddresses.Length == 0)
                                throw new Exception($"Could not resolve the SOCKS remote hostname {connectHostName}");

                            // Known limitation: when the proxy resolves the remote host to multiple
                            // addresses we only attempt the first. Per-remote-address failover would
                            // require restructuring the shared connect/timeout loop below (which iterates
                            // over the PROXY addresses, not the remote target addresses) and is left as a
                            // future improvement to avoid destabilizing the connection path.
                            connectTask = ProxySocketConnectionTaskFactory.CreateTask(
                                (ProxySocket.ProxySocket)tcpServerSocket, remoteIpAddresses[0], connectPortNumber);
                        }
                    }
                    else
                    {
                        connectTask = SocketConnectionTaskFactory.CreateTask(tcpServerSocket, ipAddress, port);
                    }

                    await Task.WhenAny(connectTask,
                        Task.Delay(proxyServer.ConnectTimeOutSeconds * 1000, cancellationToken));
                    if (!connectTask.IsCompleted || !tcpServerSocket.Connected)
                    {
                        // here we can just do some cleanup and let the loop continue since
                        // we will either get a connection or wind up with a null tcpClient
                        // which will throw
                        try
                        {
                            connectTask.Dispose();
                        }
                        catch
                        {
                            // ignore
                        }

                        try
                        {
                            tcpServerSocket?.Dispose();
                            tcpServerSocket = null;
                        }
                        catch
                        {
                            // ignore
                        }

                        continue;
                    }

                    break;
                }
                catch (Exception e)
                {
                    // dispose the current TcpClient and try the next address
                    lastException = e;
                    tcpServerSocket?.Dispose();
                    tcpServerSocket = null;
                }

            if (tcpServerSocket == null)
            {
                if (sessionArgs != null && proxyServer.CustomUpStreamProxyFailureFunc != null)
                {
                    var newUpstreamProxy = await proxyServer.CustomUpStreamProxyFailureFunc(sessionArgs);
                    if (newUpstreamProxy != null)
                    {
                        sessionArgs.CustomUpStreamProxyUsed = newUpstreamProxy;
                        sessionArgs.TimeLine["Retrying Upstream Proxy Connection"] = DateTime.UtcNow;

                        // retry with the NEW proxy: resolve its effective form (bypass rules) and
                        // recompute the cache key so the retried connection is created via, and cached
                        // under, the new proxy rather than the one that just failed.
                        var retryProxy = GetEffectiveUpstreamProxy(newUpstreamProxy, remoteHostName, remotePort);
                        var retryCacheKey = GetConnectionCacheKey(remoteHostName, remotePort, isHttps,
                            applicationProtocols, upStreamEndPoint, retryProxy, connectHost, connectPort);

                        return await CreateServerConnection(remoteHostName, remotePort, httpVersion, isHttps,
                            sslProtocol, applicationProtocols, isConnect, proxyServer, sessionArgs, upStreamEndPoint,
                            retryProxy, retryCacheKey, prefetch, cancellationToken, connectHost, connectPort);
                    }
                }

                if (prefetch) return null;

                throw new Exception($"Could not establish connection to {hostname}", lastException);
            }

            if (sessionArgs != null) sessionArgs.TimeLine["Connection Established"] = DateTime.UtcNow;

            await proxyServer.InvokeServerConnectionCreateEvent(tcpServerSocket);

            stream = new HttpServerStream(proxyServer, new NetworkStream(tcpServerSocket, true), proxyServer.BufferPool,
                cancellationToken);

            if (externalProxy != null && externalProxy.ProxyType == ExternalProxyType.Http && (isConnect || isHttps))
            {
                var authority = $"{connectHostName}:{connectPortNumber}";
                var authorityBytes = authority.GetByteString();
                var connectRequest = new ConnectRequest(authorityBytes)
                {
                    IsHttps = isHttps,
                    RequestUriString8 = authorityBytes,
                    HttpVersion = httpVersion
                };

                connectRequest.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionKeepAlive);
                connectRequest.Headers.AddHeader(KnownHeaders.Host, authority);

                if (!externalProxy.UseDefaultCredentials &&
                    !string.IsNullOrEmpty(externalProxy.UserName) && externalProxy.Password != null)
                {
                    connectRequest.Headers.AddHeader(HttpHeader.ProxyConnectionKeepAlive);
                    connectRequest.Headers.AddHeader(
                        HttpHeader.GetProxyAuthorizationHeader(externalProxy.UserName, externalProxy.Password));
                }

                var authenticationData = new InternalDataStore();
                var authenticationAttempts = 0;

                while (true)
                {
                    await proxyServer.OnBeforeUpStreamConnectRequest(connectRequest);
                    await stream.WriteRequestAsync(connectRequest, cancellationToken);

                    var httpStatus = await stream.ReadResponseStatus(cancellationToken);
                    var headers = new HeaderCollection();
                    await HeaderParser.ReadHeaders(stream, headers, cancellationToken);

                    if (httpStatus.StatusCode == (int)HttpStatusCode.OK ||
                        httpStatus.Description.EqualsIgnoreCase("Connection Established"))
                    {
                        upstreamProxyWinAuthenticated = authenticationAttempts > 0;
                        break;
                    }

                    await DrainUpstreamProxyResponseBody(stream, headers, cancellationToken);

                    if (httpStatus.StatusCode != (int)HttpStatusCode.ProxyAuthenticationRequired ||
                        !externalProxy.UseDefaultCredentials ||
                        authenticationAttempts >= MaximumUpstreamProxyAuthenticationAttempts ||
                        !TryGetUpstreamProxyAuthenticationChallenge(headers, out var scheme, out var challenge))
                        throw new Exception("Upstream proxy failed to create a secure tunnel");

                    if (headers.GetHeaderValueOrNull(KnownHeaders.Connection)
                            ?.EqualsIgnoreCase(KnownHeaders.ConnectionClose.String) == true ||
                        headers.GetHeaderValueOrNull(KnownHeaders.ProxyConnection)
                            ?.EqualsIgnoreCase(KnownHeaders.ProxyConnectionClose.String) == true)
                        throw new Exception("Upstream proxy closed the connection during authentication");

                    var token = proxyServer.GenerateUpstreamProxyWinAuthToken(externalProxy, scheme!, challenge,
                        authenticationData);
                    if (string.IsNullOrEmpty(token))
                        throw new Exception("Failed to generate an upstream proxy authentication token");

                    connectRequest.Headers.SetOrAddHeaderValue(KnownHeaders.ProxyAuthorization,
                        string.Concat(scheme, token));
                    connectRequest.Headers.SetOrAddHeaderValue(KnownHeaders.ProxyConnection,
                        KnownHeaders.ConnectionKeepAlive.String);
                    authenticationAttempts++;
                }
            }

            if (isHttps)
            {
                var sslStream = new SslStream(stream, false,
                    (sender, certificate, chain, sslPolicyErrors) =>
                        proxyServer.ValidateServerCertificate(sender, sessionArgs, certificate, chain, sslPolicyErrors),
                    (sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers) =>
                    {
                        var clientCertificate = proxyServer.SelectClientCertificate(sender, sessionArgs, targetHost,
                            localCertificates, remoteCertificate, acceptableIssuers);

                        // a per-session client certificate makes this TLS connection identity-specific;
                        // it must not be reused by another session from the pool.
                        if (clientCertificate != null) usedClientCertificate = true;

                        return clientCertificate!;
                    });
                stream = new HttpServerStream(proxyServer, sslStream, proxyServer.BufferPool, cancellationToken);

                var options = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = applicationProtocols,
                    TargetHost = remoteHostName,
                    ClientCertificates = null,
                    EnabledSslProtocols = enabledSslProtocols,
                    CertificateRevocationCheckMode = proxyServer.CheckCertificateRevocation
                };
                await sslStream.AuthenticateAsClientAsync(options, cancellationToken);
#if NET6_0_OR_GREATER
                negotiatedApplicationProtocol = sslStream.NegotiatedApplicationProtocol;
#endif

                if (sessionArgs != null) sessionArgs.TimeLine["HTTPS Established"] = DateTime.UtcNow;
            }
        }
#pragma warning disable SYSLIB0039 // TLS 1.0/1.1 are intentionally retained for legacy upstream compatibility fallback.
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80131620) && retry &&
                                     enabledSslProtocols >= SslProtocols.Tls11)
        {
            stream?.Dispose();
            tcpServerSocket?.Close();

            // Specifying Tls11 and/or Tls12 will disable the usage of Ssl3, even if it has been included.
            // https://docs.microsoft.com/en-us/dotnet/api/system.servicemodel.tcptransportsecurity.sslprotocols?view=dotnet-plat-ext-3.1
            enabledSslProtocols = proxyServer.SupportedSslProtocols & (SslProtocols)0xff;

            if (enabledSslProtocols == SslProtocols.None) throw;

            retry = false;
            goto retry;
        }
        catch (AuthenticationException ex) when (ex.HResult == unchecked((int)0x80131501) && retry &&
                                                 enabledSslProtocols >= SslProtocols.Tls11)
        {
            stream?.Dispose();
            tcpServerSocket?.Close();

            // Specifying Tls11 and/or Tls12 will disable the usage of Ssl3, even if it has been included.
            // https://docs.microsoft.com/en-us/dotnet/api/system.servicemodel.tcptransportsecurity.sslprotocols?view=dotnet-plat-ext-3.1
            enabledSslProtocols = proxyServer.SupportedSslProtocols & (SslProtocols)0xff;

            if (enabledSslProtocols == SslProtocols.None) throw;

            retry = false;
            goto retry;
        }
#pragma warning restore SYSLIB0039
        catch (Exception)
        {
            stream?.Dispose();
            tcpServerSocket?.Close();
            throw;
        }

        return new TcpServerConnection(proxyServer, tcpServerSocket, stream, remoteHostName, remotePort, isHttps,
            negotiatedApplicationProtocol, httpVersion, externalProxy, upStreamEndPoint, cacheKey)
        {
            IsWinAuthenticated = upstreamProxyWinAuthenticated,
            UsedClientCertificate = usedClientCertificate
        };
    }

    private static bool TryGetUpstreamProxyAuthenticationChallenge(HeaderCollection headers, out string? scheme,
        out string? challenge)
    {
        scheme = null;
        challenge = null;
        var authenticationHeaders = headers.GetHeaders(KnownHeaders.ProxyAuthenticate.String);
        if (authenticationHeaders == null) return false;

        foreach (var supportedScheme in UpstreamProxyAuthenticationSchemes)
            foreach (var header in authenticationHeaders)
            {
                var value = header.Value.Trim();
                if (!value.StartsWith(supportedScheme, StringComparison.OrdinalIgnoreCase) ||
                    value.Length > supportedScheme.Length && !char.IsWhiteSpace(value[supportedScheme.Length]))
                    continue;

                scheme = supportedScheme;
                challenge = value.Length == supportedScheme.Length
                    ? null
                    : value.Substring(supportedScheme.Length).Trim();
                return true;
            }

        return false;
    }

    private static async Task DrainUpstreamProxyResponseBody(HttpServerStream stream, HeaderCollection headers,
        CancellationToken cancellationToken)
    {
        var transferEncoding = headers.GetHeaderValueOrNull(KnownHeaders.TransferEncoding);
        if (transferEncoding != null && transferEncoding.ContainsIgnoreCase(KnownHeaders.TransferEncodingChunked.String))
        {
            await DrainChunkedBody(stream, cancellationToken);
            return;
        }

        var contentLengthValue = headers.GetHeaderValueOrNull(KnownHeaders.ContentLength);
        if (!long.TryParse(contentLengthValue, out var remaining) || remaining <= 0) return;

        await DrainBytes(stream, remaining, cancellationToken);
    }

    private static async Task DrainChunkedBody(HttpServerStream stream, CancellationToken cancellationToken)
    {
        while (true)
        {
            var chunkHead = await stream.ReadLineAsync(cancellationToken);
            if (chunkHead == null)
                throw new IOException("Upstream proxy closed the connection while sending a chunked response body");

            var idx = chunkHead.IndexOf(';');
            if (idx >= 0) chunkHead = chunkHead.Substring(0, idx);

            if (!int.TryParse(chunkHead, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var chunkSize))
                throw new IOException("Upstream proxy sent an invalid chunk header during authentication");

            if (chunkSize == 0)
            {
                // consume the optional trailer headers until the terminating blank line
                while (!string.IsNullOrEmpty(await stream.ReadLineAsync(cancellationToken)))
                {
                }

                return;
            }

            // chunk data followed by its trailing CRLF
            await DrainBytes(stream, chunkSize + 2, cancellationToken);
        }
    }

    private static async Task DrainBytes(HttpServerStream stream, long count, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (count > 0)
            {
                var read = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, count), cancellationToken);
                if (read <= 0)
                    throw new IOException("Upstream proxy closed the connection while sending a response body");
                count -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }


    /// <summary>
    ///     Release connection back to cache.
    /// </summary>
    /// <param name="connection">The Tcp server connection to return.</param>
    /// <param name="close">Should we just close the connection instead of reusing?</param>
    internal async Task Release(TcpServerConnection? connection, bool close = false)
    {
        if (connection == null) return;

        // already scheduled for disposal: never pool it again.
        if (connection.IsDisposalScheduled) return;

        if (close || connection.IsWinAuthenticated || connection.UsedClientCertificate
            || !Server.EnableConnectionPool || connection.IsClosed)
        {
            if (connection.TryScheduleDisposal()) disposalBag.Add(connection);
            return;
        }

        connection.LastAccess = DateTime.UtcNow;

        try
        {
            await @lock.WaitAsync();

            while (true)
            {
                if (cache.TryGetValue(connection.CacheKey, out var existingConnections))
                {
                    while (existingConnections.Count >= Server.MaxCachedConnections)
                        if (existingConnections.TryDequeue(out var staleConnection))
                            if (staleConnection.TryScheduleDisposal())
                                disposalBag.Add(staleConnection);

                    if (existingConnections.Any(x => x == connection)) break;

                    existingConnections.Enqueue(connection);
                    break;
                }

                if (cache.TryAdd(connection.CacheKey,
                        new ConcurrentQueue<TcpServerConnection>(new[] { connection })))
                    break;
            }
        }
        finally
        {
            @lock.Release();
        }
    }

    internal async Task Release(Task<TcpServerConnection?>? connectionCreateTask, bool closeServerConnection)
    {
        if (connectionCreateTask == null) return;

        TcpServerConnection? connection = null;
        try
        {
            connection = await connectionCreateTask;
        }
        catch
        {
            // ignore
        }
        finally
        {
            if (connection != null) await Release(connection, closeServerConnection);
        }
    }

    private async Task ClearOutdatedConnections()
    {
        while (runCleanUpTask)
            try
            {
                var cutOff = DateTime.UtcNow.AddSeconds(-Server.ConnectionTimeOutSeconds);
                foreach (var item in cache)
                {
                    var queue = item.Value;

                    // take the same lock used by the pool-get path so that dequeue/enqueue here
                    // does not race with a concurrent Get on the same queue.
                    lock (queue)
                    {
                        while (queue.Count > 0)
                            if (queue.TryDequeue(out var connection))
                            {
                                if (!Server.EnableConnectionPool || connection.LastAccess < cutOff)
                                {
                                    if (connection.TryScheduleDisposal())
                                        disposalBag.Add(connection);
                                }
                                else
                                {
                                    queue.Enqueue(connection);
                                    break;
                                }
                            }
                    }
                }

                try
                {
                    await @lock.WaitAsync();

                    // clear empty queues
                    var emptyKeys = cache.ToArray().Where(x => x.Value.Count == 0).Select(x => x.Key);
                    foreach (var key in emptyKeys) cache.TryRemove(key, out _);
                }
                finally
                {
                    @lock.Release();
                }

                while (!disposalBag.IsEmpty)
                    if (disposalBag.TryTake(out var connection))
                        connection?.Dispose();
            }
            catch (Exception e)
            {
                Server.ExceptionFunc?.Invoke(new Exception("An error occurred when disposing server connections.", e));
            }
            finally
            {
                // cleanup every 3 seconds by default
                await Task.Delay(1000 * 3);
            }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposed) return;

        runCleanUpTask = false;

        if (disposing)
        {
            try
            {
                @lock.Wait();

                foreach (var queue in cache.Select(x => x.Value).ToList())
                    while (!queue.IsEmpty)
                        if (queue.TryDequeue(out var connection))
                            disposalBag.Add(connection);

                cache.Clear();
            }
            finally
            {
                @lock.Release();
            }

            while (!disposalBag.IsEmpty)
                if (disposalBag.TryTake(out var connection))
                    connection?.Dispose();
        }

        disposed = true;
    }

    ~TcpConnectionFactory()
    {
        Dispose(false);
    }

    private static class SocketConnectionTaskFactory
    {
        private static IAsyncResult BeginConnect(IPAddress address, int port, AsyncCallback? requestCallback,
            object? state)
        {
            var socket = state as Socket ?? throw new InvalidOperationException("Socket APM state is missing.");
            return socket.BeginConnect(address, port, requestCallback, state);
        }

        private static void EndConnect(IAsyncResult asyncResult)
        {
            var socket = asyncResult.AsyncState as Socket
                         ?? throw new InvalidOperationException("Socket APM state is missing.");
            socket.EndConnect(asyncResult);
        }

        public static Task CreateTask(Socket socket, IPAddress ipAddress, int port)
        {
            return Task.Factory.FromAsync(BeginConnect, EndConnect, ipAddress, port, socket);
        }
    }

    private static class ProxySocketConnectionTaskFactory
    {
        private static IAsyncResult BeginConnect(IPAddress address, int port, AsyncCallback? requestCallback,
            object? state)
        {
            var socket = state as ProxySocket.ProxySocket
                         ?? throw new InvalidOperationException("Proxy socket APM state is missing.");
            return socket.BeginConnect(address, port, requestCallback, state);
        }

        private static IAsyncResult BeginConnect(string hostName, int port, AsyncCallback? requestCallback,
            object? state)
        {
            var socket = state as ProxySocket.ProxySocket
                         ?? throw new InvalidOperationException("Proxy socket APM state is missing.");
            return socket.BeginConnect(hostName, port, requestCallback, state);
        }

        private static void EndConnect(IAsyncResult asyncResult)
        {
            var socket = asyncResult.AsyncState as ProxySocket.ProxySocket
                         ?? throw new InvalidOperationException("Proxy socket APM state is missing.");
            socket.EndConnect(asyncResult);
        }

        public static Task CreateTask(ProxySocket.ProxySocket socket, IPAddress ipAddress, int port)
        {
            return Task.Factory.FromAsync(BeginConnect, EndConnect, ipAddress, port, socket);
        }

        public static Task CreateTask(ProxySocket.ProxySocket socket, string hostName, int port)
        {
            return Task.Factory.FromAsync(BeginConnect, EndConnect, hostName, port, socket);
        }
    }
}ParseOptions.0.jsonî&
nD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\TcpConnection\TcpServerConnection.cså%using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Network.Tcp;

/// <summary>
///     An object that holds TcpConnection to a particular server and port
/// </summary>
internal class TcpServerConnection : IDisposable
{
    private bool disposed;

    private int disposalScheduled;

    internal TcpServerConnection(ProxyServer proxyServer, Socket tcpSocket, HttpServerStream stream,
        string hostName, int port, bool isHttps, SslApplicationProtocol negotiatedApplicationProtocol,
        Version version, IExternalProxy? upStreamProxy, IPEndPoint? upStreamEndPoint, string cacheKey)
    {
        TcpSocket = tcpSocket;
        LastAccess = DateTime.UtcNow;
        ProxyServer = proxyServer;
        ProxyServer.UpdateServerConnectionCount(true);
        Stream = stream;
        HostName = hostName;
        Port = port;
        IsHttps = isHttps;
        NegotiatedApplicationProtocol = negotiatedApplicationProtocol;
        Version = version;
        UpStreamProxy = upStreamProxy;
        UpStreamEndPoint = upStreamEndPoint;

        CacheKey = cacheKey;
    }

    public Guid Id { get; } = Guid.NewGuid();

    private ProxyServer ProxyServer { get; }

    internal bool IsClosed => Stream.IsClosed;

    internal IExternalProxy? UpStreamProxy { get; set; }

    internal string HostName { get; set; }

    internal int Port { get; set; }

    internal bool IsHttps { get; set; }

    internal SslApplicationProtocol NegotiatedApplicationProtocol { get; set; }

    /// <summary>
    ///     Local NIC via connection is made
    /// </summary>
    internal IPEndPoint? UpStreamEndPoint { get; set; }

    /// <summary>
    ///     Http version
    /// </summary>
    internal Version Version { get; set; } = HttpHeader.VersionUnknown;

    /// <summary>
    ///     The TcpClient.
    /// </summary>
    internal Socket TcpSocket { get; }

    /// <summary>
    ///     Used to write lines to server
    /// </summary>
    internal HttpServerStream Stream { get; }

    /// <summary>
    ///     Last time this connection was used
    /// </summary>
    internal DateTime LastAccess { get; set; }

    /// <summary>
    ///     The cache key used to uniquely identify this connection properties
    /// </summary>
    internal string CacheKey { get; set; }

    /// <summary>
    ///     Is this connection authenticated via WinAuth
    /// </summary>
    internal bool IsWinAuthenticated { get; set; }

    /// <summary>
    ///     True when a per-session client certificate was presented on this TLS connection.
    ///     Such connections are identity-specific and must not be reused from the shared pool.
    /// </summary>
    internal bool UsedClientCertificate { get; set; }

    /// <summary>
    ///     True once this connection has been scheduled for disposal.
    ///     A scheduled connection must never be returned to the pool.
    /// </summary>
    internal bool IsDisposalScheduled => Volatile.Read(ref disposalScheduled) != 0;

    /// <summary>
    ///     Atomically marks this connection as scheduled for disposal.
    ///     Returns true only for the first caller, so the connection is added to the
    ///     disposal bag exactly once (avoids duplicate disposal and an O(n) membership scan).
    /// </summary>
    internal bool TryScheduleDisposal()
    {
        return Interlocked.CompareExchange(ref disposalScheduled, 1, 0) == 0;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposed) return;

        Task.Run(async () =>
        {
            // delay calling tcp connection close()
            // so that server have enough time to call close first.
            // This way we can push tcp Time_Wait to server side when possible.
            await Task.Delay(1000);

            ProxyServer.UpdateServerConnectionCount(false);

            if (disposing)
            {
                Stream.Dispose();

                try
                {
                    TcpSocket.Close();
                }
                catch
                {
                    // ignore
                }
            }
        });

        disposed = true;
    }

    ~TcpServerConnection()
    {
#if DEBUG
            // Finalizer should not be called
            System.Diagnostics.Debugger.Break();
#endif

        Dispose(false);
    }
}ParseOptions.0.jsonÓ@
dD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\WinAuth\Security\Common.cs?using System;
using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Network.WinAuth.Security;

internal class Common
{
    internal static uint NewContextAttributes = 0;
    internal static SecurityInteger NewLifeTime = new(0);

    #region internal constants

    internal const int IscReqReplayDetect = 0x00000004;
    internal const int IscReqSequenceDetect = 0x00000008;
    internal const int IscReqConfidentiality = 0x00000010;
    internal const int IscReqConnection = 0x00000800;

    internal const int SecurityNativeDataRepresentation = 0x10;
    internal const int MaximumTokenSize = 12288;
    internal const int SecurityCredentialsOutbound = 2;
    internal const int SuccessfulResult = 0;
    internal const int IntermediateResult = 0x90312;

    #endregion

    #region internal enumerations

    internal enum SecurityBufferType
    {
        SecbufferVersion = 0,
        SecbufferEmpty = 0,
        SecbufferData = 1,
        SecbufferToken = 2
    }

    [Flags]
    internal enum NtlmFlags
    {
        // The client sets this flag to indicate that it supports Unicode strings.
        NegotiateUnicode = 0x00000001,

        // This is set to indicate that the client supports OEM strings.
        NegotiateOem = 0x00000002,

        // This requests that the server send the authentication target with the Type 2 reply.
        RequestTarget = 0x00000004,

        // Indicates that NTLM authentication is supported.
        NegotiateNtlm = 0x00000200,

        // When set, the client will send with the message the name of the domain in which the workstation has membership.
        NegotiateDomainSupplied = 0x00001000,

        // Indicates that the client is sending its workstation name with the message.  
        NegotiateWorkstationSupplied = 0x00002000,

        // Indicates that communication between the client and server after authentication should carry a "dummy" signature.
        NegotiateAlwaysSign = 0x00008000,

        // Indicates that this client supports the NTLM2 signing and sealing scheme; if negotiated, this can also affect the response calculations.
        NegotiateNtlm2Key = 0x00080000,

        // Indicates that this client supports strong (128-bit) encryption.
        Negotiate128 = 0x20000000,

        // Indicates that this client supports medium (56-bit) encryption.
        Negotiate56 = unchecked((int)0x80000000)
    }

    internal enum NtlmAuthLevel
    {
        /* Use LM and NTLM, never use NTLMv2 session security. */
        LmAndNtlm,

        /* Use NTLMv2 session security if the server supports it,
         * otherwise fall back to LM and NTLM. */
        LmAndNtlmAndTryNtlMv2Session,

        /* Use NTLMv2 session security if the server supports it,
         * otherwise fall back to NTLM.  Never use LM. */
        NtlmOnly,

        /* Use NTLMv2 only. */
        NtlMv2Only
    }

    #endregion

    #region internal structures

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityHandle
    {
        internal IntPtr LowPart;
        internal IntPtr HighPart;

        internal SecurityHandle(int dummy)
        {
            LowPart = HighPart = IntPtr.Zero;
        }

        /// <summary>
        ///     Resets all internal pointers to default value
        /// </summary>
        internal void Reset()
        {
            LowPart = HighPart = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityInteger
    {
        internal uint LowPart;
        internal int HighPart;

        internal SecurityInteger(int dummy)
        {
            LowPart = 0;
            HighPart = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityBuffer
    {
        internal int cbBuffer;
        internal int cbBufferType;
        internal IntPtr pvBuffer;

        internal SecurityBuffer(int bufferSize)
        {
            cbBuffer = bufferSize;
            cbBufferType = (int)SecurityBufferType.SecbufferToken;
            pvBuffer = Marshal.AllocHGlobal(bufferSize);
        }

        internal SecurityBuffer(byte[] secBufferBytes)
        {
            cbBuffer = secBufferBytes.Length;
            cbBufferType = (int)SecurityBufferType.SecbufferToken;
            pvBuffer = Marshal.AllocHGlobal(cbBuffer);
            Marshal.Copy(secBufferBytes, 0, pvBuffer, cbBuffer);
        }

        internal SecurityBuffer(byte[] secBufferBytes, SecurityBufferType bufferType)
        {
            cbBuffer = secBufferBytes.Length;
            cbBufferType = (int)bufferType;
            pvBuffer = Marshal.AllocHGlobal(cbBuffer);
            Marshal.Copy(secBufferBytes, 0, pvBuffer, cbBuffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityBufferDescription
    {
        internal int ulVersion;
        internal int cBuffers;
        internal IntPtr pBuffers; // Point to SecBuffer

        internal SecurityBufferDescription(int bufferSize)
        {
            ulVersion = (int)SecurityBufferType.SecbufferVersion;
            cBuffers = 1;
            var thisSecBuffer = new SecurityBuffer(bufferSize);
            pBuffers = Marshal.AllocHGlobal(Marshal.SizeOf(thisSecBuffer));
            Marshal.StructureToPtr(thisSecBuffer, pBuffers, false);
        }

        internal SecurityBufferDescription(byte[] secBufferBytes)
        {
            ulVersion = (int)SecurityBufferType.SecbufferVersion;
            cBuffers = 1;
            var thisSecBuffer = new SecurityBuffer(secBufferBytes);
            pBuffers = Marshal.AllocHGlobal(Marshal.SizeOf(thisSecBuffer));
            Marshal.StructureToPtr(thisSecBuffer, pBuffers, false);
        }


        internal byte[]? GetBytes()
        {
            byte[]? buffer = null;

            if (pBuffers == IntPtr.Zero) throw new InvalidOperationException("Object has already been disposed!!!");

            if (cBuffers == 1)
            {
                var thisSecBuffer = Marshal.PtrToStructure<SecurityBuffer>(pBuffers);

                if (thisSecBuffer.cbBuffer > 0)
                {
                    buffer = new byte[thisSecBuffer.cbBuffer];
                    Marshal.Copy(thisSecBuffer.pvBuffer, buffer, 0, thisSecBuffer.cbBuffer);
                }
            }
            else
            {
                var bytesToAllocate = 0;

                for (var index = 0; index < cBuffers; index++)
                {
                    // The bits were written out the following order:
                    // int cbBuffer;
                    // int BufferType;
                    // pvBuffer;
                    // What we need to do here calculate the total number of bytes we need to copy...
                    var currentOffset = index * Marshal.SizeOf(typeof(SecurityBuffer));
                    bytesToAllocate += Marshal.ReadInt32(pBuffers, currentOffset);
                }

                buffer = new byte[bytesToAllocate];

                for (int index = 0, bufferIndex = 0; index < cBuffers; index++)
                {
                    // The bits were written out the following order:
                    // int cbBuffer;
                    // int BufferType;
                    // pvBuffer;
                    // Now iterate over the individual buffers and put them together into a
                    // byte array...
                    var currentOffset = index * Marshal.SizeOf(typeof(SecurityBuffer));
                    var bytesToCopy = Marshal.ReadInt32(pBuffers, currentOffset);
                    var secBufferpvBuffer = Marshal.ReadIntPtr(pBuffers,
                        currentOffset + Marshal.SizeOf(typeof(int)) + Marshal.SizeOf(typeof(int)));
                    Marshal.Copy(secBufferpvBuffer, buffer, bufferIndex, bytesToCopy);
                    bufferIndex += bytesToCopy;
                }
            }

            return buffer;
        }
    }

    #endregion
}ParseOptions.0.json±3
jD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\WinAuth\Security\LittleEndian.cs≠2//
// Mono.Security.BitConverterLE.cs
//  Like System.BitConverter but always little endian
//
// Author:
//   Bernie Solomon
//

//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;

namespace Titanium.Web.Proxy.Network.WinAuth.Security;

internal sealed class LittleEndian
{
    private LittleEndian()
    {
    }

    private static unsafe byte[] GetUShortBytes(byte* bytes)
    {
        if (BitConverter.IsLittleEndian) return new[] { bytes[0], bytes[1] };

        return new[] { bytes[1], bytes[0] };
    }

    private static unsafe byte[] GetUIntBytes(byte* bytes)
    {
        if (BitConverter.IsLittleEndian) return new[] { bytes[0], bytes[1], bytes[2], bytes[3] };

        return new[] { bytes[3], bytes[2], bytes[1], bytes[0] };
    }

    private static unsafe byte[] GetULongBytes(byte* bytes)
    {
        if (BitConverter.IsLittleEndian)
            return new[] { bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], bytes[7] };

        return new[] { bytes[7], bytes[6], bytes[5], bytes[4], bytes[3], bytes[2], bytes[1], bytes[0] };
    }

    internal static byte[] GetBytes(bool value)
    {
        return new[] { value ? (byte)1 : (byte)0 };
    }

    internal static unsafe byte[] GetBytes(char value)
    {
        return GetUShortBytes((byte*)&value);
    }

    internal static unsafe byte[] GetBytes(short value)
    {
        return GetUShortBytes((byte*)&value);
    }

    internal static unsafe byte[] GetBytes(int value)
    {
        return GetUIntBytes((byte*)&value);
    }

    internal static unsafe byte[] GetBytes(long value)
    {
        return GetULongBytes((byte*)&value);
    }

    internal static unsafe byte[] GetBytes(ushort value)
    {
        return GetUShortBytes((byte*)&value);
    }

    internal static unsafe byte[] GetBytes(uint value)
    {
        return GetUIntBytes((byte*)&value);
    }

    internal static unsafe byte[] GetBytes(ulong value)
    {
        return GetULongBytes((byte*)&value);
    }

    internal static unsafe byte[] GetBytes(float value)
    {
        return GetUIntBytes((byte*)&value);
    }

    internal static unsafe byte[] GetBytes(double value)
    {
        return GetULongBytes((byte*)&value);
    }

    private static unsafe void UShortFromBytes(byte* dst, byte[] src, int startIndex)
    {
        if (BitConverter.IsLittleEndian)
        {
            dst[0] = src[startIndex];
            dst[1] = src[startIndex + 1];
        }
        else
        {
            dst[0] = src[startIndex + 1];
            dst[1] = src[startIndex];
        }
    }

    private static unsafe void UIntFromBytes(byte* dst, byte[] src, int startIndex)
    {
        if (BitConverter.IsLittleEndian)
        {
            dst[0] = src[startIndex];
            dst[1] = src[startIndex + 1];
            dst[2] = src[startIndex + 2];
            dst[3] = src[startIndex + 3];
        }
        else
        {
            dst[0] = src[startIndex + 3];
            dst[1] = src[startIndex + 2];
            dst[2] = src[startIndex + 1];
            dst[3] = src[startIndex];
        }
    }

    private static unsafe void ULongFromBytes(byte* dst, byte[] src, int startIndex)
    {
        if (BitConverter.IsLittleEndian)
            for (var i = 0; i < 8; ++i)
                dst[i] = src[startIndex + i];
        else
            for (var i = 0; i < 8; ++i)
                dst[i] = src[startIndex + (7 - i)];
    }

    internal static bool ToBoolean(byte[] value, int startIndex)
    {
        return value[startIndex] != 0;
    }

    internal static unsafe char ToChar(byte[] value, int startIndex)
    {
        char ret;

        UShortFromBytes((byte*)&ret, value, startIndex);

        return ret;
    }

    internal static unsafe short ToInt16(byte[] value, int startIndex)
    {
        short ret;

        UShortFromBytes((byte*)&ret, value, startIndex);

        return ret;
    }

    internal static unsafe int ToInt32(byte[] value, int startIndex)
    {
        int ret;

        UIntFromBytes((byte*)&ret, value, startIndex);

        return ret;
    }

    internal static unsafe long ToInt64(byte[] value, int startIndex)
    {
        long ret;

        ULongFromBytes((byte*)&ret, value, startIndex);

        return ret;
    }

    internal static unsafe ushort ToUInt16(byte[] value, int startIndex)
    {
        ushort ret;

        UShortFromBytes((byte*)&ret, value, startIndex);

        return ret;
    }

    internal static unsafe uint ToUInt32(byte[] value, int startIndex)
    {
        uint ret;

        UIntFromBytes((byte*)&ret, value, startIndex);

        return ret;
    }

    internal static unsafe ulong ToUInt64(byte[] value, int startIndex)
    {
        ulong ret;

        ULongFromBytes((byte*)&ret, value, startIndex);

        return ret;
    }

    internal static unsafe float ToSingle(byte[] value, int startIndex)
    {
        float ret;

        UIntFromBytes((byte*)&ret, value, startIndex);

        return ret;
    }

    internal static unsafe double ToDouble(byte[] value, int startIndex)
    {
        double ret;

        ULongFromBytes((byte*)&ret, value, startIndex);

        return ret;
    }
}ParseOptions.0.json’
eD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\WinAuth\Security\Message.cs÷//
// Nancy.Authentication.Ntlm.Protocol.Type3Message - Authentication
//
// Author:
//	Sebastien Pouliot <sebastien@ximian.com>
//
// (C) 2003 Motus Technologies Inc. (http://www.motus.com)
// Copyright (C) 2004 Novell, Inc (http://www.novell.com)
//
// References
// a.	NTLM Authentication Scheme for HTTP, Ronald Tschal√§r
//	http://www.innovation.ch/java/ntlm.html
// b.	The NTLM Authentication Protocol, Copyright ¬© 2003 Eric Glass
//	http://davenport.sourceforge.net/ntlm.html
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Text;

namespace Titanium.Web.Proxy.Network.WinAuth.Security;

internal class Message
{
    private static readonly byte[] header = { 0x4e, 0x54, 0x4c, 0x4d, 0x53, 0x53, 0x50, 0x00 };

    private readonly int type;

    internal Message(byte[] message)
    {
        type = 3;

        if (message == null) throw new ArgumentNullException(nameof(message));

        if (message.Length < 12)
        {
            var msg = "Minimum Type3 message length is 12 bytes.";
            throw new ArgumentOutOfRangeException(nameof(message), message.Length, msg);
        }

        if (!CheckHeader(message))
        {
            var msg = "Invalid Type3 message header.";
            throw new ArgumentException(msg, nameof(message));
        }

        if (LittleEndian.ToUInt16(message, 56) != message.Length)
        {
            var msg = "Invalid Type3 message length.";
            throw new ArgumentException(msg, nameof(message));
        }

        if (message.Length >= 64)
            Flags = (Common.NtlmFlags)LittleEndian.ToUInt32(message, 60);
        else
            Flags = (Common.NtlmFlags)0x8201;

        int domLen = LittleEndian.ToUInt16(message, 28);
        int domOff = LittleEndian.ToUInt16(message, 32);

        Domain = DecodeString(message, domOff, domLen);

        int userLen = LittleEndian.ToUInt16(message, 36);
        int userOff = LittleEndian.ToUInt16(message, 40);

        Username = DecodeString(message, userOff, userLen);
    }

    /// <summary>
    ///     Domain name
    /// </summary>
    internal string Domain { get; }

    /// <summary>
    ///     Username
    /// </summary>
    internal string Username { get; }

    internal Common.NtlmFlags Flags { get; set; }

    private string DecodeString(byte[] buffer, int offset, int len)
    {
        if ((Flags & Common.NtlmFlags.NegotiateUnicode) != 0) return Encoding.Unicode.GetString(buffer, offset, len);

        return Encoding.ASCII.GetString(buffer, offset, len);
    }

    protected bool CheckHeader(byte[] message)
    {
        for (var i = 0; i < header.Length; i++)
            if (message[i] != header[i])
                return false;

        return LittleEndian.ToUInt32(message, 8) == type;
    }
}ParseOptions.0.jsonü
cD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\WinAuth\Security\State.cs¢using System;

namespace Titanium.Web.Proxy.Network.WinAuth.Security;

/// <summary>
///     Status of authenticated session
/// </summary>
internal class State
{
    /// <summary>
    ///     States during Windows Authentication
    /// </summary>
    public enum WinAuthState
    {
        Unauthorized,
        InitialToken,
        FinalToken,
        Authorized
    }

    /// <summary>
    ///     Current state of the authentication process
    /// </summary>
    internal WinAuthState AuthState;

    /// <summary>
    ///     Context will be used to validate HTLM hashes
    /// </summary>
    internal Common.SecurityHandle Context;

    /// <summary>
    ///     Credentials used to validate NTLM hashes
    /// </summary>
    internal Common.SecurityHandle Credentials;

    /// <summary>
    ///     Timestamp needed to calculate validity of the authenticated session
    /// </summary>
    internal DateTime LastSeen;

    internal State()
    {
        Credentials = new Common.SecurityHandle(0);
        Context = new Common.SecurityHandle(0);

        LastSeen = DateTime.UtcNow;
        AuthState = WinAuthState.Unauthorized;
    }

    internal void ResetHandles()
    {
        Credentials.Reset();
        Context.Reset();
        AuthState = WinAuthState.Unauthorized;
    }

    internal void UpdatePresence()
    {
        LastSeen = DateTime.UtcNow;
    }
}ParseOptions.0.jsonøN
mD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\WinAuth\Security\WinAuthEndPoint.cs∏M// http://pinvoke.net/default.aspx/secur32/InitializeSecurityContext.html

using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Network.WinAuth.Security;

using static Common;

internal class WinAuthEndPoint
{
    private const string AuthStateKey = "AuthState";

    /// <summary>
    ///     Acquire the intial client token to send
    /// </summary>
    /// <param name="hostname"></param>
    /// <param name="authScheme"></param>
    /// <param name="data"></param>
    /// <returns></returns>
    internal static byte[]? AcquireInitialSecurityToken(string hostname, string authScheme, InternalDataStore data,
        int attributes)
    {
        if (!RunTime.IsWindows) return null;

        byte[]? token;

        // null for initial call
        var serverToken = new SecurityBufferDescription();

        var clientToken = new SecurityBufferDescription(MaximumTokenSize);

        try
        {
            var state = new State();

            var result = AcquireCredentialsHandle(
                WindowsIdentity.GetCurrent().Name,
                authScheme,
                SecurityCredentialsOutbound,
                IntPtr.Zero,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                ref state.Credentials,
                ref NewLifeTime);

            if (result != SuccessfulResult) return null;

            result = InitializeSecurityContext(ref state.Credentials,
                IntPtr.Zero,
                hostname,
                attributes,
                0,
                SecurityNativeDataRepresentation,
                ref serverToken,
                0,
                out state.Context,
                out clientToken,
                out NewContextAttributes,
                out NewLifeTime);

            if (result != IntermediateResult && result != SuccessfulResult) return null;

            state.AuthState = result == SuccessfulResult
                ? State.WinAuthState.FinalToken
                : State.WinAuthState.InitialToken;
            token = clientToken.GetBytes();
            data.Add(AuthStateKey, state);
        }
        finally
        {
            DisposeToken(clientToken);
            DisposeToken(serverToken);
        }

        return token;
    }

    /// <summary>
    ///     Acquire the final token to send
    /// </summary>
    /// <param name="hostname"></param>
    /// <param name="serverChallenge"></param>
    /// <param name="data"></param>
    /// <returns></returns>
    internal static byte[]? AcquireFinalSecurityToken(string hostname, byte[] serverChallenge, InternalDataStore data,
        int attributes)
    {
        if (!RunTime.IsWindows) return null;

        byte[]? token;

        // user server challenge
        var serverToken = new SecurityBufferDescription(serverChallenge);

        var clientToken = new SecurityBufferDescription(MaximumTokenSize);

        try
        {
            var state = data.GetAs<State>(AuthStateKey);

            state.UpdatePresence();

            var result = InitializeSecurityContext(ref state.Credentials,
                ref state.Context,
                hostname,
                attributes,
                0,
                SecurityNativeDataRepresentation,
                ref serverToken,
                0,
                out state.Context,
                out clientToken,
                out NewContextAttributes,
                out NewLifeTime);

            // SuccessfulResult => authentication complete.
            // IntermediateResult => another leg is required (multi-round Negotiate).
            if (result != SuccessfulResult && result != IntermediateResult) return null;

            state.AuthState = result == SuccessfulResult
                ? State.WinAuthState.Authorized
                : State.WinAuthState.FinalToken;
            token = clientToken.GetBytes();
        }
        finally
        {
            DisposeToken(clientToken);
            DisposeToken(serverToken);
        }

        return token;
    }

    private static void DisposeToken(SecurityBufferDescription clientToken)
    {
        if (clientToken.pBuffers != IntPtr.Zero)
        {
            if (clientToken.cBuffers == 1)
            {
                var thisSecBuffer = Marshal.PtrToStructure<SecurityBuffer>(clientToken.pBuffers);
                DisposeSecBuffer(thisSecBuffer);
            }
            else
            {
                for (var index = 0; index < clientToken.cBuffers; index++)
                {
                    // The bits were written out the following order:
                    // int cbBuffer;
                    // int BufferType;
                    // pvBuffer;
                    // What we need to do here is to grab a hold of the pvBuffer allocate by the individual
                    // SecBuffer and release it...
                    var currentOffset = index * Marshal.SizeOf(typeof(SecurityBuffer));
                    var secBufferpvBuffer = Marshal.ReadIntPtr(clientToken.pBuffers,
                        currentOffset + Marshal.SizeOf(typeof(int)) + Marshal.SizeOf(typeof(int)));
                    Marshal.FreeHGlobal(secBufferpvBuffer);
                }
            }

            Marshal.FreeHGlobal(clientToken.pBuffers);
            clientToken.pBuffers = IntPtr.Zero;
        }
    }

    private static void DisposeSecBuffer(SecurityBuffer thisSecBuffer)
    {
        if (thisSecBuffer.pvBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(thisSecBuffer.pvBuffer);
            thisSecBuffer.pvBuffer = IntPtr.Zero;
        }
    }

    /// <summary>
    ///     Validates that the current WinAuth state of the connection matches the
    ///     expectation, used to detect failed authentication
    /// </summary>
    /// <param name="data"></param>
    /// <param name="expectedAuthState"></param>
    /// <returns></returns>
    internal static bool ValidateWinAuthState(InternalDataStore data, State.WinAuthState expectedAuthState)
    {
        var stateExists = data.TryGetValueAs(AuthStateKey, out State? state);

        if (expectedAuthState == State.WinAuthState.Unauthorized)
            return !stateExists ||
                   state!.AuthState == State.WinAuthState.Unauthorized ||
                   state.AuthState ==
                   State.WinAuthState.Authorized; // Server may require re-authentication on an open connection

        if (expectedAuthState == State.WinAuthState.InitialToken)
            return stateExists &&
                   (state!.AuthState == State.WinAuthState.InitialToken ||
                    state.AuthState ==
                    State.WinAuthState.Authorized); // Server may require re-authentication on an open connection

        if (expectedAuthState == State.WinAuthState.FinalToken)
            return stateExists &&
                   (state!.AuthState == State.WinAuthState.FinalToken ||
                    state.AuthState == State.WinAuthState.Authorized);

        throw new Exception("Unsupported validation of WinAuthState");
    }

    /// <summary>
    ///     Set the AuthState to authorized and update the connection state lifetime
    /// </summary>
    /// <param name="data"></param>
    internal static void AuthenticatedResponse(InternalDataStore data)
    {
        if (data.TryGetValueAs(AuthStateKey, out State? state))
        {
            state!.AuthState = State.WinAuthState.Authorized;
            state.UpdatePresence();
        }
    }

    #region Native calls to secur32.dll

    [DllImport("secur32.dll", SetLastError = true)]
    private static extern int InitializeSecurityContext(ref SecurityHandle phCredential, // PCredHandle
        IntPtr phContext, // PCtxtHandle
        string pszTargetName,
        int fContextReq,
        int reserved1,
        int targetDataRep,
        ref SecurityBufferDescription pInput, // PSecBufferDesc SecBufferDesc
        int reserved2,
        out SecurityHandle phNewContext, // PCtxtHandle
        out SecurityBufferDescription pOutput, // PSecBufferDesc SecBufferDesc
        out uint pfContextAttr, // managed ulong == 64 bits!!!
        out SecurityInteger ptsExpiry); // PTimeStamp

    [DllImport("secur32", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int InitializeSecurityContext(ref SecurityHandle phCredential, // PCredHandle
        ref SecurityHandle phContext, // PCtxtHandle
        string pszTargetName,
        int fContextReq,
        int reserved1,
        int targetDataRep,
        ref SecurityBufferDescription secBufferDesc, // PSecBufferDesc SecBufferDesc
        int reserved2,
        out SecurityHandle phNewContext, // PCtxtHandle
        out SecurityBufferDescription pOutput, // PSecBufferDesc SecBufferDesc
        out uint pfContextAttr, // managed ulong == 64 bits!!!
        out SecurityInteger ptsExpiry); // PTimeStamp

    [DllImport("secur32.dll", CharSet = CharSet.Auto, SetLastError = false)]
    private static extern int AcquireCredentialsHandle(
        string pszPrincipal, // SEC_CHAR*
        string pszPackage, // SEC_CHAR* // "Kerberos","NTLM","Negotiative"
        int fCredentialUse,
        IntPtr pAuthenticationId, // _LUID AuthenticationID,//pvLogonID, // PLUID
        IntPtr pAuthData, // PVOID
        int pGetKeyFn, // SEC_GET_KEY_FN
        IntPtr pvGetKeyArgument, // PVOID
        ref SecurityHandle phCredential, // SecHandle // PCtxtHandle ref
        ref SecurityInteger ptsExpiry); // PTimeStamp // TimeStamp ref

    #endregion
}ParseOptions.0.json˚
cD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\WinAuth\WinAuthHandler.cs˛using System;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Network.WinAuth.Security;

namespace Titanium.Web.Proxy.Network.WinAuth;

using static Common;

/// <summary>
///     A handler for NTLM/Kerberos windows authentication challenge from server
///     NTLM process details below
///     https://blogs.msdn.microsoft.com/chiranth/2013/09/20/ntlm-want-to-know-how-it-works/
/// </summary>
internal static class WinAuthHandler
{
    /// <summary>
    ///     Get the initial client token for server
    ///     using credentials of user running the proxy server process
    /// </summary>
    /// <param name="serverHostname"></param>
    /// <param name="authScheme"></param>
    /// <param name="data"></param>
    /// <returns></returns>
    internal static string GetInitialAuthToken(string serverHostname, string authScheme, InternalDataStore data)
    {
        var tokenBytes = WinAuthEndPoint.AcquireInitialSecurityToken(serverHostname, authScheme, data,
            IscReqConfidentiality | IscReqReplayDetect | IscReqSequenceDetect | IscReqConnection);
        if (tokenBytes == null) throw new InvalidOperationException("Failed to acquire the initial authentication token.");

        return string.Concat(" ", Convert.ToBase64String(tokenBytes));
    }

    /// <summary>
    ///     Get the final token given the server challenge token
    /// </summary>
    /// <param name="serverHostname"></param>
    /// <param name="serverToken"></param>
    /// <param name="data"></param>
    /// <returns></returns>
    internal static string GetFinalAuthToken(string serverHostname, string serverToken, InternalDataStore data)
    {
        var tokenBytes =
            WinAuthEndPoint.AcquireFinalSecurityToken(serverHostname, Convert.FromBase64String(serverToken),
                data, IscReqConfidentiality | IscReqReplayDetect | IscReqSequenceDetect | IscReqConnection);
        if (tokenBytes == null) throw new InvalidOperationException("Failed to acquire the final authentication token.");

        return string.Concat(" ", Convert.ToBase64String(tokenBytes));
    }

    /// <summary>
    ///     Get the initial authentication token for an upstream proxy using the current process identity.
    /// </summary>
    internal static string GetInitialProxyAuthToken(string proxyHostname, string authScheme, InternalDataStore data)
    {
        var tokenBytes = WinAuthEndPoint.AcquireInitialSecurityToken(proxyHostname, authScheme, data, 0);
        if (tokenBytes == null) throw new InvalidOperationException("Failed to acquire the initial proxy authentication token.");

        return string.Concat(" ", Convert.ToBase64String(tokenBytes));
    }

    /// <summary>
    ///     Get the response token for an upstream proxy challenge.
    /// </summary>
    internal static string GetFinalProxyAuthToken(string proxyHostname, string serverToken, InternalDataStore data)
    {
        var tokenBytes = WinAuthEndPoint.AcquireFinalSecurityToken(proxyHostname,
            Convert.FromBase64String(serverToken), data, 0);
        if (tokenBytes == null) throw new InvalidOperationException("Failed to acquire the final proxy authentication token.");

        return string.Concat(" ", Convert.ToBase64String(tokenBytes));
    }
}ParseOptions.0.jsonÄ
fD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\Writers\IHttpStreamWriter.csÄusing System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.StreamExtended.Network;

/// <summary>
///     A concrete implementation of this interface is required when calling CopyStream.
/// </summary>
public interface IHttpStreamWriter
{
    bool IsNetworkStream { get; }

    void Write(byte[] buffer, int offset, int count);

    Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);

    ValueTask WriteLineAsync(CancellationToken cancellationToken = default);

    ValueTask WriteLineAsync(string value, CancellationToken cancellationToken = default);
}ParseOptions.0.json˙
_D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Network\Writers\NullWriter.csÅusing System;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Helpers;

internal class NullWriter : IHttpStreamWriter
{
    private NullWriter()
    {
    }

    public static NullWriter Instance { get; } = new();

    public bool IsNetworkStream => false;

    public void Write(byte[] buffer, int offset, int count)
    {
    }

    public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public ValueTask WriteLineAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask WriteLineAsync(string value, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}ParseOptions.0.jsonç
\D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Properties\AssemblyInfo.csóusing System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.

[assembly: AssemblyTitle("Titanium.Web.Proxy")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("Titanium.Web.Proxy")]
[assembly: AssemblyCopyright("Copyright ¬© Titanium 2015-2020")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: InternalsVisibleTo("Titanium.Web.Proxy.UnitTests, PublicKey=" +
                              "0024000004800000940000000602000000240000525341310004000001000100e7368e0ccc717e" +
                              "eb4d57d35ad6a8305cbbed14faa222e13869405e92c83856266d400887d857005f1393ffca2b92" +
                              "de7f3ba0bdad35ec2d6057ee1846091b34be2abc3f97dc7e72c16fd4958c15126b12923df76964" +
                              "7d84922c3f4f3b80ee0ae8e4cb40bc1973b782afb90bb00519fd16adf960f217e23696e7c31654" +
                              "01d0acd6")]
[assembly: InternalsVisibleTo("Titanium.Web.Proxy.IntegrationTests, PublicKey=" +
                              "0024000004800000940000000602000000240000525341310004000001000100e7368e0ccc717e" +
                              "eb4d57d35ad6a8305cbbed14faa222e13869405e92c83856266d400887d857005f1393ffca2b92" +
                              "de7f3ba0bdad35ec2d6057ee1846091b34be2abc3f97dc7e72c16fd4958c15126b12923df76964" +
                              "7d84922c3f4f3b80ee0ae8e4cb40bc1973b782afb90bb00519fd16adf960f217e23696e7c31654" +
                              "01d0acd6")]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.

[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM

[assembly: Guid("5036e0b7-a0d0-4070-8eb0-72c129dee9b3")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version 
//      Build Number
//      Revision
//

[assembly: AssemblyVersion("1.0.1")]
[assembly: AssemblyFileVersion("1.0.1")]ParseOptions.0.json¸«
PD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\ProxyServer.csë«using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Helpers.WinHttp;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Network.WinAuth;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy;

/// <inheritdoc />
/// <summary>
///     This class is the backbone of proxy. One can create as many instances as needed.
///     However care should be taken to avoid using the same listening ports across multiple instances.
/// </summary>
public partial class ProxyServer : IDisposable
{
    /// <summary>
    ///     HTTP &amp; HTTPS scheme shorthands.
    /// </summary>
    internal static readonly string UriSchemeHttp = Uri.UriSchemeHttp;

    internal static readonly string UriSchemeHttps = Uri.UriSchemeHttps;

    internal static ByteString UriSchemeHttp8 = (ByteString)UriSchemeHttp;
    internal static ByteString UriSchemeHttps8 = (ByteString)UriSchemeHttps;

    /// <summary>
    ///     Backing field for exposed public property.
    /// </summary>
    private int clientConnectionCount;

    /// <summary>
    ///     Backing field for exposed public property.
    /// </summary>
    private ExceptionHandler? exceptionFunc;

    /// <summary>
    ///     Backing field for exposed public property.
    /// </summary>
    private int serverConnectionCount;

    /// <summary>
    ///     Upstream proxy manager.
    /// </summary>
    private WinHttpWebProxyFinder? systemProxyResolver;


    /// <inheritdoc />
    /// <summary>
    ///     Initializes a new instance of ProxyServer class with provided parameters.
    /// </summary>
    /// <param name="userTrustRootCertificate">
    ///     Should fake HTTPS certificate be trusted by this machine's user certificate
    ///     store?
    /// </param>
    /// <param name="machineTrustRootCertificate">Should fake HTTPS certificate be trusted by this machine's certificate store?</param>
    /// <param name="trustRootCertificateAsAdmin">
    ///     Should we attempt to trust certificates with elevated permissions by
    ///     prompting for UAC if required?
    /// </param>
    public ProxyServer(bool userTrustRootCertificate = true, bool machineTrustRootCertificate = false,
        bool trustRootCertificateAsAdmin = false) : this(null, null, userTrustRootCertificate,
        machineTrustRootCertificate, trustRootCertificateAsAdmin)
    {
    }

    /// <summary>
    ///     Initializes a new instance of ProxyServer class with provided parameters.
    /// </summary>
    /// <param name="rootCertificateName">Name of the root certificate.</param>
    /// <param name="rootCertificateIssuerName">Name of the root certificate issuer.</param>
    /// <param name="userTrustRootCertificate">
    ///     Should fake HTTPS certificate be trusted by this machine's user certificate
    ///     store?
    /// </param>
    /// <param name="machineTrustRootCertificate">Should fake HTTPS certificate be trusted by this machine's certificate store?</param>
    /// <param name="trustRootCertificateAsAdmin">
    ///     Should we attempt to trust certificates with elevated permissions by
    ///     prompting for UAC if required?
    /// </param>
    public ProxyServer(string? rootCertificateName, string? rootCertificateIssuerName,
        bool userTrustRootCertificate = true, bool machineTrustRootCertificate = false,
        bool trustRootCertificateAsAdmin = false)
    {
        BufferPool = new DefaultBufferPool();
        ProxyEndPoints = new List<ProxyEndPoint>();
        TcpConnectionFactory = new TcpConnectionFactory(this);
        if (RunTime.IsWindows && !RunTime.IsUwpOnWindows) SystemProxySettingsManager = new SystemProxyManager();

        CertificateManager = new CertificateManager(rootCertificateName, rootCertificateIssuerName,
            userTrustRootCertificate, machineTrustRootCertificate, trustRootCertificateAsAdmin, ExceptionFunc);
    }

    /// <summary>
    ///     An factory that creates tcp connection to server.
    /// </summary>
    private TcpConnectionFactory TcpConnectionFactory { get; }

    /// <summary>
    ///     Manage system proxy settings.
    /// </summary>
    private SystemProxyManager? SystemProxySettingsManager { get; }

    /// <summary>
    ///     Number of times to retry upon network failures when connection pool is enabled.
    /// </summary>
    public int NetworkFailureRetryAttempts { get; set; } = 1;

    /// <summary>
    ///     Is the proxy currently running?
    /// </summary>
    public bool ProxyRunning { get; private set; }

    /// <summary>
    ///     Gets or sets a value indicating whether requests will be chained to upstream gateway.
    ///     Defaults to false.
    /// </summary>
    public bool ForwardToUpstreamGateway { get; set; }

    /// <summary>
    ///     If set, the upstream proxy will be detected by a script that will be loaded from the provided Uri
    /// </summary>
    public Uri? UpstreamProxyConfigurationScript { get; set; }

    /// <summary>
    ///     Enable disable Windows Authentication (NTLM/Kerberos).
    ///     Note: NTLM/Kerberos will always send local credentials of current user
    ///     running the proxy process. This is because a man
    ///     in middle attack with Windows domain authentication is not currently supported.
    ///     Defaults to false.
    /// </summary>
    public bool EnableWinAuth { get; set; }

    /// <summary>
    ///     Overrides upstream proxy Windows authentication token generation.
    ///     Intended for internal testing; production uses the current process identity through SSPI.
    /// </summary>
    internal Func<IExternalProxy, string, string?, InternalDataStore, string?>?
        UpstreamProxyWinAuthTokenGenerator { get; set; }

    internal string? GenerateUpstreamProxyWinAuthToken(IExternalProxy proxy, string scheme, string? challenge,
        InternalDataStore data)
    {
        if (UpstreamProxyWinAuthTokenGenerator != null)
            return UpstreamProxyWinAuthTokenGenerator(proxy, scheme, challenge, data);

        // Negotiate/Kerberos require the service principal name of the proxy, not the bare host.
        var targetName = "HTTP/" + proxy.HostName;

        return challenge == null
            ? WinAuthHandler.GetInitialProxyAuthToken(targetName, scheme, data)
            : WinAuthHandler.GetFinalProxyAuthToken(targetName, challenge, data);
    }

    /// <summary>
    ///     Enable disable HTTP/2 support.
    ///     Warning: HTTP/2 support is very limited
    ///     - only enabled when both client and server supports it (no protocol changing in proxy)
    ///     - cannot modify the request/response (e.g header modifications in BeforeRequest/Response events are ignored)
    /// </summary>
    public bool EnableHttp2 { get; set; } = false;

    /// <summary>
    ///     Should we check for certificate revocation during SSL authentication to servers
    ///     Note: If enabled can reduce performance. Defaults to false.
    /// </summary>
    public X509RevocationMode CheckCertificateRevocation { get; set; }

    /// <summary>
    ///     Does this proxy uses the HTTP protocol 100 continue behaviour strictly?
    ///     Broken 100 continue implementations on server/client may cause problems if enabled.
    ///     Defaults to false.
    /// </summary>
    public bool Enable100ContinueBehaviour { get; set; }

    /// <summary>
    ///     Should we enable the server connection pool. Defaults to true.
    ///     When connection pooling is enabled, instead of creating a new TCP connection to the server for each client TCP
    ///     connection, we check if an idle server connection is available in our cached pool. If a compatible connection
    ///     (same destination, scheme, upstream proxy, credentials and negotiated protocol) created from an earlier request
    ///     is available, we reuse it. Only connections that are safe to reuse under the HTTP protocol are pooled:
    ///     the response body must be fully received and the connection must be persistent (HTTP/1.1 keep-alive, or an
    ///     HTTP/1.0 connection that explicitly opted in via "Connection: keep-alive"). Connections whose response asked to
    ///     close, that failed, or that carry connection-oriented authentication state (WinAuth NTLM/Negotiate) or a
    ///     per-session client certificate are never returned to the shared pool.
    ///     The ConnectionTimeOutSeconds parameter determines the eviction time for inactive server connections.
    ///     This reduces TCP (and TLS) connection establishment cost, both in wall clock time and CPU cycles.
    ///     Set to false to force a fresh server connection for every client connection.
    /// </summary>
    public bool EnableConnectionPool { get; set; } = true;

    /// <summary>
    ///     Should we enable tcp server connection prefetching?
    ///     When enabled, as soon as we receive a client connection we concurrently initiate
    ///     corresponding server connection process using CONNECT hostname or SNI hostname on a separate task so that after
    ///     parsing client request
    ///     we will have the server connection immediately ready or in the process of getting ready.
    ///     If a server connection is available in cache then this prefetch task will immediately return with the available
    ///     connection from cache.
    ///     Defaults to true.
    /// </summary>
    public bool EnableTcpServerConnectionPrefetch { get; set; } = true;

    /// <summary>
    ///     Gets or sets a Boolean value that specifies whether server and client stream Sockets are using the Nagle algorithm.
    ///     Defaults to true, no nagle algorithm is used.
    /// </summary>
    public bool NoDelay { get; set; } = true;

    /// <summary>
    ///     Seconds client/server connection are to be kept alive when waiting for read/write to complete.
    ///     This will also determine the pool eviction time when connection pool is enabled.
    ///     Default value is 60 seconds.
    /// </summary>
    public int ConnectionTimeOutSeconds { get; set; } = 60;

    /// <summary>
    ///     Seconds server connection are to wait for connection to be established.
    ///     Default value is 20 seconds.
    /// </summary>
    public int ConnectTimeOutSeconds { get; set; } = 20;

    /// <summary>
    ///     Maximum number of concurrent connections per remote host in cache.
    ///     Only valid when connection pooling is enabled.
    ///     Default value is 4.
    /// </summary>
    public int MaxCachedConnections { get; set; } = 4;

    /// <summary>
    ///     Number of seconds to linger when Tcp connection is in TIME_WAIT state.
    ///     Default value is 30.
    /// </summary>
    public int TcpTimeWaitSeconds { get; set; } = 30;

    /// <summary>
    ///     Should we reuse client/server tcp sockets.
    ///     Default is true (disabled for linux/macOS due to bug in .Net core).
    /// </summary>
    public bool ReuseSocket { get; set; } = true;

    /// <summary>
    ///     Total number of active client connections.
    /// </summary>
    public int ClientConnectionCount => clientConnectionCount;

    /// <summary>
    ///     Total number of active server connections.
    /// </summary>
    public int ServerConnectionCount => serverConnectionCount;

    /// <summary>
    ///     Realm used during Proxy Basic Authentication.
    /// </summary>
    public string ProxyAuthenticationRealm { get; set; } = "TitaniumProxy";

    /// <summary>
    ///     List of supported Ssl versions.
    /// </summary>
#pragma warning disable CS0618, SYSLIB0039 // SSL 3.0/TLS 1.0/1.1 remain opt-in defaults for legacy proxy compatibility.
    public SslProtocols SupportedSslProtocols { get; set; } =
        SslProtocols.Ssl3 | SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12
#if NET6_0_OR_GREATER
        | SslProtocols.Tls13
#endif
        ;
#pragma warning restore CS0618, SYSLIB0039

    /// <summary>
    ///     List of supported Server Ssl versions.
    ///     Using SslProtocol.None means to require the same SSL protocol as the proxy client.
    /// </summary>
    public SslProtocols SupportedServerSslProtocols { get; set; } = SslProtocols.None;

    /// <summary>
    ///     The buffer pool used throughout this proxy instance.
    ///     Set custom implementations by implementing this interface.
    ///     By default this uses DefaultBufferPool implementation available in StreamExtended library package.
    ///     Buffer size should be at least 10 bytes.
    /// </summary>
    public IBufferPool BufferPool { get; set; }

    /// <summary>
    ///     Manages certificates used by this proxy.
    /// </summary>
    public CertificateManager CertificateManager { get; }

    /// <summary>
    ///     External proxy used for Http requests.
    /// </summary>
    public IExternalProxy? UpStreamHttpProxy { get; set; }

    /// <summary>
    ///     External proxy used for Https requests.
    /// </summary>
    public IExternalProxy? UpStreamHttpsProxy { get; set; }

    /// <summary>
    ///     Local adapter/NIC endpoint where proxy makes request via.
    ///     Defaults via any IP addresses of this machine.
    /// </summary>
    public IPEndPoint? UpStreamEndPoint { get; set; }

    /// <summary>
    ///     A list of IpAddress and port this proxy is listening to.
    /// </summary>
    public List<ProxyEndPoint> ProxyEndPoints { get; set; }

    /// <summary>
    ///     A callback to provide authentication credentials for up stream proxy this proxy is using for HTTP(S) requests.
    ///     User should return the ExternalProxy object with valid credentials.
    /// </summary>
    public Func<SessionEventArgsBase, Task<IExternalProxy?>>? GetCustomUpStreamProxyFunc { get; set; }

    /// <summary>
    ///     A callback to provide a chance for an upstream proxy failure to be handled by a new upstream proxy.
    ///     User should return the ExternalProxy object with valid credentials or null.
    /// </summary>
    public Func<SessionEventArgsBase, Task<IExternalProxy?>>? CustomUpStreamProxyFailureFunc { get; set; }

    /// <summary>
    ///     Callback for error events in this proxy instance.
    /// </summary>
    public ExceptionHandler? ExceptionFunc
    {
        get => exceptionFunc;
        set
        {
            exceptionFunc = value;
            CertificateManager.ExceptionFunc = value;
        }
    }

    /// <summary>
    ///     A callback to authenticate proxy clients via basic authentication.
    ///     Parameters are username and password as provided by client.
    ///     Should return true for successful authentication.
    /// </summary>
    public Func<SessionEventArgsBase?, string, string, Task<bool>>? ProxyBasicAuthenticateFunc { get; set; }

    /// <summary>
    ///     A pluggable callback to authenticate clients by scheme instead of requiring basic authentication through
    ///     ProxyBasicAuthenticateFunc.
    ///     Parameters are current working session, schemeType, and token as provided by a calling client.
    ///     Should return success for successful authentication, continuation if the package requests, or failure.
    /// </summary>
    public Func<SessionEventArgsBase, string, string, Task<ProxyAuthenticationContext>>? ProxySchemeAuthenticateFunc
    {
        get;
        set;
    }

    /// <summary>
    ///     A collection of scheme types, e.g. basic, NTLM, Kerberos, Negotiate, to return if scheme authentication is
    ///     required.
    ///     Works in relation with ProxySchemeAuthenticateFunc.
    /// </summary>
    public IEnumerable<string> ProxyAuthenticationSchemes { get; set; } = new string[0];

    /// <summary>
    ///     Event occurs when client connection count changed.
    /// </summary>
    public event EventHandler? ClientConnectionCountChanged;

    /// <summary>
    ///     Event occurs when server connection count changed.
    /// </summary>
    public event EventHandler? ServerConnectionCountChanged;

    /// <summary>
    ///     Event to override the default verification logic of remote SSL certificate received during authentication.
    /// </summary>
    public event AsyncEventHandler<CertificateValidationEventArgs>? ServerCertificateValidationCallback;

    /// <summary>
    ///     Event to override client certificate selection during mutual SSL authentication.
    /// </summary>
    public event AsyncEventHandler<CertificateSelectionEventArgs>? ClientCertificateSelectionCallback;

    /// <summary>
    ///     Intercept request event to server.
    /// </summary>
    public event AsyncEventHandler<SessionEventArgs>? BeforeRequest;

    /// <summary>
    ///     Intercept request body send event to server.
    ///     Subscribe to inspect or modify the request body chunk-by-chunk as it streams to the server,
    ///     without buffering the whole body. Do not combine with SessionEventArgs.GetRequestBody (which buffers).
    /// </summary>
    public event AsyncEventHandler<BeforeBodyWriteEventArgs>? OnRequestBodyWrite;

    /// <summary>
    ///     Intercept response event from server.
    /// </summary>
    public event AsyncEventHandler<SessionEventArgs>? BeforeResponse;

    /// <summary>
    ///     Intercept response body send event to client.
    ///     Subscribe to inspect or modify the response body chunk-by-chunk as it streams to the client,
    ///     without buffering the whole body. Do not combine with SessionEventArgs.GetResponseBody (which buffers).
    /// </summary>
    public event AsyncEventHandler<BeforeBodyWriteEventArgs>? OnResponseBodyWrite;

    /// <summary>
    ///     Intercept after response event from server.
    /// </summary>
    public event AsyncEventHandler<SessionEventArgs>? AfterResponse;

    /// <summary>
    ///     Customize TcpClient used for client connection upon create.
    /// </summary>
    public event AsyncEventHandler<Socket>? OnClientConnectionCreate;

    /// <summary>
    ///     Customize TcpClient used for server connection upon create.
    /// </summary>
    public event AsyncEventHandler<Socket>? OnServerConnectionCreate;

    /// <summary>
    ///     Intercept connect request sent to upstream proxy.
    /// </summary>
    public event AsyncEventHandler<ConnectRequest>? BeforeUpStreamConnectRequest;

    /// <summary>
    ///     Customize the minimum ThreadPool size (increase it on a server)
    /// </summary>
    public int ThreadPoolWorkerThread { get; set; } = Environment.ProcessorCount;

    /// <summary>
    ///     Add a proxy end point.
    /// </summary>
    /// <param name="endPoint">The proxy endpoint.</param>
    public void AddEndPoint(ProxyEndPoint endPoint)
    {
        if (ProxyEndPoints.Any(x =>
                x.IpAddress.Equals(endPoint.IpAddress) && endPoint.Port != 0 && x.Port == endPoint.Port))
            throw new Exception("Cannot add another endpoint to same port & ip address");

        ProxyEndPoints.Add(endPoint);

        if (ProxyRunning) Listen(endPoint);
    }

    /// <summary>
    ///     Remove a proxy end point.
    ///     Will throw error if the end point doesn't exist.
    /// </summary>
    /// <param name="endPoint">The existing endpoint to remove.</param>
    public void RemoveEndPoint(ProxyEndPoint endPoint)
    {
        if (ProxyEndPoints.Contains(endPoint) == false)
            throw new Exception("Cannot remove endPoints not added to proxy");

        ProxyEndPoints.Remove(endPoint);

        if (ProxyRunning) QuitListen(endPoint);
    }

    /// <summary>
    ///     Set the given explicit end point as the default proxy server for current machine.
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    public void SetAsSystemHttpProxy(ExplicitProxyEndPoint endPoint)
    {
        SetAsSystemProxy(endPoint, ProxyProtocolType.Http);
    }

    /// <summary>
    ///     Set the given explicit end point as the default HTTP proxy server for current machine.
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    /// <param name="settings">The Windows system proxy settings.</param>
    public void SetAsSystemHttpProxy(ExplicitProxyEndPoint endPoint, SystemProxySettings settings)
    {
        SetAsSystemProxy(endPoint, ProxyProtocolType.Http, settings);
    }

    /// <summary>
    ///     Set the given explicit end point as the default proxy server for current machine.
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    public void SetAsSystemHttpsProxy(ExplicitProxyEndPoint endPoint)
    {
        SetAsSystemProxy(endPoint, ProxyProtocolType.Https);
    }

    /// <summary>
    ///     Set the given explicit end point as the default HTTPS proxy server for current machine.
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    /// <param name="settings">The Windows system proxy settings.</param>
    public void SetAsSystemHttpsProxy(ExplicitProxyEndPoint endPoint, SystemProxySettings settings)
    {
        SetAsSystemProxy(endPoint, ProxyProtocolType.Https, settings);
    }

    /// <summary>
    ///     Set the given explicit end point as the default proxy server for current machine.
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    /// <param name="protocolType">The proxy protocol type.</param>
    public void SetAsSystemProxy(ExplicitProxyEndPoint endPoint, ProxyProtocolType protocolType)
    {
        SetAsSystemProxy(endPoint, protocolType, null);
    }

    /// <summary>
    ///     Set the given explicit end point as the default proxy server for current machine.
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    /// <param name="protocolType">The proxy protocol type.</param>
    /// <param name="settings">
    ///     The Windows system proxy settings, or <see langword="null"/> to preserve the current bypass list.
    /// </param>
    public void SetAsSystemProxy(ExplicitProxyEndPoint endPoint, ProxyProtocolType protocolType,
        SystemProxySettings? settings)
    {
        if (!RunTime.IsWindows || SystemProxySettingsManager == null)
            throw new NotSupportedException(@"Setting system proxy settings are only supported in Windows.
                            Please manually configure you operating system to use this proxy's port and address.");

        ValidateEndPointAsSystemProxy(endPoint);

        // Validate bypass rules up front so a malformed rule cannot leave the proxy state half-applied.
        settings?.Validate();

        var isHttp = (protocolType & ProxyProtocolType.Http) > 0;
        var isHttps = (protocolType & ProxyProtocolType.Https) > 0;

        if (isHttps)
        {
            CertificateManager.EnsureRootCertificate();

            // If certificate was trusted by the machine
            if (!CertificateManager.CertValidated)
            {
                protocolType = protocolType & ~ProxyProtocolType.Https;
                isHttps = false;
            }
        }

        // clear any settings previously added
        if (isHttp) ProxyEndPoints.OfType<ExplicitProxyEndPoint>().ToList().ForEach(x => x.IsSystemHttpProxy = false);

        if (isHttps) ProxyEndPoints.OfType<ExplicitProxyEndPoint>().ToList().ForEach(x => x.IsSystemHttpsProxy = false);

        string? proxyOverride = null;
        if (settings != null)
        {
            var currentProxyOverride = SystemProxySettingsManager.GetProxyInfoFromRegistry()?.ProxyOverride;
            proxyOverride = settings.BuildProxyOverride(currentProxyOverride);
        }

        SystemProxySettingsManager.SetProxy(
            Equals(endPoint.IpAddress, IPAddress.Any) |
            Equals(endPoint.IpAddress, IPAddress.Loopback)
                ? "localhost"
                : endPoint.IpAddress.ToString(),
            endPoint.Port,
            protocolType,
            proxyOverride);

        if (isHttp) endPoint.IsSystemHttpProxy = true;

        if (isHttps) endPoint.IsSystemHttpsProxy = true;

        string? proxyType = null;
        switch (protocolType)
        {
            case ProxyProtocolType.Http:
                proxyType = "HTTP";
                break;
            case ProxyProtocolType.Https:
                proxyType = "HTTPS";
                break;
            case ProxyProtocolType.AllHttp:
                proxyType = "HTTP and HTTPS";
                break;
        }

        if (protocolType != ProxyProtocolType.None)
            Console.WriteLine("Set endpoint at Ip {0} and port: {1} as System {2} Proxy", endPoint.IpAddress,
                endPoint.Port, proxyType);
    }

    /// <summary>
    ///     Clear HTTP proxy settings of current machine.
    /// </summary>
    public void DisableSystemHttpProxy()
    {
        DisableSystemProxy(ProxyProtocolType.Http);
    }

    /// <summary>
    ///     Clear HTTPS proxy settings of current machine.
    /// </summary>
    public void DisableSystemHttpsProxy()
    {
        DisableSystemProxy(ProxyProtocolType.Https);
    }

    /// <summary>
    ///     Restores the original proxy settings.
    /// </summary>
    public void RestoreOriginalProxySettings()
    {
        if (!RunTime.IsWindows || SystemProxySettingsManager == null)
            throw new NotSupportedException(@"Setting system proxy settings are only supported in Windows.
                            Please manually configure your operating system to use this proxy's port and address.");

        SystemProxySettingsManager.RestoreOriginalSettings();
    }

    /// <summary>
    ///     Clear the specified proxy setting for current machine.
    /// </summary>
    public void DisableSystemProxy(ProxyProtocolType protocolType)
    {
        if (!RunTime.IsWindows || SystemProxySettingsManager == null)
            throw new NotSupportedException(@"Setting system proxy settings are only supported in Windows.
                            Please manually configure your operating system to use this proxy's port and address.");

        SystemProxySettingsManager.RemoveProxy(protocolType);
    }

    /// <summary>
    ///     Clear all proxy settings for current machine.
    /// </summary>
    public void DisableAllSystemProxies()
    {
        if (!RunTime.IsWindows || SystemProxySettingsManager == null)
            throw new NotSupportedException(@"Setting system proxy settings are only supported in Windows.
                            Please manually confugure you operating system to use this proxy's port and address.");

        SystemProxySettingsManager.DisableAllProxy();
    }

    /// <summary>
    ///     Start this proxy server instance.
    /// </summary>
    /// <param name="changeSystemProxySettings">
    ///     Whether or not clear any system proxy settings which is pointing to our own endpoint (causing a cycle).
    ///     E.g due to ungracious proxy shutdown before.
    /// </param>
    public void Start(bool changeSystemProxySettings = true)
    {
        if (ProxyRunning) throw new Exception("Proxy is already running.");

        SetThreadPoolMinThread(ThreadPoolWorkerThread);

        if (ProxyEndPoints.OfType<ExplicitProxyEndPoint>().Any(x => x.GenericCertificate == null))
            CertificateManager.EnsureRootCertificate();

        if (changeSystemProxySettings && SystemProxySettingsManager != null && RunTime.IsWindows &&
            !RunTime.IsUwpOnWindows)
        {
            var proxyInfo = SystemProxySettingsManager.GetProxyInfoFromRegistry();
            if (proxyInfo?.Proxies != null)
            {
                var protocolToRemove = ProxyProtocolType.None;
                foreach (var proxy in proxyInfo.Proxies.Values)
                    if (NetworkHelper.IsLocalIpAddress(proxy.HostName)
                        && ProxyEndPoints.Any(x => x.Port == proxy.Port))
                        protocolToRemove |= proxy.ProtocolType;

                if (protocolToRemove != ProxyProtocolType.None)
                    SystemProxySettingsManager.RemoveProxy(protocolToRemove, false);
            }
        }

        if (RunTime.IsWindows && ForwardToUpstreamGateway && GetCustomUpStreamProxyFunc == null &&
            SystemProxySettingsManager != null)
        {
            systemProxyResolver = new WinHttpWebProxyFinder();
            if (UpstreamProxyConfigurationScript != null)
                //Use the provided proxy configuration script
                systemProxyResolver.UsePacFile(UpstreamProxyConfigurationScript);
            else
                // Use WinHttp to handle PAC/WAPD scripts.
                systemProxyResolver.LoadFromIe();

            GetCustomUpStreamProxyFunc = GetSystemUpStreamProxy;
        }

        ProxyRunning = true;

        CertificateManager.ClearIdleCertificates();

        foreach (var endPoint in ProxyEndPoints) Listen(endPoint);
    }

    /// <summary>
    ///     Stop this proxy server instance.
    /// </summary>
    public void Stop()
    {
        if (!ProxyRunning) throw new Exception("Proxy is not running.");

        if (RunTime.IsWindows && SystemProxySettingsManager != null)
        {
            var setAsSystemProxy = ProxyEndPoints.OfType<ExplicitProxyEndPoint>()
                .Any(x => x.IsSystemHttpProxy || x.IsSystemHttpsProxy);

            if (setAsSystemProxy) SystemProxySettingsManager.RestoreOriginalSettings();
        }

        // Prevent accept callbacks from scheduling another accept while listeners are stopping.
        ProxyRunning = false;

        foreach (var endPoint in ProxyEndPoints) QuitListen(endPoint);

        ProxyEndPoints.Clear();

        CertificateManager?.StopClearIdleCertificates();
        TcpConnectionFactory.Dispose();

    }

    /// <summary>
    ///     Listen on given end point of local machine.
    /// </summary>
    /// <param name="endPoint">The end point to listen.</param>
    private void Listen(ProxyEndPoint endPoint)
    {
        endPoint.Listener = new TcpListener(endPoint.IpAddress, endPoint.Port);

        if (ReuseSocket && RunTime.IsSocketReuseAvailable())
            endPoint.Listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        try
        {
            endPoint.Listener.Start();

            endPoint.Port = ((IPEndPoint)endPoint.Listener.LocalEndpoint).Port;

            // accept clients asynchronously
            endPoint.Listener.BeginAcceptSocket(OnAcceptConnection, endPoint);
        }
        catch (SocketException ex)
        {
            var pex = new Exception(
                $"Endpoint {endPoint} failed to start. Check inner exception and exception data for details.", ex);
            pex.Data.Add("ipAddress", endPoint.IpAddress);
            pex.Data.Add("port", endPoint.Port);
            throw pex;
        }
    }

    /// <summary>
    ///     Verify if its safe to set this end point as system proxy.
    /// </summary>
    /// <param name="endPoint">The end point to validate.</param>
    private void ValidateEndPointAsSystemProxy(ExplicitProxyEndPoint endPoint)
    {
        if (endPoint == null) throw new ArgumentNullException(nameof(endPoint));

        if (!ProxyEndPoints.Contains(endPoint))
            throw new Exception("Cannot set endPoints not added to proxy as system proxy");

        if (!ProxyRunning) throw new Exception("Cannot set system proxy settings before proxy has been started.");
    }

    /// <summary>
    ///     Gets the system up stream proxy.
    /// </summary>
    /// <param name="sessionEventArgs">The session.</param>
    /// <returns>The external proxy as task result.</returns>
    private Task<IExternalProxy?> GetSystemUpStreamProxy(SessionEventArgsBase sessionEventArgs)
    {
        if (!RunTime.IsWindows)
            throw new PlatformNotSupportedException("System upstream proxy discovery is only supported on Windows.");

        var proxy = systemProxyResolver!.GetProxy(sessionEventArgs.HttpClient.Request.RequestUri);
        return Task.FromResult(proxy);
    }

    /// <summary>
    ///     Act when a connection is received from client.
    /// </summary>
    private void OnAcceptConnection(IAsyncResult asyn)
    {
        var endPoint = (ProxyEndPoint)asyn.AsyncState!;
        var listener = endPoint.Listener!;

        Socket? tcpClient = null;
        var listenerDisposed = false;

        try
        {
            tcpClient = listener.EndAcceptSocket(asyn);
        }
        catch (ObjectDisposedException)
        {
            // The listener was Stop()'d, disposing the underlying socket and
            // triggering the completion of the callback. We're already exiting.
            listenerDisposed = true;
        }
        catch (Exception ex)
        {
            // Errors here (e.g. transient socket errors under heavy load) are
            // reported but must not prevent re-arming the accept loop below.
            OnException(null, ex);
        }

        // Re-arm the accept loop as early as possible (before dispatching the
        // just-accepted client) so bursts of near-simultaneous connections are
        // drained from the backlog without delay.
        if (!listenerDisposed) BeginAcceptConnection(endPoint, listener);

        if (tcpClient != null)
        {
            if (ProxyRunning)
            {
                try
                {
                    tcpClient.NoDelay = NoDelay;
                }
                catch (Exception ex)
                {
                    OnException(null, ex);
                }

                var acceptedClient = tcpClient;
                Task.Run(async () => { await HandleClient(acceptedClient, endPoint); });
            }
            else
                tcpClient.Dispose();
        }
    }

    /// <summary>
    ///     (Re)arms the accept loop for the given end point.
    ///     Any exception thrown by <see cref="TcpListener.BeginAcceptSocket" /> (e.g. transient
    ///     resource exhaustion under heavy connection load) is caught and retried instead of being
    ///     allowed to escape the async I/O completion callback, which would otherwise crash the
    ///     process or silently stop the proxy from accepting any further connections.
    /// </summary>
    private void BeginAcceptConnection(ProxyEndPoint endPoint, TcpListener listener)
    {
        if (!ProxyRunning) return;

        try
        {
            listener.BeginAcceptSocket(OnAcceptConnection, endPoint);
        }
        catch (Exception ex) when (ex is ObjectDisposedException || ex is InvalidOperationException)
        {
            // The listener was Stop()'d, disposing the underlying socket and
            // triggering the completion of the callback. We're already exiting,
            // so just return.
        }
        catch (Exception ex)
        {
            OnException(null, ex);

            // Retry shortly instead of permanently abandoning the accept loop.
            _ = Task.Run(async () =>
            {
                await Task.Delay(100).ConfigureAwait(false);
                BeginAcceptConnection(endPoint, listener);
            });
        }
    }


    /// <summary>
    ///     Change the ThreadPool.WorkerThread minThread
    /// </summary>
    /// <param name="workerThreads">minimum Threads allocated in the ThreadPool</param>
    private void SetThreadPoolMinThread(int workerThreads)
    {
        ThreadPool.GetMinThreads(out var minWorkerThreads, out var minCompletionPortThreads);
        ThreadPool.GetMaxThreads(out var maxWorkerThreads, out _);

        minWorkerThreads = Math.Min(maxWorkerThreads, Math.Max(workerThreads, Environment.ProcessorCount));

        ThreadPool.SetMinThreads(minWorkerThreads, minCompletionPortThreads);
    }


    /// <summary>
    ///     Handle the client.
    /// </summary>
    /// <param name="tcpClientSocket">The client socket.</param>
    /// <param name="endPoint">The proxy endpoint.</param>
    /// <returns>The task.</returns>
    private async Task HandleClient(Socket tcpClientSocket, ProxyEndPoint endPoint)
    {
        tcpClientSocket.ReceiveTimeout = ConnectionTimeOutSeconds * 1000;
        tcpClientSocket.SendTimeout = ConnectionTimeOutSeconds * 1000;

        tcpClientSocket.LingerState = new LingerOption(true, TcpTimeWaitSeconds);

        await InvokeClientConnectionCreateEvent(tcpClientSocket);

        using (var clientConnection = new TcpClientConnection(this, tcpClientSocket))
        {
            if (endPoint is ExplicitProxyEndPoint eep)
                await HandleClient(eep, clientConnection);
            else if (endPoint is TransparentProxyEndPoint tep)
                await HandleClient(tep, clientConnection);
            else if (endPoint is SocksProxyEndPoint sep) await HandleClient(sep, clientConnection);
        }
    }

    /// <summary>
    ///     Handle exception.
    /// </summary>
    /// <param name="clientStream">The client stream.</param>
    /// <param name="exception">The exception.</param>
    private void OnException(HttpClientStream? clientStream, Exception exception)
    {
        ExceptionFunc?.Invoke(exception);
    }

    /// <summary>
    ///     Quit listening on the given end point.
    /// </summary>
    private void QuitListen(ProxyEndPoint endPoint)
    {
        var listener = endPoint.Listener;
        if (listener == null) return;

        listener.Stop();
        listener.Server.Dispose();
    }

    /// <summary>
    ///     Update client connection count.
    /// </summary>
    /// <param name="increment">Should we increment/decrement?</param>
    internal void UpdateClientConnectionCount(bool increment)
    {
        if (increment)
            Interlocked.Increment(ref clientConnectionCount);
        else
            Interlocked.Decrement(ref clientConnectionCount);

        try
        {
            ClientConnectionCountChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            OnException(null, ex);
        }
    }

    /// <summary>
    ///     Update server connection count.
    /// </summary>
    /// <param name="increment">Should we increment/decrement?</param>
    internal void UpdateServerConnectionCount(bool increment)
    {
        if (increment)
            Interlocked.Increment(ref serverConnectionCount);
        else
            Interlocked.Decrement(ref serverConnectionCount);

        try
        {
            ServerConnectionCountChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            OnException(null, ex);
        }
    }

    /// <summary>
    ///     Invoke client tcp connection events if subscribed by API user.
    /// </summary>
    /// <param name="clientSocket">The TcpClient object.</param>
    /// <returns></returns>
    internal async Task InvokeClientConnectionCreateEvent(Socket clientSocket)
    {
        // client connection created
        if (OnClientConnectionCreate != null)
            await OnClientConnectionCreate.InvokeAsync(this, clientSocket, ExceptionFunc);
    }

    /// <summary>
    ///     Invoke server tcp connection events if subscribed by API user.
    /// </summary>
    /// <param name="serverSocket">The Socket object.</param>
    /// <returns></returns>
    internal async Task InvokeServerConnectionCreateEvent(Socket serverSocket)
    {
        // server connection created
        if (OnServerConnectionCreate != null)
            await OnServerConnectionCreate.InvokeAsync(this, serverSocket, ExceptionFunc);
    }

    /// <summary>
    ///     Connection retry policy when using connection pool.
    /// </summary>
    private RetryPolicy<T> RetryPolicy<T>() where T : Exception
    {
        return new RetryPolicy<T>(NetworkFailureRetryAttempts, TcpConnectionFactory);
    }

    private bool disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (disposed) return;

        disposed = true;

        if (ProxyRunning)
            try
            {
                Stop();
            }
            catch
            {
                // ignore
            }

        if (disposing)
        {
            CertificateManager?.Dispose();
            BufferPool?.Dispose();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~ProxyServer()
    {
        Dispose(false);
    }
}ParseOptions.0.json⁄'
jD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\ProxySocket\Authentication\AuthMethod.cs÷&/*
    Copyright ¬© 2002, The KPD-Team
    All rights reserved.
    http://www.mentalis.org/

  Redistribution and use in source and binary forms, with or without
  modification, are permitted provided that the following conditions
  are met:

    - Redistributions of source code must retain the above copyright
       notice, this list of conditions and the following disclaimer. 

    - Neither the name of the KPD-Team, nor the names of its contributors
       may be used to endorse or promote products derived from this
       software without specific prior written permission. 

  THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
  "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
  LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
  FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL
  THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
  INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
  (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
  SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
  HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
  STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
  ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
  OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;
using System.Net;
using System.Net.Sockets;

namespace Titanium.Web.Proxy.ProxySocket.Authentication;

/// <summary>
///     Implements a SOCKS authentication scheme.
/// </summary>
/// <remarks>This is an abstract class; it must be inherited.</remarks>
internal abstract class AuthMethod
{
    /// <summary>Holds the address of the method to call when the proxy has authenticated the client.</summary>
    private HandShakeComplete? callback;
    private byte[]? buffer;

    // private variables

    /// <summary>Holds the value of the Server property.</summary>
    private Socket server;

    /// <summary>
    ///     Initializes an AuthMethod instance.
    /// </summary>
    /// <param name="server">The socket connection with the proxy server.</param>
    public AuthMethod(Socket server)
    {
        this.server = server ?? throw new ArgumentNullException(nameof(server));
    }

    /// <summary>
    ///     Gets or sets the socket connection with the proxy server.
    /// </summary>
    /// <value>The socket connection with the proxy server.</value>
    protected Socket Server
    {
        get => server;
        set => server = value ?? throw new ArgumentNullException();
    }

    /// <summary>
    ///     Gets or sets a byt array that can be used to store data.
    /// </summary>
    /// <value>A byte array to store data.</value>
    protected HandShakeComplete CallBack
    {
        get => callback ?? throw new InvalidOperationException("Authentication callback has not been assigned.");
        set => callback = value ?? throw new ArgumentNullException(nameof(value));
    }

    protected byte[] Buffer
    {
        get => buffer ?? throw new InvalidOperationException("Authentication buffer has not been assigned.");
        set => buffer = value ?? throw new ArgumentNullException(nameof(value));
    }

    protected byte[] TakeBuffer()
    {
        var value = Buffer;
        buffer = null;
        return value;
    }

    /// <summary>
    ///     Gets or sets the number of bytes that have been received from the remote proxy server.
    /// </summary>
    /// <value>An integer that holds the number of bytes that have been received from the remote proxy server.</value>
    protected int Received { get; set; }

    /// <summary>
    ///     Authenticates the user.
    /// </summary>
    /// <exception cref="ProxyException">Authentication with the proxy server failed.</exception>
    /// <exception cref="ProtocolViolationException">The proxy server uses an invalid protocol.</exception>
    /// <exception cref="SocketException">An operating system error occurs while accessing the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    public abstract void Authenticate();

    /// <summary>
    ///     Authenticates the user asynchronously.
    /// </summary>
    /// <param name="callback">The method to call when the authentication is complete.</param>
    /// <exception cref="ProxyException">Authentication with the proxy server failed.</exception>
    /// <exception cref="ProtocolViolationException">The proxy server uses an invalid protocol.</exception>
    /// <exception cref="SocketException">An operating system error occurs while accessing the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    public abstract void BeginAuthenticate(HandShakeComplete callback);
}ParseOptions.0.jsonÃ
hD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\ProxySocket\Authentication\AuthNone.cs /*
    Copyright ¬© 2002, The KPD-Team
    All rights reserved.
    http://www.mentalis.org/

  Redistribution and use in source and binary forms, with or without
  modification, are permitted provided that the following conditions
  are met:

    - Redistributions of source code must retain the above copyright
       notice, this list of conditions and the following disclaimer. 

    - Neither the name of the KPD-Team, nor the names of its contributors
       may be used to endorse or promote products derived from this
       software without specific prior written permission. 

  THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
  "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
  LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
  FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL
  THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
  INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
  (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
  SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
  HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
  STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
  ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
  OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System.Net.Sockets;

namespace Titanium.Web.Proxy.ProxySocket.Authentication;

/// <summary>
///     This class implements the 'No Authentication' scheme.
/// </summary>
internal sealed class AuthNone : AuthMethod
{
    /// <summary>
    ///     Initializes an AuthNone instance.
    /// </summary>
    /// <param name="server">The socket connection with the proxy server.</param>
    public AuthNone(Socket server) : base(server)
    {
    }

    /// <summary>
    ///     Authenticates the user.
    /// </summary>
    public override void Authenticate()
    {
    }

    /// <summary>
    ///     Authenticates the user asynchronously.
    /// </summary>
    /// <param name="callback">The method to call when the authentication is complete.</param>
    /// <remarks>This method immediately calls the callback method.</remarks>
    public override void BeginAuthenticate(HandShakeComplete callback)
    {
        callback(null);
    }
}ParseOptions.0.json‰;
lD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\ProxySocket\Authentication\AuthUserPass.csﬁ:/*
    Copyright ¬© 2002, The KPD-Team
    All rights reserved.
    http://www.mentalis.org/

  Redistribution and use in source and binary forms, with or without
  modification, are permitted provided that the following conditions
  are met:

    - Redistributions of source code must retain the above copyright
       notice, this list of conditions and the following disclaimer. 

    - Neither the name of the KPD-Team, nor the names of its contributors
       may be used to endorse or promote products derived from this
       software without specific prior written permission. 

  THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
  "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
  LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
  FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL
  THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
  INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
  (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
  SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
  HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
  STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
  ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
  OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;
using System.Buffers;
using System.Net.Sockets;
using System.Text;

namespace Titanium.Web.Proxy.ProxySocket.Authentication;

/// <summary>
///     This class implements the 'username/password authentication' scheme.
/// </summary>
internal sealed class AuthUserPass : AuthMethod
{
    /// <summary>Holds the value of the Password property.</summary>
    private string password = string.Empty;

    // private variables
    /// <summary>Holds the value of the Username property.</summary>
    private string username = string.Empty;

    /// <summary>
    ///     Initializes a new AuthUserPass instance.
    /// </summary>
    /// <param name="server">The socket connection with the proxy server.</param>
    /// <param name="user">The username to use.</param>
    /// <param name="pass">The password to use.</param>
    /// <exception cref="ArgumentNullException"><c>user</c> -or- <c>pass</c> is null.</exception>
    public AuthUserPass(Socket server, string user, string pass) : base(server)
    {
        Username = user;
        Password = pass;
    }

    /// <summary>
    ///     Gets or sets the username to use when authenticating with the proxy server.
    /// </summary>
    /// <value>The username to use when authenticating with the proxy server.</value>
    /// <exception cref="ArgumentNullException">The specified value is null.</exception>
    private string Username
    {
        get => username;
        set => username = value ?? throw new ArgumentNullException();
    }

    /// <summary>
    ///     Gets or sets the password to use when authenticating with the proxy server.
    /// </summary>
    /// <value>The password to use when authenticating with the proxy server.</value>
    /// <exception cref="ArgumentNullException">The specified value is null.</exception>
    private string Password
    {
        get => password;
        set => password = value ?? throw new ArgumentNullException();
    }

    /// <summary>
    ///     Creates an array of bytes that has to be sent if the user wants to authenticate with the username/password
    ///     authentication scheme.
    /// </summary>
    /// <returns>
    ///     An array of bytes that has to be sent if the user wants to authenticate with the username/password
    ///     authentication scheme.
    /// </returns>
    private void GetAuthenticationBytes(Memory<byte> buffer)
    {
        var span = buffer.Span;
        span[0] = 1;
        span[1] = (byte)Username.Length;
        Encoding.ASCII.GetBytes(Username).CopyTo(span.Slice(2));
        span[Username.Length + 2] = (byte)Password.Length;
        Encoding.ASCII.GetBytes(Password).CopyTo(span.Slice(Username.Length + 3));
    }

    private int GetAuthenticationLength()
    {
        return 3 + Username.Length + Password.Length;
    }

    /// <summary>
    ///     Starts the authentication process.
    /// </summary>
    public override void Authenticate()
    {
        var length = GetAuthenticationLength();
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            GetAuthenticationBytes(buffer);
            if (Server.Send(buffer, 0, length, SocketFlags.None) < length) throw new SocketException(10054);

            var received = 0;
            while (received != 2)
            {
                var recv = Server.Receive(buffer, received, 2 - received, SocketFlags.None);
                if (recv == 0)
                    throw new SocketException(10054);

                received += recv;
            }

            if (buffer[1] == 0) return;

            Server.Close();
            throw new ProxyException("Username/password combination rejected.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///     Starts the asynchronous authentication process.
    /// </summary>
    /// <param name="callback">The method to call when the authentication is complete.</param>
    public override void BeginAuthenticate(HandShakeComplete callback)
    {
        var length = GetAuthenticationLength();
        Buffer = ArrayPool<byte>.Shared.Rent(length);
        GetAuthenticationBytes(Buffer);
        CallBack = callback;
        Server.BeginSend(Buffer, 0, length, SocketFlags.None, OnSent, Server);
    }

    /// <summary>
    ///     Called when the authentication bytes have been sent.
    /// </summary>
    /// <param name="ar">Stores state information for this asynchronous operation as well as any user-defined data.</param>
    private void OnSent(IAsyncResult ar)
    {
        try
        {
            if (Server.EndSend(ar) < GetAuthenticationLength())
                throw new SocketException(10054);

            Server.BeginReceive(Buffer, 0, 2, SocketFlags.None, OnReceive, Server);
        }
        catch (Exception e)
        {
            OnCallBack(e);
        }
    }

    /// <summary>
    ///     Called when the socket received an authentication reply.
    /// </summary>
    /// <param name="ar">Stores state information for this asynchronous operation as well as any user-defined data.</param>
    private void OnReceive(IAsyncResult ar)
    {
        try
        {
            var recv = Server.EndReceive(ar);
            if (recv <= 0)
                throw new SocketException(10054);

            Received += recv;
            if (Received == 2)
                if (Buffer[1] == 0)
                    OnCallBack(null);
                else
                    throw new ProxyException("Username/password combination not accepted.");
            else
                Server.BeginReceive(Buffer, Received, 2 - Received, SocketFlags.None,
                    OnReceive, Server);
        }
        catch (Exception e)
        {
            OnCallBack(e);
        }
    }

    private void OnCallBack(Exception? exception)
    {
        ArrayPool<byte>.Shared.Return(TakeBuffer());
        CallBack(exception);
    }
}ParseOptions.0.jsonƒn
]D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\ProxySocket\HttpsHandler.csÕm/*
    Copyright ¬© 2002, The KPD-Team
    All rights reserved.
    http://www.mentalis.org/

  Redistribution and use in source and binary forms, with or without
  modification, are permitted provided that the following conditions
  are met:

    - Redistributions of source code must retain the above copyright
       notice, this list of conditions and the following disclaimer. 

    - Neither the name of the KPD-Team, nor the names of its contributors
       may be used to endorse or promote products derived from this
       software without specific prior written permission. 

  THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
  "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
  LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
  FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL
  THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
  INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
  (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
  SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
  HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
  STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
  ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
  OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Titanium.Web.Proxy.ProxySocket;

/// <summary>
///     Implements the HTTPS (CONNECT) protocol.
/// </summary>
internal sealed class HttpsHandler : SocksHandler
{
    // private variables
    /// <summary>Holds the value of the Password property.</summary>
    private string password = string.Empty;

    /// <summary>Holds the count of newline characters received.</summary>
    private int receivedNewlineChars;

    /// <summary>
    ///     Initializes a new HttpsHandler instance.
    /// </summary>
    /// <param name="server">The socket connection with the proxy server.</param>
    /// <exception cref="ArgumentNullException"><c>server</c>  is null.</exception>
    public HttpsHandler(Socket server) : this(server, "")
    {
    }

    /// <summary>
    ///     Initializes a new HttpsHandler instance.
    /// </summary>
    /// <param name="server">The socket connection with the proxy server.</param>
    /// <param name="user">The username to use.</param>
    /// <exception cref="ArgumentNullException"><c>server</c> -or- <c>user</c> is null.</exception>
    public HttpsHandler(Socket server, string user) : this(server, user, "")
    {
    }

    /// <summary>
    ///     Initializes a new HttpsHandler instance.
    /// </summary>
    /// <param name="server">The socket connection with the proxy server.</param>
    /// <param name="user">The username to use.</param>
    /// <param name="pass">The password to use.</param>
    /// <exception cref="ArgumentNullException"><c>server</c> -or- <c>user</c> -or- <c>pass</c> is null.</exception>
    public HttpsHandler(Socket server, string user, string pass) : base(server, user)
    {
        Password = pass;
    }

    /// <summary>
    ///     Gets or sets the password to use when authenticating with the HTTPS server.
    /// </summary>
    /// <value>The password to use when authenticating with the HTTPS server.</value>
    private string Password
    {
        get => password;
        set => password = value ?? throw new ArgumentNullException();
    }

    /// <summary>
    ///     Creates an array of bytes that has to be sent when the user wants to connect to a specific IPEndPoint.
    /// </summary>
    /// <returns>An array of bytes that has to be sent when the user wants to connect to a specific IPEndPoint.</returns>
    private byte[] GetConnectBytes(string host, int port)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Format("CONNECT {0}:{1} HTTP/1.1", host, port));
        sb.AppendLine(string.Format("Host: {0}:{1}", host, port));
        if (!string.IsNullOrEmpty(Username))
        {
            var auth =
                Convert.ToBase64String(Encoding.ASCII.GetBytes(string.Format("{0}:{1}", Username, Password)));
            sb.AppendLine(string.Format("Proxy-Authorization: Basic {0}", auth));
        }

        sb.AppendLine();
        var buffer = Encoding.ASCII.GetBytes(sb.ToString());
        return buffer;
    }

    /// <summary>
    ///     Verifies that proxy server successfully connected to requested host
    /// </summary>
    /// <param name="buffer">Input data array</param>
    /// <param name="length">The data count in the buffer</param>
    private void VerifyConnectHeader(byte[] buffer, int length)
    {
        var header = Encoding.ASCII.GetString(buffer, 0, length);
        if (!header.StartsWith("HTTP/1.1 ", StringComparison.OrdinalIgnoreCase) &&
            !header.StartsWith("HTTP/1.0 ", StringComparison.OrdinalIgnoreCase) || !header.EndsWith(" "))
            throw new ProtocolViolationException();

        var code = header.Substring(9, 3);
        if (code != "200")
            throw new ProxyException("Invalid HTTP status. Code: " + code);
    }

    /// <summary>
    ///     Starts negotiating with the SOCKS server.
    /// </summary>
    /// <param name="remoteEp">The IPEndPoint to connect to.</param>
    /// <exception cref="ArgumentNullException"><c>remoteEP</c> is null.</exception>
    /// <exception cref="ProxyException">The proxy rejected the request.</exception>
    /// <exception cref="SocketException">An operating system error occurs while accessing the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    /// <exception cref="ProtocolViolationException">The proxy server uses an invalid protocol.</exception>
    public override void Negotiate(IPEndPoint remoteEp)
    {
        if (remoteEp == null)
            throw new ArgumentNullException();
        Negotiate(remoteEp.Address.ToString(), remoteEp.Port);
    }

    /// <summary>
    ///     Starts negotiating with the SOCKS server.
    /// </summary>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    /// <exception cref="ArgumentNullException"><c>host</c> is null.</exception>
    /// <exception cref="ArgumentException"><c>port</c> is invalid.</exception>
    /// <exception cref="ProxyException">The proxy rejected the request.</exception>
    /// <exception cref="SocketException">An operating system error occurs while accessing the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    /// <exception cref="ProtocolViolationException">The proxy server uses an invalid protocol.</exception>
    public override void Negotiate(string host, int port)
    {
        if (host == null)
            throw new ArgumentNullException();

        if (port <= 0 || port > 65535 || host.Length > 255)
            throw new ArgumentException();

        var buffer = GetConnectBytes(host, port);
        if (Server.Send(buffer, 0, buffer.Length, SocketFlags.None) < buffer.Length) throw new SocketException(10054);

        ReadBytes(buffer, 13); // buffer is always longer than 13 bytes. Check the code in GetConnectBytes
        VerifyConnectHeader(buffer, 13);

        // Read bytes 1 by 1 until we reach "\r\n\r\n"
        var receivedNewlineChars = 0;
        while (receivedNewlineChars < 4)
        {
            var recv = Server.Receive(buffer, 0, 1, SocketFlags.None);
            if (recv == 0) throw new SocketException(10054);

            var b = buffer[0];
            if (b == (receivedNewlineChars % 2 == 0 ? '\r' : '\n'))
                receivedNewlineChars++;
            else
                receivedNewlineChars = b == '\r' ? 1 : 0;
        }
    }

    /// <summary>
    ///     Starts negotiating asynchronously with the HTTPS server.
    /// </summary>
    /// <param name="remoteEp">An IPEndPoint that represents the remote device.</param>
    /// <param name="callback">The method to call when the negotiation is complete.</param>
    /// <param name="proxyEndPoint">The IPEndPoint of the HTTPS proxy server.</param>
    /// <param name="state">The state.</param>
    /// <returns>An IAsyncProxyResult that references the asynchronous connection.</returns>
    public override AsyncProxyResult BeginNegotiate(IPEndPoint remoteEp, HandShakeComplete callback,
        IPEndPoint proxyEndPoint, AsyncProxyResult asyncResult)
    {
        return BeginNegotiate(remoteEp.Address.ToString(), remoteEp.Port, callback, proxyEndPoint, asyncResult);
    }

    /// <summary>
    ///     Starts negotiating asynchronously with the HTTPS server.
    /// </summary>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    /// <param name="callback">The method to call when the negotiation is complete.</param>
    /// <param name="proxyEndPoint">The IPEndPoint of the HTTPS proxy server.</param>
    /// <param name="state">The state.</param>
    /// <returns>An IAsyncProxyResult that references the asynchronous connection.</returns>
    public override AsyncProxyResult BeginNegotiate(string host, int port, HandShakeComplete callback,
        IPEndPoint proxyEndPoint, AsyncProxyResult asyncResult)
    {
        ProtocolComplete = callback;
        Buffer = GetConnectBytes(host, port);
        // Assign all callback-visible state before BeginConnect can complete.
        var result = asyncResult ?? throw new ArgumentNullException(nameof(asyncResult));
        Server.BeginConnect(proxyEndPoint, OnConnect, Server);
        return result;
    }

    /// <summary>
    ///     Called when the socket is connected to the remote server.
    /// </summary>
    /// <param name="ar">Stores state information for this asynchronous operation as well as any user-defined data.</param>
    private void OnConnect(IAsyncResult ar)
    {
        try
        {
            Server.EndConnect(ar);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
            return;
        }

        try
        {
            Server.BeginSend(Buffer, 0, Buffer.Length, SocketFlags.None, OnConnectSent,
                null);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
        }
    }

    /// <summary>
    ///     Called when the connect request bytes have been sent.
    /// </summary>
    /// <param name="ar">Stores state information for this asynchronous operation as well as any user-defined data.</param>
    private void OnConnectSent(IAsyncResult ar)
    {
        try
        {
            HandleEndSend(ar, Buffer.Length);
            Buffer = new byte[13];
            Received = 0;
            Server.BeginReceive(Buffer, 0, 13, SocketFlags.None, OnConnectReceive, Server);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
        }
    }

    /// <summary>
    ///     Called when an connect reply has been received.
    /// </summary>
    /// <param name="ar">Stores state information for this asynchronous operation as well as any user-defined data.</param>
    private void OnConnectReceive(IAsyncResult ar)
    {
        try
        {
            HandleEndReceive(ar);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
            return;
        }

        try
        {
            if (Received < 13)
            {
                Server.BeginReceive(Buffer, Received, 13 - Received, SocketFlags.None,
                    OnConnectReceive, Server);
            }
            else
            {
                VerifyConnectHeader(Buffer, 13);
                ReadUntilHeadersEnd(true);
            }
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
        }
    }

    /// <summary>
    ///     Reads socket buffer byte by byte until we reach "\r\n\r\n".
    /// </summary>
    /// <param name="readFirstByte"></param>
    private void ReadUntilHeadersEnd(bool readFirstByte)
    {
        while (Server.Available > 0 && receivedNewlineChars < 4)
        {
            if (!readFirstByte)
            {
                readFirstByte = false;
            }
            else
            {
                var recv = Server.Receive(Buffer, 0, 1, SocketFlags.None);
                if (recv == 0)
                    throw new SocketException(10054);
            }

            if (Buffer[0] == (receivedNewlineChars % 2 == 0 ? '\r' : '\n'))
                receivedNewlineChars++;
            else
                receivedNewlineChars = Buffer[0] == '\r' ? 1 : 0;
        }

        if (receivedNewlineChars == 4)
            OnProtocolComplete(null);
        else
            Server.BeginReceive(Buffer, 0, 1, SocketFlags.None, OnEndHeadersReceive,
                Server);
    }

    // I think we should never reach this function in practice
    // But let's define it just in case
    /// <summary>
    ///     Called when additional headers have been received.
    /// </summary>
    /// <param name="ar">Stores state information for this asynchronous operation as well as any user-defined data.</param>
    private void OnEndHeadersReceive(IAsyncResult ar)
    {
        try
        {
            HandleEndReceive(ar);
            ReadUntilHeadersEnd(false);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
        }
    }

    protected override void OnProtocolComplete(Exception? exception)
    {
        // do not return the base Buffer
        ProtocolComplete(exception);
    }
}ParseOptions.0.json‘(
bD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\ProxySocket\IAsyncProxyResult.csÿ'/*
    Copyright ¬© 2002, The KPD-Team
    All rights reserved.
    http://www.mentalis.org/

  Redistribution and use in source and binary forms, with or without
  modification, are permitted provided that the following conditions
  are met:

    - Redistributions of source code must retain the above copyright
       notice, this list of conditions and the following disclaimer. 

    - Neither the name of the KPD-Team, nor the names of its contributors
       may be used to endorse or promote products derived from this
       software without specific prior written permission. 

  THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
  "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
  LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
  FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL
  THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
  INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
  (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
  SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
  HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
  STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
  ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
  OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;
using System.Threading;

namespace Titanium.Web.Proxy.ProxySocket;

/// <summary>
///     A class that implements the IAsyncResult interface. Objects from this class are returned by the BeginConnect method
///     of the ProxySocket class.
/// </summary>
internal class AsyncProxyResult : IAsyncResult
{
    // private variables

    /// <summary>Holds the value of the WaitHandle property.</summary>
    private readonly object syncRoot = new();
    private ManualResetEvent? waitHandle;
    private volatile bool isCompleted;

    /// <summary>Initializes the internal variables of this object</summary>
    /// <param name="stateObject">An object that contains state information for this request.</param>
    internal AsyncProxyResult(object? stateObject = null)
    {
        AsyncState = stateObject;
    }

    /// <summary>
    ///     Gets a value that indicates whether the server has completed processing the call. It is illegal for the server
    ///     to use any client supplied resources outside of the agreed upon sharing semantics after it sets the IsCompleted
    ///     property to "true". Thus, it is safe for the client to destroy the resources after IsCompleted property returns
    ///     "true".
    /// </summary>
    /// <value>A boolean that indicates whether the server has completed processing the call.</value>
    public bool IsCompleted => isCompleted;

    internal Exception? Error { get; private set; }

    /// <summary>
    ///     Gets a value that indicates whether the BeginXXXX call has been completed synchronously. If this is detected
    ///     in the AsyncCallback delegate, it is probable that the thread that called BeginInvoke is the current thread.
    /// </summary>
    /// <value>Returns false.</value>
    public bool CompletedSynchronously => false;

    /// <summary>Gets an object that was passed as the state parameter of the BeginXXXX method call.</summary>
    /// <value>The object that was passed as the state parameter of the BeginXXXX method call.</value>
    public object? AsyncState { get; }

    /// <summary>
    ///     The AsyncWaitHandle property returns the WaitHandle that can use to perform a WaitHandle.WaitOne or WaitAny or
    ///     WaitAll. The object which implements IAsyncResult need not derive from the System.WaitHandle classes directly. The
    ///     WaitHandle wraps its underlying synchronization primitive and should be signaled after the call is completed. This
    ///     enables the client to wait for the call to complete instead polling. The Runtime supplies a number of waitable
    ///     objects that mirror Win32 synchronization primitives e.g. ManualResetEvent, AutoResetEvent and Mutex.
    ///     WaitHandle supplies methods that support waiting for such synchronization objects to become signaled with "any" or
    ///     "all" semantics i.e. WaitHandle.WaitOne, WaitAny and WaitAll. Such methods are context aware to avoid deadlocks.
    ///     The AsyncWaitHandle can be allocated eagerly or on demand. It is the choice of the IAsyncResult implementer.
    /// </summary>
    /// <value>The WaitHandle associated with this asynchronous result.</value>
    public WaitHandle AsyncWaitHandle
    {
        get
        {
            lock (syncRoot)
                return waitHandle ??= new ManualResetEvent(isCompleted);
        }
    }

    /// <summary>Initializes the internal variables of this object</summary>
    internal void Complete(Exception? error)
    {
        Error = error;
        isCompleted = true;
        lock (syncRoot)
            waitHandle?.Set();
    }
}ParseOptions.0.json∏
_D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\ProxySocket\ProxyException.csø/*
    Copyright ¬© 2002, The KPD-Team
    All rights reserved.
    http://www.mentalis.org/

  Redistribution and use in source and binary forms, with or without
  modification, are permitted provided that the following conditions
  are met:

    - Redistributions of source code must retain the above copyright
       notice, this list of conditions and the following disclaimer. 

    - Neither the name of the KPD-Team, nor the names of its contributors
       may be used to endorse or promote products derived from this
       software without specific prior written permission. 

  THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
  "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
  LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
  FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL
  THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
  INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
  (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
  SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
  HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
  STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
  ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
  OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;

namespace Titanium.Web.Proxy.ProxySocket;

/// <summary>
///     The exception that is thrown when a proxy error occurs.
/// </summary>
[Serializable]
internal class ProxyException : Exception
{
    /// <summary>
    ///     Initializes a new instance of the ProxyException class.
    /// </summary>
    public ProxyException() : this("An error occured while talking to the proxy server.")
    {
    }

    /// <summary>
    ///     Initializes a new instance of the ProxyException class.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public ProxyException(string message) : base(message)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the ProxyException class.
    /// </summary>
    /// <param name="socks5Error">The error number returned by a SOCKS5 server.</param>
    public ProxyException(int socks5Error) : this(Socks5ToString(socks5Error))
    {
    }

    /// <summary>
    ///     Converts a SOCKS5 error number to a human readable string.
    /// </summary>
    /// <param name="socks5Error">The error number returned by a SOCKS5 server.</param>
    /// <returns>A string representation of the specified SOCKS5 error number.</returns>
    public static string Socks5ToString(int socks5Error)
    {
        switch (socks5Error)
        {
            case 0:
                return "Connection succeeded.";
            case 1:
                return "General SOCKS server failure.";
            case 2:
                return "Connection not allowed by ruleset.";
            case 3:
                return "Network unreachable.";
            case 4:
                return "Host unreachable.";
            case 5:
                return "Connection refused.";
            case 6:
                return "TTL expired.";
            case 7:
                return "Command not supported.";
            case 8:
                return "Address type not supported.";
            default:
                return "Unspecified SOCKS error.";
        }
    }
}ParseOptions.0.jsonÖß
\D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\ProxySocket\ProxySocket.csé¶/*
    Copyright ¬© 2002, The KPD-Team
    All rights reserved.
    http://www.mentalis.org/

  Redistribution and use in source and binary forms, with or without
  modification, are permitted provided that the following conditions
  are met:

    - Redistributions of source code must retain the above copyright
       notice, this list of conditions and the following disclaimer. 

    - Neither the name of the KPD-Team, nor the names of its contributors
       may be used to endorse or promote products derived from this
       software without specific prior written permission. 

  THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
  "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
  LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
  FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL
  THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
  INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
  (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
  SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
  HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
  STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
  ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
  OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;
using System.Net;
using System.Net.Sockets;

// Implements a number of classes to allow Sockets to connect trough a firewall.
namespace Titanium.Web.Proxy.ProxySocket;

/// <summary>
///     Specifies the type of proxy servers that an instance of the ProxySocket class can use.
/// </summary>
internal enum ProxyTypes
{
    /// <summary>No proxy server; the ProxySocket object behaves exactly like an ordinary Socket object.</summary>
    None,

    /// <summary>A HTTPS (CONNECT) proxy server.</summary>
    Https,

    /// <summary>A SOCKS4[A] proxy server.</summary>
    Socks4,

    /// <summary>A SOCKS5 proxy server.</summary>
    Socks5
}

/// <summary>
///     Implements a Socket class that can connect trough a SOCKS proxy server.
/// </summary>
/// <remarks>
///     This class implements SOCKS4[A] and SOCKS5.
///     <br>It does not, however, implement the BIND commands, so you cannot .</br>
/// </remarks>
internal class ProxySocket : Socket
{
    /// <summary>Holds the value of the ProxyPass property.</summary>
    private string proxyPass = string.Empty;

    // private variables

    /// <summary>Holds the value of the ProxyUser property.</summary>
    private string proxyUser = string.Empty;

    /// <summary>
    ///     Initializes a new instance of the ProxySocket class.
    /// </summary>
    /// <param name="addressFamily">One of the AddressFamily values.</param>
    /// <param name="socketType">One of the SocketType values.</param>
    /// <param name="protocolType">One of the ProtocolType values.</param>
    /// <exception cref="SocketException">
    ///     The combination of addressFamily, socketType, and protocolType results in an invalid
    ///     socket.
    /// </exception>
    public ProxySocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType) : this(
        addressFamily, socketType, protocolType, "")
    {
    }

    /// <summary>
    ///     Initializes a new instance of the ProxySocket class.
    /// </summary>
    /// <param name="addressFamily">One of the AddressFamily values.</param>
    /// <param name="socketType">One of the SocketType values.</param>
    /// <param name="protocolType">One of the ProtocolType values.</param>
    /// <param name="proxyUsername">The username to use when authenticating with the proxy server.</param>
    /// <exception cref="SocketException">
    ///     The combination of addressFamily, socketType, and protocolType results in an invalid
    ///     socket.
    /// </exception>
    /// <exception cref="ArgumentNullException"><c>proxyUsername</c> is null.</exception>
    public ProxySocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType,
        string proxyUsername) : this(addressFamily, socketType, protocolType, proxyUsername, "")
    {
    }

    /// <summary>
    ///     Initializes a new instance of the ProxySocket class.
    /// </summary>
    /// <param name="addressFamily">One of the AddressFamily values.</param>
    /// <param name="socketType">One of the SocketType values.</param>
    /// <param name="protocolType">One of the ProtocolType values.</param>
    /// <param name="proxyUsername">The username to use when authenticating with the proxy server.</param>
    /// <param name="proxyPassword">The password to use when authenticating with the proxy server.</param>
    /// <exception cref="SocketException">
    ///     The combination of addressFamily, socketType, and protocolType results in an invalid
    ///     socket.
    /// </exception>
    /// <exception cref="ArgumentNullException"><c>proxyUsername</c> -or- <c>proxyPassword</c> is null.</exception>
    public ProxySocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType,
        string proxyUsername, string proxyPassword) : base(addressFamily, socketType, protocolType)
    {
        ProxyUser = proxyUsername;
        ProxyPass = proxyPassword;
    }

    /// <summary>
    ///     Gets or sets the EndPoint of the proxy server.
    /// </summary>
    /// <value>An IPEndPoint object that holds the IP address and the port of the proxy server.</value>
    public IPEndPoint? ProxyEndPoint { get; set; }

    /// <summary>
    ///     Gets or sets the type of proxy server to use.
    /// </summary>
    /// <value>One of the ProxyTypes values.</value>
    public ProxyTypes ProxyType { get; set; } = ProxyTypes.None;

    /// <summary>
    ///     Gets or sets the username to use when authenticating with the proxy.
    /// </summary>
    /// <value>A string that holds the username that's used when authenticating with the proxy.</value>
    /// <exception cref="ArgumentNullException">The specified value is null.</exception>
    public string ProxyUser
    {
        get => proxyUser;
        set => proxyUser = value ?? throw new ArgumentNullException();
    }

    /// <summary>
    ///     Gets or sets the password to use when authenticating with the proxy.
    /// </summary>
    /// <value>A string that holds the password that's used when authenticating with the proxy.</value>
    /// <exception cref="ArgumentNullException">The specified value is null.</exception>
    public string ProxyPass
    {
        get => proxyPass;
        set => proxyPass = value ?? throw new ArgumentNullException();
    }

    /// <summary>
    ///     Establishes a connection to a remote device.
    /// </summary>
    /// <param name="address">An EndPoint address that represents the remote device.</param>
    /// <param name="port">An EndPoint port that represents the remote device.</param>
    /// <exception cref="ArgumentNullException">The remoteEP parameter is a null reference (Nothing in Visual Basic).</exception>
    /// <exception cref="SocketException">An operating system error occurs while accessing the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    /// <exception cref="ProxyException">An error occurred while talking to the proxy server.</exception>
    public new void Connect(IPAddress address, int port)
    {
        var remoteEp = new IPEndPoint(address, port);
        Connect(remoteEp);
    }

    /// <summary>
    ///     Establishes a connection to a remote device.
    /// </summary>
    /// <param name="remoteEp">An EndPoint that represents the remote device.</param>
    /// <exception cref="ArgumentNullException">The remoteEP parameter is a null reference (Nothing in Visual Basic).</exception>
    /// <exception cref="SocketException">An operating system error occurs while accessing the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    /// <exception cref="ProxyException">An error occurred while talking to the proxy server.</exception>
    public new void Connect(EndPoint remoteEp)
    {
        if (remoteEp == null)
            throw new ArgumentNullException("<remoteEP> cannot be null.");
        if (ProtocolType != ProtocolType.Tcp || ProxyType == ProxyTypes.None || ProxyEndPoint == null)
        {
            base.Connect(remoteEp);
        }
        else
        {
            base.Connect(ProxyEndPoint);
            if (ProxyType == ProxyTypes.Https)
                new HttpsHandler(this, ProxyUser, ProxyPass).Negotiate((IPEndPoint)remoteEp);
            else if (ProxyType == ProxyTypes.Socks4)
                new Socks4Handler(this, ProxyUser).Negotiate((IPEndPoint)remoteEp);
            else if (ProxyType == ProxyTypes.Socks5)
                new Socks5Handler(this, ProxyUser, ProxyPass).Negotiate((IPEndPoint)remoteEp);
        }
    }

    /// <summary>
    ///     Establishes a connection to a remote device.
    /// </summary>
    /// <param name="host">The remote host to connect to.</param>
    /// <param name="port">The remote port to connect to.</param>
    /// <exception cref="ArgumentNullException">The host parameter is a null reference (Nothing in Visual Basic).</exception>
    /// <exception cref="ArgumentException">The port parameter is invalid.</exception>
    /// <exception cref="SocketException">An operating system error occurs while accessing the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    /// <exception cref="ProxyException">An error occurred while talking to the proxy server.</exception>
    /// <remarks>
    ///     If you use this method with a SOCKS4 server, it will let the server resolve the hostname. Not all SOCKS4
    ///     servers support this 'remote DNS' though.
    /// </remarks>
    public new void Connect(string host, int port)
    {
        if (host == null)
            throw new ArgumentNullException(nameof(host));

        if (port <= 0 || port > 65535)
            throw new ArgumentException(nameof(port));

        if (ProtocolType != ProtocolType.Tcp || ProxyType == ProxyTypes.None || ProxyEndPoint == null)
        {
            base.Connect(new IPEndPoint(Dns.GetHostEntry(host).AddressList[0], port));
        }
        else
        {
            base.Connect(ProxyEndPoint);
            if (ProxyType == ProxyTypes.Https)
                new HttpsHandler(this, ProxyUser, ProxyPass).Negotiate(host, port);
            else if (ProxyType == ProxyTypes.Socks4)
                new Socks4Handler(this, ProxyUser).Negotiate(host, port);
            else if (ProxyType == ProxyTypes.Socks5)
                new Socks5Handler(this, ProxyUser, ProxyPass).Negotiate(host, port);
        }
    }

    /// <summary>
    ///     Begins an asynchronous request for a connection to a network device.
    /// </summary>
    /// <param name="address">An EndPoint address that represents the remote device.</param>
    /// <param name="port">An EndPoint port that represents the remote device.</param>
    /// <param name="callback">The AsyncCallback delegate.</param>
    /// <param name="state">An object that contains state information for this request.</param>
    /// <returns>An IAsyncResult that references the asynchronous connection.</returns>
    /// <exception cref="ArgumentNullException">The remoteEP parameter is a null reference (Nothing in Visual Basic).</exception>
    /// <exception cref="SocketException">An operating system error occurs while creating the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    public new IAsyncResult BeginConnect(IPAddress address, int port, AsyncCallback? callback, object? state)
    {
        var remoteEp = new IPEndPoint(address, port);
        return BeginConnect(remoteEp, callback, state);
    }

    /// <summary>
    ///     Begins an asynchronous request for a connection to a network device.
    /// </summary>
    /// <param name="remoteEp">An EndPoint that represents the remote device.</param>
    /// <param name="callback">The AsyncCallback delegate.</param>
    /// <param name="state">An object that contains state information for this request.</param>
    /// <returns>An IAsyncResult that references the asynchronous connection.</returns>
    /// <exception cref="ArgumentNullException">The remoteEP parameter is a null reference (Nothing in Visual Basic).</exception>
    /// <exception cref="SocketException">An operating system error occurs while creating the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    public new IAsyncResult BeginConnect(EndPoint remoteEp, AsyncCallback? callback, object? state)
    {
        if (remoteEp == null)
            throw new ArgumentNullException();

        if (ProtocolType != ProtocolType.Tcp || ProxyType == ProxyTypes.None || ProxyEndPoint == null)
            return base.BeginConnect(remoteEp, callback, state);

        var result = new AsyncProxyResult(state);
        HandShakeComplete protocolComplete = error => OnHandShakeComplete(result, callback, error);
        if (ProxyType == ProxyTypes.Https)
        {
            return new HttpsHandler(this, ProxyUser, ProxyPass).BeginNegotiate((IPEndPoint)remoteEp,
                protocolComplete, ProxyEndPoint, result);
        }

        if (ProxyType == ProxyTypes.Socks4)
        {
            return new Socks4Handler(this, ProxyUser).BeginNegotiate((IPEndPoint)remoteEp,
                protocolComplete, ProxyEndPoint, result);
        }

        if (ProxyType == ProxyTypes.Socks5)
        {
            return new Socks5Handler(this, ProxyUser, ProxyPass).BeginNegotiate((IPEndPoint)remoteEp,
                protocolComplete, ProxyEndPoint, result);
        }

        throw new InvalidOperationException($"Unsupported proxy type: {ProxyType}.");
    }

    /// <summary>
    ///     Begins an asynchronous request for a connection to a network device.
    /// </summary>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The port on the remote host to connect to.</param>
    /// <param name="callback">The AsyncCallback delegate.</param>
    /// <param name="state">An object that contains state information for this request.</param>
    /// <returns>An IAsyncResult that references the asynchronous connection.</returns>
    /// <exception cref="ArgumentNullException">The host parameter is a null reference (Nothing in Visual Basic).</exception>
    /// <exception cref="ArgumentException">The port parameter is invalid.</exception>
    /// <exception cref="SocketException">An operating system error occurs while creating the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    public new IAsyncResult BeginConnect(string host, int port, AsyncCallback? callback, object? state)
    {
        if (host == null)
            throw new ArgumentNullException();
        if (port <= 0 || port > 65535)
            throw new ArgumentException();
        var result = new AsyncProxyResult(state);
        HandShakeComplete protocolComplete = error => OnHandShakeComplete(result, callback, error);
        if (ProtocolType != ProtocolType.Tcp || ProxyType == ProxyTypes.None || ProxyEndPoint == null)
        {
            BeginDns(host, port, protocolComplete, result);
            return result;
        }

        if (ProxyType == ProxyTypes.Https)
        {
            return new HttpsHandler(this, ProxyUser, ProxyPass).BeginNegotiate(host, port,
                protocolComplete, ProxyEndPoint, result);
        }

        if (ProxyType == ProxyTypes.Socks4)
        {
            return new Socks4Handler(this, ProxyUser).BeginNegotiate(host, port,
                protocolComplete, ProxyEndPoint, result);
        }

        if (ProxyType == ProxyTypes.Socks5)
        {
            return new Socks5Handler(this, ProxyUser, ProxyPass).BeginNegotiate(host, port,
                protocolComplete, ProxyEndPoint, result);
        }

        throw new InvalidOperationException($"Unsupported proxy type: {ProxyType}.");
    }

    /// <summary>
    ///     Ends a pending asynchronous connection request.
    /// </summary>
    /// <param name="asyncResult">Stores state information for this asynchronous operation as well as any user-defined data.</param>
    /// <exception cref="ArgumentNullException">The asyncResult parameter is a null reference (Nothing in Visual Basic).</exception>
    /// <exception cref="ArgumentException">The asyncResult parameter was not returned by a call to the BeginConnect method.</exception>
    /// <exception cref="SocketException">An operating system error occurs while accessing the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    /// <exception cref="InvalidOperationException">EndConnect was previously called for the asynchronous connection.</exception>
    /// <exception cref="ProxyException">The proxy server refused the connection.</exception>
    public new void EndConnect(IAsyncResult asyncResult)
    {
        if (asyncResult == null)
            throw new ArgumentNullException();
        // In case we called Socket.BeginConnect() directly
        if (!(asyncResult is AsyncProxyResult proxyResult))
        {
            base.EndConnect(asyncResult);
            return;
        }

        if (!asyncResult.IsCompleted)
            asyncResult.AsyncWaitHandle.WaitOne();
        if (proxyResult.Error != null)
            throw proxyResult.Error;
    }

    /// <summary>
    ///     Begins an asynchronous request to resolve a DNS host name or IP address in dotted-quad notation to an IPAddress
    ///     instance.
    /// </summary>
    /// <param name="host">The host to resolve.</param>
    /// <param name="callback">The method to call when the hostname has been resolved.</param>
    /// <param name="state">The state.</param>
    /// <returns>An IAsyncResult instance that references the asynchronous request.</returns>
    /// <exception cref="SocketException">There was an error while trying to resolve the host.</exception>
    private void BeginDns(string host, int port, HandShakeComplete callback, AsyncProxyResult result)
    {
        try
        {
            Dns.BeginGetHostEntry(host, OnResolved, new DnsConnectState(port, callback, result));
        }
        catch
        {
            throw new SocketException();
        }
    }

    /// <summary>
    ///     Called when the specified hostname has been resolved.
    /// </summary>
    /// <param name="asyncResult">The result of the asynchronous operation.</param>
    private void OnResolved(IAsyncResult asyncResult)
    {
        var state = asyncResult.AsyncState as DnsConnectState
                    ?? throw new InvalidOperationException("DNS callback state is missing.");
        try
        {
            var dns = Dns.EndGetHostEntry(asyncResult);
            base.BeginConnect(new IPEndPoint(dns.AddressList[0], state.Port), OnConnect, state);
        }
        catch (Exception e)
        {
            state.Callback(e);
        }
    }

    /// <summary>
    ///     Called when the Socket is connected to the remote host.
    /// </summary>
    /// <param name="asyncResult">The result of the asynchronous operation.</param>
    private void OnConnect(IAsyncResult asyncResult)
    {
        var state = asyncResult.AsyncState as DnsConnectState
                    ?? throw new InvalidOperationException("Connect callback state is missing.");
        try
        {
            base.EndConnect(asyncResult);
            state.Callback(null);
        }
        catch (Exception e)
        {
            state.Callback(e);
        }
    }

    /// <summary>
    ///     Called when the Socket has finished talking to the proxy server and is ready to relay data.
    /// </summary>
    /// <param name="error">The error to throw when the EndConnect method is called.</param>
    private void OnHandShakeComplete(AsyncProxyResult result, AsyncCallback? callback, Exception? error)
    {
        if (error != null)
            Close();

        result.Complete(error);
        callback?.Invoke(result);
    }

    private sealed class DnsConnectState
    {
        internal DnsConnectState(int port, HandShakeComplete callback, AsyncProxyResult result)
        {
            Port = port;
            Callback = callback;
            Result = result;
        }

        internal int Port { get; }

        internal HandShakeComplete Callback { get; }

        internal AsyncProxyResult Result { get; }
    }
}ParseOptions.0.json∆g
^D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\ProxySocket\Socks4Handler.csŒf/*
    Copyright ¬© 2002, The KPD-Team
    All rights reserved.
    http://www.mentalis.org/

  Redistribution and use in source and binary forms, with or without
  modification, are permitted provided that the following conditions
  are met:

    - Redistributions of source code must retain the above copyright
       notice, this list of conditions and the following disclaimer. 

    - Neither the name of the KPD-Team, nor the names of its contributors
       may be used to endorse or promote products derived from this
       software without specific prior written permission. 

  THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
  "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
  LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
  FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL
  THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
  INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
  (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
  SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
  HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
  STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
  ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
  OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Titanium.Web.Proxy.ProxySocket;

/// <summary>
///     Implements the SOCKS4[A] protocol.
/// </summary>
internal sealed class Socks4Handler : SocksHandler
{
    /// <summary>
    ///     Initializes a new instance of the SocksHandler class.
    /// </summary>
    /// <param name="server">The socket connection with the proxy server.</param>
    /// <param name="user">The username to use when authenticating with the server.</param>
    /// <exception cref="ArgumentNullException"><c>server</c> -or- <c>user</c> is null.</exception>
    public Socks4Handler(Socket server, string user) : base(server, user)
    {
    }

    /// <summary>
    ///     Creates an array of bytes that has to be sent when the user wants to connect to a specific host/port combination.
    /// </summary>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    /// <param name="buffer">The buffer which contains the result data.</param>
    /// <returns>An array of bytes that has to be sent when the user wants to connect to a specific host/port combination.</returns>
    /// <remarks>
    ///     Resolving the host name will be done at server side. Do note that some SOCKS4 servers do not implement this
    ///     functionality.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><c>host</c> is null.</exception>
    /// <exception cref="ArgumentException"><c>port</c> is invalid.</exception>
    private int GetHostPortBytes(string host, int port, Memory<byte> buffer)
    {
        if (host == null)
            throw new ArgumentNullException(nameof(host));

        if (port <= 0 || port > 65535)
            throw new ArgumentException(nameof(port));

        var length = 10 + Username.Length + host.Length;
        Debug.Assert(buffer.Length >= length);

        var connect = buffer.Span;
        connect[0] = 4;
        connect[1] = 1;
        PortToBytes(port, connect.Slice(2));
        connect[4] = connect[5] = connect[6] = 0;
        connect[7] = 1;
        var userNameArray = Encoding.ASCII.GetBytes(Username);
        userNameArray.CopyTo(connect.Slice(8));
        connect[8 + Username.Length] = 0;
        Encoding.ASCII.GetBytes(host).CopyTo(connect.Slice(9 + Username.Length));
        connect[length - 1] = 0;
        return length;
    }

    /// <summary>
    ///     Creates an array of bytes that has to be sent when the user wants to connect to a specific IPEndPoint.
    /// </summary>
    /// <param name="remoteEp">The IPEndPoint to connect to.</param>
    /// <param name="buffer">The buffer which contains the result data.</param>
    /// <returns>An array of bytes that has to be sent when the user wants to connect to a specific IPEndPoint.</returns>
    /// <exception cref="ArgumentNullException"><c>remoteEP</c> is null.</exception>
    private int GetEndPointBytes(IPEndPoint remoteEp, Memory<byte> buffer)
    {
        if (remoteEp == null)
            throw new ArgumentNullException(nameof(remoteEp));

        var length = 9 + Username.Length;
        Debug.Assert(buffer.Length >= length);

        var connect = buffer.Span;
        connect[0] = 4;
        connect[1] = 1;
        PortToBytes(remoteEp.Port, connect.Slice(2));
        remoteEp.Address.GetAddressBytes().CopyTo(connect.Slice(4));
        Encoding.ASCII.GetBytes(Username).CopyTo(connect.Slice(8));
        connect[length - 1] = 0;
        return length;
    }

    /// <summary>
    ///     Starts negotiating with the SOCKS server.
    /// </summary>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    /// <exception cref="ArgumentNullException"><c>host</c> is null.</exception>
    /// <exception cref="ArgumentException"><c>port</c> is invalid.</exception>
    /// <exception cref="ProxyException">The proxy rejected the request.</exception>
    /// <exception cref="SocketException">An operating system error occurs while accessing the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    public override void Negotiate(string host, int port)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(10 + Username.Length + host.Length);
        try
        {
            var length = GetHostPortBytes(host, port, buffer);
            Negotiate(buffer, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///     Starts negotiating with the SOCKS server.
    /// </summary>
    /// <param name="remoteEp">The IPEndPoint to connect to.</param>
    /// <exception cref="ArgumentNullException"><c>remoteEP</c> is null.</exception>
    /// <exception cref="ProxyException">The proxy rejected the request.</exception>
    /// <exception cref="SocketException">An operating system error occurs while accessing the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    public override void Negotiate(IPEndPoint remoteEp)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(9 + Username.Length);
        try
        {
            var length = GetEndPointBytes(remoteEp, buffer);
            Negotiate(buffer, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///     Starts negotiating with the SOCKS server.
    /// </summary>
    /// <param name="connect">The bytes to send when trying to authenticate.</param>
    /// <param name="length">The byte count to send when trying to authenticate.</param>
    /// <exception cref="ArgumentNullException"><c>connect</c> is null.</exception>
    /// <exception cref="ArgumentException"><c>connect</c> is too small.</exception>
    /// <exception cref="ProxyException">The proxy rejected the request.</exception>
    /// <exception cref="SocketException">An operating system error occurs while accessing the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    private void Negotiate(byte[] connect, int length)
    {
        if (connect == null)
            throw new ArgumentNullException(nameof(connect));

        if (length < 2)
            throw new ArgumentException(nameof(length));

        if (Server.Send(connect, 0, length, SocketFlags.None) < length)
            throw new SocketException(10054);

        ReadBytes(connect, 8);
        if (connect[1] != 90)
        {
            Server.Close();
            throw new ProxyException("Negotiation failed.");
        }
    }

    /// <summary>
    ///     Starts negotiating asynchronously with a SOCKS proxy server.
    /// </summary>
    /// <param name="host">The remote server to connect to.</param>
    /// <param name="port">The remote port to connect to.</param>
    /// <param name="callback">The method to call when the connection has been established.</param>
    /// <param name="proxyEndPoint">The IPEndPoint of the SOCKS proxy server.</param>
    /// <param name="state">The state.</param>
    /// <returns>An IAsyncProxyResult that references the asynchronous connection.</returns>
    public override AsyncProxyResult BeginNegotiate(string host, int port, HandShakeComplete callback,
        IPEndPoint proxyEndPoint, AsyncProxyResult asyncResult)
    {
        ProtocolComplete = callback;
        Buffer = ArrayPool<byte>.Shared.Rent(10 + Username.Length + host.Length);
        BufferCount = GetHostPortBytes(host, port, Buffer);
        // Assign all callback-visible state before BeginConnect can complete.
        var result = asyncResult ?? throw new ArgumentNullException(nameof(asyncResult));
        Server.BeginConnect(proxyEndPoint, OnConnect, Server);
        return result;
    }

    /// <summary>
    ///     Starts negotiating asynchronously with a SOCKS proxy server.
    /// </summary>
    /// <param name="remoteEp">An IPEndPoint that represents the remote device.</param>
    /// <param name="callback">The method to call when the connection has been established.</param>
    /// <param name="proxyEndPoint">The IPEndPoint of the SOCKS proxy server.</param>
    /// <param name="state">The state.</param>
    /// <returns>An IAsyncProxyResult that references the asynchronous connection.</returns>
    public override AsyncProxyResult BeginNegotiate(IPEndPoint remoteEp, HandShakeComplete callback,
        IPEndPoint proxyEndPoint, AsyncProxyResult asyncResult)
    {
        ProtocolComplete = callback;
        Buffer = ArrayPool<byte>.Shared.Rent(9 + Username.Length);
        BufferCount = GetEndPointBytes(remoteEp, Buffer);
        // Assign all callback-visible state before BeginConnect can complete.
        var result = asyncResult ?? throw new ArgumentNullException(nameof(asyncResult));
        Server.BeginConnect(proxyEndPoint, OnConnect, Server);
        return result;
    }

    /// <summary>
    ///     Called when the Socket is connected to the remote proxy server.
    /// </summary>
    /// <param name="ar">Stores state information for this asynchronous operation as well as any user-defined data.</param>
    private void OnConnect(IAsyncResult ar)
    {
        try
        {
            Server.EndConnect(ar);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
            return;
        }

        try
        {
            Server.BeginSend(Buffer, 0, BufferCount, SocketFlags.None, OnSent, Server);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
        }
    }

    /// <summary>
    ///     Called when the Socket has sent the handshake data.
    /// </summary>
    /// <param name="ar">Stores state information for this asynchronous operation as well as any user-defined data.</param>
    private void OnSent(IAsyncResult ar)
    {
        try
        {
            HandleEndSend(ar, BufferCount);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
            return;
        }

        try
        {
            BufferCount = 8;
            Received = 0;
            Server.BeginReceive(Buffer, 0, BufferCount, SocketFlags.None, OnReceive, Server);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
        }
    }

    /// <summary>
    ///     Called when the Socket has received a reply from the remote proxy server.
    /// </summary>
    /// <param name="ar">Stores state information for this asynchronous operation as well as any user-defined data.</param>
    private void OnReceive(IAsyncResult ar)
    {
        try
        {
            HandleEndReceive(ar);
            if (Received == 8)
            {
                if (Buffer[1] == 90)
                {
                    OnProtocolComplete(null);
                }
                else
                {
                    Server.Close();
                    OnProtocolComplete(new ProxyException("Negotiation failed."));
                }
            }
            else
            {
                Server.BeginReceive(Buffer, Received, BufferCount - Received, SocketFlags.None, OnReceive,
                    Server);
            }
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
        }
    }
}ParseOptions.0.json«ß
^D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\ProxySocket\Socks5Handler.csŒ¶/*
    Copyright ¬© 2002, The KPD-Team
    All rights reserved.
    http://www.mentalis.org/

  Redistribution and use in source and binary forms, with or without
  modification, are permitted provided that the following conditions
  are met:

    - Redistributions of source code must retain the above copyright
       notice, this list of conditions and the following disclaimer. 

    - Neither the name of the KPD-Team, nor the names of its contributors
       may be used to endorse or promote products derived from this
       software without specific prior written permission. 

  THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
  "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
  LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
  FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL
  THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
  INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
  (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
  SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
  HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
  STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
  ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
  OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Titanium.Web.Proxy.ProxySocket.Authentication;

namespace Titanium.Web.Proxy.ProxySocket;

/// <summary>
///     Implements the SOCKS5 protocol.
/// </summary>
internal sealed class Socks5Handler : SocksHandler
{
    private const int ConnectOffset = 4;

    /// <summary>
    ///     The length of the connect request.
    /// </summary>
    private int handShakeLength;

    // private variables
    /// <summary>Holds the value of the Password property.</summary>
    private string password = string.Empty;

    /// <summary>
    ///     Initializes a new Socks5Handler instance.
    /// </summary>
    /// <param name="server">The socket connection with the proxy server.</param>
    /// <exception cref="ArgumentNullException"><c>server</c>  is null.</exception>
    public Socks5Handler(Socket server) : this(server, "")
    {
    }

    /// <summary>
    ///     Initializes a new Socks5Handler instance.
    /// </summary>
    /// <param name="server">The socket connection with the proxy server.</param>
    /// <param name="user">The username to use.</param>
    /// <exception cref="ArgumentNullException"><c>server</c> -or- <c>user</c> is null.</exception>
    public Socks5Handler(Socket server, string user) : this(server, user, "")
    {
    }

    /// <summary>
    ///     Initializes a new Socks5Handler instance.
    /// </summary>
    /// <param name="server">The socket connection with the proxy server.</param>
    /// <param name="user">The username to use.</param>
    /// <param name="pass">The password to use.</param>
    /// <exception cref="ArgumentNullException"><c>server</c> -or- <c>user</c> -or- <c>pass</c> is null.</exception>
    public Socks5Handler(Socket server, string user, string pass) : base(server, user)
    {
        Password = pass;
    }

    /// <summary>
    ///     Gets or sets the password to use when authenticating with the SOCKS5 server.
    /// </summary>
    /// <value>The password to use when authenticating with the SOCKS5 server.</value>
    private string Password
    {
        get => password;
        set => password = value ?? throw new ArgumentNullException();
    }

    /// <summary>
    ///     Starts the synchronous authentication process.
    /// </summary>
    /// <exception cref="ProxyException">Authentication with the proxy server failed.</exception>
    /// <exception cref="ProtocolViolationException">The proxy server uses an invalid protocol.</exception>
    /// <exception cref="SocketException">An operating system error occurs while accessing the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    private void Authenticate(byte[] buffer)
    {
        buffer[0] = 5;
        buffer[1] = 2;
        buffer[2] = 0;
        buffer[3] = 2;
        if (Server.Send(buffer, 0, 4, SocketFlags.None) < 4)
            throw new SocketException(10054);

        ReadBytes(buffer, 2);
        if (buffer[1] == 255)
            throw new ProxyException("No authentication method accepted.");

        AuthMethod authenticate;
        switch (buffer[1])
        {
            case 0:
                authenticate = new AuthNone(Server);
                break;
            case 2:
                authenticate = new AuthUserPass(Server, Username, Password);
                break;
            default:
                throw new ProtocolViolationException();
        }

        authenticate.Authenticate();
    }

    /// <summary>
    ///     Creates an array of bytes that has to be sent when the user wants to connect to a specific host/port combination.
    /// </summary>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    /// <param name="buffer">The buffer which contains the result data.</param>
    /// <returns>An array of bytes that has to be sent when the user wants to connect to a specific host/port combination.</returns>
    /// <exception cref="ArgumentNullException"><c>host</c> is null.</exception>
    /// <exception cref="ArgumentException"><c>port</c> or <c>host</c> is invalid.</exception>
    private int GetHostPortBytes(string host, int port, Memory<byte> buffer)
    {
        if (host == null)
            throw new ArgumentNullException();

        if (port <= 0 || port > 65535 || host.Length > 255)
            throw new ArgumentException();

        var length = 7 + host.Length;
        if (buffer.Length < length)
            throw new ArgumentException(nameof(buffer));

        var connect = buffer.Span;
        connect[0] = 5;
        connect[1] = 1;
        connect[2] = 0; // reserved
        connect[3] = 3;
        connect[4] = (byte)host.Length;
        Encoding.ASCII.GetBytes(host).CopyTo(connect.Slice(5));
        PortToBytes(port, connect.Slice(host.Length + 5));
        return length;
    }

    /// <summary>
    ///     Creates an array of bytes that has to be sent when the user wants to connect to a specific IPEndPoint.
    /// </summary>
    /// <param name="remoteEp">The IPEndPoint to connect to.</param>
    /// <param name="buffer">The buffer which contains the result data.</param>
    /// <returns>An array of bytes that has to be sent when the user wants to connect to a specific IPEndPoint.</returns>
    /// <exception cref="ArgumentNullException"><c>remoteEP</c> is null.</exception>
    private int GetEndPointBytes(IPEndPoint remoteEp, Memory<byte> buffer)
    {
        if (remoteEp == null)
            throw new ArgumentNullException();

        if (buffer.Length < 10)
            throw new ArgumentException(nameof(buffer));

        var connect = buffer.Span;
        connect[0] = 5;
        connect[1] = 1;
        connect[2] = 0; // reserved
        connect[3] = 1;
        remoteEp.Address.GetAddressBytes().CopyTo(connect.Slice(4));
        PortToBytes(remoteEp.Port, connect.Slice(8));
        return 10;
    }

    /// <summary>
    ///     Starts negotiating with the SOCKS server.
    /// </summary>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    /// <exception cref="ArgumentNullException"><c>host</c> is null.</exception>
    /// <exception cref="ArgumentException"><c>port</c> is invalid.</exception>
    /// <exception cref="ProxyException">The proxy rejected the request.</exception>
    /// <exception cref="SocketException">An operating system error occurs while accessing the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    /// <exception cref="ProtocolViolationException">The proxy server uses an invalid protocol.</exception>
    public override void Negotiate(string host, int port)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(258, 10 + host.Length + Username.Length + Password.Length));
        try
        {
            Authenticate(buffer);

            var length = GetHostPortBytes(host, port, buffer);
            Negotiate(buffer, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///     Starts negotiating with the SOCKS server.
    /// </summary>
    /// <param name="remoteEp">The IPEndPoint to connect to.</param>
    /// <exception cref="ArgumentNullException"><c>remoteEP</c> is null.</exception>
    /// <exception cref="ProxyException">The proxy rejected the request.</exception>
    /// <exception cref="SocketException">An operating system error occurs while accessing the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    /// <exception cref="ProtocolViolationException">The proxy server uses an invalid protocol.</exception>
    public override void Negotiate(IPEndPoint remoteEp)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(258, 13 + Username.Length + Password.Length));
        try
        {
            Authenticate(buffer);

            var length = GetEndPointBytes(remoteEp, buffer);
            Negotiate(buffer, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///     Starts negotiating with the SOCKS server.
    /// </summary>
    /// <param name="buffer">The bytes to send when trying to authenticate.</param>
    /// <param name="length">The byte count to send when trying to authenticate.</param>
    /// <exception cref="ArgumentNullException"><c>connect</c> is null.</exception>
    /// <exception cref="ArgumentException"><c>connect</c> is too small.</exception>
    /// <exception cref="ProxyException">The proxy rejected the request.</exception>
    /// <exception cref="SocketException">An operating system error occurs while accessing the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    /// <exception cref="ProtocolViolationException">The proxy server uses an invalid protocol.</exception>
    private void Negotiate(byte[] buffer, int length)
    {
        if (Server.Send(buffer, 0, length, SocketFlags.None) < length)
            throw new SocketException(10054);

        ReadBytes(buffer, 4);
        if (buffer[1] != 0)
        {
            Server.Close();
            throw new ProxyException(buffer[1]);
        }

        switch (buffer[3])
        {
            case 1:
                ReadBytes(buffer, 6); // IPv4 address with port
                break;
            case 3:
                ReadBytes(buffer, 1); // domain name length
                ReadBytes(buffer, buffer[0] + 2); // domain name with port
                break;
            case 4:
                ReadBytes(buffer, 18); //IPv6 address with port
                break;
            default:
                Server.Close();
                throw new ProtocolViolationException();
        }
    }

    /// <summary>
    ///     Starts negotiating asynchronously with the SOCKS server.
    /// </summary>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    /// <param name="callback">The method to call when the negotiation is complete.</param>
    /// <param name="proxyEndPoint">The IPEndPoint of the SOCKS proxy server.</param>
    /// <param name="state">The state.</param>
    /// <returns>An IAsyncProxyResult that references the asynchronous connection.</returns>
    public override AsyncProxyResult BeginNegotiate(string host, int port, HandShakeComplete callback,
        IPEndPoint proxyEndPoint, AsyncProxyResult asyncResult)
    {
        ProtocolComplete = callback;
        Buffer = ArrayPool<byte>.Shared.Rent(Math.Max(258, 10 + host.Length + Username.Length + Password.Length));

        // first {ConnectOffset} bytes are reserved for authentication 
        handShakeLength = GetHostPortBytes(host, port, Buffer.AsMemory(ConnectOffset));
        // Assign all callback-visible state before BeginConnect can complete.
        var result = asyncResult ?? throw new ArgumentNullException(nameof(asyncResult));
        Server.BeginConnect(proxyEndPoint, OnConnect, Server);
        return result;
    }

    /// <summary>
    ///     Starts negotiating asynchronously with the SOCKS server.
    /// </summary>
    /// <param name="remoteEp">An IPEndPoint that represents the remote device.</param>
    /// <param name="callback">The method to call when the negotiation is complete.</param>
    /// <param name="proxyEndPoint">The IPEndPoint of the SOCKS proxy server.</param>
    /// <param name="state">The state.</param>
    /// <returns>An IAsyncProxyResult that references the asynchronous connection.</returns>
    public override AsyncProxyResult BeginNegotiate(IPEndPoint remoteEp, HandShakeComplete callback,
        IPEndPoint proxyEndPoint, AsyncProxyResult asyncResult)
    {
        ProtocolComplete = callback;
        Buffer = ArrayPool<byte>.Shared.Rent(Math.Max(258, 13 + Username.Length + Password.Length));

        // first {ConnectOffset} bytes are reserved for authentication 
        handShakeLength = GetEndPointBytes(remoteEp, Buffer.AsMemory(ConnectOffset));
        // Assign all callback-visible state before BeginConnect can complete.
        var result = asyncResult ?? throw new ArgumentNullException(nameof(asyncResult));
        Server.BeginConnect(proxyEndPoint, OnConnect, Server);
        return result;
    }

    /// <summary>
    ///     Called when the socket is connected to the remote server.
    /// </summary>
    /// <param name="ar">Stores state information for this asynchronous operation as well as any user-defined data.</param>
    private void OnConnect(IAsyncResult ar)
    {
        try
        {
            Server.EndConnect(ar);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
            return;
        }

        try
        {
            Buffer[0] = 5;
            Buffer[1] = 2;
            Buffer[2] = 0;
            Buffer[3] = 2;
            Server.BeginSend(Buffer, 0, 4, SocketFlags.None, OnAuthSent,
                Server);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
        }
    }

    /// <summary>
    ///     Called when the authentication bytes have been sent.
    /// </summary>
    /// <param name="ar">Stores state information for this asynchronous operation as well as any user-defined data.</param>
    private void OnAuthSent(IAsyncResult ar)
    {
        try
        {
            HandleEndSend(ar, 4);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
            return;
        }

        try
        {
            BufferCount = 2;
            Received = 0;
            Server.BeginReceive(Buffer, 0, BufferCount, SocketFlags.None, OnAuthReceive,
                Server);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
        }
    }

    /// <summary>
    ///     Called when an authentication reply has been received.
    /// </summary>
    /// <param name="ar">Stores state information for this asynchronous operation as well as any user-defined data.</param>
    private void OnAuthReceive(IAsyncResult ar)
    {
        try
        {
            HandleEndReceive(ar);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
            return;
        }

        try
        {
            if (Received < BufferCount)
            {
                Server.BeginReceive(Buffer, Received, BufferCount - Received, SocketFlags.None,
                    OnAuthReceive, Server);
            }
            else
            {
                AuthMethod authenticate;
                switch (Buffer[1])
                {
                    case 0:
                        authenticate = new AuthNone(Server);
                        break;
                    case 2:
                        authenticate = new AuthUserPass(Server, Username, Password);
                        break;
                    default:
                        OnProtocolComplete(new SocketException());
                        return;
                }

                authenticate.BeginAuthenticate(OnAuthenticated);
            }
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
        }
    }

    /// <summary>
    ///     Called when the socket has been successfully authenticated with the server.
    /// </summary>
    /// <param name="e">The exception that has occurred while authenticating, or <em>null</em> if no error occurred.</param>
    private void OnAuthenticated(Exception? e)
    {
        if (e != null)
        {
            OnProtocolComplete(e);
            return;
        }

        try
        {
            Server.BeginSend(Buffer, ConnectOffset, handShakeLength, SocketFlags.None, OnSent,
                Server);
        }
        catch (Exception ex)
        {
            OnProtocolComplete(ex);
        }
    }

    /// <summary>
    ///     Called when the connection request has been sent.
    /// </summary>
    /// <param name="ar">Stores state information for this asynchronous operation as well as any user-defined data.</param>
    private void OnSent(IAsyncResult ar)
    {
        try
        {
            HandleEndSend(ar, BufferCount - ConnectOffset);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
            return;
        }

        try
        {
            BufferCount = 5;
            Received = 0;
            Server.BeginReceive(Buffer, 0, BufferCount, SocketFlags.None, OnReceive,
                Server);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
        }
    }

    /// <summary>
    ///     Called when a connection reply has been received.
    /// </summary>
    /// <param name="ar">Stores state information for this asynchronous operation as well as any user-defined data.</param>
    private void OnReceive(IAsyncResult ar)
    {
        try
        {
            HandleEndReceive(ar);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
            return;
        }

        try
        {
            if (Received == BufferCount)
                ProcessReply(Buffer);
            else
                Server.BeginReceive(Buffer, Received, BufferCount - Received, SocketFlags.None,
                    OnReceive, Server);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
        }
    }

    /// <summary>
    ///     Processes the received reply.
    /// </summary>
    /// <param name="buffer">The received reply</param>
    /// <exception cref="ProtocolViolationException">The received reply is invalid.</exception>
    private void ProcessReply(byte[] buffer)
    {
        int lengthToRead;
        switch (buffer[3])
        {
            case 1:
                lengthToRead = 5; //IPv4 address with port - 1 byte
                break;
            case 3:
                lengthToRead = buffer[4] + 2; //domain name with port
                break;
            case 4:
                lengthToRead = 17; //IPv6 address with port - 1 byte
                break;
            default:
                throw new ProtocolViolationException();
        }

        Received = 0;
        BufferCount = lengthToRead;
        Server.BeginReceive(Buffer, 0, BufferCount, SocketFlags.None, OnReadLast, Server);
    }

    /// <summary>
    ///     Called when the last bytes are read from the socket.
    /// </summary>
    /// <param name="ar">Stores state information for this asynchronous operation as well as any user-defined data.</param>
    private void OnReadLast(IAsyncResult ar)
    {
        try
        {
            HandleEndReceive(ar);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
            return;
        }

        try
        {
            if (Received == BufferCount)
                OnProtocolComplete(null);
            else
                Server.BeginReceive(Buffer, Received, BufferCount - Received, SocketFlags.None,
                    OnReadLast, Server);
        }
        catch (Exception e)
        {
            OnProtocolComplete(e);
        }
    }
}ParseOptions.0.json¬Q
]D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\ProxySocket\SocksHandler.csÀP/*
    Copyright ¬© 2002, The KPD-Team
    All rights reserved.
    http://www.mentalis.org/

  Redistribution and use in source and binary forms, with or without
  modification, are permitted provided that the following conditions
  are met:

    - Redistributions of source code must retain the above copyright
       notice, this list of conditions and the following disclaimer. 

    - Neither the name of the KPD-Team, nor the names of its contributors
       may be used to endorse or promote products derived from this
       software without specific prior written permission. 

  THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
  "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
  LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
  FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL
  THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
  INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
  (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
  SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
  HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
  STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
  ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
  OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;

namespace Titanium.Web.Proxy.ProxySocket;

/// <summary>
///     References the callback method to be called when the protocol negotiation is completed.
/// </summary>
internal delegate void HandShakeComplete(Exception? error);

/// <summary>
///     Implements a specific version of the SOCKS protocol. This is an abstract class; it must be inherited.
/// </summary>
internal abstract class SocksHandler
{
    /// <summary>Holds the address of the method to call when the SOCKS protocol has been completed.</summary>
    private HandShakeComplete? protocolComplete;

    // private variables
    /// <summary>Holds the value of the Server property.</summary>
    private Socket server;

    /// <summary>Holds the value of the Username property.</summary>
    private string username = string.Empty;

    private byte[]? buffer;

    /// <summary>
    ///     Initializes a new instance of the SocksHandler class.
    /// </summary>
    /// <param name="server">The socket connection with the proxy server.</param>
    /// <param name="user">The username to use when authenticating with the server.</param>
    /// <exception cref="ArgumentNullException"><c>server</c> -or- <c>user</c> is null.</exception>
    public SocksHandler(Socket server, string user)
    {
        this.server = server ?? throw new ArgumentNullException(nameof(server));
        username = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <summary>
    ///     Gets or sets the socket connection with the proxy server.
    /// </summary>
    /// <value>A Socket object that represents the connection with the proxy server.</value>
    /// <exception cref="ArgumentNullException">The specified value is null.</exception>
    protected Socket Server
    {
        get => server;
        set => server = value ?? throw new ArgumentNullException();
    }

    /// <summary>
    ///     Gets or sets the username to use when authenticating with the proxy server.
    /// </summary>
    /// <value>A string that holds the username to use when authenticating with the proxy server.</value>
    /// <exception cref="ArgumentNullException">The specified value is null.</exception>
    protected string Username
    {
        get => username;
        set => username = value ?? throw new ArgumentNullException();
    }

    /// <summary>
    ///     Gets or sets the return value of the BeginConnect call.
    /// </summary>
    /// <value>An IAsyncProxyResult object that is the return value of the BeginConnect call.</value>
    protected HandShakeComplete ProtocolComplete
    {
        get => protocolComplete ?? throw new InvalidOperationException("Protocol callback has not been assigned.");
        set => protocolComplete = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    ///     Gets or sets a byte buffer.
    /// </summary>
    /// <value>An array of bytes.</value>
    protected byte[] Buffer
    {
        get => buffer ?? throw new InvalidOperationException("Protocol buffer has not been assigned.");
        set => buffer = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    ///     Gets or sets actual data count in the buffer.
    /// </summary>
    protected int BufferCount { get; set; }

    /// <summary>
    ///     Gets or sets the number of bytes that have been received from the remote proxy server.
    /// </summary>
    /// <value>An integer that holds the number of bytes that have been received from the remote proxy server.</value>
    protected int Received { get; set; }

    /// <summary>
    ///     Converts a port number to an array of bytes.
    /// </summary>
    /// <param name="port">The port to convert.</param>
    /// <param name="buffer">The buffer which contains the result data.</param>
    /// <returns>An array of two bytes that represents the specified port.</returns>
    protected void PortToBytes(int port, Span<byte> buffer)
    {
        buffer[0] = (byte)(port / 256);
        buffer[1] = (byte)(port % 256);
    }

    /// <summary>
    ///     Converts an IP address to an array of bytes.
    /// </summary>
    /// <param name="address">The IP address to convert.</param>
    /// <returns>An array of four bytes that represents the specified IP address.</returns>
    protected byte[] AddressToBytes(long address)
    {
        var ret = new byte[4];
        ret[0] = (byte)(address % 256);
        ret[1] = (byte)(address / 256 % 256);
        ret[2] = (byte)(address / 65536 % 256);
        ret[3] = (byte)(address / 16777216);
        return ret;
    }

    /// <summary>
    ///     Reads a specified number of bytes from the Server socket.
    /// </summary>
    /// <param name="buffer">The result buffer.</param>
    /// <param name="count">The number of bytes to return.</param>
    /// <returns>An array of bytes.</returns>
    /// <exception cref="ArgumentException">The number of bytes to read is invalid.</exception>
    /// <exception cref="SocketException">An operating system error occurs while accessing the Socket.</exception>
    /// <exception cref="ObjectDisposedException">The Socket has been closed.</exception>
    protected void ReadBytes(byte[] buffer, int count)
    {
        if (count <= 0)
            throw new ArgumentException();

        var received = 0;
        while (received != count)
        {
            var recv = Server.Receive(buffer, received, count - received, SocketFlags.None);
            if (recv == 0) throw new SocketException(10054);

            received += recv;
        }
    }

    /// <summary>
    ///     Reads number of received bytes and ensures that socket was not shut down
    /// </summary>
    /// <param name="ar">IAsyncResult for receive operation</param>
    /// <returns></returns>
    protected void HandleEndReceive(IAsyncResult ar)
    {
        var recv = Server.EndReceive(ar);
        if (recv <= 0)
            throw new SocketException(10054);

        Received += recv;
    }

    /// <summary>
    ///     Verifies that whole buffer was sent successfully
    /// </summary>
    /// <param name="ar">IAsyncResult for receive operation</param>
    /// <param name="expectedLength">Length of buffer that was sent</param>
    /// <returns></returns>
    protected void HandleEndSend(IAsyncResult ar, int expectedLength)
    {
        if (Server.EndSend(ar) < expectedLength)
            throw new SocketException(10054);
    }

    protected virtual void OnProtocolComplete(Exception? exception)
    {
        if (buffer != null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = null;
        }

        ProtocolComplete(exception);
    }

    /// <summary>
    ///     Starts negotiating with a SOCKS proxy server.
    /// </summary>
    /// <param name="host">The remote server to connect to.</param>
    /// <param name="port">The remote port to connect to.</param>
    public abstract void Negotiate(string host, int port);

    /// <summary>
    ///     Starts negotiating with a SOCKS proxy server.
    /// </summary>
    /// <param name="remoteEp">The remote endpoint to connect to.</param>
    public abstract void Negotiate(IPEndPoint remoteEp);

    /// <summary>
    ///     Starts negotiating asynchronously with a SOCKS proxy server.
    /// </summary>
    /// <param name="remoteEp">An IPEndPoint that represents the remote device. </param>
    /// <param name="callback">The method to call when the connection has been established.</param>
    /// <param name="proxyEndPoint">The IPEndPoint of the SOCKS proxy server.</param>
    /// <param name="state">The state.</param>
    /// <returns>An IAsyncProxyResult that references the asynchronous connection.</returns>
    public abstract AsyncProxyResult BeginNegotiate(IPEndPoint remoteEp, HandShakeComplete callback,
        IPEndPoint proxyEndPoint, AsyncProxyResult asyncResult);

    /// <summary>
    ///     Starts negotiating asynchronously with a SOCKS proxy server.
    /// </summary>
    /// <param name="host">The remote server to connect to.</param>
    /// <param name="port">The remote port to connect to.</param>
    /// <param name="callback">The method to call when the connection has been established.</param>
    /// <param name="proxyEndPoint">The IPEndPoint of the SOCKS proxy server.</param>
    /// <param name="state">The state.</param>
    /// <returns>An IAsyncProxyResult that references the asynchronous connection.</returns>
    public abstract AsyncProxyResult BeginNegotiate(string host, int port, HandShakeComplete callback,
        IPEndPoint proxyEndPoint, AsyncProxyResult asyncResult);
}ParseOptions.0.jsoníã
SD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\RequestHandler.cs§äusing System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy;

/// <summary>
///     Handle the request
/// </summary>
public partial class ProxyServer
{
    /// <summary>
    ///     This is the core request handler method for a particular connection from client.
    ///     Will create new session (request/response) sequence until
    ///     client/server abruptly terminates connection or by normal HTTP termination.
    /// </summary>
    /// <param name="endPoint">The proxy endpoint.</param>
    /// <param name="clientStream">The client stream.</param>
    /// <param name="cancellationTokenSource">The cancellation token source for this async task.</param>
    /// <param name="connectArgs">The Connect request if this is a HTTPS request from explicit endpoint.</param>
    /// <param name="prefetchConnectionTask">Prefetched server connection for current client using Connect/SNI headers.</param>
    /// <param name="isHttps">Is HTTPS</param>
    private async Task HandleHttpSessionRequest(ProxyEndPoint endPoint, HttpClientStream clientStream,
        CancellationTokenSource cancellationTokenSource, TunnelConnectSessionEventArgs? connectArgs = null,
        Task<TcpServerConnection?>? prefetchConnectionTask = null, bool isHttps = false)
    {
        var connectRequest = connectArgs?.HttpClient.ConnectRequest;

        var prefetchTask = prefetchConnectionTask;
        TcpServerConnection? connection = null;
        var closeServerConnection = false;

        try
        {
            var cancellationToken = cancellationTokenSource.Token;

            // Loop through each subsequent request on this particular client connection
            // (assuming HTTP connection is kept alive by client)
            while (true)
            {
                if (clientStream.IsClosed) return;

                // read the request line
                var requestLine = await clientStream.ReadRequestLine(cancellationToken);
                if (requestLine.IsEmpty()) return;

                var args = new SessionEventArgs(this, endPoint, clientStream, connectRequest, cancellationTokenSource)
                {
                    UserData = connectArgs?.UserData
                };

                var request = args.HttpClient.Request;
                if (isHttps) request.IsHttps = true;

                try
                {
                    try
                    {
                        // Read the request headers in to unique and non-unique header collections
                        await HeaderParser.ReadHeaders(clientStream, args.HttpClient.Request.Headers,
                            cancellationToken);

                        if (connectRequest != null)
                        {
                            request.IsHttps = connectRequest.IsHttps;
                            request.Authority = connectRequest.Authority;
                        }

                        request.RequestUriString8 = requestLine.RequestUri;

                        request.Method = requestLine.Method;
                        request.HttpVersion = requestLine.Version;

                        // we need this to syphon out data from connection if API user changes them.
                        request.SetOriginalHeaders();

                        // If user requested interception do it
                        await OnBeforeRequest(args);

                        if (!args.IsTransparent && !args.IsSocks)
                        {
                            // proxy authorization check
                            if (connectRequest == null && await CheckAuthorization(args) == false)
                            {
                                await OnBeforeResponse(args);

                                // send the response
                                await clientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);
                                return;
                            }

                            PrepareRequestHeaders(request.Headers);
                            request.Host = request.RequestUri.Authority;
                        }

                        // if win auth is enabled
                        // we need a cache of request body
                        // so that we can send it after authentication in WinAuthHandler.cs
                        if (args.EnableWinAuth && request.HasBody) await args.GetRequestBody(cancellationToken);

                        var response = args.HttpClient.Response;

                        if (request.CancelRequest)
                        {
                            if (!(Enable100ContinueBehaviour && request.ExpectContinue))
                                // syphon out the request body from client before setting the new body
                                await args.SyphonOutBodyAsync(true, cancellationToken);

                            await HandleHttpSessionResponse(args);

                            if (!response.KeepAlive) return;

                            continue;
                        }

                        // If prefetch task is available.
                        if (connection == null && prefetchTask != null)
                        {
                            try
                            {
                                connection = await prefetchTask;
                            }
                            catch (SocketException e)
                            {
                                if (e.SocketErrorCode != SocketError.HostNotFound) throw;
                            }

                            prefetchTask = null;
                        }

                        if (connection != null)
                        {
                            var socket = connection.TcpSocket;
                            var part1 = socket.Poll(1000, SelectMode.SelectRead);
                            var part2 = socket.Available == 0;
                            if (part1 & part2)
                            {
                                //connection is closed
                                await TcpConnectionFactory.Release(connection, true);
                                connection = null;
                            }
                        }

                        // create a new connection if cache key changes.
                        // only gets hit when connection pool is disabled.
                        // or when prefetch task has a unexpectedly different connection.
                        if (connection != null
                            && await TcpConnectionFactory.GetConnectionCacheKey(this, args,
                                clientStream.Connection.NegotiatedApplicationProtocol)
                            != connection.CacheKey)
                        {
                            await TcpConnectionFactory.Release(connection);
                            connection = null;
                        }

                        var result = await HandleHttpSessionRequest(args, connection,
                            clientStream.Connection.NegotiatedApplicationProtocol,
                            cancellationToken, cancellationTokenSource);

                        var newConnection = result.LatestConnection;
                        if (connection != newConnection && connection != null)
                            await TcpConnectionFactory.Release(connection);

                        // update connection to latest used
                        connection = result.LatestConnection;

                        closeServerConnection = !result.Continue;

                        // throw if exception happened
                        if (result.Exception != null) throw result.Exception;

                        if (!result.Continue) return;

                        // user requested
                        if (args.HttpClient.CloseServerConnection)
                        {
                            closeServerConnection = true;
                            return;
                        }

                        // if connection is closing exit
                        if (!response.KeepAlive)
                        {
                            closeServerConnection = true;
                            return;
                        }

                        if (cancellationTokenSource.IsCancellationRequested)
                            throw new Exception("Session was terminated by user.");

                        // Release the server connection back to the shared pool after each HTTP session
                        // (rather than holding it for the whole client connection). This is more efficient
                        // when a client idly holds a server connection between sessions without using it.
                        // We only get here when the response was persistent (response.KeepAlive above) and its
                        // body was fully received, so the connection is at a clean message boundary and safe to reuse.
                        // WinAuth (NTLM/Negotiate) connections are deliberately NOT returned to the shared pool:
                        // they are authenticated to a specific identity and are connection-oriented, so they stay
                        // bound to this client session (reused for its subsequent requests) and are closed when the
                        // client connection ends, never shared with another client.
                        if (EnableConnectionPool && connection != null
                                                 && !connection.IsWinAuthenticated)
                        {
                            await TcpConnectionFactory.Release(connection);
                            connection = null;
                        }
                    }
                    catch (Exception e) when (!(e is ProxyHttpException))
                    {
                        throw new ProxyHttpException("Error occured whilst handling session request", e, args);
                    }
                }
                catch (Exception e)
                {
                    args.Exception = e;
                    closeServerConnection = true;
                    throw;
                }
                finally
                {
                    await OnAfterResponse(args);
                    args.Dispose();
                }
            }
        }
        finally
        {
            if (connection != null) await TcpConnectionFactory.Release(connection, closeServerConnection);

            await TcpConnectionFactory.Release(prefetchTask, closeServerConnection);
        }
    }

    private async Task<RetryResult> HandleHttpSessionRequest(SessionEventArgs args,
        TcpServerConnection? serverConnection, SslApplicationProtocol sslApplicationProtocol,
        CancellationToken cancellationToken, CancellationTokenSource cancellationTokenSource)
    {
        args.HttpClient.Request.Locked = true;

        // do not cache server connections for WebSockets
        var noCache = args.HttpClient.Request.UpgradeToWebSocket;

        if (noCache) serverConnection = null;

        // a connection generator task with captured parameters via closure.
        var generator = () =>
            TcpConnectionFactory.GetServerConnection(this,
                args,
                false,
                sslApplicationProtocol,
                noCache,
                cancellationToken);

        /// Retry with new connection if the initial stream.WriteAsync call to server fails.
        /// i.e if request line and headers failed to get send.
        /// Do not retry after reading data from client stream, 
        /// because subsequent try will not have data to read from client 
        /// and will hang at clientStream.ReadAsync call.
        /// So, throw RetryableServerConnectionException only when we are sure we can retry safely.
        return await RetryPolicy<RetryableServerConnectionException>().ExecuteAsync(async connection =>
        {
            // set the connection and send request headers
            args.HttpClient.SetConnection(connection);

            args.TimeLine["Connection Ready"] = DateTime.UtcNow;

            if (args.HttpClient.Request.UpgradeToWebSocket)
            {
                // connectRequest can be null for SOCKS connection
                if (args.HttpClient.ConnectRequest != null)
                    args.HttpClient.ConnectRequest!.TunnelType = TunnelType.Websocket;

                // if upgrading to websocket then relay the request without reading the contents
                await HandleWebSocketUpgrade(args, args.ClientStream, connection, cancellationTokenSource,
                    cancellationToken);
                return false;
            }

            // construct the web request that we are going to issue on behalf of the client.
            await HandleHttpSessionRequest(args);
            return true;
        }, generator, serverConnection);
    }

    private async Task HandleHttpSessionRequest(SessionEventArgs args)
    {
        var cancellationToken = args.CancellationTokenSource.Token;
        var request = args.HttpClient.Request;

        var body = request.CompressBodyAndUpdateContentLength();

        await args.HttpClient.SendRequest(Enable100ContinueBehaviour, args.IsTransparent,
            cancellationToken);

        // If a successful 100 continue request was made, inform that to the client and reset response
        if (request.ExpectationSucceeded)
        {
            var writer = args.ClientStream;
            var response = args.HttpClient.Response;

            var headerBuilder = new HeaderBuilder();
            headerBuilder.WriteResponseLine(response.HttpVersion, response.StatusCode, response.StatusDescription);
            headerBuilder.WriteHeaders(response.Headers);
            await writer.WriteHeadersAsync(headerBuilder, cancellationToken);

            await args.ClearResponse(cancellationToken);
        }

        // send body to server if available
        if (request.HasBody)
        {
            if (request.IsBodyRead)
                await args.HttpClient.Connection.Stream.WriteBodyAsync(body!, request.IsChunked, cancellationToken);
            else if (!request.ExpectationFailed)
                // get the request body unless an unsuccessful 100 continue request was made
                await args.CopyRequestBodyAsync(args.HttpClient.Connection.Stream, TransformationMode.None,
                    cancellationToken);
        }

        args.TimeLine["Request Sent"] = DateTime.UtcNow;

        // parse and send response
        await HandleHttpSessionResponse(args);
    }

    /// <summary>
    ///     Prepare the request headers so that we can avoid encodings not parseable by this proxy
    /// </summary>
    private void PrepareRequestHeaders(HeaderCollection requestHeaders)
    {
        var acceptEncoding = requestHeaders.GetHeaderValueOrNull(KnownHeaders.AcceptEncoding);

        if (acceptEncoding != null)
        {
            var supportedAcceptEncoding = new List<string>();

            // only allow proxy supported compressions
            supportedAcceptEncoding.AddRange(acceptEncoding.Split(',')
                .Select(x => x.Trim())
                .Where(x => ProxyConstants.ProxySupportedCompressions.Contains(x)));

            // uncompressed is always supported by proxy
            supportedAcceptEncoding.Add("identity");

            requestHeaders.SetOrAddHeaderValue(KnownHeaders.AcceptEncoding,
                string.Join(", ", supportedAcceptEncoding));
        }

        requestHeaders.FixProxyHeaders();
    }

    /// <summary>
    ///     Invoke before request handler if it is set.
    /// </summary>
    /// <param name="args">The session event arguments.</param>
    /// <returns></returns>
    private async Task OnBeforeRequest(SessionEventArgs args)
    {
        args.TimeLine["Request Received"] = DateTime.UtcNow;

        if (BeforeRequest != null) await BeforeRequest.InvokeAsync(this, args, ExceptionFunc);
    }

    /// <summary>
    ///     Invoke before request handler if it is set.
    /// </summary>
    /// <param name="request">The COONECT request.</param>
    /// <returns></returns>
    internal async Task OnBeforeUpStreamConnectRequest(ConnectRequest request)
    {
        if (BeforeUpStreamConnectRequest != null)
            await BeforeUpStreamConnectRequest.InvokeAsync(this, request, ExceptionFunc);
    }

    internal bool ShouldCallBeforeRequestBodyWrite()
    {
        return OnRequestBodyWrite != null;
    }

    internal async Task OnBeforeRequestBodyWrite(BeforeBodyWriteEventArgs args)
    {
        if (OnRequestBodyWrite != null)
        {
            await OnRequestBodyWrite.InvokeAsync(this, args, ExceptionFunc);
        }
    }
}ParseOptions.0.jsonÜB
TD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\ResponseHandler.csòAusing System;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network.WinAuth.Security;

namespace Titanium.Web.Proxy;

/// <summary>
///     Handle the response from server.
/// </summary>
public partial class ProxyServer
{
    /// <summary>
    ///     Called asynchronously when a request was successful and we received the response.
    /// </summary>
    /// <param name="args">The session event arguments.</param>
    /// <returns> The task.</returns>
    private async Task HandleHttpSessionResponse(SessionEventArgs args)
    {
        var cancellationToken = args.CancellationTokenSource.Token;

        // read response & headers from server
        await args.HttpClient.ReceiveResponse(cancellationToken);

        // Server may send expect-continue even if not asked for it in request.
        // According to spec "the client can simply discard this interim response."
        if (args.HttpClient.Response.StatusCode == (int)HttpStatusCode.Continue)
        {
            await args.ClearResponse(cancellationToken);
            await args.HttpClient.ReceiveResponse(cancellationToken);
        }

        args.TimeLine["Response Received"] = DateTime.UtcNow;

        var response = args.HttpClient.Response;
        args.ReRequest = false;

        // check for windows authentication
        var serverWinAuthReRequest = false;
        if (args.EnableWinAuth)
        {
            if (response.StatusCode == (int)HttpStatusCode.Unauthorized)
            {
                await Handle401UnAuthorized(args);

                // A 401 that triggers a re-request is a connection-oriented NTLM/Negotiate
                // handshake leg (ISC_REQ_CONNECTION); it must continue on the SAME server connection.
                serverWinAuthReRequest = args.ReRequest;
            }
            // don't mark the connection as authenticated on a 407, otherwise the
            // upstream proxy authentication state below would be corrupted.
            else if (response.StatusCode != (int)HttpStatusCode.ProxyAuthenticationRequired)
                WinAuthEndPoint.AuthenticatedResponse(args.HttpClient.Data);
        }

        if (response.StatusCode == (int)HttpStatusCode.ProxyAuthenticationRequired)
            await Handle407ProxyAuthorization(args);

        // save original values so that if user changes them
        // we can still use original values when syphoning out data from attached tcp connection.
        response.SetOriginalHeaders();

        // if user requested call back then do it
        if (!response.Locked) await OnBeforeResponse(args);

        // it may changed in the user event
        response = args.HttpClient.Response;

        var clientStream = args.ClientStream;

        // user set custom response by ignoring original response from server.
        if (response.Locked)
        {
            // write custom user response with body and return.
            await clientStream.WriteResponseAsync(response, cancellationToken);

            // if the user requested a streamed body, produce it now without buffering.
            if (response.StreamBodyWriter != null && !response.IsBodySent)
            {
                var bodyWriter = new BodyStreamWriter(clientStream, response.IsChunked);
                await response.StreamBodyWriter(bodyWriter, cancellationToken);
                await bodyWriter.CompleteAsync(cancellationToken);
                response.IsBodySent = true;
            }

            if (args.HttpClient.HasConnection && !args.HttpClient.CloseServerConnection)
                // syphon out the original response body from server connection
                // so that connection will be good to be reused.
                await args.SyphonOutBodyAsync(false, cancellationToken);

            return;
        }

        // if user requested to send request again
        // likely after making modifications from User Response Handler
        if (args.ReRequest)
        {
            var serverConnection = args.HttpClient.HasConnection ? args.HttpClient.Connection : null;

            // Connection-oriented auth handshakes must reuse the SAME server connection for every leg:
            //  - a 407 from an upstream proxy (proxy authentication), and
            //  - a 401 from the origin server handled by NTLM/Negotiate (server authentication).
            // Any other re-request (e.g. user-initiated from the response handler) may target a
            // different destination, so it gets a fresh connection.
            var keepConnectionForAuth = args.HttpClient.HasConnection &&
                                        ShouldReuseConnectionForAuthReRequest(response.StatusCode,
                                            serverWinAuthReRequest);

            // Always drain the challenge response body from the current server connection first,
            // so the connection is clean before it is reused or released.
            // (Never release/pool a connection while its body is still on the wire.)
            await args.ClearResponse(cancellationToken);

            if (args.HttpClient.HasConnection && !keepConnectionForAuth)
            {
                serverConnection = null;
                await TcpConnectionFactory.Release(args.HttpClient.Connection);
            }

            var result = await HandleHttpSessionRequest(args, serverConnection,
                args.ClientConnection.NegotiatedApplicationProtocol,
                cancellationToken, args.CancellationTokenSource);
            if (result.LatestConnection != null) args.HttpClient.SetConnection(result.LatestConnection);

            return;
        }

        response.Locked = true;

        if (!args.IsTransparent && !args.IsSocks) response.Headers.FixProxyHeaders();

        await clientStream.WriteResponseAsync(response, cancellationToken);

        if (response.OriginalHasBody)
        {
            if (response.IsBodySent)
            {
                // syphon out body
                await args.SyphonOutBodyAsync(false, cancellationToken);
            }
            else
            {
                // Copy body if exists
                var serverStream = args.HttpClient.Connection.Stream;
                await serverStream.CopyBodyAsync(response, false, clientStream, TransformationMode.None,
                    false, args, cancellationToken);
            }

            response.IsBodyReceived = true;
        }

        args.TimeLine["Response Sent"] = DateTime.UtcNow;
    }

    /// <summary>
    ///     Decides whether a re-request must reuse the same server connection.
    ///     Connection-oriented authentication handshakes (proxy 407, or a server 401 handled by
    ///     NTLM/Negotiate) require every leg to travel over the same TCP connection.
    /// </summary>
    internal static bool ShouldReuseConnectionForAuthReRequest(int responseStatusCode, bool serverWinAuthReRequest)
    {
        return responseStatusCode == (int)HttpStatusCode.ProxyAuthenticationRequired || serverWinAuthReRequest;
    }

    /// <summary>
    ///     Invoke before response if it is set.
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    private async Task OnBeforeResponse(SessionEventArgs args)
    {
        if (BeforeResponse != null) await BeforeResponse.InvokeAsync(this, args, ExceptionFunc);
    }

    /// <summary>
    ///     Invoke after response if it is set.
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    private async Task OnAfterResponse(SessionEventArgs args)
    {
        if (AfterResponse != null) await AfterResponse.InvokeAsync(this, args, ExceptionFunc);
    }
    internal bool ShouldCallBeforeResponseBodyWrite()
    {
        return OnResponseBodyWrite != null;
    }

    internal async Task OnBeforeResponseBodyWrite(BeforeBodyWriteEventArgs args)
    {
        if (OnResponseBodyWrite != null)
        {
            await OnResponseBodyWrite.InvokeAsync(this, args, ExceptionFunc);
        }
    }
}ParseOptions.0.json¸
ZD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\Shared\ProxyConstants.csàusing System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Shared;

/// <summary>
///     Literals shared by Proxy Server
/// </summary>
internal class ProxyConstants
{
    internal static readonly char DotSplit = '.';

    internal static readonly string NewLine = "\r\n";
    internal static readonly byte[] NewLineBytes = { (byte)'\r', (byte)'\n' };

    internal static readonly HashSet<string> ProxySupportedCompressions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            KnownHeaders.ContentEncodingGzip.String,
            KnownHeaders.ContentEncodingDeflate.String,
            KnownHeaders.ContentEncodingBrotli.String
        };

    internal static readonly Regex CnRemoverRegex =
        new(@"^CN\s*=\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}ParseOptions.0.jsonà*
WD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\SocksClientHandler.csó)using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>
    ///     This is called when this proxy acts as a reverse proxy (like a real http server).
    ///     So for HTTPS requests we would start SSL negotiation right away without expecting a CONNECT request from client
    /// </summary>
    /// <param name="endPoint">The transparent endpoint.</param>
    /// <param name="clientConnection">The client connection.</param>
    /// <returns></returns>
    private async Task HandleClient(SocksProxyEndPoint endPoint, TcpClientConnection clientConnection)
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        var stream = clientConnection.GetStream();
        var buffer = BufferPool.GetBuffer();
        var port = 0;
        try
        {
            var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            if (read < 3) return;

            if (buffer[0] == 4)
            {
                if (read < 9 || buffer[1] != 1)
                    // not a connect request
                    return;

                port = (buffer[2] << 8) + buffer[3];

                buffer[0] = 0;
                buffer[1] = 90; // request granted
                await stream.WriteAsync(buffer, 0, 8, cancellationToken);
            }
            else if (buffer[0] == 5)
            {
                int authenticationMethodCount = buffer[1];
                if (read < authenticationMethodCount + 2) return;

                var acceptedMethod = 255;
                for (var i = 0; i < authenticationMethodCount; i++)
                {
                    int method = buffer[i + 2];
                    if (method == 0 && ProxyBasicAuthenticateFunc == null)
                    {
                        acceptedMethod = 0;
                        break;
                    }

                    if (method == 2)
                    {
                        acceptedMethod = 2;
                        break;
                    }
                }

                buffer[1] = (byte)acceptedMethod;
                await stream.WriteAsync(buffer, 0, 2, cancellationToken);

                if (acceptedMethod == 255)
                    // no acceptable method
                    return;

                if (acceptedMethod == 2)
                {
                    read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (read < 3 || buffer[0] != 1)
                        // authentication version should be 1
                        return;

                    int userNameLength = buffer[1];
                    if (read < 3 + userNameLength) return;

                    var userName = Encoding.ASCII.GetString(buffer, 2, userNameLength);

                    int passwordLength = buffer[2 + userNameLength];
                    if (read < 3 + userNameLength + passwordLength) return;

                    var password = Encoding.ASCII.GetString(buffer, 3 + userNameLength, passwordLength);
                    var success = true;
                    if (ProxyBasicAuthenticateFunc != null)
                        success = await ProxyBasicAuthenticateFunc.Invoke(null, userName, password);

                    buffer[1] = success ? (byte)0 : (byte)1;
                    await stream.WriteAsync(buffer, 0, 2, cancellationToken);
                    if (!success) return;
                }

                read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                if (read < 10 || buffer[1] != 1) return;

                int portIdx;
                switch (buffer[3])
                {
                    case 1:
                        // IPv4
                        portIdx = 8;
                        break;
                    case 3:
                        // Domainname
                        portIdx = buffer[4] + 5;

#if DEBUG
                            var hostname = new ByteString(buffer.AsMemory(5, buffer[4]));
                            string hostnameStr = hostname.GetString();
#endif
                        break;
                    case 4:
                        // IPv6
                        portIdx = 20;
                        break;
                    default:
                        return;
                }

                if (read < portIdx + 2) return;

                port = (buffer[portIdx] << 8) + buffer[portIdx + 1];
                buffer[1] = 0; // succeeded
                await stream.WriteAsync(buffer, 0, read, cancellationToken);
            }
            else
            {
                return;
            }
        }
        finally
        {
            BufferPool.ReturnBuffer(buffer);
        }

        await HandleClient(endPoint, clientConnection, port, cancellationTokenSource, cancellationToken);
    }
}ParseOptions.0.json“
^D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\SystemProxyBypassRuleMode.cs⁄namespace Titanium.Web.Proxy;

/// <summary>
///     Controls how configured bypass rules are combined with the current Windows system proxy bypass list.
/// </summary>
public enum SystemProxyBypassRuleMode
{
    /// <summary>
    ///     Preserve the current bypass rules and add the configured rules.
    /// </summary>
    Merge,

    /// <summary>
    ///     Replace the current bypass rules with the configured rules.
    /// </summary>
    Replace
}
ParseOptions.0.jsonÈ
aD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\SystemProxyLoopbackPlacement.csÓnamespace Titanium.Web.Proxy;

/// <summary>
///     Controls where the <c>&lt;-loopback&gt;</c> rule is placed within the Windows system proxy bypass list.
/// </summary>
public enum SystemProxyLoopbackPlacement
{
    /// <summary>
    ///     Place the <c>&lt;-loopback&gt;</c> rule before all other bypass rules.
    /// </summary>
    First,

    /// <summary>
    ///     Place the <c>&lt;-loopback&gt;</c> rule after all other bypass rules.
    /// </summary>
    Last
}
ParseOptions.0.jsonÕ
XD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\SystemProxySettings.cs€using System;
using System.Collections.Generic;

namespace Titanium.Web.Proxy;

/// <summary>
///     Options applied when configuring an explicit endpoint as the Windows system proxy.
/// </summary>
public class SystemProxySettings
{
    private const string SubtractImplicitLoopbackRule = "<-loopback>";

    /// <summary>
    ///     Gets or sets whether loopback requests should use the proxy.
    /// </summary>
    /// <remarks>
    ///     This adds the WinINET <c>&lt;-loopback&gt;</c> rule. It only affects applications that honor compatible
    ///     Windows system proxy settings and can expose otherwise trusted local traffic to the proxy.
    /// </remarks>
    public bool ProxyLoopback { get; set; }

    /// <summary>
    ///     Gets or sets where the <c>&lt;-loopback&gt;</c> rule is placed in the bypass list.
    /// </summary>
    /// <remarks>
    ///     Ordering matters because rules are evaluated left-to-right; a subtractive rule such as
    ///     <c>&lt;-loopback&gt;</c> has a different effect before versus after a contradicting bypass rule.
    /// </remarks>
    public SystemProxyLoopbackPlacement ProxyLoopbackPlacement { get; set; } = SystemProxyLoopbackPlacement.First;

    /// <summary>
    ///     Gets additional WinINET host patterns that should bypass the proxy.
    /// </summary>
    public IList<string> BypassRules { get; } = new List<string>();

    /// <summary>
    ///     Gets or sets how <see cref="BypassRules"/> are combined with the current Windows system proxy bypass list.
    /// </summary>
    public SystemProxyBypassRuleMode BypassRuleMode { get; set; } = SystemProxyBypassRuleMode.Merge;

    /// <summary>
    ///     Validates the configured bypass rules, throwing when any rule is malformed.
    /// </summary>
    internal void Validate()
    {
        foreach (var rule in BypassRules)
        {
            if (string.IsNullOrWhiteSpace(rule))
                throw new ArgumentException("System proxy bypass rules cannot be null or empty.");

            if (rule.Contains(";"))
                throw new ArgumentException(
                    "Add each system proxy bypass rule separately; rules cannot contain semicolons.");
        }
    }

    internal string BuildProxyOverride(string? currentProxyOverride)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (ProxyLoopback && ProxyLoopbackPlacement == SystemProxyLoopbackPlacement.First)
            AddRule(result, seen, SubtractImplicitLoopbackRule);

        if (BypassRuleMode == SystemProxyBypassRuleMode.Merge && !string.IsNullOrWhiteSpace(currentProxyOverride))
            foreach (var rule in currentProxyOverride!.Split(';'))
                AddRule(result, seen, rule);

        foreach (var rule in BypassRules) AddRule(result, seen, rule);

        if (ProxyLoopback && ProxyLoopbackPlacement == SystemProxyLoopbackPlacement.Last)
            AddRule(result, seen, SubtractImplicitLoopbackRule);

        return string.Join(";", result);
    }

    private static void AddRule(List<string> result, HashSet<string> seen, string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return;

        var normalizedRule = rule!.Trim();
        if (seen.Add(normalizedRule)) result.Add(normalizedRule);
    }
}
ParseOptions.0.jsonÁE
]D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\TransparentClientHandler.csDusing System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.StreamExtended;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>
    ///     This is called when this proxy acts as a reverse proxy (like a real http server).
    ///     So for HTTPS requests we would start SSL negotiation right away without expecting a CONNECT request from client
    /// </summary>
    /// <param name="endPoint">The transparent endpoint.</param>
    /// <param name="clientConnection">The client connection.</param>
    /// <returns></returns>
    private Task HandleClient(TransparentProxyEndPoint endPoint, TcpClientConnection clientConnection)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;
        return HandleClient(endPoint, clientConnection, endPoint.Port, cancellationTokenSource, cancellationToken);
    }

    private async Task HandleClient(TransparentBaseProxyEndPoint endPoint, TcpClientConnection clientConnection,
        int port, CancellationTokenSource cancellationTokenSource, CancellationToken cancellationToken)
    {
        var isHttps = false;
        var clientStream = new HttpClientStream(this, clientConnection, clientConnection.GetStream(), BufferPool,
            cancellationToken);

        try
        {
            var clientHelloInfo = await SslTools.PeekClientHello(clientStream, BufferPool, cancellationToken);

            if (clientHelloInfo != null)
            {
                var httpsHostName = clientHelloInfo.GetServerName() ?? endPoint.GenericCertificateName;

                var args = new BeforeSslAuthenticateEventArgs(this, clientConnection, cancellationTokenSource,
                    httpsHostName);

                // seed the forward target from the endpoint's fixed forward configuration (if any);
                // the BeforeSslAuthenticate event can still override it per request.
                var forwardHost = endPoint.ForwardHost;
                if (forwardHost != null && forwardHost.Length != 0)
                    args.ForwardHttpsHostName = forwardHost;
                if (endPoint.ForwardPort is int forwardPort)
                    args.ForwardHttpsPort = forwardPort;

                await endPoint.InvokeBeforeSslAuthenticate(this, args, ExceptionFunc);

                if (cancellationTokenSource.IsCancellationRequested)
                    throw new Exception("Session was terminated by user.");

                if (endPoint.DecryptSsl && args.DecryptSsl)
                {
                    var sslProtocol = clientHelloInfo.SslProtocol & SupportedSslProtocols;
                    if (sslProtocol == SslProtocols.None)
                    {
                        throw new Exception("Unsupported client SSL version.");
                    }

                    clientStream.Connection.SslProtocol = sslProtocol;

                    // do client authentication using certificate
                    X509Certificate2? certificate = null;
                    SslStream? sslStream = null;
                    try
                    {
                        sslStream = new SslStream(clientStream, false);

                        var certName = HttpHelper.GetWildCardDomainName(httpsHostName,
                            CertificateManager.DisableWildCardCertificates);
                        certificate = endPoint.GenericCertificate ??
                                      await CertificateManager.CreateServerCertificate(certName);
                        if (certificate == null)
                            throw new InvalidOperationException(
                                $"Could not create a server certificate for '{certName}'.");

                        // Successfully managed to authenticate the client using the certificate
                        await sslStream.AuthenticateAsServerAsync(certificate, false, SslProtocols.Tls12, false);

                        // HTTPS server created - we can now decrypt the client's traffic
                        clientStream = new HttpClientStream(this, clientStream.Connection, sslStream, BufferPool,
                            cancellationToken);
                        sslStream = null; // clientStream was created, no need to keep SSL stream reference
                        isHttps = true;
                    }
                    catch (Exception e)
                    {
                        sslStream?.Dispose();

                        var certName = certificate?.GetNameInfo(X509NameType.SimpleName, false);
                        var session = new SessionEventArgs(this, endPoint, clientStream, null, cancellationTokenSource);
                        throw new ProxyConnectException(
                            $"Couldn't authenticate host '{httpsHostName}' with certificate '{certName}'.", e, session);
                    }
                }
                else
                {
                    var sessionArgs = new SessionEventArgs(this, endPoint, clientStream, null, cancellationTokenSource);
                    var forwardHttpsHostName = args.ForwardHttpsHostName ??
                                               throw new InvalidOperationException("Forward HTTPS host is not set.");
                    var connection = (await TcpConnectionFactory.GetServerConnection(this, forwardHttpsHostName,
                        args.ForwardHttpsPort,
                        HttpHeader.VersionUnknown, false, null,
                        true, sessionArgs, UpStreamEndPoint,
                        UpStreamHttpsProxy, true, false, cancellationToken))!;

                    try
                    {
                        var available = clientStream.Available;

                        if (available > 0)
                        {
                            // send the buffered data
                            var data = BufferPool.GetBuffer();
                            try
                            {
                                // clientStream.Available should be at most BufferSize because it is using the same buffer size
                                var remaining = available;
                                while (remaining > 0)
                                {
                                    var bytesRead = await clientStream.ReadAsync(data, 0, remaining, cancellationToken);
                                    if (bytesRead == 0) break;

                                    remaining -= bytesRead;
                                    await connection.Stream.WriteAsync(data, 0, bytesRead, true, cancellationToken);
                                }
                            }
                            finally
                            {
                                BufferPool.ReturnBuffer(data);
                            }
                        }

                        if (!clientStream.IsClosed && !connection.Stream.IsClosed)
                            await TcpHelper.SendRaw(clientStream, connection.Stream, BufferPool,
                                null, null, cancellationTokenSource, ExceptionFunc);
                    }
                    finally
                    {
                        await TcpConnectionFactory.Release(connection, true);
                    }

                    return;
                }
            }

            // HTTPS server created - we can now decrypt the client's traffic
            // Now create the request
            await HandleHttpSessionRequest(endPoint, clientStream, cancellationTokenSource, isHttps: isHttps);
        }
        catch (ProxyException e)
        {
            OnException(clientStream, e);
        }
        catch (IOException e)
        {
            OnException(clientStream, new Exception("Connection was aborted", e));
        }
        catch (SocketException e)
        {
            OnException(clientStream, new Exception("Could not connect", e));
        }
        catch (Exception e)
        {
            OnException(clientStream, new Exception("Error occured in whilst handling the client", e));
        }
        finally
        {
            clientStream.Dispose();
        }
    }
}ParseOptions.0.json™#
_D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\WebSocket\WebSocketDecoder.cs±"using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy;

public class WebSocketDecoder
{
    private byte[] buffer;

    private long bufferLength;

    internal WebSocketDecoder(IBufferPool bufferPool)
    {
        buffer = new byte[bufferPool.BufferSize];
    }

    public IEnumerable<WebSocketFrame> Decode(byte[] data, int offset, int count)
    {
        var buffer = data.AsMemory(offset, count);

        var copied = false;
        if (bufferLength > 0)
        {
            // already have remaining data
            buffer = CopyToBuffer(buffer);
            copied = true;
        }

        while (true)
        {
            var data1 = buffer.Span;
            if (!IsDataEnough(data1)) break;

            var opCode = (WebsocketOpCode)(data1[0] & 0xf);
            var isFinal = (data1[0] & 0x80) != 0;
            var b = data1[1];
            long size = b & 0x7f;

            // todo: size > int.Max??

            var masked = (b & 0x80) != 0;

            var idx = 2;
            if (size > 125)
            {
                if (size == 126)
                {
                    size = (data1[2] << 8) + data1[3];
                    idx = 4;
                }
                else
                {
                    size = ((long)data1[2] << 56) + ((long)data1[3] << 48) + ((long)data1[4] << 40) +
                           ((long)data1[5] << 32) +
                           ((long)data1[6] << 24) + (data1[7] << 16) + (data1[8] << 8) + data1[9];
                    idx = 10;
                }
            }

            if (data1.Length < idx + size) break;

            if (masked)
            {
                //mask = (uint)(((long)data1[idx++] << 24) + (data1[idx++] << 16) + (data1[idx++] << 8) + data1[idx++]);
                //mask = (uint)(data1[idx++] + (data1[idx++] << 8) + (data1[idx++] << 16) + ((long)data1[idx++] << 24));
                var uData = MemoryMarshal.Cast<byte, uint>(data1.Slice(idx, (int)size + 4));
                idx += 4;

                var mask = uData[0];
                var size1 = size;
                if (size > 4)
                {
                    uData = uData.Slice(1);
                    for (var i = 0; i < uData.Length; i++) uData[i] = uData[i] ^ mask;

                    size1 -= uData.Length * 4;
                }

                if (size1 > 0)
                {
                    var pos = (int)(idx + size - size1);
                    data1[pos] ^= (byte)mask;

                    if (size1 > 1) data1[pos + 1] ^= (byte)(mask >> 8);

                    if (size1 > 2) data1[pos + 2] ^= (byte)(mask >> 16);
                }
            }

            var frameData = buffer.Slice(idx, (int)size);
            var frame = new WebSocketFrame { IsFinal = isFinal, Data = frameData, OpCode = opCode };
            yield return frame;

            buffer = buffer.Slice((int)(idx + size));
        }

        if (!copied && buffer.Length > 0) CopyToBuffer(buffer);

        if (copied)
        {
            if (buffer.Length == 0)
            {
                bufferLength = 0;
            }
            else
            {
                buffer.CopyTo(this.buffer);
                bufferLength = buffer.Length;
            }
        }
    }

    private Memory<byte> CopyToBuffer(ReadOnlyMemory<byte> data)
    {
        var requiredLength = bufferLength + data.Length;
        if (requiredLength > buffer.Length) Array.Resize(ref buffer, (int)Math.Min(requiredLength, buffer.Length * 2));

        data.CopyTo(buffer.AsMemory((int)bufferLength));
        bufferLength += data.Length;
        return buffer.AsMemory(0, (int)bufferLength);
    }

    private static bool IsDataEnough(ReadOnlySpan<byte> data)
    {
        var length = data.Length;
        if (length < 2)
            return false;

        var size = data[1];
        if ((size & 0x80) != 0) // masked
            length -= 4;

        size &= 0x7f;

        if (size == 126)
        {
            if (length < 2) return false;
        }
        else if (size == 127)
        {
            if (length < 10) return false;
        }

        return length >= size;
    }
}ParseOptions.0.json•
]D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\WebSocket\WebSocketFrame.csÆusing System;
using System.Text;

namespace Titanium.Web.Proxy;

public class WebSocketFrame
{
    public bool IsFinal { get; internal set; }

    public WebsocketOpCode OpCode { get; internal set; }

    public ReadOnlyMemory<byte> Data { get; internal set; }

    public string GetText()
    {
        return GetText(Encoding.UTF8);
    }

    public string GetText(Encoding encoding)
    {
#if NET6_0_OR_GREATER
        return encoding.GetString(Data.Span);
#else
        return encoding.GetString(Data.ToArray());
#endif
    }
}ParseOptions.0.jsonõ
^D:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\WebSocket\WebsocketOpCode.cs£namespace Titanium.Web.Proxy;

public enum WebsocketOpCode : byte
{
    Continuation,
    Text,
    Binary,
    ConnectionClose = 8,
    Ping,
    Pong
}ParseOptions.0.json¯
áD:\a\titanium-web-proxy\titanium-web-proxy\src\Titanium.Web.Proxy\obj\Release\net462\.NETFramework,Version=v4.6.2.AssemblyAttributes.cs÷// <autogenerated />
using System;
using System.Reflection;
[assembly: global::System.Runtime.Versioning.TargetFrameworkAttribute(".NETFramework,Version=v4.6.2", FrameworkDisplayName = ".NET Framework 4.6.2")]
ParseOptions.0.json