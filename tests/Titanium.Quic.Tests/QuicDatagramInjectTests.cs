using System;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Quic.Tests;

[TestClass]
public sealed class QuicDatagramInjectTests
{
    [TestMethod]
    public async Task InjectedListenerRepliesWithoutASocket()
    {
        var remote = new IPEndPoint(IPAddress.Loopback, 9);
        IPEndPoint? sentTo = null;
        int sent = 0;
        await using ManagedQuicListener listener = ManagedQuicListener.CreateInjected(
            new ManagedQuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                AllowManagedStackUnderFips = true,
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
                {
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions(),
                }),
            },
            new IPEndPoint(IPAddress.Loopback, 443),
            (datagram, destination, _) =>
            {
                sent = datagram.Length;
                sentTo = destination;
                return ValueTask.CompletedTask;
            });

        var packet = new byte[1200];
        packet[0] = 0xC0;
        packet[4] = 2;
        packet[5] = 4;
        packet[10] = 0;
        await listener.HandleDatagramAsync(packet, remote);
        Assert.IsTrue(sent > 0);
        Assert.AreEqual(remote, sentTo);
    }

    [TestMethod]
    public async Task SocketListenerRejectsAnInjectedDatagram()
    {
        using X509Certificate2 certificate = Certificate();
        await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            AllowManagedStackUnderFips = true,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                ServerAuthenticationOptions = new SslServerAuthenticationOptions { ServerCertificate = certificate },
            }),
        });

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            listener.HandleDatagramAsync(new byte[1200], new IPEndPoint(IPAddress.Loopback, 9)).AsTask());
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => listener.DriveAsync().AsTask());
    }

    private static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
