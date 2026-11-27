using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Tls.Tests.Support;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class ClientHandshakeTests
{
    [TestMethod]
    public void FixedEntropyMakesTheClientHelloRepeatable()
    {
        byte[] script = new byte[96];
        script.AsSpan().Fill(0x11);
        byte[] first = Hello(script);
        byte[] second = Hello(script);
        CollectionAssert.AreEqual(first, second);
        Assert.AreEqual(22, first[0]);
        Assert.AreEqual(0x0301, (first[1] << 8) | first[2]);
        Assert.AreEqual(1, first[5]);
        Assert.AreEqual(0x0303, (first[9] << 8) | first[10]);
        CollectionAssert.AreEqual(script.AsSpan(0, 32).ToArray(), first.AsSpan(11, 32).ToArray());
        Assert.AreEqual(32, first[43]);
        Assert.IsTrue(first.AsSpan(5).IndexOf(new byte[] { 0x13, 0x01 }) > 0);
        Assert.IsTrue(first.AsSpan(5).IndexOf(new byte[] { 0x03, 0x04 }) > 0);
    }

    [TestMethod]
    public void FullHandshakeAgreesOnTheClientFinished()
    {
        using var client = new TlsEngine(TlsRole.Client, serverName: "localhost");
        var output = new TlsOutput();
        client.Advance([], out _, output);
        using var server = ScriptedServer.Accept(output.Bytes.ToArray());
        byte[] flight = server.Flight();
        client.Advance(flight, out int consumed, output);
        Assert.AreEqual(flight.Length, consumed);
        Assert.AreEqual(TlsEventKind.ServerCertificateReceived, output.Event(0).Kind);
        Assert.AreEqual(TlsEngineState.WaitCertificateDecision, client.State);
        client.Resume(true, output);
        Assert.AreEqual(TlsEngineState.Connected, client.State);
        Assert.AreEqual(TlsEventKind.HandshakeCompleted, output.Event(0).Kind);
        server.VerifyClientFinished(output.Bytes.ToArray());
    }

    [TestMethod]
    public void IgnoredPreSharedKeyFallsBackToAFullHandshake()
    {
        var cache = new MemoryTlsSessionCache();
        var psk = new byte[32];
        psk.AsSpan().Fill(0x22);
        cache.Store("localhost", "tls", new TlsResumptionTicket([7], 60, 5, [0], psk, 0x1301, 100));
        using var client = new TlsEngine(TlsRole.Client, serverName: "localhost", cache: cache) { UnixTimeSeconds = 101 };
        var output = new TlsOutput();
        client.Advance([], out _, output);
        Assert.AreEqual(0x0029, ReadLastExtension(output.Bytes.ToArray()));
        using var server = ScriptedServer.Accept(output.Bytes.ToArray());
        byte[] flight = server.Flight();
        client.Advance(flight, out _, output);
        client.Resume(true, output);
        Assert.AreEqual(TlsEngineState.Connected, client.State);
        server.VerifyClientFinished(output.Bytes.ToArray());
    }

    [TestMethod]
    public void CertificateRequestSendsAnEmptyClientCertificate()
    {
        using var client = new TlsEngine(TlsRole.Client, serverName: "localhost");
        var output = new TlsOutput();
        client.Advance([], out _, output);
        using var server = ScriptedServer.Accept(output.Bytes.ToArray());
        (byte[] verify, byte[] finished) = server.CertificateRequestProof();
        client.Advance(server.ServerHelloRecord(), out _, output);
        client.Advance(server.EncryptedExtensionsMessage(), out _, output);
        client.Advance(server.EncryptedCertificateRequestMessage(), out _, output);
        client.Advance(server.EncryptedCertificateMessage(), out _, output);
        Assert.AreEqual(TlsEngineState.WaitCertificateDecision, client.State);
        client.Resume(true, output);
        client.Advance(server.Encrypted(verify), out _, output);
        client.Advance(server.Encrypted(finished), out _, output);
        Assert.AreEqual(TlsEngineState.Connected, client.State);
        byte[][] messages = server.ClientHandshakeMessages(output.Bytes.ToArray());
        CollectionAssert.AreEqual(new byte[] { 11, 0, 0, 4, 0, 0, 0, 0 }, messages[0]);
        Assert.AreEqual(20, messages[1][0]);
    }

    [TestMethod]
    public void NewSessionTicketIsStored()
    {
        var cache = new MemoryTlsSessionCache();
        using var client = new TlsEngine(TlsRole.Client, serverName: "localhost", cache: cache) { UnixTimeSeconds = 200 };
        var output = new TlsOutput();
        client.Advance([], out _, output);
        using var server = ScriptedServer.Accept(output.Bytes.ToArray());
        byte[] flight = server.Flight();
        client.Advance(flight, out _, output);
        client.Resume(true, output);
        Assert.AreEqual(TlsEngineState.Connected, client.State);
        client.Advance(server.EncryptedApplication(
        [
            4, 0, 0, 16,
            0, 0, 0, 60,
            0, 0, 0, 1,
            1, 0,
            0, 2, 9, 9,
            0, 0,
        ]), out _, output);
        Assert.AreEqual(TlsEventKind.TicketReceived, output.Event(0).Kind);
        Assert.IsTrue(cache.TryGet("localhost", "tls", 200, out TlsResumptionTicket stored));
        CollectionAssert.AreEqual(new byte[] { 9, 9 }, stored.Ticket);
        Assert.AreEqual(60u, stored.LifetimeSeconds);
    }

    [TestMethod]
    public void RejectedCertificateCloses()
    {
        using var client = new TlsEngine(TlsRole.Client);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        using var server = ScriptedServer.Accept(output.Bytes.ToArray());
        client.Advance(server.Flight(), out _, output);
        client.Resume(false, output);
        Assert.AreEqual(42, output.Bytes[6]);
        Assert.AreEqual(TlsEngineState.Closed, client.State);
    }

    [TestMethod]
    public void HelloRetryReplacesTheKeyShareOnce()
    {
        using var client = new TlsEngine(TlsRole.Client);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        byte[] session = SessionId(output.Bytes.ToArray());
        client.Advance(Record(HelloRetry(session, 0x0017, cookie: null)), out _, output);
        Assert.AreEqual(TlsEngineState.WaitServerHelloOrHrr, client.State);
        Assert.AreEqual(0x0017, KeyShareGroup(output.Bytes.ToArray()));
        client.Advance(Record(HelloRetry(session, 0x0017, cookie: [1])), out _, output);
        AssertAlert(output, 10);
    }

    [TestMethod]
    public void HelloRetryWithoutAChangeIsIllegal()
    {
        using var client = new TlsEngine(TlsRole.Client);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        byte[] session = SessionId(output.Bytes.ToArray());
        client.Advance(Record(HelloRetry(session, 0x001D, cookie: null)), out _, output);
        AssertAlert(output, 47);
    }

    [TestMethod]
    public void ServerHelloNegativesFailClosed()
    {
        AssertAlert(Feed(static (session, random) => ServerHello(session, random, 0x1302, includeVersion: true, includeShare: true)), 47);
        AssertAlert(Feed(static (session, random) => ServerHello(session, random, 0x1301, includeVersion: true, includeShare: false)), 109);
        byte[] wrongSession = new byte[32];
        wrongSession[0] = 9;
        AssertAlert(Feed(static (_, random) => ServerHello(new byte[32], random, 0x1301, includeVersion: true, includeShare: true), wrongSession), 47);
        Assert.ThrowsException<TlsVersionNotSupportedException>(() => Feed(static (session, random) => ServerHello(session, random, 0x1301, includeVersion: false, includeShare: true)));
        byte[] sentinel = new byte[32];
        Downgrade().CopyTo(sentinel.AsSpan(24));
        AssertAlert(Feed(static (session, random) => ServerHello(session, random, 0x1301, includeVersion: false, includeShare: true), sentinel), 47);
    }

    [TestMethod]
    public void UnexpectedMessageIsRejectedInEachClientState()
    {
        byte[] types = [1, 2, 4, 5, 8, 11, 13, 15, 20, 24, 25];
        foreach (byte type in types)
        {
            if (type == 2)
            {
                continue;
            }

            AssertAlert(FeedMessage(null, type), 10);
        }

        using var client = new TlsEngine(TlsRole.Client);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        using var server = ScriptedServer.Accept(output.Bytes.ToArray());
        client.Advance(server.ServerHelloRecord(), out _, output);
        Assert.AreEqual(TlsEngineState.WaitEncryptedExtensions, client.State);
        client.Advance(server.Encrypted(Finished(20)), out _, output);
        AssertAlert(output, 10);
        Assert.AreEqual(TlsEngineState.Closed, client.State);
    }

    [TestMethod]
    public void LaterClientStatesRejectEveryOtherHandshakeType()
    {
        byte[] types = [1, 2, 4, 5, 8, 11, 13, 15, 20, 24, 25];
        foreach (byte type in types)
        {
            if (type != 8)
            {
                Reject("hello", type, static (client, server, output) => client.Advance(server.ServerHelloRecord(), out _, output));
            }

            if (type is not 11 and not 13)
            {
                Reject("extensions", type, static (client, server, output) =>
                {
                    client.Advance(server.ServerHelloRecord(), out _, output);
                    client.Advance(server.EncryptedExtensionsMessage(), out _, output);
                });
            }

            if (type != 11)
            {
                Reject("request", type, static (client, server, output) =>
                {
                    client.Advance(server.ServerHelloRecord(), out _, output);
                    client.Advance(server.EncryptedExtensionsMessage(), out _, output);
                    client.Advance(server.EncryptedCertificateRequestMessage(), out _, output);
                });
            }

            if (type != 15)
            {
                Reject("verify", type, static (client, server, output) =>
                {
                    client.Advance(server.ServerHelloRecord(), out _, output);
                    client.Advance(server.EncryptedExtensionsMessage(), out _, output);
                    client.Advance(server.EncryptedCertificateMessage(), out _, output);
                    client.Resume(true, output);
                });
            }

            if (type != 20)
            {
                Reject("finished", type, static (client, server, output) =>
                {
                    client.Advance(server.ServerHelloRecord(), out _, output);
                    client.Advance(server.EncryptedExtensionsMessage(), out _, output);
                    client.Advance(server.EncryptedCertificateMessage(), out _, output);
                    client.Resume(true, output);
                    client.Advance(server.EncryptedCertificateVerifyMessage(), out _, output);
                });
            }

            if (type is not 4 and not 24)
            {
                Reject("connected", type, static (client, server, output) =>
                {
                    client.Advance(server.ServerHelloRecord(), out _, output);
                    client.Advance(server.EncryptedExtensionsMessage(), out _, output);
                    client.Advance(server.EncryptedCertificateMessage(), out _, output);
                    client.Resume(true, output);
                    client.Advance(server.EncryptedCertificateVerifyMessage(), out _, output);
                    client.Advance(server.EncryptedFinishedMessage(), out _, output);
                }, application: true);
            }
        }
    }

    [TestMethod]
    public void ExpiredTicketIsNotOffered()
    {
        var cache = new MemoryTlsSessionCache();
        cache.Store("", "tls", new TlsResumptionTicket([9, 9], 10, 1, [0], new byte[32], 0x1301, 100));
        using var client = new TlsEngine(TlsRole.Client, cache: cache) { UnixTimeSeconds = 1000 };
        var output = new TlsOutput();
        client.Advance([], out _, output);
        Assert.AreNotEqual((ushort)0x0029, ReadLastExtension(output.Bytes.ToArray()));
    }

    [TestMethod]
    public void ResumptionOffersAPreSharedKey()
    {
        var cache = new MemoryTlsSessionCache();
        var psk = new byte[32];
        psk.AsSpan().Fill(0x22);
        cache.Store("localhost", "tls", new TlsResumptionTicket([7, 7, 7], 60, 5, [0, 1], psk, 0x1301, 100));
        byte[] script = new byte[96];
        script.AsSpan().Fill(0x31);
        using var client = new TlsEngine(TlsRole.Client, entropy: new FixedEntropy(script), serverName: "localhost", cache: cache) { UnixTimeSeconds = 101 };
        var output = new TlsOutput();
        client.Advance([], out _, output);
        byte[] hello = output.Bytes.ToArray();
        Assert.IsTrue(hello.AsSpan().IndexOf(new byte[] { 0x00, 0x29 }) > 0);
        Assert.AreEqual(0x0029, ReadLastExtension(hello));
    }

    private static void Reject(string stage, byte type, Action<TlsEngine, ScriptedServer, TlsOutput> reach, bool application = false)
    {
        using var client = new TlsEngine(TlsRole.Client);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        using var server = ScriptedServer.Accept(output.Bytes.ToArray());
        reach(client, server, output);
        Assert.AreNotEqual(TlsEngineState.Closed, client.State, stage + " " + type + " " + (output.Bytes.Length < 7 ? output.Bytes.Length : output.Bytes[6]));
        byte[] message = [type, 0, 0, 0];
        client.Advance(application ? server.EncryptedApplication(message) : server.Encrypted(message), out _, output);
        Assert.AreEqual(10, output.Bytes.Length < 7 ? output.Bytes.Length : output.Bytes[6], stage + " " + type + " " + client.State);
        Assert.AreEqual(TlsEngineState.Closed, client.State);
    }

    private static byte[] Hello(byte[] script)
    {
        using var client = new TlsEngine(TlsRole.Client, entropy: new FixedEntropy(script));
        var output = new TlsOutput();
        client.Advance([], out _, output);
        return output.Bytes.ToArray();
    }

    private static byte[] Feed(Func<byte[], byte[], byte[]> hello, byte[]? random = null)
    {
        using var client = new TlsEngine(TlsRole.Client);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        byte[] session = SessionId(output.Bytes.ToArray());
        byte[] chosen = random ?? new byte[32];
        if (random is null)
        {
            chosen[0] = 1;
        }

        try
        {
            client.Advance(Record(hello(session, chosen)), out _, output);
        }
        catch (TlsVersionNotSupportedException)
        {
            Assert.AreEqual(TlsEngineState.Closed, client.State);
            throw;
        }

        return output.Bytes.ToArray();
    }

    private static byte[] FeedMessage(byte[]? prefix, byte type)
    {
        using var client = new TlsEngine(TlsRole.Client);
        var output = new TlsOutput();
        client.Advance([], out _, output);
        if (prefix is not null)
        {
            client.Advance(prefix, out _, output);
        }

        client.Advance(Record([type, 0, 0, 0]), out _, output);
        return output.Bytes.ToArray();
    }

    private static void AssertAlert(TlsOutput output, byte description)
    {
        Assert.AreEqual(7, output.Bytes.Length);
        Assert.AreEqual(21, output.Bytes[0]);
        Assert.AreEqual(description, output.Bytes[6]);
    }

    private static void AssertAlert(byte[] alert, byte description)
    {
        Assert.AreEqual(7, alert.Length);
        Assert.AreEqual(21, alert[0]);
        Assert.AreEqual(description, alert[6]);
    }

    private static byte[] Record(byte[] handshake)
    {
        var record = new byte[5 + handshake.Length];
        record[0] = 22;
        record[1] = 3;
        record[2] = 3;
        record[3] = (byte)(handshake.Length >> 8);
        record[4] = (byte)handshake.Length;
        handshake.CopyTo(record, 5);
        return record;
    }

    private static byte[] SessionId(byte[] record)
    {
        var id = new byte[32];
        record.AsSpan(44, 32).CopyTo(id);
        return id;
    }

    private static ushort KeyShareGroup(byte[] record)
    {
        ReadOnlySpan<byte> hello = record.AsSpan(5);
        int extensions = ExtensionStart(hello);
        TlsReader reader = new(hello[extensions..]);
        while (reader.TryReadUInt16(out ushort type) && reader.TryReadVector16(out ReadOnlySpan<byte> body))
        {
            if (type == ExtensionType.KeyShare)
            {
                return (ushort)((body[2] << 8) | body[3]);
            }
        }

        return 0;
    }

    private static ushort ReadLastExtension(byte[] record)
    {
        ReadOnlySpan<byte> hello = record.AsSpan(5);
        int extensions = ExtensionStart(hello);
        TlsReader reader = new(hello[extensions..]);
        ushort last = 0;
        while (reader.TryReadUInt16(out ushort type) && reader.TryReadVector16(out _))
        {
            last = type;
        }

        return last;
    }

    private static int ExtensionStart(ReadOnlySpan<byte> hello)
    {
        int offset = 4 + 2 + 32;
        offset += 1 + hello[offset];
        offset += 2 + ((hello[offset] << 8) | hello[offset + 1]);
        offset += 1 + hello[offset];
        offset += 2;
        return offset;
    }

    private static byte[] HelloRetry(byte[] session, ushort group, byte[]? cookie)
    {
        var random = new byte[32];
        Convert.FromHexString("CF21AD74E59A6111BE1D8C021E65B891C2A211167ABB8C5E079E09E2C8A8339C").CopyTo(random);
        var body = new System.Collections.Generic.List<byte>();
        body.Add(2);
        Add24(body, 0);
        body.Add(3);
        body.Add(3);
        body.AddRange(random);
        body.Add(32);
        body.AddRange(session);
        body.Add(0x13);
        body.Add(0x01);
        body.Add(0);
        var extensions = new System.Collections.Generic.List<byte>();
        AddExtension(extensions, 0x002B, [0x03, 0x04]);
        AddExtension(extensions, 0x0033, [(byte)(group >> 8), (byte)group]);
        if (cookie is not null)
        {
            AddExtension(extensions, 44, cookie);
        }

        body.Add((byte)(extensions.Count >> 8));
        body.Add((byte)extensions.Count);
        body.AddRange(extensions);
        Patch24(body, 1, body.Count - 4);
        return body.ToArray();
    }

    private static byte[] ServerHello(byte[] session, byte[] random, ushort cipher, bool includeVersion, bool includeShare)
    {
        var body = new System.Collections.Generic.List<byte> { 2, 0, 0, 0, 3, 3 };
        body.AddRange(random);
        body.Add(32);
        body.AddRange(session);
        body.Add((byte)(cipher >> 8));
        body.Add((byte)cipher);
        body.Add(0);
        var extensions = new System.Collections.Generic.List<byte>();
        if (includeVersion)
        {
            AddExtension(extensions, 0x002B, [0x03, 0x04]);
        }

        if (includeShare)
        {
            var share = new byte[36];
            share[1] = 0x1D;
            share[3] = 32;
            share.AsSpan(4).Fill(0x44);
            AddExtension(extensions, 0x0033, share);
        }

        body.Add((byte)(extensions.Count >> 8));
        body.Add((byte)extensions.Count);
        body.AddRange(extensions);
        Patch24(body, 1, body.Count - 4);
        return body.ToArray();
    }

    private static byte[] Finished(byte type)
    {
        return [type, 0, 0, 0];
    }

    private static byte[] Downgrade()
    {
        return [0x44, 0x4F, 0x57, 0x4E, 0x47, 0x52, 0x44, 0x01];
    }

    private static void AddExtension(System.Collections.Generic.List<byte> destination, ushort type, byte[] body)
    {
        destination.Add((byte)(type >> 8));
        destination.Add((byte)type);
        destination.Add((byte)(body.Length >> 8));
        destination.Add((byte)body.Length);
        destination.AddRange(body);
    }

    private static void Add24(System.Collections.Generic.List<byte> destination, int value)
    {
        destination.Add((byte)(value >> 16));
        destination.Add((byte)(value >> 8));
        destination.Add((byte)value);
    }

    private static void Patch24(System.Collections.Generic.List<byte> destination, int at, int value)
    {
        destination[at] = (byte)(value >> 16);
        destination[at + 1] = (byte)(value >> 8);
        destination[at + 2] = (byte)value;
    }
}

