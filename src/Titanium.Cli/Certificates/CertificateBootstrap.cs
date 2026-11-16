using System.Net;
using System.Security.Cryptography.X509Certificates;
using Certes;
using Certes.Acme.Resource;
using Titanium.Cli.Config;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions.Network;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;

namespace Titanium.Cli.Certificates;

/// <summary>Applies certificate paths and ACME HTTP-01 challenge / issuance.</summary>
internal static class CertificateBootstrap
{
    private static readonly Dictionary<string, string> AcmeTokens = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (string Kind, DateOnly Day)> ReportedExpiry = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Warn when a served certificate has this much time or less remaining.</summary>
    internal static readonly TimeSpan ExpiryWarnWindow = TimeSpan.FromDays(30);

    /// <summary>How often the ACME renewal loop re-checks a still-valid certificate.</summary>
    internal static TimeSpan RenewalCheckInterval { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Optional test hook replacing Certes issuance.</summary>
    internal static Func<ProxyServer, CertificatesConfig, CancellationToken, Task>? IssueOverride { get; set; }

    /// <summary>Optional delay hook for the renewal loop (tests can no-op or advance immediately).</summary>
    internal static Func<TimeSpan, CancellationToken, Task>? DelayAsync { get; set; }

    public static void Apply(ProxyServer proxy, CertificatesConfig? certificates)
    {
        if (certificates is null)
        {
            return;
        }

        TryApplyLeafCertificate(proxy, certificates);
        ApplySniItems(proxy, certificates);
        if (string.IsNullOrEmpty(certificates.AcmeDomain))
        {
            return;
        }

        RegisterAcmeChallengeHandling(proxy, certificates);
    }

    private static void TryApplyLeafCertificate(ProxyServer proxy, CertificatesConfig certificates)
    {
        if (string.IsNullOrEmpty(certificates.CertificatePath))
        {
            return;
        }

        AsyncConsole.WriteLine($"Certificate path configured: {certificates.CertificatePath}");
        if (TryLoadLeaf(certificates.CertificatePath, certificates.PrivateKeyPath, out var leaf) && leaf is not null)
        {
            ReportServedCertificateExpiry(certificates.CertificatePath, leaf);
            AssignGenericCertificate(proxy, leaf);
            AsyncConsole.WriteLine("Loaded leaf certificate onto DecryptSsl endpoints.");
        }
        else if (File.Exists(certificates.CertificatePath))
        {
            AsyncConsole.WriteLine(
                "Certificate file present but could not load (need PEM+PrivateKeyPath or PFX).");
        }
    }

    /// <summary>
    ///     Logs when a served certificate is expired or within <see cref="ExpiryWarnWindow"/>.
    ///     Does not refuse the certificate. Dedupes one message per file path per day.
    /// </summary>
    internal static void ReportServedCertificateExpiry(string path, X509Certificate2 certificate)
    {
        var remaining = certificate.NotAfter - DateTime.Now;
        string kind;
        string message;
        if (remaining <= TimeSpan.Zero)
        {
            kind = "expired";
            message =
                $"Served certificate '{path}' has expired (NotAfter={certificate.NotAfter:u}).";
        }
        else if (remaining <= ExpiryWarnWindow)
        {
            kind = "expiring";
            var days = Math.Max(0, (int)Math.Ceiling(remaining.TotalDays));
            message =
                $"Served certificate '{path}' expires in {days} day(s) (NotAfter={certificate.NotAfter:u}).";
        }
        else
        {
            return;
        }

        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        lock (ReportedExpiry)
        {
            if (ReportedExpiry.TryGetValue(path, out var prior) && prior.Kind == kind && prior.Day == day)
            {
                return;
            }

            ReportedExpiry[path] = (kind, day);
        }

        if (kind == "expired")
        {
            AsyncConsole.WriteError(message);
        }
        else
        {
            AsyncConsole.WriteLine(message);
        }
    }

    private static void RegisterAcmeChallengeHandling(ProxyServer proxy, CertificatesConfig certificates)
    {
        AsyncConsole.WriteLine($"ACME domain configured: {certificates.AcmeDomain} (email={certificates.AcmeEmail ?? "(none)"})");
        AsyncConsole.WriteLine("HTTP-01: place challenge tokens via env TITANIUM_ACME_TOKEN=token:keyAuth or file under .well-known.");

        if (!string.IsNullOrEmpty(certificates.CertificatePath) &&
            !File.Exists(certificates.CertificatePath))
        {
            AsyncConsole.WriteLine(
                "Note: ACME domain set but certificate files are missing. " +
                "Serve HTTP-01, place PEM/PFX at CertificatePath (+ PrivateKeyPath), then call ReplaceCertificate " +
                "or IssueOrRenewAsync (also honors TITANIUM_ACME_CERT_PATH).");
        }

        SeedTokenFromEnvironment();

        proxy.BeforeRequest += (_, e) =>
        {
            var path = e.HttpClient.Request.RequestUri?.AbsolutePath ?? "";
            const string prefix = "/.well-known/acme-challenge/";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return Task.CompletedTask;
            }

            var token = path[prefix.Length..];
            if (AcmeTokens.TryGetValue(token, out var keyAuth))
            {
                e.Ok(keyAuth, [new HttpHeader("Content-Type", "text/plain")]);
                return Task.CompletedTask;
            }

            var file = Path.Combine(AppContext.BaseDirectory, ".well-known", "acme-challenge", token);
            if (File.Exists(file))
            {
                e.Ok(File.ReadAllText(file), [new HttpHeader("Content-Type", "text/plain")]);
                return Task.CompletedTask;
            }

            e.GenericResponse("challenge not found", HttpStatusCode.NotFound);
            return Task.CompletedTask;
        };
    }

