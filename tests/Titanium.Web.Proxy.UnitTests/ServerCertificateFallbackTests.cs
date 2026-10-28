using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ServerCertificateFallbackTests
{
    /// <summary>
    ///     The product-default root subject. On machines where a different certificate with this subject is
    ///     already in the OS store (Inspector, examples) Windows fails the chain pre-build; the handshake must
    ///     still work. On clean machines this simply exercises the normal context path.
    /// </summary>
    private const string ProductDefaultRootName = "Titanium Root Certificate Authority";

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task ConfigureServerCertificate_WithDefaultSubjectRoot_StillCompletesHandshake()
    {
        using var mgr = new CertificateManager(ProductDefaultRootName, IsolatedTestCa.Issuer, false, false, false,
            NullLogger.Instance)
        {
            CertificateEngine = CertificateEngine.BouncyCastle
        };
        Assert.IsTrue(mgr.CreateRootCertificate(false));
        using var leaf = (await mgr.CreateServerCertificate("fallback.example"))!;

        var options = new SslServerAuthenticationOptions();
        mgr.ConfigureServerCertificate(options, leaf);
        Assert.IsTrue(options.ServerCertificateContext != null || options.ServerCertificate != null,
            "a certificate must always be configured, even when the OS chain pre-build fails");

        // Second call (possibly served from the failure memo) must behave identically.
        var again = new SslServerAuthenticationOptions();
        mgr.ConfigureServerCertificate(again, leaf);
        Assert.IsTrue(again.ServerCertificateContext != null || again.ServerCertificate != null);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serverTask = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync();
                using var serverSsl = new SslStream(accepted.GetStream(), false);
                await serverSsl.AuthenticateAsServerAsync(options);
            });

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var clientSsl = new SslStream(client.GetStream(), false);
            var chainPolicy = new X509ChainPolicy
            {
                RevocationMode = X509RevocationMode.NoCheck,
                TrustMode = X509ChainTrustMode.CustomRootTrust
            };
            chainPolicy.CustomTrustStore.Add(mgr.RootCertificate!);
            await clientSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "fallback.example",
                CertificateChainPolicy = chainPolicy
            });            await serverTask;
        }
        finally
        {
            listener.Stop();
        }
    }
}
