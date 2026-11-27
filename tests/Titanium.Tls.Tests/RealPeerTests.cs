using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class RealPeerTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Slow")]
    [Timeout(120000)]
    public void ManagedClientHandshakesWellKnownHosts()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TITANIUM_REAL_PEERS"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set TITANIUM_REAL_PEERS=1 to handshake the fixed host list.");
        }

        string[] hosts = ["www.cloudflare.com", "www.google.com", "github.com", "www.microsoft.com", "www.wikipedia.org"];
        var report = new StringBuilder();
        int succeeded = 0;
        foreach (string host in hosts)
        {
            string line = Handshake(host);
            report.AppendLine(line);
            TestContext.WriteLine(line);
            if (line.Contains(" ok ", StringComparison.Ordinal))
            {
                succeeded++;
            }
        }

        Assert.IsTrue(succeeded > 0, report.ToString());
    }

    private static string Handshake(string host)
    {
        using var tcp = new TcpClient();
        tcp.ReceiveTimeout = 15000;
        tcp.SendTimeout = 15000;
        try
        {
            tcp.Connect(host, 443);
            using var stream = new TlsStream(tcp.GetStream());
            stream.AuthenticateAsClient(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls13,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
            });
            return host + " ok " + stream.NegotiatedCipherSuite + " " + stream.NegotiatedApplicationProtocol;
        }
        catch (Exception ex)
        {
            return host + " fail " + ex.GetType().Name + ": " + ex.Message;
        }
    }
}