    private static void SeedTokenFromEnvironment()
    {
        var env = Environment.GetEnvironmentVariable("TITANIUM_ACME_TOKEN");
        if (string.IsNullOrEmpty(env))
        {
            return;
        }

        var parts = env.Split(':', 2);
        if (parts.Length == 2)
        {
            AcmeTokens[parts[0]] = parts[1];
        }
    }

    /// <summary>
    /// Reloads a leaf certificate from disk and assigns it to all <see cref="ProxyEndPoint.DecryptSsl"/> endpoints.
    /// </summary>
    public static void ReplaceCertificate(ProxyServer proxy, string certPath, string? keyPath)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentException.ThrowIfNullOrWhiteSpace(certPath);

        if (!TryLoadLeaf(certPath, keyPath, out var leaf) || leaf is null)
        {
            throw new InvalidOperationException(
                $"Unable to load certificate from '{certPath}' (key='{keyPath ?? "(none)"}').");
        }

        AssignGenericCertificate(proxy, leaf);
        AsyncConsole.WriteLine($"Replaced GenericCertificate from {certPath}.");
    }

    /// <summary>
    /// Issues a certificate via ACME HTTP-01 when <see cref="CertificatesConfig.AcmeDirectory"/> is set;
    /// otherwise documents the operator path and seeds challenge tokens from env.
    /// Skips the ACME order when an existing certificate still has more than
    /// <see cref="ExpiryWarnWindow"/> remaining.
    /// </summary>
    public static async Task IssueAcmeCertificateAsync(ProxyServer proxy, CertificatesConfig certificates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(certificates);

        if (string.IsNullOrEmpty(certificates.AcmeEmail) || string.IsNullOrEmpty(certificates.AcmeDomain))
        {
            return;
        }

        if (TryApplyExistingFreshCertificate(proxy, certificates))
        {
            return;
        }

        if (IssueOverride is not null)
        {
            await IssueOverride(proxy, certificates, cancellationToken).ConfigureAwait(false);
            return;
        }

        SeedTokenFromEnvironment();

        var directory = certificates.AcmeDirectory
                        ?? Environment.GetEnvironmentVariable("TITANIUM_ACME_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory))
        {
            AsyncConsole.WriteLine(
                $"ACME: no AcmeDirectory for {certificates.AcmeDomain} — " +
                "set certificates.acmeDirectory or TITANIUM_ACME_DIRECTORY for automated issue, " +
                "or place PEM/PFX and call ReplaceCertificate / IssueOrRenewAsync.");
            return;
        }

        AsyncConsole.WriteLine($"ACME: issuing for {certificates.AcmeDomain} via {directory}");
        await IssueWithCertesAsync(proxy, certificates, directory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Resolves the ACME certificate and key paths: <c>TITANIUM_ACME_CERT_PATH</c> / <c>TITANIUM_ACME_KEY_PATH</c>,
    ///     then the configured paths, then <c>acme-cert.pem</c> / <c>acme-key.pem</c> next to the executable.
    /// </summary>
    internal static (string CertPath, string KeyPath) ResolveAcmePaths(CertificatesConfig certificates)
    {
        var envCert = Environment.GetEnvironmentVariable("TITANIUM_ACME_CERT_PATH");
        var envKey = Environment.GetEnvironmentVariable("TITANIUM_ACME_KEY_PATH");
        var certPath = !string.IsNullOrEmpty(envCert) ? envCert
            : !string.IsNullOrEmpty(certificates.CertificatePath) ? certificates.CertificatePath
            : Path.Combine(AppContext.BaseDirectory, "acme-cert.pem");
        var keyPath = !string.IsNullOrEmpty(envKey) ? envKey
            : !string.IsNullOrEmpty(certificates.PrivateKeyPath) ? certificates.PrivateKeyPath
            : Path.Combine(AppContext.BaseDirectory, "acme-key.pem");
        return (certPath, keyPath);
    }
    /// <summary>
    ///     True when <see cref="CertificatesConfig.CertificatePath"/> loads and has more than
    ///     <see cref="ExpiryWarnWindow"/> remaining. Applies the leaf and skips ACME.
    /// </summary>
    internal static bool TryApplyExistingFreshCertificate(ProxyServer proxy, CertificatesConfig certificates)
    {
        var (certPath, keyPath) = ResolveAcmePaths(certificates);
        if (!File.Exists(certPath))
        {
            return false;
        }

        if (!TryLoadLeaf(certPath, keyPath, out var leaf) || leaf is null)
        {
            return false;
        }

        if (leaf.NotAfter - DateTime.Now <= ExpiryWarnWindow)
        {
            leaf.Dispose();
            return false;
        }

        ReportServedCertificateExpiry(certPath, leaf);
        AssignGenericCertificate(proxy, leaf);
        AsyncConsole.WriteLine(
            $"ACME: existing certificate at {certPath} still has more than {ExpiryWarnWindow.TotalDays:0} days left; skipping issue.");
        return true;
    }

    /// <summary>
    ///     Issues or renews once, then rechecks every <see cref="RenewalCheckInterval"/> with jitter.
    ///     Renews when 30 days or less remain. On failure keeps the current cert and backs off.
    /// </summary>
    public static async Task RunAcmeRenewalLoopAsync(
        ProxyServer proxy,
        CertificatesConfig certificates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(certificates);

        var backoff = TimeSpan.FromHours(1);
        var first = true;
        var delay = DelayAsync ?? ((ts, ct) => Task.Delay(ts, ct));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // First pass always runs so an existing fresh certificate is applied; later passes only
                // contact the ACME server when the served certificate is within the renewal window.
                if (first || NeedsRenewal(certificates))
                {
                    first = false;
                    await IssueOrRenewAsync(proxy, certificates, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }

                backoff = TimeSpan.FromHours(1);

                if (!NeedsRenewal(certificates))
                {
                    var jitterMs = Random.Shared.Next(0, (int)TimeSpan.FromMinutes(30).TotalMilliseconds);
                    await delay(RenewalCheckInterval + TimeSpan.FromMilliseconds(jitterMs), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AsyncConsole.WriteError(
                    $"ACME renewal failed; keeping current certificate. Retry in {backoff.TotalHours:0.##}h: {ex.Message}");
                await delay(backoff, cancellationToken).ConfigureAwait(false);
                var nextHours = Math.Min(12, backoff.TotalHours * 2);
                backoff = TimeSpan.FromHours(nextHours);
                continue;
            }

            // Still needs renewal after a successful IssueOrRenew (for example poll timeout) — back off briefly.
            await delay(backoff, cancellationToken).ConfigureAwait(false);
            var doubled = Math.Min(12, backoff.TotalHours * 2);
            backoff = TimeSpan.FromHours(doubled);
        }
    }

    /// <summary>True when the configured certificate is missing, unreadable, or within the warn window.</summary>
    internal static bool NeedsRenewal(CertificatesConfig certificates)
    {
        var (certPath, keyPath) = ResolveAcmePaths(certificates);
        if (!File.Exists(certPath))
        {
            return true;
        }

        if (!TryLoadLeaf(certPath, keyPath, out var leaf) || leaf is null)
        {
            return true;
        }

        try
        {
            return leaf.NotAfter - DateTime.Now <= ExpiryWarnWindow;
        }
        finally
        {
            leaf.Dispose();
        }
    }

    private static async Task IssueWithCertesAsync(
        ProxyServer proxy,
        CertificatesConfig certificates,
        string directoryUrl,
        CancellationToken cancellationToken)
    {
        var directoryUri = new Uri(directoryUrl);
        var acme = new AcmeContext(directoryUri);
        await acme.NewAccount(certificates.AcmeEmail!, true).ConfigureAwait(false);

        var order = await acme.NewOrder([certificates.AcmeDomain!]).ConfigureAwait(false);
        var authz = (await order.Authorizations().ConfigureAwait(false)).First();
        var httpChallenge = await authz.Http().ConfigureAwait(false);
        SetChallengeToken(httpChallenge.Token, httpChallenge.KeyAuthz);
        await httpChallenge.Validate().ConfigureAwait(false);

        // Poll authorization
        for (var i = 0; i < 30; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resource = await authz.Resource().ConfigureAwait(false);
            if (resource.Status == AuthorizationStatus.Valid)
            {
                break;
            }

            if (resource.Status == AuthorizationStatus.Invalid)
            {
                throw new InvalidOperationException("ACME authorization invalid.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        var privateKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var cert = await order.Generate(new CsrInfo
        {
            CommonName = certificates.AcmeDomain,
        }, privateKey).ConfigureAwait(false);

        var certPem = cert.ToPem();
        var keyPem = privateKey.ToPem();
        var (certPath, keyPath) = ResolveAcmePaths(certificates);
        await File.WriteAllTextAsync(certPath, certPem, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(keyPath, keyPem, cancellationToken).ConfigureAwait(false);
        ReplaceCertificate(proxy, certPath, keyPath);
        AsyncConsole.WriteLine($"ACME: certificate written to {certPath}");
    }

    /// <summary>
    /// Runs ACME issue (when directory configured), then polls for cert files and calls <see cref="ReplaceCertificate"/>.
    /// </summary>
    public static async Task IssueOrRenewAsync(ProxyServer proxy, CertificatesConfig certificates,
        TimeSpan? pollTimeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(certificates);

        await IssueAcmeCertificateAsync(proxy, certificates, cancellationToken).ConfigureAwait(false);

        var timeout = pollTimeout ?? TimeSpan.FromMinutes(5);
        if (int.TryParse(Environment.GetEnvironmentVariable("TITANIUM_ACME_POLL_SECONDS"), out var secs) && secs > 0)
        {
            timeout = TimeSpan.FromSeconds(secs);
        }

        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (certPath, keyPath) = ResolveAcmePaths(certificates);

            if (File.Exists(certPath) &&
                TryLoadLeaf(certPath, keyPath, out _))
            {
                ReplaceCertificate(proxy, certPath, keyPath);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        AsyncConsole.WriteLine(
            "IssueOrRenewAsync timed out waiting for certificate files. " +
            "Place certs and call ReplaceCertificate when ready.");
    }

    public static void SetChallengeToken(string token, string keyAuthorization) =>
        AcmeTokens[token] = keyAuthorization;

    /// <summary>Binds <see cref="CertificatesConfig.Items"/> onto decrypting listeners. Does not drop connections.</summary>
    public static void ApplySniItems(ProxyServer proxy, CertificatesConfig? certificates)
    {
        if (certificates?.Items is not { Count: > 0 } items)
        {
            return;
        }

        var bindings = new List<SniCertificateBinding>();
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Path) || item.Hosts is not { Count: > 0 })
            {
                continue;
            }

            if (!TryLoadLeaf(item.Path, keyPath: null, out var certificate, item.Password) || certificate is null)
            {
                continue;
            }

            var hosts = item.Hosts.Where(host => !string.IsNullOrWhiteSpace(host)).ToList();

            if (hosts.Count == 0)
            {
                certificate.Dispose();
                continue;
            }

            ReportServedCertificateExpiry(item.Path, certificate);
            bindings.Add(SniCertificateBinding.Create(hosts, certificate));
        }

        if (bindings.Count == 0)
        {
            return;
        }

        IReadOnlyList<SniCertificateBinding> frozen = bindings;
        foreach (var endPoint in proxy.ProxyEndPoints)
        {
            if (endPoint.DecryptSsl || endPoint is TransparentBaseProxyEndPoint { TerminateTls: true })
            {
                endPoint.SniCertificates = frozen;
            }
        }
    }

    /// <summary>Rebuilds leaf and SNI bindings when a configured certificate file changes.</summary>
    public static IDisposable? WatchCertificateFiles(ProxyServer proxy, CertificatesConfig? certificates,
        IEnumerable<ListenerConfig>? listeners = null)
    {
        var existing = ExistingCertificatePaths(certificates, listeners);
        if (existing.Count == 0 || certificates is null)
        {
            return null;
        }

        // Content-hash watch: a cert-manager or Kubernetes Secret mount swaps ..data, which a watcher on the
        // file name alone would never see. Apply and the CA reload run one at a time.
        var gate = new object();
        return new ContentWatcher(
            existing,
            () =>
            {
                lock (gate)
                {
                    Apply(proxy, certificates);
                    if (listeners is not null)
                    {
                        ReloadClientCertificateAuthorities(proxy, listeners);
                    }
                }

                return Task.CompletedTask;
            },
            onError: ex => AsyncConsole.WriteError("Certificate reload failed: " + ex.Message));
    }

    private static List<string> ExistingCertificatePaths(CertificatesConfig? certificates, IEnumerable<ListenerConfig>? listeners)
    {
        return CollectCertificatePaths(certificates, listeners)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> CollectCertificatePaths(CertificatesConfig? certificates, IEnumerable<ListenerConfig>? listeners)
    {
        var paths = new List<string>();
        AddPath(paths, certificates?.CertificatePath);
        AddPath(paths, certificates?.PrivateKeyPath);
        if (certificates?.Items is not null)
        {
            foreach (var item in certificates.Items)
            {
                AddPath(paths, item.Path);
            }
        }

        if (listeners is not null)
        {
            foreach (var listener in listeners)
            {
                AddPath(paths, listener.ClientCertificateCaPath);
            }
        }

        return paths;
    }

    private static void AddPath(List<string> paths, string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            paths.Add(path);
        }
    }

    internal static void ReloadClientCertificateAuthorities(ProxyServer proxy, IEnumerable<ListenerConfig> listeners)
    {
        foreach (var listener in listeners)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(listener.ClientCertificateCaPath) || !File.Exists(listener.ClientCertificateCaPath))
                {
                    continue;
                }

                var roots = ClientCertificateAuthorityFiles.Load(listener.ClientCertificateCaPath);
                var address = ListenerAddress(listener.Host);
                foreach (var endPoint in proxy.ProxyEndPoints)
                {
                    if (endPoint.Port == listener.Port && endPoint.IpAddress.Equals(address))
                    {
                        // Assign a new collection. An in-flight handshake keeps the reference it captured.
                        endPoint.ClientCertificateTrustedRoots = roots;
                    }
                }
            }
            catch (Exception ex)
            {
                AsyncConsole.WriteError(
                    $"Client certificate authorities for {listener.Host}:{listener.Port} were not reloaded ({ex.Message}).");
            }
        }
    }

    private static IPAddress ListenerAddress(string? host)
    {
        host ??= "0.0.0.0";
        return host is "0.0.0.0" or "*" ? IPAddress.Any : IPAddress.Parse(host);
    }

    private static void AssignGenericCertificate(ProxyServer proxy, X509Certificate2 leaf)
    {
        foreach (var endPoint in proxy.ProxyEndPoints)
        {
            if (endPoint.DecryptSsl)
            {
                endPoint.GenericCertificate = leaf;
            }
        }
    }

    private static bool TryLoadLeaf(string certPath, string? keyPath, out X509Certificate2? leaf, string? pfxPassword = null)
    {
        leaf = null;
        if (string.IsNullOrEmpty(certPath) || !File.Exists(certPath))
        {
            return false;
        }

        try
        {
            var ext = Path.GetExtension(certPath);
            if (ext.Equals(".pfx", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".p12", StringComparison.OrdinalIgnoreCase))
            {
                var password = pfxPassword ?? Environment.GetEnvironmentVariable("TITANIUM_CERT_PASSWORD");
                // Exportable (not EphemeralKeySet): Windows Schannel needs a usable private key
                // for SslStream.AuthenticateAsServer on TLS-terminate listeners.
                leaf = X509CertificateLoader.LoadPkcs12(
                    File.ReadAllBytes(certPath),
                    password,
                    X509KeyStorageFlags.Exportable);
                return true;
            }

            if (!string.IsNullOrEmpty(keyPath) && File.Exists(keyPath))
            {
                using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
                // Re-wrap as PKCS#12 with Exportable so Schannel can present the leaf.
                leaf = X509CertificateLoader.LoadPkcs12(
                    pem.Export(X509ContentType.Pfx),
                    password: null,
                    X509KeyStorageFlags.Exportable);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            AsyncConsole.WriteError($"Certificate load failed: {ex.Message}");
            return false;
        }
    }
}