internal sealed class ScriptedServer : IDisposable
{
    private readonly TranscriptStore _transcript = new(HashAlgorithmName.SHA256);
    private readonly TlsKeySchedule _schedule = new(HashAlgorithmName.SHA256);
    private readonly ECDsa _key;
    private readonly X509Certificate2 _certificate;
    private readonly TlsRecordProtector _write;
    private readonly TlsRecordProtector _application;
    private readonly byte[] _clientHandshake = new byte[32];
    private readonly byte[] _serverHello;
    private readonly byte[] _payload;
    private readonly byte[] _verify;
    private readonly byte[] _finished;
    private readonly byte[] _clientHello;
    private readonly byte[] _serverHelloMessage;
    private readonly byte[] _serverHandshake;
    private bool _disposed;

    private ScriptedServer(
        ECDsa key,
        X509Certificate2 certificate,
        TlsRecordProtector write,
        TlsRecordProtector application,
        byte[] serverHello,
        byte[] payload,
        byte[] verify,
        byte[] finished,
        byte[] clientHello,
        byte[] serverHelloMessage,
        byte[] serverHandshake)
    {
        _key = key;
        _certificate = certificate;
        _write = write;
        _application = application;
        _serverHello = serverHello;
        _payload = payload;
        _verify = verify;
        _finished = finished;
        _clientHello = clientHello;
        _serverHelloMessage = serverHelloMessage;
        _serverHandshake = serverHandshake;
    }

