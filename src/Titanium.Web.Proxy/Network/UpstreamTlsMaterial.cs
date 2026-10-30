using System;
using System.Runtime.CompilerServices;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Network;

namespace Titanium.Web.Proxy.Network;

/// <summary>
///     Parsed upstream TLS settings of one <see cref="ClusterHttpClientConfig" />: client certificate, trust
///     anchors, SNI override, and hostname policy. Built once per config instance (at cluster apply, or on
///     first use) so no handshake ever reads a file. Null for a cluster that sets none of them, which keeps
///     the default path free of any extra work.
/// </summary>
internal sealed class UpstreamTlsMaterial
{
    private static readonly ConditionalWeakTable<ClusterHttpClientConfig, UpstreamTlsMaterial> Cache = new();

    private int errorLogged;

    private UpstreamTlsMaterial(ClusterHttpClientConfig config, UpstreamTlsFileSnapshot files)
    {
        ServerName = string.IsNullOrWhiteSpace(config.ServerName) ? null : config.ServerName.Trim();
        VerifyHostname = config.VerifyHostname;
        AcceptAny = config.AcceptAnyServerCertificate;
        Fingerprint = files.Fingerprint;
        Error = files.Error;
        if (Error is not null)
        {
            return;
        }

        try
        {
            if (files.Certificate is not null)
            {
                ClientCertificate = LoadClientCertificate(config, files);
            }

            if (files.CaBundle is not null)
            {
                var roots = ClientCertificateAuthorityFiles.Parse(files.CaBundle);
                if (roots.Count == 0)
                {
                    Error = $"caBundlePath '{config.CaBundlePath}' contains no certificates.";
                }
                else
                {
                    Roots = roots;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException
                                       or InvalidOperationException or FormatException)
        {
            ClientCertificate = null;
            Roots = null;
            Error = $"upstream TLS material for '{config.ClientCertificatePath ?? config.CaBundlePath}' cannot be parsed: {ex.Message}";
        }
    }

    /// <summary>Client certificate with a usable private key, or null.</summary>
    internal X509Certificate2? ClientCertificate { get; private set; }

    /// <summary>Trust anchors replacing the machine store, or null.</summary>
    internal X509Certificate2Collection? Roots { get; private set; }

    /// <summary>Overrides TLS SNI and the name checked against the certificate. Null keeps the origin host.</summary>
    internal string? ServerName { get; }

    /// <summary>False masks only <see cref="System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch" />.</summary>
    internal bool VerifyHostname { get; }

    internal bool AcceptAny { get; }

    /// <summary>Lowercase hex SHA-256 of the configured files and policy. Part of the pool key.</summary>
    internal string Fingerprint { get; }

    /// <summary>Set when a configured file could not be read or parsed. Every handshake then fails closed.</summary>
    internal string? Error { get; private set; }

    /// <summary>Returns the material for a cluster, or null when it sets no upstream TLS option.</summary>
    internal static UpstreamTlsMaterial? For(ClusterHttpClientConfig? config)
    {
        if (config is null || !config.HasTlsMaterial)
        {
            return null;
        }

        return Cache.GetValue(config, static c => new UpstreamTlsMaterial(c, c.TlsFiles));
    }

    /// <summary>
    ///     Applies the cluster's trust anchors and hostname policy to the errors SslStream reported. Only a
    ///     name mismatch is ever masked, and only when <see cref="VerifyHostname" /> is off. Anchors replace
    ///     the machine store: the platform's chain verdict is discarded and the chain is rebuilt against
    ///     only those roots.
    /// </summary>
    internal SslPolicyErrors ApplyPolicy(X509Certificate? certificate, X509Chain? chain,
        SslPolicyErrors sslPolicyErrors, X509RevocationMode revocationMode)
    {
        if (!VerifyHostname)
            sslPolicyErrors &= ~SslPolicyErrors.RemoteCertificateNameMismatch;

        if (Roots == null || certificate == null
            || (sslPolicyErrors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
            return sslPolicyErrors;

        sslPolicyErrors &= ~SslPolicyErrors.RemoteCertificateChainErrors;
        using var leaf = new X509Certificate2(certificate);
        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.AddRange(Roots);
        custom.ChainPolicy.RevocationMode = revocationMode;
        if (chain != null)
        {
            foreach (var element in chain.ChainElements)
                custom.ChainPolicy.ExtraStore.Add(element.Certificate);
        }

        if (!custom.Build(leaf))
            sslPolicyErrors |= SslPolicyErrors.RemoteCertificateChainErrors;

        return sslPolicyErrors;
    }
    /// <summary>True once per material, so a broken file is logged once rather than per request.</summary>
    internal bool TryClaimErrorLog() => Interlocked.Exchange(ref errorLogged, 1) == 0;

    /// <summary>
    ///     Throws when the configured files were unusable. The exception text names the setting and path but
    ///     never the password or key bytes.
    /// </summary>
    internal void ThrowIfFaulted()
    {
        if (Error is { } error)
        {
            throw new AuthenticationException("Upstream TLS configuration is unusable: " + error);
        }
    }

    private static X509Certificate2 LoadClientCertificate(ClusterHttpClientConfig config, UpstreamTlsFileSnapshot files)
    {
        var password = ReadPassword(config.ClientCertificatePasswordEnv);
        X509Certificate2 loaded;
        if (files.Key is not null)
        {
            var certPem = Encoding.UTF8.GetString(files.Certificate!);
            var keyPem = Encoding.UTF8.GetString(files.Key);
            loaded = keyPem.Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal)
                ? X509Certificate2.CreateFromEncryptedPem(certPem, keyPem, password)
                : X509Certificate2.CreateFromPem(certPem, keyPem);
            try
            {
                if (!loaded.HasPrivateKey)
                {
                    throw new InvalidOperationException("the private key does not match the certificate.");
                }

                // SChannel needs a persisted key; a PEM-created key is ephemeral and the handshake would fail.
                return X509CertificateLoader.LoadPkcs12(loaded.Export(X509ContentType.Pkcs12), null);
            }
            finally
            {
                loaded.Dispose();
            }
        }

        loaded = X509CertificateLoader.LoadPkcs12(files.Certificate!, password);
        if (!loaded.HasPrivateKey)
        {
            loaded.Dispose();
            throw new InvalidOperationException("the PFX file holds no private key.");
        }

        return loaded;
    }

    private static string? ReadPassword(string? envName)
    {
        if (string.IsNullOrEmpty(envName))
        {
            return null;
        }

        return Environment.GetEnvironmentVariable(envName)
               ?? throw new InvalidOperationException($"environment variable '{envName}' is not set.");
    }
}
