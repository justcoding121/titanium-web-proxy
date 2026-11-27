using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Titanium.Tls;

/// <summary>
/// Applies <see cref="RemoteCertificateValidationCallback"/> the way <c>SslStream</c> does, then disposes the chain.
/// </summary>
internal static class TlsCertificateDecision
{
    internal const string RemoteCertificateInvalid = "The remote certificate is invalid according to the validation procedure.";

    internal const string RemoteCertificateRejected = "The remote certificate was rejected by the provided RemoteCertificateValidationCallback.";

    internal static void ThrowIfRejected(SslPolicyErrors errors, RemoteCertificateValidationCallback? callback, object? sender, X509Certificate? certificate, X509Chain? chain)
    {
        try
        {
            bool accepted = errors == SslPolicyErrors.None;
            if (callback is not null)
            {
                accepted = callback(sender!, certificate, chain, errors);
            }

            if (!accepted)
            {
                string message = callback is not null
                    ? RemoteCertificateRejected
                    : ChainErrorMessage(chain);
                throw new AuthenticationException(message);
            }
        }
        finally
        {
            chain?.Dispose();
        }
    }

    private static string ChainErrorMessage(X509Chain? chain)
    {
        if (chain is null || chain.ChainStatus.Length == 0)
        {
            return RemoteCertificateInvalid;
        }

        var text = new StringBuilder("The remote certificate is invalid because of errors in the certificate chain: ");
        for (int i = 0; i < chain.ChainStatus.Length; i++)
        {
            if (i > 0)
            {
                text.Append(", ");
            }

            text.Append(chain.ChainStatus[i].Status);
        }

        return text.ToString();
    }
}
