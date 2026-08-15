using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Network.Certificate;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Network;

/// <summary>
///     Certificate Engine option.
/// </summary>
public enum CertificateEngine
{
    /// <summary>
    ///     Uses BouncyCastle 3rd party library. Default. Issues a distinct key pair per leaf
    ///     (RSA-2048 or ECDSA P-256 per <see cref="CertificateKeyAlgorithm" /> /
    ///     <see cref="CertificateManager.LeafCertificateKeyAlgorithm" />). RSA-2048 keys are normally
    ///     taken from a process-wide background buffer (see
    ///     <see cref="CertificateManager.LeafRsaKeyPairBufferSize" />).
    /// </summary>
    BouncyCastle = 0,

    /// <summary>
    ///     Faster BouncyCastle variant.
    ///     Note: for performance it reuses a single pre-generated key pair (RSA or ECDSA, matching
    ///     <see cref="CertificateManager.LeafCertificateKeyAlgorithm" />) across ALL generated leaf
    ///     certificates. This means every intercepted host's certificate shares the same public key.
    ///     Prefer <see cref="BouncyCastle" /> if per-host key isolation matters for your threat model.
    /// </summary>
    BouncyCastleFast = 2,

    /// <summary>
    ///     Uses Windows Certification Generation API and is only valid on Windows.
    ///     Observed to be faster than BouncyCastle.
    ///     Note: this engine also reuses a shared private key across generated leaf certificates
    ///     (Windows-only; non-Windows runtimes coerce the engine to <see cref="BouncyCastle" />).
    /// </summary>
    DefaultWindows = 1
}

/// <summary>
///     Key algorithm used for the leaf ("fake") certificates the proxy generates per intercepted host.
/// </summary>
public enum CertificateKeyAlgorithm
{
    /// <summary>
    ///     RSA 2048. The default, and what every TLS client in existence accepts - including legacy
    ///     stacks with no elliptic-curve support, which is often exactly what a debugging proxy is
    ///     pointed at. Key generation is expensive, but
    ///     <see cref="CertificateManager.LeafRsaKeyPairBufferSize" /> (default 8) pre-generates RSA-2048
    ///     pairs so many first visits avoid paying that cost on the CONNECT path. Certificate caching
    ///     can also avoid regeneration entirely.
    /// </summary>
    Rsa2048 = 0,

    /// <summary>
    ///     ECDSA over NIST P-256. Roughly fifty times cheaper to generate than
    ///     <see cref="Rsa2048" /> while still giving every host its own key, which effectively removes
    ///     certificate generation from first-visit latency. Requires clients that accept ECDSA server
    ///     certificates - universal among current browsers and TLS libraries, but not in very old ones.
    ///     The root certificate is unaffected and stays RSA, so it continues to sign these leaves.
    /// </summary>
    EcdsaP256 = 1
}

/// <summary>
///     A class to manage SSL certificates used by this proxy server.
/// </summary>
public sealed class CertificateManager : IDisposable
{
    private const string RunAsAdministrator = "runas";
    private const string DefaultRootCertificateIssuer = "Titanium";

    private const string DefaultRootRootCertificateName = "Titanium Root Certificate Authority";

    private static readonly ConcurrentDictionary<string, object> _saveCertificateLocks = new();

    /// <summary>
    ///     Cache dictionary
    /// </summary>
    private readonly ConcurrentDictionary<string, CachedCertificate> cachedCertificates = new();

    /// <summary>
    ///     Certificates removed from <see cref="cachedCertificates" /> by <see cref="EnforceCertificateCacheBound" />
    ///     or the idle sweep in <see cref="ClearIdleCertificates" />, awaiting disposal. Not disposed at
    ///     eviction time because the certificate object may still be held by an in-flight TLS
    ///     handshake; <see cref="DisposePendingEvictions" /> reclaims the native CAPI/OpenSSL key
    ///     handle once at least one full sweep interval has passed, which is comfortably longer than
    ///     any handshake, instead of waiting on GC finalization indefinitely.
    /// </summary>
    private readonly ConcurrentQueue<PendingCertificateDisposal> pendingDisposals = new();

    private readonly CancellationTokenSource clearCertificatesTokenSource = new();

    /// <summary>
    ///     Used to prevent multiple threads working on same certificate generation
    ///     when burst certificate generation requests happen for same certificate.
    /// </summary>
    private readonly SemaphoreSlim pendingCertificateCreationTaskLock = new(1);

    /// <summary>
    ///     Caps concurrent leaf crypto (MakeCertificate). Without this, a CONNECT stampede
    ///     queues dozens of <see cref="Task.Run"/> RSA generations that starve unrelated hosts
    ///     (and even disk-cache loads that used to share that same queue).
    /// </summary>
    private readonly SemaphoreSlim certificateCreationThrottle =
        new(Math.Clamp(Environment.ProcessorCount, 2, 8));

