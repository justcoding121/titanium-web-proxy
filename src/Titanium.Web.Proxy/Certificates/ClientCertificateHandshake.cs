using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.Certificates;

/// <summary>
/// Requests an inbound client certificate for <see cref="ClientCertificateMode.Allow"/> and
/// <see cref="ClientCertificateMode.Require"/>. Allow accepts a missing certificate. Both verify a
/// presented certificate with the platform check. A verified certificate is copied onto the connection.
/// </summary>
internal static class ClientCertificateHandshake
{
    internal static bool RequestsCertificate(ClientCertificateMode mode) =>
        mode != ClientCertificateMode.None;

    internal static void Apply(SslServerAuthenticationOptions options, ClientCertificateMode mode,
        X509Certificate2Collection? trustedRoots = null)
    {
        if (mode == ClientCertificateMode.None)
        {
            return;
        }

        options.ClientCertificateRequired = true;
        if (trustedRoots is { Count: > 0 } roots)
        {
            // The callback closes over this collection reference. A reload assigns a new collection
            // on the endpoint; a handshake already in progress keeps the one it captured.
            options.RemoteCertificateValidationCallback = (_, certificate, chain, _) =>
                ValidateWithRoots(certificate, chain, mode, roots);
            return;
        }

        if (mode == ClientCertificateMode.Allow)
        {
            options.RemoteCertificateValidationCallback = static (_, certificate, _, errors) =>
                AcceptOptional(certificate, errors);
        }
    }

    internal static void Capture(TcpClientConnection connection, SslStream stream, ClientCertificateMode mode)
    {
        if (mode == ClientCertificateMode.None || stream.RemoteCertificate is not X509Certificate remote)
        {
            return;
        }

        var copy = X509CertificateLoader.LoadCertificate(remote.Export(X509ContentType.Cert));
        var previous = connection.ClientCertificate;
        connection.ClientCertificate = copy;
        previous?.Dispose();
    }

    /// <summary>
    /// Allow mode: a missing certificate is fine. A presented certificate must pass the same platform
    /// chain check as Require. The subject is forwarded to the origin as identity, so an unverified
    /// certificate is refused rather than recorded.
    /// </summary>
    internal static bool AcceptOptional(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            return true;
        }

        return errors == SslPolicyErrors.None;
    }

    /// <summary>
    ///     Custom-root check used when the endpoint has trust anchors. Revocation is not checked and
    ///     authority-information downloads are disabled. A client certificate must carry the
    ///     client-authentication EKU; a certificate with no EKU extension is rejected.
    /// </summary>
    internal static bool ValidateWithRoots(X509Certificate? certificate, X509Chain? platformChain,
        ClientCertificateMode mode, X509Certificate2Collection roots)
    {
        if (certificate is null)
        {
            return mode == ClientCertificateMode.Allow;
        }

        X509Certificate2? copy = null;
        X509Chain? chain = null;
        try
        {
            copy = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
            chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(roots);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationTime = DateTime.UtcNow;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
            if (platformChain is not null)
            {
                foreach (var element in platformChain.ChainElements)
                {
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                }
            }

            if (!chain.Build(copy))
            {
                return false;
            }

            foreach (var extension in copy.Extensions)
            {
                if (extension is not X509EnhancedKeyUsageExtension eku)
                {
                    continue;
                }

                foreach (var oid in eku.EnhancedKeyUsages)
                {
                    if (oid.Value == "1.3.6.1.5.5.7.3.2")
                    {
                        return true;
                    }
                }

                return false;
            }

            return false;
        }
        finally
        {
            chain?.Dispose();
            copy?.Dispose();
        }
    }
}