    internal static ScriptedServer Accept(byte[] clientRecord)
    {
        byte[] handshake = clientRecord.AsSpan(5, ((clientRecord[3] << 8) | clientRecord[4])).ToArray();
        Assert.IsTrue(HandshakeMessages.TryReadClientHello(handshake, out ClientHelloView hello, out _));
        byte[] clientShare = KeyShare(hello.Extensions);
        var scalar = new byte[32];
        scalar.AsSpan().Fill(0x27);
        var serverShare = new byte[32];
        var secret = new byte[32];
        X25519.PublicKey(scalar, serverShare);
        X25519.ScalarMult(scalar, clientShare, secret);
        var transcript = new TranscriptStore(HashAlgorithmName.SHA256);
        transcript.Append(handshake);
        byte[] serverHello = ServerHelloMessage(hello.SessionId.ToArray(), serverShare);
        transcript.Append(serverHello);
        var schedule = new TlsKeySchedule(HashAlgorithmName.SHA256);
        schedule.InitializeEarly([]);
        schedule.AdvanceToHandshake(secret);
        Span<byte> hash = stackalloc byte[32];
        transcript.CurrentHash(hash);
        var serverHs = new byte[32];
        var clientHs = new byte[32];
        schedule.HandshakeTrafficSecret(false, hash, serverHs);
        schedule.HandshakeTrafficSecret(true, hash, clientHs);
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256)!;
        var request = new CertificateRequest("CN=localhost", ecdsa, HashAlgorithmName.SHA256);
        X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        byte[] ee = [8, 0, 0, 2, 0, 0];
        byte[] certificateMessage = CertificateMessage(certificate.RawData);
        transcript.Append(ee);
        transcript.Append(certificateMessage);
        transcript.CurrentHash(hash);
        byte[] verify = CertificateVerify(ecdsa, hash);
        transcript.Append(verify);
        transcript.CurrentHash(hash);
        Span<byte> finishedKey = stackalloc byte[32];
        Span<byte> verifyData = stackalloc byte[32];
        TlsKeySchedule.FinishedKey(HashAlgorithmName.SHA256, serverHs, finishedKey);
        HMACSHA256.HashData(finishedKey, hash, verifyData);
        var finished = new byte[36];
        finished[0] = 20;
        finished[3] = 32;
        verifyData.CopyTo(finished.AsSpan(4));
        transcript.Append(finished);
        var payload = new byte[ee.Length + certificateMessage.Length + verify.Length + finished.Length];
        int offset = 0;
        Copy(ee, payload, ref offset);
        Copy(certificateMessage, payload, ref offset);
        Copy(verify, payload, ref offset);
        Copy(finished, payload, ref offset);
        Span<byte> key = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[12];
        TlsKeySchedule.DeriveTrafficKeys(HashAlgorithmName.SHA256, serverHs, key, iv);
        var protector = new TlsRecordProtector(TlsAeadSuite.Aes128Gcm, key, iv);
        transcript.CurrentHash(hash);
        schedule.AdvanceToMaster();
        Span<byte> serverApp = stackalloc byte[32];
        schedule.ApplicationTrafficSecret(false, hash, serverApp);
        TlsKeySchedule.DeriveTrafficKeys(HashAlgorithmName.SHA256, serverApp, key, iv);
        var application = new TlsRecordProtector(TlsAeadSuite.Aes128Gcm, key, iv);
        var serverHelloRecord = new byte[5 + serverHello.Length];
        serverHelloRecord[0] = 22;
        serverHelloRecord[1] = 3;
        serverHelloRecord[2] = 3;
        serverHelloRecord[3] = (byte)(serverHello.Length >> 8);
        serverHelloRecord[4] = (byte)serverHello.Length;
        serverHello.CopyTo(serverHelloRecord, 5);
        var server = new ScriptedServer(ecdsa, certificate, protector, application, serverHelloRecord, payload, verify, finished, handshake, serverHello, serverHs);
        server._transcript.Dispose();
        transcript.CurrentHash(hash);
        server._transcript.Dispose();
        ReplaceTranscript(server, transcript);
        clientHs.CopyTo(server._clientHandshake);
        server._schedule.Dispose();
        ReplaceSchedule(server, schedule);
        return server;
    }

    internal byte[] ServerHelloRecord() => _serverHello;

    internal byte[] Flight()
    {
        byte[] encrypted = Encrypted(_payload);
        var flight = new byte[_serverHello.Length + 6 + encrypted.Length];
        _serverHello.CopyTo(flight, 0);
        int at = _serverHello.Length;
        flight[at] = 20;
        flight[at + 1] = 3;
        flight[at + 2] = 3;
        flight[at + 4] = 1;
        flight[at + 5] = 1;
        encrypted.CopyTo(flight, at + 6);
        return flight;
    }

    internal byte[] EncryptedExtensionsMessage() => Encrypted([8, 0, 0, 2, 0, 0]);

    internal byte[] EncryptedCertificateRequestMessage() => Encrypted(
    [
        13, 0, 0, 11,
        0,
        0, 8,
        0, 13, 0, 4, 0, 2, 4, 3,
    ]);

    internal byte[] EncryptedCertificateMessage() => Encrypted(CertificateMessage(_certificate.RawData));

    internal byte[] EncryptedCertificateVerifyMessage() => Encrypted(_verify);

    internal byte[] EncryptedFinishedMessage() => Encrypted(_finished);

    internal (byte[] Verify, byte[] Finished) CertificateRequestProof()
    {
        byte[] extensions = [8, 0, 0, 2, 0, 0];
        byte[] request =
        [
            13, 0, 0, 11,
            0,
            0, 8,
            0, 13, 0, 4, 0, 2, 4, 3,
        ];
        byte[] certificate = CertificateMessage(_certificate.RawData);
        using var transcript = new TranscriptStore(HashAlgorithmName.SHA256);
        transcript.Append(_clientHello);
        transcript.Append(_serverHelloMessage);
        transcript.Append(extensions);
        transcript.Append(request);
        transcript.Append(certificate);
        Span<byte> hash = stackalloc byte[32];
        transcript.CurrentHash(hash);
        byte[] verify = CertificateVerify(_key, hash);
        transcript.Append(verify);
        transcript.CurrentHash(hash);
        Span<byte> finishedKey = stackalloc byte[32];
        Span<byte> verifyData = stackalloc byte[32];
        TlsKeySchedule.FinishedKey(HashAlgorithmName.SHA256, _serverHandshake, finishedKey);
        HMACSHA256.HashData(finishedKey, hash, verifyData);
        var finished = new byte[36];
        finished[0] = 20;
        finished[3] = 32;
        verifyData.CopyTo(finished.AsSpan(4));
        return (verify, finished);
    }

    internal byte[][] ClientHandshakeMessages(byte[] records)
    {
        var messages = new System.Collections.Generic.List<byte[]>();
        int offset = records.Length > 0 && records[0] == 20 ? 6 : 0;
        Span<byte> key = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[12];
        TlsKeySchedule.DeriveTrafficKeys(HashAlgorithmName.SHA256, _clientHandshake, key, iv);
        using var read = new TlsRecordProtector(TlsAeadSuite.Aes128Gcm, key, iv);
        while (offset + 5 <= records.Length)
        {
            int length = 5 + ((records[offset + 3] << 8) | records[offset + 4]);
            var record = new byte[length];
            records.AsSpan(offset, length).CopyTo(record);
            byte content = read.Unprotect(record, out int contentLength);
            Assert.AreEqual(22, content);
            messages.Add(record.AsSpan(5, contentLength).ToArray());
            offset += length;
        }

        return messages.ToArray();
    }

    internal byte[] EncryptedApplication(byte[] handshake)
    {
        var encrypted = new byte[handshake.Length + 64];
        int written = _application.Protect(22, handshake, encrypted);
        return encrypted.AsSpan(0, written).ToArray();
    }

    internal byte[] Encrypted(byte[] handshake)
    {
        var encrypted = new byte[handshake.Length + 64];
        int written = _write.Protect(22, handshake, encrypted);
        return encrypted.AsSpan(0, written).ToArray();
    }

    internal void VerifyClientFinished(byte[] records)
    {
        int offset = records[0] == 20 ? 6 : 0;
        int length = 5 + ((records[offset + 3] << 8) | records[offset + 4]);
        var record = new byte[length];
        records.AsSpan(offset, length).CopyTo(record);
        Span<byte> key = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[12];
        TlsKeySchedule.DeriveTrafficKeys(HashAlgorithmName.SHA256, _clientHandshake, key, iv);
        using var read = new TlsRecordProtector(TlsAeadSuite.Aes128Gcm, key, iv);
        byte content = read.Unprotect(record, out int contentLength);
        Assert.AreEqual(22, content);
        ReadOnlySpan<byte> finished = record.AsSpan(5, contentLength);
        Span<byte> hash = stackalloc byte[32];
        _transcript.CurrentHash(hash);
        Span<byte> finishedKey = stackalloc byte[32];
        Span<byte> expected = stackalloc byte[32];
        TlsKeySchedule.FinishedKey(HashAlgorithmName.SHA256, _clientHandshake, finishedKey);
        HMACSHA256.HashData(finishedKey, hash, expected);
        Assert.IsTrue(finished.Length >= 36);
        Assert.IsTrue(CryptographicOperations.FixedTimeEquals(expected, finished.Slice(4, 32)));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _key.Dispose();
        _certificate.Dispose();
        _write.Dispose();
        _application.Dispose();
        _transcript.Dispose();
        _schedule.Dispose();
    }

    private static void ReplaceTranscript(ScriptedServer server, TranscriptStore transcript)
    {
        typeof(ScriptedServer).GetField("_transcript", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(server, transcript);
    }

    private static void ReplaceSchedule(ScriptedServer server, TlsKeySchedule schedule)
    {
        typeof(ScriptedServer).GetField("_schedule", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(server, schedule);
    }

    private static byte[] KeyShare(ReadOnlySpan<byte> extensions)
    {
        TlsReader reader = new(extensions);
        while (reader.TryReadUInt16(out ushort type) && reader.TryReadVector16(out ReadOnlySpan<byte> body))
        {
            if (type == ExtensionType.KeyShare)
            {
                return body.Slice(6, 32).ToArray();
            }
        }

        throw new InvalidOperationException("The ClientHello has no X25519 share.");
    }

    private static byte[] ServerHelloMessage(byte[] session, byte[] share)
    {
        var random = new byte[32];
        random[0] = 0x21;
        var keyShare = new byte[36];
        keyShare[1] = 0x1D;
        keyShare[3] = 32;
        share.CopyTo(keyShare.AsSpan(4));
        var extensions = new System.Collections.Generic.List<byte>();
        Add(extensions, 0x002B, [0x03, 0x04]);
        Add(extensions, 0x0033, keyShare);
        var body = new System.Collections.Generic.List<byte> { 2, 0, 0, 0, 3, 3 };
        body.AddRange(random);
        body.Add(32);
        body.AddRange(session);
        body.Add(0x13);
        body.Add(0x01);
        body.Add(0);
        body.Add((byte)(extensions.Count >> 8));
        body.Add((byte)extensions.Count);
        body.AddRange(extensions);
        body[1] = (byte)((body.Count - 4) >> 16);
        body[2] = (byte)((body.Count - 4) >> 8);
        body[3] = (byte)(body.Count - 4);
        return body.ToArray();
    }

    private static byte[] CertificateMessage(byte[] der)
    {
        var entry = new byte[3 + der.Length + 2];
        entry[0] = (byte)(der.Length >> 16);
        entry[1] = (byte)(der.Length >> 8);
        entry[2] = (byte)der.Length;
        der.CopyTo(entry, 3);
        var message = new byte[4 + 1 + 3 + entry.Length];
        message[0] = 11;
        int body = 1 + 3 + entry.Length;
        message[1] = (byte)(body >> 16);
        message[2] = (byte)(body >> 8);
        message[3] = (byte)body;
        message[5] = (byte)(entry.Length >> 16);
        message[6] = (byte)(entry.Length >> 8);
        message[7] = (byte)entry.Length;
        entry.CopyTo(message, 8);
        return message;
    }

    private static byte[] CertificateVerify(ECDsa key, ReadOnlySpan<byte> hash)
    {
        Span<byte> content = stackalloc byte[64 + 33 + 1 + 32];
        content[..64].Fill(0x20);
        "TLS 1.3, server CertificateVerify"u8.CopyTo(content[64..]);
        hash.CopyTo(content[(64 + 34)..]);
        byte[] signature = key.SignData(content, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var message = new byte[4 + 2 + 2 + signature.Length];
        message[0] = 15;
        int body = 2 + 2 + signature.Length;
        message[2] = (byte)(body >> 8);
        message[3] = (byte)body;
        message[4] = 0x04;
        message[5] = 0x03;
        message[6] = (byte)(signature.Length >> 8);
        message[7] = (byte)signature.Length;
        signature.CopyTo(message, 8);
        return message;
    }

    private static void Add(System.Collections.Generic.List<byte> destination, ushort type, byte[] body)
    {
        destination.Add((byte)(type >> 8));
        destination.Add((byte)type);
        destination.Add((byte)(body.Length >> 8));
        destination.Add((byte)body.Length);
        destination.AddRange(body);
    }

    private static void Copy(byte[] source, byte[] destination, ref int offset)
    {
        source.CopyTo(destination, offset);
        offset += source.Length;
    }
}