    /// <summary>
    ///     A list of pending certificate creation tasks.
    /// </summary>
    private readonly Dictionary<string, Task<X509Certificate2?>> pendingCertificateCreationTasks = new();

    private readonly object rootCertCreationLock = new();

    private ICertificateMaker? certEngineValue;

    private ICertificateCache certificateCache = new DefaultCertificateDiskCache();

    private bool disposed;

    private CertificateEngine engine;

    private CertificateKeyAlgorithm leafKeyAlgorithm = CertificateKeyAlgorithm.Rsa2048;

    private string? issuer;

    private X509Certificate2? rootCertificate;

    private string? rootCertificateName;

    /// <summary>
    ///     Absolute path to certutil.exe under <see cref="Environment.SystemDirectory" /> (S4036).
    /// </summary>
    private static string CertUtilExecutablePath =>
        Path.Combine(Environment.SystemDirectory, "certutil.exe");

    /// <summary>
    ///     Initializes a new instance of the <see cref="CertificateManager" /> class.
    /// </summary>
    /// <param name="rootCertificateName"></param>
    /// <param name="rootCertificateIssuerName"></param>
    /// <param name="userTrustRootCertificate">
    ///     Should the proxy root CA be trusted in the current-user Root store?
    /// </param>
    /// <param name="machineTrustRootCertificate">Should the proxy root CA be trusted in the local-machine Root store?</param>
    /// <param name="trustRootCertificateAsAdmin">
    ///     Should we attempt to trust certificates with elevated permissions by
    ///     prompting for UAC if required?
    /// </param>
    /// <param name="logger">The initial logger to report certificate operations through.</param>
    /// <param name="maxCacheEntriesProvider">
    ///     Read live (not snapshotted) on every cache insertion, so that assigning a new
    ///     <see cref="ProxyServer.ResourceLimits" /> after construction is honored without recreating
    ///     this <see cref="CertificateManager" />. Bounds the in-memory cache only. <see langword="null" />
    ///     return value means unbounded.
    /// </param>
    /// <param name="maxDiskCacheEntriesProvider">
    ///     Read live on every disk save, independently of <paramref name="maxCacheEntriesProvider" />.
    ///     <see langword="null" /> return value means unbounded.
    /// </param>
    internal CertificateManager(string? rootCertificateName, string? rootCertificateIssuerName, // NOSONAR S107 -- Constructor preserves established configuration wiring.
        bool userTrustRootCertificate, bool machineTrustRootCertificate, bool trustRootCertificateAsAdmin,
        ILogger logger, Func<int?>? maxCacheEntriesProvider = null, Func<int?>? maxDiskCacheEntriesProvider = null)
    {
        Logger = logger;

        UserTrustRoot = userTrustRootCertificate || machineTrustRootCertificate;

        MachineTrustRoot = machineTrustRootCertificate;
        TrustRootAsAdministrator = trustRootCertificateAsAdmin;

        if (rootCertificateName != null) RootCertificateName = rootCertificateName;

        if (rootCertificateIssuerName != null) RootCertificateIssuerName = rootCertificateIssuerName;

        CertificateEngine = CertificateEngine.BouncyCastle;

        this.maxCacheEntriesProvider = maxCacheEntriesProvider ?? NoMaxCacheEntries;
        this.maxDiskCacheEntriesProvider = maxDiskCacheEntriesProvider ?? NoMaxCacheEntries;

        ProxyMetrics.RegisterCertificateManager(this);
    }

    /// <summary>
    ///     Current number of entries in the in-memory certificate cache, for the
    ///     <c>twp.certificates.cached</c> observable gauge in <see cref="ProxyMetrics" />.
    /// </summary>
    internal int CachedCertificateCount => cachedCertificates.Count;

    private static int? NoMaxCacheEntries() => null;

    private readonly Func<int?> maxCacheEntriesProvider;

    /// <summary>
    ///     Read live on every disk save, independently of <see cref="maxCacheEntriesProvider" /> (the
    ///     in-memory bound): disk is far cheaper than a live <see cref="X509Certificate2" /> handle, so
    ///     the two are deliberately separate knobs rather than the memory bound also silently pruning
    ///     <c>.pfx</c> files on disk. <see langword="null" /> return value means unbounded.
    /// </summary>
    private readonly Func<int?> maxDiskCacheEntriesProvider;

    private readonly record struct PendingCertificateDisposal(X509Certificate2 Certificate, DateTime EvictedAtUtc);

