using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class ServerHandshakeTests
{
    [TestMethod]
    public void ClientAndServerFinishTheHandshake()
    {
        using var pair = HandshakePair.Run();
        Assert.AreEqual(TlsEngineState.Connected, pair.Client.State);
        Assert.AreEqual(TlsEngineState.Connected, pair.Server.State);
    }

    [TestMethod]
    public void RejectedHelloIsHandshakeFailure()
    {
        using var client = new TlsEngine(TlsRole.Client, serverName: "localhost");
        using var server = new TlsEngine(TlsRole.Server);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        server.Advance(output.Bytes.ToArray(), out _, output);
        server.Resume(false, output);
        Assert.AreEqual(40, output.Bytes[6]);
        Assert.AreEqual(TlsEngineState.Closed, server.State);
    }

    [TestMethod]
    public async Task CallbackExceptionIsInternalError()
    {
        using var client = new TlsEngine(TlsRole.Client);
        using var server = new TlsEngine(TlsRole.Server);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        server.Advance(output.Bytes.ToArray(), out _, output);
        await Task.Yield();
        server.Resume(static _ => throw new InvalidOperationException("selection failed"), output);
        Assert.AreEqual(80, output.Bytes[6]);
        Assert.AreEqual(TlsEngineState.Closed, server.State);
    }

    [TestMethod]
    public void ServerNameIsVisibleToTheCallback()
    {
        using var client = new TlsEngine(TlsRole.Client, serverName: "localhost");
        using var server = new TlsEngine(TlsRole.Server);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        server.Advance(output.Bytes.ToArray(), out _, output);
        string? seen = null;
        using X509Certificate2 certificate = Certificate();
        server.Resume(info =>
        {
            seen = info.ServerName;
            return new TlsServerSelection { Certificate = certificate };
        }, output);
        Assert.AreEqual("localhost", seen);
        Assert.AreEqual(TlsEngineState.WaitFinished, server.State);
    }

    [TestMethod]
    public void HelloRetryThenFinishes()
    {
        using X509Certificate2 certificate = Certificate();
        using var client = new TlsEngine(TlsRole.Client, serverName: "localhost");
        using var server = new TlsEngine(TlsRole.Server);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        server.Advance(output.Bytes.ToArray(), out _, output);
        server.Resume(_ => new TlsServerSelection { Certificate = certificate, Groups = [NamedGroup.Secp256r1] }, output);
        Assert.AreEqual(0xCF, output.Bytes[11]);
        client.Advance(output.Bytes.ToArray(), out _, output);
        Assert.AreEqual(TlsEngineState.WaitServerHelloOrHrr, client.State);
        server.Advance(output.Bytes.ToArray(), out _, output);
        Assert.AreEqual(TlsEngineState.WaitFinished, server.State);
        client.Advance(output.Bytes.ToArray(), out _, output);
        client.Resume(true, output);
        server.Advance(output.Bytes.ToArray(), out _, output);
        Assert.AreEqual(TlsEngineState.Connected, client.State);
        Assert.AreEqual(TlsEngineState.Connected, server.State);
    }

    [TestMethod]
    public void NoCommonCipherOrGroupFails()
    {
        using X509Certificate2 certificate = Certificate();
        AssertAlert(certificate, new TlsServerSelection { Certificate = certificate, CipherSuites = [0x1302] }, 40);
        AssertAlert(certificate, new TlsServerSelection { Certificate = certificate, Groups = [NamedGroup.Secp384r1] }, 40);
    }

    [TestMethod]
    public void ClientCipherOrderIsHonoredUnlessTheServerPrefersItsOwn()
    {
        byte[] hello = ScriptedHello([0x1303, 0x1301], alpn: null);
        using X509Certificate2 certificate = Certificate();
        byte[] clientFirst = Answer(hello, new TlsServerSelection { Certificate = certificate });
        Assert.AreEqual(0x1303, Cipher(clientFirst));
        byte[] serverFirst = Answer(hello, new TlsServerSelection { Certificate = certificate, PreferServerCipherOrder = true, CipherSuites = [0x1301, 0x1303] });
        Assert.AreEqual(0x1301, Cipher(serverFirst));
    }

    [TestMethod]
    public void ApplicationProtocolMismatchCanBeRequired()
    {
        byte[] hello = ScriptedHello([0x1301], "h2");
        using X509Certificate2 certificate = Certificate();
        var output = new TlsOutput();
        using (var server = new TlsEngine(TlsRole.Server))
        {
            server.Advance(hello, out _, output);
            server.Resume(_ => new TlsServerSelection
            {
                Certificate = certificate,
                ApplicationProtocols = ["http/1.1"],
                RequireApplicationProtocol = true,
            }, output);
            Assert.AreEqual(120, output.Bytes[6]);
        }

        using var selected = new TlsEngine(TlsRole.Server);
        selected.Advance(hello, out _, output);
        selected.Resume(_ => new TlsServerSelection { Certificate = certificate, ApplicationProtocols = ["h2"] }, output);
        Assert.AreEqual("h2", selected.SelectedApplicationProtocol);
        Assert.AreEqual(TlsEngineState.WaitFinished, selected.State);
    }

    [TestMethod]
    public void RsaCertificateWithoutAMatchingSchemeFails()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        AssertAlert(certificate, new TlsServerSelection { Certificate = certificate }, 40);
    }

    [TestMethod]
    public void ResumptionUsesTheTicketTwiceAndRejectsABadOne()
    {
        using X509Certificate2 certificate = Certificate();
        using var tickets = new MemoryTicketProtector { UnixTimeSeconds = 1_000 };
        var cache = new MemoryTlsSessionCache();
        using var first = Complete(certificate, tickets, cache, clientClock: 1_000, serverClock: 1_000);
        Assert.AreEqual(TlsEngineState.Connected, first.Client.State, first.Client.State + " " + first.Server.State);
        Assert.IsTrue(cache.TryGet("localhost", "tls", 1_000, out _), first.Client.State + "/" + first.Server.State);
        using var resumed = Complete(certificate, tickets, cache, clientClock: 1_001, serverClock: 1_001);
        Assert.IsTrue(resumed.Server.IsResumption);
        Assert.AreEqual(TlsEngineState.Connected, resumed.Client.State);
        using var replayed = Complete(certificate, tickets, cache, clientClock: 1_002, serverClock: 1_002);
        Assert.IsTrue(replayed.Server.IsResumption);

        tickets.ForceRotate();
        using var afterRotation = Complete(certificate, tickets, cache, clientClock: 1_003, serverClock: 1_003);
        Assert.IsTrue(afterRotation.Server.IsResumption);

        using X509Certificate2 other = Certificate();
        using var differentCertificate = Complete(other, tickets, cache, clientClock: 1_004, serverClock: 1_004);
        Assert.IsFalse(differentCertificate.Server.IsResumption);
        Assert.AreEqual(TlsEngineState.Connected, differentCertificate.Client.State);
    }

    [TestMethod]
    public void ExpiredTamperedAndReboundTicketsFallBack()
    {
        using X509Certificate2 certificate = Certificate();
        using var tickets = new MemoryTicketProtector { UnixTimeSeconds = 5_000 };
        var cache = new MemoryTlsSessionCache();
        Complete(certificate, tickets, cache, clientClock: 5_000, serverClock: 5_000);

        using var expired = Complete(certificate, tickets, cache, clientClock: 5_001, serverClock: 5_000 + (7 * 60 * 60));
        Assert.IsFalse(expired.Server.IsResumption);

        Assert.IsTrue(cache.TryGet("localhost", "tls", 5_001, out TlsResumptionTicket stored));
        RejectOfferedTicket(certificate, tickets, cache, before: null, hello => hello[^1] ^= 0xFF);
        RejectOfferedTicket(certificate, tickets, cache, before: () => stored.Ticket[3] ^= 0xFF, hello => { });
    }

    private static void RejectOfferedTicket(X509Certificate2 certificate, MemoryTicketProtector tickets, MemoryTlsSessionCache cache, Action? before, Action<byte[]> mutateHello)
    {
        before?.Invoke();
        using var client = new TlsEngine(TlsRole.Client, serverName: "localhost", cache: cache) { UnixTimeSeconds = 5_010 };
        using var server = new TlsEngine(TlsRole.Server) { UnixTimeSeconds = 5_010 };
        var output = new TlsOutput();
        client.Advance([], out _, output);
        byte[] hello = output.Bytes.ToArray();
        mutateHello(hello);
        server.Advance(hello, out _, output);
        server.Resume(_ => new TlsServerSelection { Certificate = certificate, Tickets = tickets }, output);
        Assert.IsFalse(server.IsResumption);
        Assert.AreEqual(TlsEngineState.WaitFinished, server.State);
    }

    [TestMethod]
    public void TicketProtectorKeepsThePreviousKey()
    {
        using var tickets = new MemoryTicketProtector { UnixTimeSeconds = 10 };
        byte[] first = tickets.Protect([1, 2, 3, 4]);
        tickets.ForceRotate();
        var plain = new byte[4];
        Assert.IsTrue(tickets.TryUnprotect(first, plain, out int written));
        Assert.AreEqual(4, written);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, plain);
        byte[] second = tickets.Protect([9]);
        tickets.ForceRotate();
        Assert.IsFalse(tickets.TryUnprotect(first, plain, out _));
        Assert.IsTrue(tickets.TryUnprotect(second, plain, out written));
        Assert.AreEqual(1, written);
    }

    private static void AssertAlert(X509Certificate2 certificate, TlsServerSelection selection, byte description)
    {
        using var client = new TlsEngine(TlsRole.Client);
        using var server = new TlsEngine(TlsRole.Server);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        server.Advance(output.Bytes.ToArray(), out _, output);
        server.Resume(_ => selection, output);
        Assert.AreEqual(description, output.Bytes[6]);
        Assert.AreEqual(TlsEngineState.Closed, server.State);
        _ = certificate;
    }

    private static byte[] Answer(byte[] hello, TlsServerSelection selection)
    {
        using var server = new TlsEngine(TlsRole.Server);
        var output = new TlsOutput();
        server.Advance(hello, out _, output);
        server.Resume(_ => selection, output);
        return output.Bytes.ToArray();
    }

    private static int Cipher(byte[] record)
    {
        int session = record[43];
        int at = 5 + 4 + 2 + 32 + 1 + session;
        return (record[at] << 8) | record[at + 1];
    }

    private static HandshakePair Complete(X509Certificate2 certificate, MemoryTicketProtector tickets, MemoryTlsSessionCache cache, long clientClock, long serverClock)
    {
        var client = new TlsEngine(TlsRole.Client, serverName: "localhost", cache: cache) { UnixTimeSeconds = clientClock };
        var server = new TlsEngine(TlsRole.Server) { UnixTimeSeconds = serverClock };
        var output = new TlsOutput();
        client.Advance([], out _, output);
        server.Advance(output.Bytes.ToArray(), out _, output);
        server.Resume(_ => new TlsServerSelection { Certificate = certificate, Tickets = tickets }, output);
        client.Advance(output.Bytes.ToArray(), out _, output);
        if (client.State == TlsEngineState.WaitCertificateDecision)
        {
            client.Resume(true, output);
        }
        byte[] finished = output.Bytes.ToArray();
        server.Advance(finished, out _, output);
        if (output.Bytes.Length > 0)
        {
            client.Advance(output.Bytes.ToArray(), out _, output);
        }

        return new HandshakePair(client, server, certificate, ownCertificate: false);
    }

    private static byte[] ScriptedHello(ushort[] ciphers, string? alpn)
    {
        var scalar = new byte[32];
        scalar[0] = 9;
        var share = new byte[32];
        X25519.PublicKey(scalar, share);
        var body = new System.Collections.Generic.List<byte> { 1, 0, 0, 0, 3, 3 };
        body.AddRange(new byte[32]);
        body.Add(32);
        body.AddRange(new byte[32]);
        var cipherBytes = new byte[ciphers.Length * 2];
        for (int i = 0; i < ciphers.Length; i++)
        {
            cipherBytes[i * 2] = (byte)(ciphers[i] >> 8);
            cipherBytes[(i * 2) + 1] = (byte)ciphers[i];
        }

        AddVector(body, cipherBytes);
        body.Add(1);
        body.Add(0);
        int extensionsAt = body.Count;
        body.Add(0);
        body.Add(0);
        AddExtension(body, 0x000A, [0x00, 0x04, 0x00, 0x1D, 0x00, 0x17]);
        AddExtension(body, 0x000D, [0x00, 0x02, 0x04, 0x03]);
        AddExtension(body, 0x002B, [0x02, 0x03, 0x04]);
        var keyShare = new byte[4 + 2 + share.Length];
        keyShare[1] = (byte)(2 + 2 + share.Length);
        keyShare[3] = 0x1D;
        keyShare[5] = 32;
        share.CopyTo(keyShare.AsSpan(6));
        AddExtension(body, 0x0033, keyShare);
        if (alpn is not null)
        {
            byte[] name = System.Text.Encoding.ASCII.GetBytes(alpn);
            var protocol = new byte[2 + 1 + name.Length];
            protocol[1] = (byte)(1 + name.Length);
            protocol[2] = (byte)name.Length;
            name.CopyTo(protocol.AsSpan(3));
            AddExtension(body, 0x0010, protocol);
        }

        int extensionLength = body.Count - extensionsAt - 2;
        body[extensionsAt] = (byte)(extensionLength >> 8);
        body[extensionsAt + 1] = (byte)extensionLength;
        int handshakeLength = body.Count - 4;
        body[1] = (byte)(handshakeLength >> 16);
        body[2] = (byte)(handshakeLength >> 8);
        body[3] = (byte)handshakeLength;
        var record = new byte[5 + body.Count];
        record[0] = 22;
        record[1] = 3;
        record[2] = 1;
        record[3] = (byte)(body.Count >> 8);
        record[4] = (byte)body.Count;
        body.CopyTo(record, 5);
        return record;
    }

    private static void AddVector(System.Collections.Generic.List<byte> destination, byte[] value)
    {
        destination.Add((byte)(value.Length >> 8));
        destination.Add((byte)value.Length);
        destination.AddRange(value);
    }

    private static void AddExtension(System.Collections.Generic.List<byte> destination, ushort type, byte[] contents)
    {
        destination.Add((byte)(type >> 8));
        destination.Add((byte)type);
        destination.Add((byte)(contents.Length >> 8));
        destination.Add((byte)contents.Length);
        destination.AddRange(contents);
    }

    internal static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}

internal sealed class HandshakePair : IDisposable
{
    internal HandshakePair(TlsEngine client, TlsEngine server, X509Certificate2 certificate, bool ownCertificate)
    {
        Client = client;
        Server = server;
        Certificate = certificate;
        _ownCertificate = ownCertificate;
    }

    private readonly bool _ownCertificate;

    internal TlsEngine Client { get; }

    internal TlsEngine Server { get; }

    internal X509Certificate2 Certificate { get; }

    internal static HandshakePair Run()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var client = new TlsEngine(TlsRole.Client, serverName: "localhost");
        var server = new TlsEngine(TlsRole.Server);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        byte[] hello = output.Bytes.ToArray();
        server.Advance(hello, out _, output);
        server.Resume(_ => new TlsServerSelection { Certificate = certificate }, output);
        byte[] flight = output.Bytes.ToArray();
        client.Advance(flight, out _, output);
        client.Resume(true, output);
        byte[] finished = output.Bytes.ToArray();
        server.Advance(finished, out _, output);
        return new HandshakePair(client, server, certificate, ownCertificate: true);
    }

    public void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
        if (_ownCertificate)
        {
            Certificate.Dispose();
        }
    }
}
