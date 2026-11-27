using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Tls;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Logging;

namespace Titanium.Web.Proxy.Network.Tcp;

internal partial class TcpConnectionFactory
{
    private static async Task<(SslApplicationProtocol Protocol, bool UsedClientCertificate)> AuthenticateManagedClientAsync(
        ProxyServer proxyServer,
        TlsStream managed,
        SessionEventArgsBase? sessionArgs,
        string remoteHostName,
        int remotePort,
        List<SslApplicationProtocol>? applicationProtocols,
        SslProtocols enabledSslProtocols,
        UpstreamConnectionTiming? timing,
        CancellationToken cancellationToken)
    {
        var usedClientCertificate = false;
        var options = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = applicationProtocols,
            TargetHost = ClusterTargetHost(sessionArgs, remoteHostName),
            ClientCertificates = null,
            EnabledSslProtocols = ClusterSslProtocols(sessionArgs, enabledSslProtocols),
            CertificateRevocationCheckMode = proxyServer.CheckCertificateRevocation,
            RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                proxyServer.ValidateServerCertificate(sender, sessionArgs, certificate, chain, sslPolicyErrors),
            LocalCertificateSelectionCallback = (sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers) =>
            {
                X509Certificate? clientCertificate = proxyServer.SelectClientCertificate(
                    sender, sessionArgs, targetHost, localCertificates, remoteCertificate, acceptableIssuers);
                if (clientCertificate != null)
                {
                    usedClientCertificate = true;
                }

                return clientCertificate!;
            },
        };

        ProxyLog.OriginHandshakeStarting(proxyServer.Logger, remoteHostName, remotePort, applicationProtocols);
        await managed.AuthenticateAsClientAsync(options, cancellationToken).ConfigureAwait(false);
        SslApplicationProtocol negotiated = managed.NegotiatedApplicationProtocol;
        ProxyLog.OriginHandshakeSucceeded(proxyServer.Logger, remoteHostName, remotePort, negotiated);
        timing?.MarkTlsHandshakeCompleted();
        return (negotiated, usedClientCertificate);
    }
}