    /// <summary>
    ///     Evicts the least-recently-used cached certificates until the count is at or below
    ///     <see cref="ProxyResourceLimits.MaxCertificateCacheEntries" /> (via the provider passed to the
    ///     constructor). Each entry holds a full <see cref="X509Certificate2" /> with a private key in
    ///     memory, so - unlike <see cref="ClearIdleCertificates" />'s time-based sweep, which only bounds
    ///     how long an entry can live - nothing previously bounded how large the cache could grow
    ///     <em>within</em> that window; a client (or attacker) requesting many distinct hostnames in a
    ///     burst could otherwise accumulate unbounded generated-certificate memory before the next sweep.
    /// </summary>
    private void EnforceCertificateCacheBound()
    {
        var max = maxCacheEntriesProvider();
        if (max is not > 0) return;

        var excess = cachedCertificates.Count - max.Value;
        if (excess <= 0) return;

        foreach (var pair in cachedCertificates.OrderBy(x => x.Value.LastAccess).Take(excess))
            EvictCertificate(pair.Key);
    }

    /// <summary>
    ///     Removes <paramref name="certificateName" /> from the in-memory cache without disposing it
    ///     immediately, queuing it for <see cref="DisposePendingEvictions" /> instead. See
    ///     <see cref="pendingDisposals" /> for why disposal is deferred.
    /// </summary>
    private void EvictCertificate(string certificateName)
    {
        if (cachedCertificates.TryRemove(certificateName, out var removed))
            pendingDisposals.Enqueue(new PendingCertificateDisposal(removed.Certificate, DateTime.UtcNow));
    }

