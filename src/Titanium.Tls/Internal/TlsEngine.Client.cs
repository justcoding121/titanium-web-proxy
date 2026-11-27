using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Tls;

internal sealed partial class TlsEngine
{
    private const ushort CipherAes128GcmSha256 = 0x1301;
    private const byte AlertUnexpectedMessage = 10;
    private const byte AlertIllegalParameter = 47;
    private const byte AlertDecryptError = 51;
    private const byte AlertMissingExtension = 109;

    private static readonly byte[] EmptyCertificate = [11, 0, 0, 4, 0, 0, 0, 0];
    private const string ProfileName = "tls";

    private static ReadOnlySpan<byte> DowngradeTls12 => [0x44, 0x4F, 0x57, 0x4E, 0x47, 0x52, 0x44, 0x01];

    private static ReadOnlySpan<byte> DowngradeTls11 => [0x44, 0x4F, 0x57, 0x4E, 0x47, 0x52, 0x44, 0x00];

    private IPrivateKeyShare? _share;
    private readonly byte[] _clientRandom = new byte[32];
    private readonly byte[] _legacySessionId = new byte[32];
    private TlsRecordProtector? _read;
    private TlsRecordProtector? _write;
    private SecretBuffer? _clientHandshakeSecret;
    private SecretBuffer? _serverHandshakeSecret;
    private SecretBuffer? _resumptionMaster;
    private byte[]? _leaf;
    private byte[]? _cookie;
    private byte[]? _psk;
    private byte[]? _ticketIdentity;
    private uint _ticketAgeAdd;
    private uint _ticketLifetime;
    private long _ticketIssued;
    private ushort _shareGroup = (ushort)NamedGroup.X25519;
    private ushort _generatedGroup;
    private int _publicLength = 32;
    private ushort _selectedCipher;
    private bool _hrrSeen;
    private bool _certificateRequested;
    private bool _pskSelected;
    private bool _randomReady;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _share?.Dispose();
        _read?.Dispose();
        _write?.Dispose();
        _clientHandshakeSecret?.Dispose();
        _serverHandshakeSecret?.Dispose();
        _clientApplication?.Dispose();
        _serverApplication?.Dispose();
        _resumptionMaster?.Dispose();
        _transcript?.Dispose();
        _schedule?.Dispose();
        if (_psk is not null)
        {
            CryptographicOperations.ZeroMemory(_psk);
        }
    }

    private void WriteClientHello(TlsOutput output, bool helloRetry)
    {
        if (_schedule!.Stage == KeyScheduleStage.None)
        {
            LoadTicket();
            _schedule.InitializeEarly(_psk ?? ReadOnlySpan<byte>.Empty);
        }

        if (!_randomReady)
        {
            _entropy.Fill(_clientRandom);
            _entropy.Fill(_legacySessionId);
            _randomReady = true;
        }

        EnsureShare();
        byte[] hello = BuildClientHello();
        if (_psk is not null)
        {
            PatchBinder(hello);
        }

        _transcript!.Append(hello);
        WritePlaintextHandshake(output, hello);
    }

    private void HandleClient(HandshakeType type, int bodyLength, TlsOutput output)
    {
        if (type == HandshakeType.ServerHello)
        {
            OnServerHello(bodyLength, output);
            return;
        }

        if (type == HandshakeType.EncryptedExtensions)
        {
            TlsEngineState next = _pskSelected ? TlsEngineState.WaitFinished : TlsEngineState.WaitCertificateRequestOrCertificate;
            OnSimple(type, bodyLength, output, static message => HandshakeMessages.TryReadEncryptedExtensions(message, out _, out HandshakeAlert alert) ? default : alert, next);
            return;
        }

        if (type == HandshakeType.CertificateRequest)
        {
            _certificateRequested = true;
            OnSimple(type, bodyLength, output, static message => HandshakeMessages.TryReadCertificateRequest(message, out _, out _, out HandshakeAlert alert) ? default : alert, TlsEngineState.WaitCertificate);
            return;
        }

        if (type == HandshakeType.Certificate)
        {
            OnCertificate(bodyLength, output);
            return;
        }

        if (type == HandshakeType.CertificateVerify)
        {
            OnCertificateVerify(bodyLength, output);
            return;
        }

        if (type == HandshakeType.Finished)
        {
            OnFinished(bodyLength, output);
            return;
        }

        if (type == HandshakeType.NewSessionTicket)
        {
            OnTicket(bodyLength, output);
            return;
        }

        if (type == HandshakeType.KeyUpdate)
        {
            if (!HandshakeMessages.TryReadKeyUpdate(Message(type, bodyLength), out _, out HandshakeAlert alert))
            {
                Fail(output, (byte)alert);
                return;
            }

            output.AddEvent(TlsEventKind.KeyUpdateRequired);
        }
    }

    private void OnServerHello(int bodyLength, TlsOutput output)
    {
        byte[] message = Message(HandshakeType.ServerHello, bodyLength);
        if (!HandshakeMessages.TryReadServerHello(message, out ServerHelloView hello, out HandshakeAlert alert))
        {
            Fail(output, (byte)alert);
            return;
        }

        if (hello.LegacyVersion != 0x0303 || hello.CompressionMethod != 0)
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        if (!hello.SessionId.SequenceEqual(_legacySessionId))
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        if (hello.IsHelloRetryRequest)
        {
            OnHelloRetry(hello, message, output);
            return;
        }

        if (!IsTls13(hello, out bool sentinel))
        {
            if (sentinel)
            {
                Fail(output, AlertIllegalParameter);
                return;
            }

            State = TlsEngineState.Closed;
            throw new TlsVersionNotSupportedException();
        }

        if (hello.CipherSuite != CipherAes128GcmSha256 || (_hrrSeen && hello.CipherSuite != _selectedCipher))
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        if (!TrySharedSecret(hello.Extensions, out byte[]? secret))
        {
            Fail(output, secret is null ? AlertMissingExtension : AlertIllegalParameter);
            return;
        }

        if (!TrySelectedPreSharedKey(hello.Extensions, out bool selectedPreSharedKey, out byte pskAlert))
        {
            CryptographicOperations.ZeroMemory(secret);
            Fail(output, pskAlert);
            return;
        }

        _pskSelected = selectedPreSharedKey;
        if (!selectedPreSharedKey && _psk is not null)
        {
            _schedule!.AbandonPreSharedKey();
            CryptographicOperations.ZeroMemory(_psk);
            _psk = null;
        }

        _selectedCipher = hello.CipherSuite;
        _transcript!.Append(message);
        Span<byte> hash = stackalloc byte[32];
        _transcript.CurrentHash(hash);
        _schedule!.AdvanceToHandshake(secret);
        CryptographicOperations.ZeroMemory(secret);
        InstallHandshakeKeys(hash);
        State = TlsEngineState.WaitEncryptedExtensions;
    }

    private void InstallHandshakeKeys(ReadOnlySpan<byte> transcriptHash)
    {
        _clientHandshakeSecret?.Dispose();
        _serverHandshakeSecret?.Dispose();
        _clientHandshakeSecret = new SecretBuffer(32);
        _serverHandshakeSecret = new SecretBuffer(32);
        _schedule!.HandshakeTrafficSecret(true, transcriptHash, _clientHandshakeSecret.Span);
        _schedule.HandshakeTrafficSecret(false, transcriptHash, _serverHandshakeSecret.Span);
        Span<byte> key = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[12];
        TlsKeySchedule.DeriveTrafficKeys(HashAlgorithmName.SHA256, _serverHandshakeSecret.Span, key, iv);
        _read?.Dispose();
        _read = new TlsRecordProtector(TlsAeadSuite.Aes128Gcm, key, iv);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(iv);
    }

    private void OnHelloRetry(ServerHelloView hello, byte[] message, TlsOutput output)
    {
        if (_hrrSeen)
        {
            Fail(output, AlertUnexpectedMessage);
            return;
        }

        if (hello.CipherSuite != CipherAes128GcmSha256 || !IsTls13(hello, out _))
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        bool hasGroup = TryHelloRetryGroup(hello.Extensions, out ushort group);
        bool hasCookie = TryExtension(hello.Extensions, ExtensionType.Cookie, out ReadOnlySpan<byte> cookie);
        if (!hasGroup && !hasCookie)
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        if (hasGroup && group != (ushort)NamedGroup.X25519 && group != (ushort)NamedGroup.Secp256r1)
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        if (hasGroup && group == _shareGroup && !hasCookie)
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        _hrrSeen = true;
        _selectedCipher = hello.CipherSuite;
        if (hasGroup)
        {
            _shareGroup = group;
        }

        _cookie = hasCookie ? cookie.ToArray() : null;
        _transcript!.ReplaceWithMessageHash();
        _transcript.Append(message);
        WriteClientHello(output, helloRetry: true);
        State = TlsEngineState.WaitServerHelloOrHrr;
    }

    private void OnCertificate(int bodyLength, TlsOutput output)
    {
        byte[] message = Message(HandshakeType.Certificate, bodyLength);
        if (!HandshakeMessages.TryReadCertificate(message, out CertificateView certificate, out HandshakeAlert alert))
        {
            Fail(output, (byte)alert);
            return;
        }

        if (!TryCopyLeaf(certificate.CertificateList))
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        _transcript!.Append(message);
        State = TlsEngineState.WaitCertificateDecision;
        _paused = true;
        output.AddEvent(TlsEventKind.ServerCertificateReceived);
    }

    private void OnCertificateVerify(int bodyLength, TlsOutput output)
    {
        byte[] message = Message(HandshakeType.CertificateVerify, bodyLength);
        if (!HandshakeMessages.TryReadCertificateVerify(message, out ushort scheme, out ReadOnlySpan<byte> signature, out HandshakeAlert alert))
        {
            Fail(output, (byte)alert);
            return;
        }

        if (scheme != 0x0403 || _leaf is null || !VerifyServerSignature(signature))
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        _transcript!.Append(message);
        State = TlsEngineState.WaitFinished;
    }

    private void OnFinished(int bodyLength, TlsOutput output)
    {
        byte[] message = Message(HandshakeType.Finished, bodyLength);
        if (!HandshakeMessages.TryReadFinished(message, out ReadOnlySpan<byte> verifyData, out HandshakeAlert alert))
        {
            Fail(output, (byte)alert);
            return;
        }

        Span<byte> hash = stackalloc byte[32];
        Span<byte> expected = stackalloc byte[32];
        Span<byte> finishedKey = stackalloc byte[32];
        _transcript!.CurrentHash(hash);
        TlsKeySchedule.FinishedKey(HashAlgorithmName.SHA256, _serverHandshakeSecret!.Span, finishedKey);
        HMACSHA256.HashData(finishedKey, hash, expected);
        CryptographicOperations.ZeroMemory(finishedKey);
        if (!CryptographicOperations.FixedTimeEquals(expected, verifyData))
        {
            Fail(output, AlertDecryptError);
            return;
        }

        _transcript.Append(message);
        _transcript.CurrentHash(hash);
        _schedule!.AdvanceToMaster();
        Span<byte> clientApp = stackalloc byte[32];
        Span<byte> serverApp = stackalloc byte[32];
        Span<byte> exporter = stackalloc byte[32];
        _schedule.ApplicationTrafficSecret(true, hash, clientApp);
        _schedule.ApplicationTrafficSecret(false, hash, serverApp);
        _schedule.ExporterMaster(hash, exporter);
        if (_certificateRequested)
        {
            _transcript.Append(EmptyCertificate);
            _transcript.CurrentHash(hash);
        }

        TlsKeySchedule.FinishedKey(HashAlgorithmName.SHA256, _clientHandshakeSecret!.Span, finishedKey);
        HMACSHA256.HashData(finishedKey, hash, expected);
        CryptographicOperations.ZeroMemory(finishedKey);
        SendClientFinished(output, expected, clientApp, serverApp);
        CryptographicOperations.ZeroMemory(clientApp);
        CryptographicOperations.ZeroMemory(serverApp);
        CryptographicOperations.ZeroMemory(exporter);
        _transcript.CurrentHash(hash);
        _resumptionMaster = new SecretBuffer(32);
        _schedule.ResumptionMaster(hash, _resumptionMaster.Span);
        State = TlsEngineState.Connected;
        output.AddEvent(TlsEventKind.HandshakeCompleted);
    }

    private void OnTicket(int bodyLength, TlsOutput output)
    {
        byte[] message = Message(HandshakeType.NewSessionTicket, bodyLength);
        if (!HandshakeMessages.TryReadNewSessionTicket(message, out uint lifetime, out uint ageAdd, out ReadOnlySpan<byte> nonce, out ReadOnlySpan<byte> ticket, out _, out HandshakeAlert alert))
        {
            Fail(output, (byte)alert);
            return;
        }

        if (lifetime > 7 * 24 * 60 * 60)
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        output.AddEvent(TlsEventKind.TicketReceived);
        if (_cache is null || _resumptionMaster is null || lifetime == 0)
        {
            return;
        }

        var psk = new byte[32];
        TlsKeySchedule.ResumptionPsk(HashAlgorithmName.SHA256, _resumptionMaster.Span, nonce, psk);
        _cache.Store(_serverName ?? string.Empty, ProfileName, new TlsResumptionTicket(ticket.ToArray(), lifetime, ageAdd, nonce.ToArray(), psk, _selectedCipher, Now()));
    }

    private void OnSimple(HandshakeType type, int bodyLength, TlsOutput output, Func<byte[], HandshakeAlert?> validate, TlsEngineState next)
    {
        byte[] message = Message(type, bodyLength);
        HandshakeAlert? alert = validate(message);
        if (alert is HandshakeAlert value && value != default)
        {
            Fail(output, (byte)value);
            return;
        }

        _transcript!.Append(message);
        State = next;
    }

    private void SendClientFinished(TlsOutput output, ReadOnlySpan<byte> verifyData, ReadOnlySpan<byte> clientApp, ReadOnlySpan<byte> serverApp)
    {
        Span<byte> finished = stackalloc byte[4 + 32];
        finished[0] = (byte)HandshakeType.Finished;
        finished[3] = 32;
        verifyData.CopyTo(finished[4..]);
        _transcript!.Append(finished);
        Span<byte> key = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[12];
        TlsKeySchedule.DeriveTrafficKeys(HashAlgorithmName.SHA256, _clientHandshakeSecret!.Span, key, iv);
        _write?.Dispose();
        _write = new TlsRecordProtector(TlsAeadSuite.Aes128Gcm, key, iv);
        var encrypted = new byte[128];
        output.AddBytes([20, 3, 3, 0, 1, 1]);
        if (_certificateRequested)
        {
            int certificateWritten = _write.Protect(22, EmptyCertificate, encrypted);
            output.AddBytes(encrypted.AsSpan(0, certificateWritten));
        }

        int written = _write.Protect(22, finished, encrypted);
        output.AddBytes(encrypted.AsSpan(0, written));
        _write.Dispose();
        _read?.Dispose();
        TlsKeySchedule.DeriveTrafficKeys(HashAlgorithmName.SHA256, serverApp, key, iv);
        _read = new TlsRecordProtector(TlsAeadSuite.Aes128Gcm, key, iv);
        TlsKeySchedule.DeriveTrafficKeys(HashAlgorithmName.SHA256, clientApp, key, iv);
        _write = new TlsRecordProtector(TlsAeadSuite.Aes128Gcm, key, iv);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(iv);
    }

    private byte[] BuildClientHello()
    {
        Span<byte> share = stackalloc byte[128];
        _share!.WritePublicKey(share[.._publicLength]);
        var buffer = new byte[1024];
        TlsWriter writer = TlsWriter.Over(buffer);
        if (!writer.TryWriteUInt8((byte)HandshakeType.ClientHello) || !writer.TryReserveLength(3, out int lengthAt))
        {
            throw new TlsProtocolViolation("The ClientHello buffer is too small.");
        }

        writer.TryWriteUInt16(0x0303);
        writer.TryWrite(_clientRandom);
        writer.TryWriteVector8(_legacySessionId);
        writer.TryWriteVector16([0x13, 0x01]);
        writer.TryWriteVector8([0]);
        int extensionsAt = writer.Written;
        writer.TryReserveLength(2, out int extensionsLengthAt);
        WriteExtension(ref writer, ExtensionType.SupportedGroups, [0x00, 0x04, 0x00, 0x1D, 0x00, 0x17]);
        WriteExtension(ref writer, ExtensionType.SignatureAlgorithms, [0x00, 0x02, 0x04, 0x03]);
        WriteExtension(ref writer, ExtensionType.SupportedVersions, [0x02, 0x03, 0x04]);
        byte[] shareBytes = share[.._publicLength].ToArray();
        WriteKeyShare(ref writer, shareBytes);
        if (_cookie is not null)
        {
            WriteExtension(ref writer, ExtensionType.Cookie, _cookie);
        }

        if (_serverName is not null)
        {
            WriteServerName(ref writer, _serverName);
        }

        if (_psk is not null)
        {
            WriteExtension(ref writer, ExtensionType.PskKeyExchangeModes, [0x01, 0x01]);
            WritePreSharedKey(ref writer);
        }

        writer.TryPatchLength(extensionsLengthAt, 2);
        _ = extensionsAt;
        writer.TryPatchLength(lengthAt, 3);
        var hello = new byte[writer.Written];
        buffer.AsSpan(0, writer.Written).CopyTo(hello);
        return hello;
    }

    private void WriteKeyShare(ref TlsWriter writer, ReadOnlySpan<byte> share)
    {
        var body = new byte[2 + 2 + 2 + share.Length];
        body[0] = (byte)((2 + 2 + share.Length) >> 8);
        body[1] = (byte)(2 + 2 + share.Length);
        body[2] = (byte)(_shareGroup >> 8);
        body[3] = (byte)_shareGroup;
        body[4] = (byte)(share.Length >> 8);
        body[5] = (byte)share.Length;
        share.CopyTo(body.AsSpan(6));
        WriteExtension(ref writer, ExtensionType.KeyShare, body);
    }

    private void WritePreSharedKey(ref TlsWriter writer)
    {
        uint age = ObfuscatedAge();
        int identities = 2 + _ticketIdentity!.Length + 4;
        int binders = 1 + 32;
        var body = new byte[2 + identities + 2 + binders];
        int offset = 0;
        body[offset++] = (byte)(identities >> 8);
        body[offset++] = (byte)identities;
        body[offset++] = (byte)(_ticketIdentity.Length >> 8);
        body[offset++] = (byte)_ticketIdentity.Length;
        _ticketIdentity.CopyTo(body.AsSpan(offset));
        offset += _ticketIdentity.Length;
        body[offset++] = (byte)(age >> 24);
        body[offset++] = (byte)(age >> 16);
        body[offset++] = (byte)(age >> 8);
        body[offset++] = (byte)age;
        body[offset++] = 0;
        body[offset++] = (byte)(1 + 32);
        body[offset++] = 32;
        WriteExtension(ref writer, ExtensionType.PreSharedKey, body);
    }

    private void PatchBinder(byte[] hello)
    {
        const int binderTail = 2 + 1 + 32;
        Span<byte> hash = stackalloc byte[32];
        _transcript!.HashWith(hello.AsSpan(0, hello.Length - binderTail), hash);
        Span<byte> empty = stackalloc byte[32];
        SHA256.HashData(ReadOnlySpan<byte>.Empty, empty);
        Span<byte> binderKey = stackalloc byte[32];
        Span<byte> finishedKey = stackalloc byte[32];
        _schedule!.DeriveEarly("res binder"u8, empty, binderKey);
        TlsKeySchedule.FinishedKey(HashAlgorithmName.SHA256, binderKey, finishedKey);
        HMACSHA256.HashData(finishedKey, hash, hello.AsSpan(hello.Length - 32));
        CryptographicOperations.ZeroMemory(binderKey);
        CryptographicOperations.ZeroMemory(finishedKey);
    }

    private void EnsureShare()
    {
        if (_share is not null && _generatedGroup == _shareGroup)
        {
            return;
        }

        _share?.Dispose();
        IKeyExchangeGroup group = KeyExchangeGroups.Create((NamedGroup)_shareGroup);
        _publicLength = group.PublicKeyLength;
        _share = group.GenerateKeyShare(_entropy);
        _generatedGroup = _shareGroup;
    }

    private void LoadTicket()
    {
        if (_cache is null || !_cache.TryGet(_serverName ?? string.Empty, ProfileName, Now(), out TlsResumptionTicket ticket))
        {
            return;
        }

        if (ticket.CipherSuite != CipherAes128GcmSha256)
        {
            return;
        }

        _psk = (byte[])ticket.Psk.Clone();
        _ticketIdentity = (byte[])ticket.Ticket.Clone();
        _ticketAgeAdd = ticket.AgeAdd;
        _ticketLifetime = ticket.LifetimeSeconds;
        _ticketIssued = ticket.IssuedUnixSeconds;
    }

    private bool TrySelectedPreSharedKey(ReadOnlySpan<byte> extensions, out bool selected, out byte alert)
    {
        selected = false;
        alert = 0;
        if (!TryExtension(extensions, ExtensionType.PreSharedKey, out ReadOnlySpan<byte> body))
        {
            return true;
        }

        if (_psk is null || body.Length != 2 || ((body[0] << 8) | body[1]) != 0)
        {
            alert = AlertIllegalParameter;
            return false;
        }

        selected = true;
        return true;
    }

    private bool TrySharedSecret(ReadOnlySpan<byte> extensions, out byte[]? secret)
    {
        secret = null;
        if (!TryExtension(extensions, ExtensionType.KeyShare, out ReadOnlySpan<byte> body))
        {
            return false;
        }

        TlsReader reader = new(body);
        if (!reader.TryReadUInt16(out ushort group) || !reader.TryReadVector16(out ReadOnlySpan<byte> key) || reader.Remaining != 0)
        {
            secret = [];
            return false;
        }

        if (group != _shareGroup)
        {
            secret = [];
            return false;
        }

        secret = new byte[32];
        if (!_share!.TryComputeSharedSecret(key, secret, out int written) || written != secret.Length)
        {
            CryptographicOperations.ZeroMemory(secret);
            secret = [];
            return false;
        }

        return true;
    }

    private bool VerifyServerSignature(ReadOnlySpan<byte> signature)
    {
        using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(_leaf!);
        using ECDsa? key = certificate.GetECDsaPublicKey();
        if (key is null)
        {
            return false;
        }

        Span<byte> hash = stackalloc byte[32];
        _transcript!.CurrentHash(hash);
        Span<byte> content = stackalloc byte[64 + 33 + 1 + 32];
        content[..64].Fill(0x20);
        "TLS 1.3, server CertificateVerify"u8.CopyTo(content[64..]);
        hash.CopyTo(content[(64 + 33 + 1)..]);
        return key.VerifyData(content, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    private bool TryCopyLeaf(ReadOnlySpan<byte> list)
    {
        TlsReader reader = new(list);
        if (!reader.TryReadVector24(out ReadOnlySpan<byte> certificate) || certificate.IsEmpty)
        {
            return false;
        }

        _leaf = certificate.ToArray();
        return true;
    }

    private static bool IsTls13(ServerHelloView hello, out bool sentinel)
    {
        sentinel = hello.Random[^8..].SequenceEqual(DowngradeTls12) || hello.Random[^8..].SequenceEqual(DowngradeTls11);
        if (!TryExtension(hello.Extensions, ExtensionType.SupportedVersions, out ReadOnlySpan<byte> version))
        {
            return false;
        }

        return version.Length == 2 && version[0] == 3 && version[1] == 4;
    }

    private static bool TryHelloRetryGroup(ReadOnlySpan<byte> extensions, out ushort group)
    {
        group = 0;
        if (!TryExtension(extensions, ExtensionType.KeyShare, out ReadOnlySpan<byte> body) || body.Length != 2)
        {
            return false;
        }

        group = (ushort)((body[0] << 8) | body[1]);
        return true;
    }

    private static bool TryExtension(ReadOnlySpan<byte> extensions, ushort wanted, out ReadOnlySpan<byte> body)
    {
        body = default;
        TlsReader reader = new(extensions);
        while (reader.Remaining > 0)
        {
            if (!reader.TryReadUInt16(out ushort type) || !reader.TryReadVector16(out ReadOnlySpan<byte> value))
            {
                return false;
            }

            if (type == wanted)
            {
                body = value;
                return true;
            }
        }

        return false;
    }

    private byte[] Message(HandshakeType type, int bodyLength)
    {
        var message = new byte[4 + bodyLength];
        message[0] = (byte)type;
        message[1] = (byte)(bodyLength >> 16);
        message[2] = (byte)(bodyLength >> 8);
        message[3] = (byte)bodyLength;
        _body.AsSpan(0, bodyLength).CopyTo(message.AsSpan(4));
        return message;
    }

    private static void WritePlaintextHandshake(TlsOutput output, ReadOnlySpan<byte> handshake)
    {
        var record = new byte[5 + handshake.Length];
        record[0] = 22;
        record[1] = 3;
        record[2] = 1;
        record[3] = (byte)(handshake.Length >> 8);
        record[4] = (byte)handshake.Length;
        handshake.CopyTo(record.AsSpan(5));
        output.AddBytes(record);
    }

    private static void WriteExtension(ref TlsWriter writer, ushort type, ReadOnlySpan<byte> body)
    {
        writer.TryWriteUInt16(type);
        writer.TryWriteVector16(body);
    }

    private static void WriteServerName(ref TlsWriter writer, string name)
    {
        byte[] ascii = System.Text.Encoding.ASCII.GetBytes(name);
        var body = new byte[2 + 1 + 2 + ascii.Length];
        int list = 1 + 2 + ascii.Length;
        body[0] = (byte)(list >> 8);
        body[1] = (byte)list;
        body[3] = (byte)(ascii.Length >> 8);
        body[4] = (byte)ascii.Length;
        ascii.CopyTo(body.AsSpan(5));
        WriteExtension(ref writer, ExtensionType.ServerName, body);
    }

    private uint ObfuscatedAge()
    {
        long milliseconds = Math.Max(0, Now() - _ticketIssued) * 1000L;
        return (uint)(milliseconds + _ticketAgeAdd);
    }

    private long Now()
    {
        return UnixTimeSeconds > 0 ? UnixTimeSeconds : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}
