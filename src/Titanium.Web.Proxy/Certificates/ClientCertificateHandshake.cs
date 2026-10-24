using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.Certificates;

/// <summary>
/// Requests an inbound client certificate for <see cref="ClientCertificateMode.Allow"/> and
/// <see cref="ClientCertificateMode.Require"/>. Allow accepts a missing certificate. Require keeps
/// the platform check. A presented certificate is copied onto the connection.
/// </summary>
internal static class ClientCertificateHandshake
{
    internal static bool RequestsCertificate(ClientCertificateMode mode) =>
        mode != ClientCertificateMode.None;

    internal static void Apply(SslServerAuthenticationOptions options, ClientCertificateMode mode)
    {
        if (mode == ClientCertificateMode.None)
        {
            return;
        }

        options.ClientCertificateRequired = true;
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
    /// Allow mode: a missing certificate is fine. A presented certificate is kept even when the
    /// OS chain is untrusted, because Allow has no configured trust store. Name and availability
    /// errors are still rejected.
    /// </summary>
    internal static bool AcceptOptional(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            return true;
        }

        const SslPolicyErrors blocked =
            SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable;
        return (errors & blocked) == SslPolicyErrors.None;
    }
}