    /// <summary>
    ///     Disposes certificates queued by <see cref="EvictCertificate" /> once at least one full sweep
    ///     interval (<see cref="ClearIdleCertificates" />'s one-minute cadence) has elapsed since
    ///     eviction, so native CAPI/OpenSSL key handles are released promptly instead of waiting on GC
    ///     finalization, while still giving any in-flight TLS handshake that grabbed a reference just
    ///     before eviction comfortably long enough to finish using it.
    /// </summary>
    private void DisposePendingEvictions()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        while (pendingDisposals.TryPeek(out var pending) && pending.EvictedAtUtc <= cutoff)
        {
            if (pendingDisposals.TryDequeue(out pending))
                try { pending.Certificate.Dispose(); } catch { /* best effort */ }
        }
    }

    private ICertificateMaker CertEngine
    {
        get
        {
            if (certEngineValue == null)
                switch (engine)
                {
                    case CertificateEngine.BouncyCastle:
                        certEngineValue = new BcCertificateMaker(CertificateValidDays, CertificateGraceDays,
                            leafKeyAlgorithm);
                        break;
                    case CertificateEngine.BouncyCastleFast:
                        certEngineValue = new BcCertificateMakerFast(CertificateValidDays, CertificateGraceDays,
                            leafKeyAlgorithm);
                        break;
                    default:
                        if (!RunTime.IsWindows)
                            throw new PlatformNotSupportedException("The Windows certificate engine requires Windows.");
                        certEngineValue = new WinCertificateMaker(CertificateValidDays, CertificateGraceDays);
                        break;
                }

            // Every switch arm assigns certEngineValue or throws.
            return certEngineValue;
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
    ///     The logger used to report certificate operation failures. Kept as a live-swappable reference
    ///     (rather than snapshotted once) so that changing <see cref="ProxyServer.Logging" /> is
    ///     reflected here without recreating this <see cref="CertificateManager" />; resets the cached
    ///     certificate engine so a lazily-created maker also picks up the new logger.
    /// </summary>
    internal ILogger Logger
    {
        get => logger;
        set
        {
            logger = value;
            certEngineValue = null;
        }
    }

    private ILogger logger = NullLogger.Instance;

    /// <summary>
    ///     Selects the certificate generation engine. Default is <see cref="CertificateEngine.BouncyCastle" />
    ///     on all platforms. On non-Windows runtimes, <see cref="CertificateEngine.DefaultWindows" /> is
    ///     coerced to <see cref="CertificateEngine.BouncyCastle" />; both BouncyCastle engines are supported.
    /// </summary>
    public CertificateEngine CertificateEngine
    {
        get => engine;
        set
        {
            // Non-Windows runtimes cannot use Windows X509Enrollment; Fast is fully managed BC.
            value = CoerceEngineForPlatform(value, RunTime.IsWindows);

            if (value != engine)
            {
                certEngineValue = null;
                engine = value;
            }
        }
    }

    /// <summary>
    ///     Key algorithm for generated leaf certificates. Honoured by the BouncyCastle engines; the
    ///     Windows engine always issues RSA. Defaults to <see cref="CertificateKeyAlgorithm.Rsa2048" />.
    ///     <para>
    ///         Switching to <see cref="CertificateKeyAlgorithm.EcdsaP256" /> makes generating a
    ///         certificate for a not-yet-seen host roughly fifty times cheaper, which is the single
    ///         largest cost the proxy adds to a first visit. Only clients that accept ECDSA server
    ///         certificates can be intercepted afterwards.
    ///     </para>
    /// </summary>
    public CertificateKeyAlgorithm LeafCertificateKeyAlgorithm
    {
        get => leafKeyAlgorithm;
        set
        {
            if (value == leafKeyAlgorithm) return;

            // The makers capture the algorithm when constructed (BouncyCastleFast generates its single
            // shared key pair right there), so the cached instance has to go.
            certEngineValue = null;
            leafKeyAlgorithm = value;
        }
    }

    /// <summary>
    ///     How many RSA-2048 leaf private keys to keep ready in a background-refilled buffer so first
    ///     visits do not pay key-generation cost on the CONNECT that needs the certificate.
    ///     Defaults to 8. Set to 0 to disable buffering (keys are generated on demand).
    ///     <para>
    ///         Only applies when <see cref="LeafCertificateKeyAlgorithm" /> is
    ///         <see cref="CertificateKeyAlgorithm.Rsa2048" />. ECDSA P-256 keys are cheap enough that
    ///         they are always generated inline. The buffer is process-wide and shared by every
    ///         <see cref="CertificateManager" /> instance.
    ///     </para>
    /// </summary>
    public static int LeafRsaKeyPairBufferSize
    {
        get => LeafKeyPairSource.RsaBufferCapacity;
        set => LeafKeyPairSource.RsaBufferCapacity = value;
    }

    /// <summary>
    ///     Password of the Root certificate file.
    ///     <para>Set a password for the .pfx file</para>
    /// </summary>
    public string PfxPassword { get; set; } = string.Empty;

    /// <summary>
    ///     Name(path) of the Root certificate file.
    ///     <para>
    ///         Set the name or path of the .pfx file. When empty, the file is named <c>rootCert.pfx</c>.
    ///         Relative or empty values are resolved under the per-user Titanium.Web.Proxy directory
    ///         (%LocalAppData% on Windows, ApplicationData on Linux/macOS). Absolute paths are honored as-is.
    ///     </para>
    /// </summary>
    public string PfxFilePath { get; set; } = string.Empty;

    /// <summary>
    ///     Number of days generated HTTPS leaf certificates are valid for, measured forward from the
    ///     moment of creation.  The certificate's <c>NotBefore</c> is set to
    ///     <c>UtcNow - <see cref="CertificateGraceDays" /></c>, so the effective total validity window
    ///     (NotAfter − NotBefore) equals <c>CertificateValidDays + CertificateGraceDays</c>.
    ///     <para>
    ///         Chrome 70+ and iOS 14+ reject certificates whose total validity window exceeds 398 days.
    ///         To stay within that limit, keep <c>CertificateValidDays + CertificateGraceDays &lt;= 398</c>.
    ///         The default value of 396, combined with the default grace of 2, equals exactly 398 days total.
    ///     </para>
    /// </summary>
    public int CertificateValidDays { get; set; } = 396;

    /// <summary>
    ///     Number of days by which the certificate's <c>NotBefore</c> timestamp is backdated relative to
    ///     the current UTC time.  A small backdate (the default is 2 days) compensates for minor clock-skew
    ///     between the proxy machine and clients; it is not necessary to backdate by a year.
    ///     <para>
    ///         The total certificate lifetime is <c>CertificateValidDays + CertificateGraceDays</c>.
    ///         Chrome 70+ and iOS 14+ cap this at 398 days for TLS leaf certificates.
    ///     </para>
    /// </summary>
    public int CertificateGraceDays { get; set; } = 2;

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
    ///     Subject/CN name used when generating a root certificate.
    ///     (This is valid only when <see cref="RootCertificate" /> property is not set.)
    ///     If no certificate is provided then a default root certificate will be created and used.
    ///     Persistence uses <see cref="PfxFilePath" /> / <see cref="CertificateStorage" /> under the
    ///     per-user Titanium.Web.Proxy directory (not the process executable directory).
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
            // Only invalidate cached leaf certificates when the signing root's identity actually
            // changes. Reloading the same persisted root on startup must NOT discard valid cached
            // leaves. Critically: the first assignment (field still null → loaded rootCert.pfx) is
            // not a root change — clearing here used to Directory.Delete("crts") on every process
            // start, forcing full MakeCertificate stampede and multi-second CONNECT latency.
            if (rootCertificate != null)
            {
                var rootChanged = value == null ||
                                  !string.Equals(rootCertificate.Thumbprint, value.Thumbprint,
                                      StringComparison.OrdinalIgnoreCase);
                if (rootChanged)
                    ClearRootCertificate();
            }

            rootCertificate = value;
        }
    }

    /// <summary>
    ///     Additional certificates to send to clients as part of the TLS certificate chain.
    ///     Use this when <see cref="RootCertificate" /> is an intermediate CA rather than the trust anchor:
    ///     set this to the ordered list of intermediate certificates between the signing certificate
    ///     and the client-trusted root so that clients can build a complete verified chain.
    ///     When <see cref="RootCertificate" /> is not self-signed it is automatically included in the
    ///     chain even if this collection is empty; any certificates in this collection are appended
    ///     after it.
    /// </summary>
    public X509Certificate2Collection? IntermediateCertificates { get; set; }

    /// <summary>
    ///     When true, persist generated leaf certificates via <see cref="CertificateStorage" /> so
    ///     subsequent runs can reload them instead of regenerating.
    /// </summary>
    public bool SaveFakeCertificates { get; set; } = false;

    /// <summary>
    ///     The fake certificate cache storage.
    ///     The default implementation stores leaf certificates in a <c>crts</c> subdirectory of the
    ///     per-user Titanium.Web.Proxy directory (%LocalAppData% on Windows, ApplicationData on
    ///     Linux/macOS). Implement <see cref="ICertificateCache" /> and assign a concrete class here to customize.
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
    ///     When true, issue per-host certificates instead of <c>*.parent.tld</c> wildcards.
    ///     Default false (wildcards enabled where applicable).
    /// </summary>
    public bool DisableWildCardCertificates { get; set; } = false;

    public void Dispose()
    {
        Dispose(true);
    }

    /// <summary>
    ///     For CertificateEngine.DefaultWindows to work we need to also check in personal store
    /// </summary>
    /// <param name="storeLocation"></param>
    /// <returns></returns>
    private bool RootCertificateInstalled(StoreLocation storeLocation)
    {
        var certificate = RootCertificate;
        if (certificate == null) throw new InvalidOperationException("Root certificate is null.");

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
        if (certificate == null) throw new InvalidOperationException("Could not install certificate as it is null or empty.");

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
        if (!isRootCertificate && RootCertificate == null)
        {
            CreateRootCertificate();
            if (RootCertificate == null)
                throw new InvalidOperationException(
                    "A root certificate is required to create leaf certificates, but root creation failed.");
        }

        var certificate = CertEngine.MakeCertificate(certificateName, isRootCertificate ? null : RootCertificate);

        if (CertificateEngine == CertificateEngine.DefaultWindows)
            Task.Run(() => UninstallCertificate(StoreName.My, StoreLocation.CurrentUser, certificate),
                clearCertificatesTokenSource.Token);

        return certificate;
    }

    private void OnException(Exception exception)
    {
        ProxyDiagnostics.ReportException(Logger, "Certificate operation failed", exception);
    }

    /// <summary>
    ///     Create an SSL certificate
    /// </summary>
    /// <param name="certificateName"></param>
    /// <param name="isRootCertificate"></param>
    /// <returns></returns>
    internal X509Certificate2? CreateCertificate(string certificateName, bool isRootCertificate) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
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
                        certificate.Dispose();
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

                                    // Unlike the in-memory dictionary, this on-disk cache has no
                                    // process-lifetime sweep to ever remove old entries at all - every
                                    // distinct hostname ever visited previously accumulated a permanent
                                    // .pfx file. Prune it to the disk-specific bound, which is
                                    // deliberately independent of the in-memory bound (see
                                    // maxDiskCacheEntriesProvider).
                                    if (certificateCache is DefaultCertificateDiskCache diskCache)
                                        diskCache.PruneToMaxEntries(maxDiskCacheEntriesProvider());
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
                    }, clearCertificatesTokenSource.Token);
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
    public async Task<X509Certificate2?> CreateServerCertificate(string certificateName) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        // check in cache first
        if (TryGetValidCachedCertificate(certificateName, out var cachedCertificate))
            return cachedCertificate;

        // Disk hit must not wait behind Task.Run(MakeCertificate) stampede — load synchronously.
        if (SaveFakeCertificates)
        {
            var fromDisk = TryLoadFakeCertificateFromDisk(certificateName);
            if (fromDisk != null)
            {
                if (cachedCertificates.TryAdd(certificateName,
                        new CachedCertificate(fromDisk) { LastAccess = DateTime.UtcNow }))
                {
                    EnforceCertificateCacheBound();
                    return fromDisk;
                }

                fromDisk.Dispose();
                if (TryGetValidCachedCertificate(certificateName, out cachedCertificate))
                    return cachedCertificate;
            }
        }

        var createdTask = false;
        Task<X509Certificate2?> createCertificateTask;
        await pendingCertificateCreationTaskLock.WaitAsync(clearCertificatesTokenSource.Token);
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
                    certificateCreationThrottle.Wait(clearCertificatesTokenSource.Token);
                    try
                    {
                        var result = CreateCertificate(certificateName, false);
                        if (result != null)
                        {
                            cachedCertificates.TryAdd(certificateName,
                                new CachedCertificate(result) { LastAccess = DateTime.UtcNow });
                            EnforceCertificateCacheBound();
                        }

                        return result;
                    }
                    finally
                    {
                        certificateCreationThrottle.Release();
                    }
                }, clearCertificatesTokenSource.Token);

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
            await pendingCertificateCreationTaskLock.WaitAsync(clearCertificatesTokenSource.Token);
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
    ///     Loads a previously saved leaf certificate from the disk cache (no crypto).
    ///     Used as a fast path so CONNECT for known hosts is not queued behind unrelated keygen.
    /// </summary>
    private X509Certificate2? TryLoadFakeCertificateFromDisk(string certificateName)
    {
        try
        {
            var subjectName = ProxyConstants.CnRemoverRegex
                .Replace(certificateName, string.Empty)
                .Replace("*", "$x$");

            var certificate = certificateCache.LoadCertificate(subjectName, StorageFlag);
            if (certificate == null) return null;

            if (certificate.NotAfter <= DateTime.Now)
            {
                OnException(new Exception($"Cached certificate for {subjectName} has expired."));
                certificate.Dispose();
                return null;
            }

            return certificate;
        }
        catch (Exception e)
        {
            OnException(new Exception("Failed to load fake certificate.", e));
            return null;
        }
    }

    /// <summary>
    ///     A method to clear outdated certificates
    /// </summary>
    internal async Task ClearIdleCertificates()
    {
        var cancellationToken = clearCertificatesTokenSource.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            // Fire-and-forget from ProxyServer.Start; keep the sweep resilient to transient failures.
            try
            {
                var cutOff = DateTime.UtcNow.AddMinutes(-CertificateCacheTimeOutMinutes);

                var outdated = cachedCertificates.Where(x => x.Value.LastAccess < cutOff).ToList();

                foreach (var cache in outdated)
                    EvictCertificate(cache.Key);

                // A runtime change to ResourceLimits (e.g. lowering MaxCertificateCacheEntries) was
                // previously only honored on the next cache insertion; enforcing it here too means a
                // lowered bound - or a burst that grew the cache between sweeps - is corrected within
                // one sweep interval even on an otherwise-idle proxy.
                EnforceCertificateCacheBound();

                DisposePendingEvictions();
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
    ///     Creates an <see cref="System.Net.Security.SslStreamCertificateContext" /> for the given leaf certificate,
    ///     including any intermediate CA certificates that the client needs to build a verified chain.
    ///     <para>
    ///         When <see cref="RootCertificate" /> is not self-signed (i.e. it is an intermediate CA), it is
    ///         automatically added to the chain so that clients trust-anchored at the root CA can still verify
    ///         generated leaf certificates. Additional certificates from <see cref="IntermediateCertificates" />
    ///         are appended after it.
    ///     </para>
    ///     <para>
    ///         Uses <c>offline: true</c> so chain building does not consult the Windows certificate stores or the
    ///         network. Without that, Schannel can latch onto a different root that happens to share the same
    ///         subject DN — for example the product default <c>Titanium Root Certificate Authority</c> that the
    ///         Basic/WPF examples trust into CurrentUser\Root on <c>ProxyServer.Start()</c> — and then present a
    ///         store-backed leaf instead of the one we just generated, breaking callers that validate against a
    ///         different in-memory/session root (integration tests, or a second proxy instance).
    ///     </para>
    /// </summary>
    internal System.Net.Security.SslStreamCertificateContext CreateSslCertificateContext(X509Certificate2 leaf)
    {
        var extras = new X509Certificate2Collection();
        System.Net.Security.SslCertificateTrust? trust = null;

        if (rootCertificate != null && !IsSelfSigned(rootCertificate))
        {
            extras.Add(rootCertificate);
            // Offline Create has no path to a system trust anchor when the configured signer is
            // an intermediate CA. Custom trust lets Create assemble leaf → intermediate.
            trust = System.Net.Security.SslCertificateTrust.CreateForX509Collection(
                new X509Certificate2Collection(rootCertificate), sendTrustInHandshake: false);
            // Windows SslStreamCertificateContext's constructor rebuilds the chain without
            // ExtraStore/custom trust; when that OS build throws (rather than returning false),
            // its own "add to Intermediate CA store" fallback never runs. Stage the intermediate
            // first so the constructor's chain build can succeed.
            StageIntermediateForOsChainBuild(rootCertificate);
        }

        if (IntermediateCertificates != null)
            foreach (X509Certificate2 cert in IntermediateCertificates)
                extras.Add(cert);

        try
        {
            return System.Net.Security.SslStreamCertificateContext.Create(
                leaf, extras.Count > 0 ? extras : null, offline: true, trust);
        }
        catch (CryptographicException) when (trust != null)
        {
            return System.Net.Security.SslStreamCertificateContext.Create(
                leaf, null, offline: true, trust);
        }
    }

    /// <summary>
    ///     Best-effort staging of an intermediate into the CurrentUser Intermediate CA store so
    ///     Windows <see cref="System.Net.Security.SslStreamCertificateContext" /> construction can
    ///     complete its OS chain build. Mirrors the store fallback inside that type's constructor.
    /// </summary>
    private static void StageIntermediateForOsChainBuild(X509Certificate2 intermediate)
    {
        try
        {
            using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Add(intermediate);
        }
        catch (CryptographicException)
        {
            // Permission or store issues - Create may still succeed via custom trust alone.
        }
    }

    /// <summary>
    ///     Coerces an engine selection for the current platform. Extracted so unit tests can verify
    ///     that only <see cref="CertificateEngine.DefaultWindows" /> is rewritten off Windows.
    /// </summary>
    internal static CertificateEngine CoerceEngineForPlatform(CertificateEngine value, bool isWindows)
    {
        if (!isWindows && value == CertificateEngine.DefaultWindows)
            return CertificateEngine.BouncyCastle;
        return value;
    }

    private static bool IsSelfSigned(X509Certificate2 cert) =>
        cert.SubjectName.RawData.SequenceEqual(cert.IssuerName.RawData);

    /// <summary>
    ///     Attempts to create a RootCertificate.
    /// </summary>
    /// <param name="persistToFile">if set to <c>true</c> try to load/save the certificate from rootCert.pfx.</param>
    /// <returns>
    ///     true if succeeded, else false.
    /// </returns>
    public bool CreateRootCertificate(bool persistToFile = true) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
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
                        rootCert.Dispose();
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
    ///     Loads the root certificate via <see cref="CertificateStorage" /> (default: per-user
    ///     Titanium.Web.Proxy directory, file name from <see cref="PfxFilePath" /> or <c>rootCert.pfx</c>).
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
                rootCert.Dispose();
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
    ///     Set the name or path of the .pfx file. When empty, the file is named <c>rootCert.pfx</c>
    ///     under the per-user Titanium.Web.Proxy directory. Absolute paths are honored as-is.
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
    ///     Trusts the root certificate in the current-user Personal and Trusted Root stores,
    ///     and optionally also in the local-machine Personal and Trusted Root stores.
    /// </summary>
    /// <param name="machineTrusted">
    ///     When <see langword="true"/>, also install into the local-machine stores. Defaults to
    ///     <see langword="false"/> — user-only trust is the recommended default for interactive
    ///     apps; machine trust needs elevation (or a privileged service account) and otherwise
    ///     fails silently.
    /// </param>
    public void TrustRootCertificate(bool machineTrusted = false)
    {
        // currentUser\personal
        InstallCertificate(StoreName.My, StoreLocation.CurrentUser);
        // currentUser\Root
        InstallCertificate(StoreName.Root, StoreLocation.CurrentUser);

        if (machineTrusted)
        {
            // localMachine\personal
            InstallCertificate(StoreName.My, StoreLocation.LocalMachine);
            // localMachine\Root
            InstallCertificate(StoreName.Root, StoreLocation.LocalMachine);
        }
    }

    /// <summary>
    ///     Puts the certificate to the user store, optionally also to the machine store, prompting
    ///     with UAC when elevation is required. Works only on Windows.
    /// </summary>
    /// <param name="machineTrusted">
    ///     When <see langword="true"/>, elevate to install into local-machine stores. Defaults to
    ///     <see langword="false"/> (user store only).
    /// </param>
    /// <returns>True if success.</returns>
    public bool TrustRootCertificateAsAdmin(bool machineTrusted = false)
    {
        if (!RunTime.IsWindows) return false;

        var certificate = RootCertificate;
        if (certificate == null) return false;

        // currentUser\Personal + currentUser\Root (machine elevation is only needed for LocalMachine).
        InstallCertificate(StoreName.My, StoreLocation.CurrentUser);
        InstallCertificate(StoreName.Root, StoreLocation.CurrentUser);

        // certutil.exe only accepts the PFX password via a plain "-p password" command-line argument -
        // it has no file/stdin-based alternative (confirmed: no documented option to read it from a
        // file). ProcessStartInfo.Arguments is visible to any other process/user that lists this
        // process's command line (Task Manager, Get-Process, WMI, etc.) for as long as certutil runs.
        // The configured PfxPassword protects the *long-lived* cached root PFX on disk (see
        // SaveRootCertificate) and must never appear there. This temp file exists only for certutil to
        // consume for the few moments before it's deleted below, so export it under a throwaway,
        // single-use empty password instead of reusing the real secret on the command line.
        const string transientPfxPassword = "";
        var pfxFileName = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pfx");
        try
        {
            File.WriteAllBytes(pfxFileName, certificate.Export(X509ContentType.Pkcs12, transientPfxPassword));

            // Elevated: currentUser\Root (when !machineTrusted) or localMachine Personal+Root.
            var info = new ProcessStartInfo
            {
                FileName = CertUtilExecutablePath,
                CreateNoWindow = true,
                UseShellExecute = true,
                Verb = RunAsAdministrator,
                ErrorDialog = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (!machineTrusted)
                info.Arguments = "-f -user -p \"" + transientPfxPassword + "\" -importpfx root \"" + pfxFileName + "\"";
            else
                info.Arguments = "-importPFX -p \"" + transientPfxPassword + "\" -f \"" + pfxFileName + "\"";

            try
            {
                var process = Process.Start(info);
                if (process == null) return false;

                process.WaitForExit();
            }
            catch (Exception e)
            {
                OnException(e);
                return false;
            }
        }
        finally
        {
            // Guaranteed even if Export/WriteAllBytes/Process.Start/WaitForExit throws - otherwise a
            // temporary file holding the exported root private key is left behind on disk indefinitely.
            try { File.Delete(pfxFileName); } catch { /* best effort */ }
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
    /// </summary>
    /// <param name="userTrustRootCertificate">
    ///     Trust in the current-user stores. Prefer true for interactive MITM; false for fully opt-in trust.
    /// </param>
    /// <param name="machineTrustRootCertificate">
    ///     Also trust in local-machine stores (needs elevation). Implies user trust. Prefer false unless
    ///     installing for a service / all users.
    /// </param>
    /// <param name="trustRootCertificateAsAdmin">
    ///     Elevate via UAC when installing (Windows only). Defaults to false.
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
    ///     Removes the trusted certificates from the current-user Personal and Trusted Root stores,
    ///     and optionally also from the local-machine Personal and Trusted Root stores.
    /// </summary>
    /// <param name="machineTrusted">
    ///     When <see langword="true"/>, also remove from local-machine stores (needs elevation;
    ///     fails silently otherwise). Pass the same value used when trusting.
    /// </param>
    public void RemoveTrustedRootCertificate(bool machineTrusted = false)
    {
        // currentUser\personal
        UninstallCertificate(StoreName.My, StoreLocation.CurrentUser, RootCertificate);
        // currentUser\Root
        UninstallCertificate(StoreName.Root, StoreLocation.CurrentUser, RootCertificate);

        if (machineTrusted)
        {
            // localMachine\personal
            UninstallCertificate(StoreName.My, StoreLocation.LocalMachine, RootCertificate);
            // localMachine\Root
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

        // currentUser\Personal + currentUser\Root
        UninstallCertificate(StoreName.My, StoreLocation.CurrentUser, RootCertificate);
        UninstallCertificate(StoreName.Root, StoreLocation.CurrentUser, RootCertificate);

        var infos = new List<ProcessStartInfo>();
        if (!machineTrusted)
            infos.Add(new ProcessStartInfo
            {
                FileName = CertUtilExecutablePath,
                Arguments = "-delstore -user Root \"" + RootCertificateName + "\"",
                CreateNoWindow = true,
                UseShellExecute = true,
                Verb = RunAsAdministrator,
                ErrorDialog = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        else
            infos.AddRange(
                new List<ProcessStartInfo>
                {
                    // localMachine\Personal
                    new()
                    {
                        FileName = CertUtilExecutablePath,
                        Arguments = "-delstore My \"" + RootCertificateName + "\"",
                        CreateNoWindow = true,
                        UseShellExecute = true,
                        Verb = RunAsAdministrator,
                        ErrorDialog = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    },

                    // localMachine\Root
                    new()
                    {
                        FileName = CertUtilExecutablePath,
                        Arguments = "-delstore Root \"" + RootCertificateName + "\"",
                        CreateNoWindow = true,
                        UseShellExecute = true,
                        Verb = RunAsAdministrator,
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

        // Dispose every cached leaf cert before clearing so native CAPI/OpenSSL handles
        // are released promptly rather than waiting for GC finalization.
        foreach (var pair in cachedCertificates)
            if (cachedCertificates.TryRemove(pair.Key, out var entry))
                try { entry.Certificate.Dispose(); }
                catch
                {
                    // Best-effort cleanup; continue disposing the remaining cached certificates.
                }

        // Also dispose anything already evicted but still waiting out its grace period: the root is
        // changing, so leaves signed by the old root are not worth holding onto even briefly.
        while (pendingDisposals.TryDequeue(out var pending))
            try { pending.Certificate.Dispose(); }
            catch
            {
                // Best-effort cleanup; continue disposing the remaining pending certificates.
            }

        // Do not dispose rootCertificate: it may have been supplied by the caller and still
        // referenced outside this manager (e.g. shared test CA / persisted root reload).
        rootCertificate = null;
    }

    private void Dispose(bool disposing) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        if (disposed) return;

        if (disposing)
        {
            clearCertificatesTokenSource.Dispose();
            pendingCertificateCreationTaskLock.Dispose();
            certificateCreationThrottle.Dispose();

            // Release native CAPI/OpenSSL handles on all cached leaf certificates (manager-owned).
            foreach (var pair in cachedCertificates)
                if (cachedCertificates.TryRemove(pair.Key, out var entry))
                    try { entry.Certificate.Dispose(); }
                    catch
                    {
                        // Best-effort cleanup; continue disposing the remaining cached certificates.
                    }

            // Also dispose anything already evicted but still waiting out its grace period.
            while (pendingDisposals.TryDequeue(out var pending))
                try { pending.Certificate.Dispose(); }
                catch
                {
                    // Best-effort cleanup; continue disposing the remaining pending certificates.
                }

            // Do not dispose rootCertificate: ownership may belong to the caller.
            rootCertificate = null;
        }

        disposed = true;
    }
}