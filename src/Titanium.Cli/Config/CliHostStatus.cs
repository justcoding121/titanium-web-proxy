using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Config;

/// <summary>
///     What the CLI tells the Plus dashboard about itself: which config is running, how the last reload went, and
///     when the certificate files it serves or presents expire. Nothing here carries a secret: reload errors pass
///     through <see cref="SecretRedactor"/>, and certificate entries hold subject, issuer, and expiry only.
/// </summary>
internal sealed class CliHostStatus : IPlusHostStatus
{
    private const int MaxCertificatesPerFile = 50;
    private static readonly TimeSpan InventoryTtl = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Func<string, string?> _readEnv;
    private TwpConfig _config;
    private string _configHash;
    private readonly DateTimeOffset _loaded;
    private DateTimeOffset? _lastReload;
    private string? _lastResult;
    private string? _lastError;
    private IReadOnlyList<PlusCertificateInfo>? _inventory;
    private DateTimeOffset _inventoryAt;

    public CliHostStatus(TwpConfig config, string configHash, TimeProvider? timeProvider = null, Func<string, string?>? readEnv = null)
    {
        _config = config;
        _configHash = configHash;
        _time = timeProvider ?? TimeProvider.System;
        _readEnv = readEnv ?? Environment.GetEnvironmentVariable;
        _loaded = _time.GetUtcNow();
    }

    public void MarkReloaded(TwpConfig applied, string configHash)
    {
        lock (_gate)
        {
            _config = applied;
            _configHash = configHash;
            _lastReload = _time.GetUtcNow();
            _lastResult = "ok";
            _lastError = null;
            _inventory = null;
        }
    }

    public void MarkReloadFailed(string message)
    {
        lock (_gate)
        {
            _lastReload = _time.GetUtcNow();
            _lastResult = "failed";
            _lastError = SecretRedactor.Redact(message);
        }
    }

    public PlusConfigStatus GetConfigStatus()
    {
        lock (_gate)
        {
            return new PlusConfigStatus
            {
                SchemaVersion = _config.SchemaVersion,
                ConfigHash = _configHash,
                LoadedUtc = _loaded,
                LastReloadUtc = _lastReload,
                LastReloadResult = _lastResult,
                LastReloadError = _lastError,
            };
        }
    }

    public IReadOnlyList<PlusCertificateInfo> GetCertificates()
    {
        TwpConfig config;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_inventory is not null && now - _inventoryAt < InventoryTtl)
            {
                return _inventory;
            }

            config = _config;
        }

        var inventory = Collect(config, _readEnv);
        lock (_gate)
        {
            if (ReferenceEquals(config, _config))
            {
                _inventory = inventory;
                _inventoryAt = _time.GetUtcNow();
            }
        }

        return inventory;
    }

    /// <summary>Lists every certificate file the config names, with its expiry. A file that cannot be read becomes an entry with an error.</summary>
    internal static IReadOnlyList<PlusCertificateInfo> Collect(TwpConfig config, Func<string, string?> readEnv)
    {
        var list = new List<PlusCertificateInfo>();
        if (config.Certificates is { } certificates)
        {
            Add(list, "listener", "server certificate", certificates.CertificatePath, null);
            foreach (var item in certificates.Items)
            {
                Add(list, "listener", "SNI certificate", item.Path, item.Password);
            }
        }

        foreach (var listener in config.Listeners)
        {
            Add(list, "listener", $"client CA bundle (port {listener.Port})", listener.ClientCertificateCaPath, null);
        }

        foreach (var cluster in config.Clusters)
        {
            if (cluster.HttpClient is not { } http)
            {
                continue;
            }

            string? password = null;
            if (!string.IsNullOrWhiteSpace(http.ClientCertificatePasswordEnv))
            {
                password = readEnv(http.ClientCertificatePasswordEnv.Trim());
            }

            Add(list, "upstream", $"cluster {cluster.Id} client certificate", http.ClientCertificatePath, password);
            Add(list, "upstream", $"cluster {cluster.Id} CA bundle", http.CaBundlePath, null);
        }

        return list;
    }

    private static void Add(List<PlusCertificateInfo> list, string role, string what, string? path, string? password)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var name = $"{what}: {Path.GetFileName(path.Trim())}";
        X509Certificate2Collection? certificates = null;
        try
        {
            certificates = Load(path.Trim(), password);
            var total = certificates.Count;
            for (var i = 0; i < Math.Min(total, MaxCertificatesPerFile); i++)
            {
                var cert = certificates[i];
                list.Add(new PlusCertificateInfo
                {
                    Name = total > 1 ? $"{name} ({i + 1}/{total})" : name,
                    Role = role,
                    Subject = cert.Subject,
                    Issuer = cert.Issuer,
                    NotAfterUtc = new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero),
                });
            }

            if (total == 0)
            {
                list.Add(new PlusCertificateInfo { Name = name, Role = role, Error = "no certificate found in file" });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException
                                       or ArgumentException or PlatformNotSupportedException)
        {
            // The type is enough to act on; the message of a crypto error can echo file content.
            list.Add(new PlusCertificateInfo { Name = name, Role = role, Error = $"could not be read ({ex.GetType().Name})" });
        }
        finally
        {
            if (certificates is not null)
            {
                foreach (var cert in certificates)
                {
                    cert.Dispose();
                }
            }
        }
    }

    private static X509Certificate2Collection Load(string path, string? password)
    {
        var bytes = File.ReadAllBytes(path);
        if (LooksLikePem(bytes))
        {
            var pem = new X509Certificate2Collection();
            pem.ImportFromPem(System.Text.Encoding.UTF8.GetString(bytes));
            return pem;
        }

        try
        {
            return X509CertificateLoader.LoadPkcs12Collection(bytes, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or CryptographicException && OperatingSystem.IsMacOS())
        {
            return X509CertificateLoader.LoadPkcs12Collection(bytes, password, X509KeyStorageFlags.DefaultKeySet);
        }
    }

    private static bool LooksLikePem(byte[] bytes)
    {
        var head = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 64));
        return head.Contains("-----BEGIN", StringComparison.Ordinal);
    }
}
