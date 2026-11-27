using System;
using System.Net.Security;
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

    private readonly ushort[] _offered = new ushort[16];
    private int _offeredCount;
    private bool _certificateAllowsP256;
    private IPrivateKeyShare? _share;
    private readonly byte[] _clientRandom = new byte[32];
    private readonly byte[] _legacySessionId = new byte[32];
    private TlsRecordProtector? _read;
    private TlsRecordProtector? _write;
    private SecretBuffer? _clientHandshakeSecret;
    private SecretBuffer? _serverHandshakeSecret;
    private SecretBuffer? _resumptionMaster;
    private byte[]? _leaf;

    internal X509Certificate2Collection? TrustAnchors { get; set; }

    internal X509Certificate2? LocalCertificate { get; set; }

    internal string[]? ApplicationProtocols { get; set; }

    internal bool OfferStatusRequest { get; set; }

    internal SslPolicyErrors ServerCertificateErrors { get; private set; }

    internal byte[]? StapledOcspResponse { get; private set; }

    internal byte[]? SignedCertificateTimestamps { get; private set; }
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
        _exporter?.Dispose();
        _serverCheck?.Dispose();
        _remoteCertificate?.Dispose();
        if (_plaintext is not null)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(_plaintext, clearArray: true);
            _plaintext = null;
        }

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
            // Stryker disable once statement: the following handshake type does not match
            return;
        }

        if (type == HandshakeType.EncryptedExtensions)
        {
            OnEncryptedExtensions(bodyLength, output);
            // Stryker disable once statement: the following handshake type does not match
            return;
        }

        if (type == HandshakeType.CertificateRequest)
        {
            OnCertificateRequest(bodyLength, output);
            // Stryker disable once statement: the following handshake type does not match
            return;
        }

        if (type == HandshakeType.Certificate)
        {
            OnCertificate(bodyLength, output);
            // Stryker disable once statement: the following handshake type does not match
            return;
        }

        if (type == HandshakeType.CertificateVerify)
        {
            OnCertificateVerify(bodyLength, output);
            // Stryker disable once statement: the following handshake type does not match
            return;
        }

        if (type == HandshakeType.Finished)
        {
            OnFinished(bodyLength, output);
            // Stryker disable once statement: the following handshake type does not match
            return;
        }

        if (type == HandshakeType.NewSessionTicket)
        {
            OnTicket(bodyLength, output);
            // Stryker disable once statement: the following handshake type does not match
            return;
        }

        if (type == HandshakeType.KeyUpdate)
        {
            OnKeyUpdate(bodyLength, output);
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

        if (!ResponseExtensionsAreOffered(hello.Extensions, hello.IsHelloRetryRequest, out byte extensionAlert))
        {
            Fail(output, extensionAlert);
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

        if (!TryServerVersion(hello, out byte versionAlert, out bool legacyServer))
        {
            if (legacyServer)
            {
                State = TlsEngineState.Closed;
                throw new TlsVersionNotSupportedException();
            }

            Fail(output, versionAlert);
            return;
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
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(secret);
        InstallHandshakeKeys(hash);
        State = TlsEngineState.WaitEncryptedExtensions;
        _endOfRecord = true;
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
        TlsKeySchedule.DeriveTrafficKeys(HashAlgorithmName.SHA256, _clientHandshakeSecret.Span, key, iv);
        _write?.Dispose();
        _write = new TlsRecordProtector(TlsAeadSuite.Aes128Gcm, key, iv);
        _alertsEncrypted = true;
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(key);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(iv);
    }

    private void OnHelloRetry(ServerHelloView hello, byte[] message, TlsOutput output)
    {
        if (_hrrSeen)
        {
            Fail(output, AlertUnexpectedMessage);
            return;
        }

        bool tls13 = TryServerVersion(hello, out byte versionAlert, out _);
        if (hello.CipherSuite != CipherAes128GcmSha256 || !tls13)
        {
            Fail(output, versionAlert == 0 ? AlertIllegalParameter : versionAlert);
            return;
        }

        bool hasGroup = TryHelloRetryGroup(hello.Extensions, out ushort group);
        bool hasCookie = TryExtension(hello.Extensions, ExtensionType.Cookie, out ReadOnlySpan<byte> cookie);
        if (!hasGroup && !hasCookie)
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        if (hasGroup && !ClientOfferedGroup(group))
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        if (hasGroup && group == _shareGroup)
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

        if (!certificate.RequestContext.IsEmpty)
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        if (certificate.CertificateList.IsEmpty)
        {
            Fail(output, (byte)HandshakeAlert.DecodeError);
            return;
        }

        if (!CertificateEntriesAreOffered(certificate.CertificateList, out byte entryAlert))
        {
            Fail(output, entryAlert);
            return;
        }

        if (!TryCopyLeaf(certificate.CertificateList))
        {
            Fail(output, (byte)HandshakeAlert.DecodeError);
            return;
        }

        _transcript!.Append(message);
        _serverCheck?.Dispose();
        _serverCheck = TlsChainValidator.Evaluate(certificate.CertificateList, _serverName, TrustAnchors, serverCertificate: true);
        ServerCertificateErrors = _serverCheck.Errors;
        StapledOcspResponse = _serverCheck.StapledStatus;
        SignedCertificateTimestamps = _serverCheck.SignedCertificateTimestamps;

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

        if (_leaf is null || !ClientAcceptsScheme(scheme))
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        if (!VerifyServerSignature(scheme, signature))
        {
            Fail(output, AlertDecryptError);
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
        // Stryker disable once statement: the temporary is not read after the bytes are copied
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
        RememberTrafficSecrets(clientApp, serverApp, exporter);
        ReadOnlySpan<byte> clientCertificate = default;
        ReadOnlySpan<byte> clientVerify = default;
        byte[]? certificateMessage = null;
        byte[]? verifyMessage = null;
        if (_certificateRequested)
        {
            if (LocalCertificate is not null && _certificateAllowsP256 && CanSignP256(LocalCertificate))
            {
                certificateMessage = CertificateMessage(LocalCertificate.RawData);
                _transcript.Append(certificateMessage);
                _transcript.CurrentHash(hash);
                verifyMessage = SignClientCertificate(hash);
                _transcript.Append(verifyMessage);
                clientCertificate = certificateMessage;
                clientVerify = verifyMessage;
                _sentClientCertificate = true;
            }
            else
            {
                _transcript.Append(EmptyCertificate);
                clientCertificate = EmptyCertificate;
            }

            _transcript.CurrentHash(hash);
        }

        TlsKeySchedule.FinishedKey(HashAlgorithmName.SHA256, _clientHandshakeSecret!.Span, finishedKey);
        HMACSHA256.HashData(finishedKey, hash, expected);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(finishedKey);
        SendClientFinished(output, expected, clientApp, serverApp, clientCertificate, clientVerify);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(clientApp);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(serverApp);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(exporter);
        _transcript.CurrentHash(hash);
        _resumptionMaster = new SecretBuffer(32);
        _schedule.ResumptionMaster(hash, _resumptionMaster.Span);
        State = TlsEngineState.Connected;
        _endOfRecord = true;
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

    private byte[] SignClientCertificate(ReadOnlySpan<byte> hash)
    {
        ECDsa key = CertificateKeys.Ecdsa(LocalCertificate!)!;
        Span<byte> content = stackalloc byte[64 + 33 + 1 + 32];
        content[..64].Fill(0x20);
        "TLS 1.3, client CertificateVerify"u8.CopyTo(content[64..]);
        content[64 + 33] = 0;
        hash.CopyTo(content[(64 + 33 + 1)..]);
        byte[] signature = key.SignData(content, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return CertificateVerifyMessage(0x0403, signature);
    }

    private static bool CanSignP256(X509Certificate2 certificate)
    {
        ECDsa? key = CertificateKeys.Ecdsa(certificate);
        return key is not null && key.ExportParameters(false).Curve.Oid.Value == "1.2.840.10045.3.1.7";
    }

    private void SendClientFinished(TlsOutput output, ReadOnlySpan<byte> verifyData, ReadOnlySpan<byte> clientApp, ReadOnlySpan<byte> serverApp, ReadOnlySpan<byte> clientCertificate, ReadOnlySpan<byte> clientVerify)
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
        output.AddBytes([20, 3, 3, 0, 1, 1]);
        if (!clientCertificate.IsEmpty)
        {
            ProtectHandshakeRecord(output, clientCertificate);
        }

        if (!clientVerify.IsEmpty)
        {
            ProtectHandshakeRecord(output, clientVerify);
        }

        ProtectHandshakeRecord(output, finished);
        _write.Dispose();
        _read?.Dispose();
        TlsKeySchedule.DeriveTrafficKeys(HashAlgorithmName.SHA256, serverApp, key, iv);
        _read = new TlsRecordProtector(TlsAeadSuite.Aes128Gcm, key, iv);
        TlsKeySchedule.DeriveTrafficKeys(HashAlgorithmName.SHA256, clientApp, key, iv);
        _write = new TlsRecordProtector(TlsAeadSuite.Aes128Gcm, key, iv);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(key);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(iv);
    }

    private void ProtectHandshakeRecord(TlsOutput output, ReadOnlySpan<byte> plaintext)
    {
        var encrypted = new byte[plaintext.Length + 64];
        int written = _write!.Protect(22, plaintext, encrypted);
        output.AddBytes(encrypted.AsSpan(0, written));
    }

    private byte[] BuildClientHello()
    {
        _offeredCount = 0;
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
        ReadOnlySpan<byte> groups = _algorithmPolicy == TlsAlgorithmPolicy.FipsApprovedOnly
            ? [0x00, 0x04, 0x00, 0x17, 0x00, 0x18]
            : [0x00, 0x04, 0x00, 0x1D, 0x00, 0x17];
        ReadOnlySpan<byte> signatures = _algorithmPolicy == TlsAlgorithmPolicy.FipsApprovedOnly
            ? [0x00, 0x0A, 0x04, 0x03, 0x05, 0x03, 0x08, 0x04, 0x08, 0x05, 0x08, 0x06]
            : [0x00, 0x02, 0x04, 0x03];
        WriteOffered(ref writer, ExtensionType.SupportedGroups, groups);
        WriteOffered(ref writer, ExtensionType.SignatureAlgorithms, signatures);
        WriteOffered(ref writer, ExtensionType.SupportedVersions, [0x02, 0x03, 0x04]);
        byte[] shareBytes = share[.._publicLength].ToArray();
        WriteKeyShare(ref writer, shareBytes);
        if (_cookie is not null)
        {
            WriteOffered(ref writer, ExtensionType.Cookie, _cookie);
        }

        if (_serverName is not null)
        {
            WriteServerName(ref writer, _serverName);
        }

        if (ApplicationProtocols is { Length: > 0 })
        {
            WriteAlpn(ref writer, ApplicationProtocols);
        }

        if (OfferStatusRequest)
        {
            WriteOffered(ref writer, ExtensionType.StatusRequest, [1, 0, 0, 0, 0]);
            WriteOffered(ref writer, ExtensionType.SignedCertificateTimestamp, []);
        }

        if (_psk is not null)
        {
            WriteOffered(ref writer, ExtensionType.PskKeyExchangeModes, [0x01, 0x01]);
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
        WriteOffered(ref writer, ExtensionType.KeyShare, body);
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
        WriteOffered(ref writer, ExtensionType.PreSharedKey, body);
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
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(binderKey);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
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

        secret = new byte[KeyExchangeGroups.Create((NamedGroup)_shareGroup).SharedSecretLength];
        if (!_share!.TryComputeSharedSecret(key, secret, out int written) || written != secret.Length)
        {
            CryptographicOperations.ZeroMemory(secret);
            secret = [];
            return false;
        }

        return true;
    }

    private bool VerifyServerSignature(ushort scheme, ReadOnlySpan<byte> signature)
    {
        using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(_leaf!);
        Span<byte> hash = stackalloc byte[32];
        _transcript!.CurrentHash(hash);
        Span<byte> content = stackalloc byte[64 + 33 + 1 + 32];
        content[..64].Fill(0x20);
        "TLS 1.3, server CertificateVerify"u8.CopyTo(content[64..]);
        hash.CopyTo(content[(64 + 33 + 1)..]);
        return TlsCertificateVerifier.Verify(certificate, scheme, content, signature);
    }

    private bool ClientOfferedGroup(ushort group)
    {
        if (_algorithmPolicy == TlsAlgorithmPolicy.FipsApprovedOnly)
        {
            return group is (ushort)NamedGroup.Secp256r1 or (ushort)NamedGroup.Secp384r1;
        }

        return group is (ushort)NamedGroup.X25519 or (ushort)NamedGroup.Secp256r1;
    }

    private bool ClientAcceptsScheme(ushort scheme)
    {
        if (_algorithmPolicy == TlsAlgorithmPolicy.FipsApprovedOnly)
        {
            return TlsAlgorithmSet.AllowsSignature(_algorithmPolicy, scheme);
        }

        return scheme == 0x0403;
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

    private static bool TryServerVersion(ServerHelloView hello, out byte alert, out bool legacyServer)
    {
        alert = 0;
        legacyServer = false;
        bool sentinel = hello.Random[^8..].SequenceEqual(DowngradeTls12) || hello.Random[^8..].SequenceEqual(DowngradeTls11);
        if (!TryExtension(hello.Extensions, ExtensionType.SupportedVersions, out ReadOnlySpan<byte> version))
        {
            if (sentinel)
            {
                alert = AlertIllegalParameter;
                return false;
            }

            legacyServer = true;
            return false;
        }

        if (version.Length != 2)
        {
            alert = (byte)HandshakeAlert.DecodeError;
            return false;
        }

        if (version[0] != 3 || version[1] != 4 || sentinel)
        {
            alert = AlertIllegalParameter;
            return false;
        }

        return true;
    }

    private void OnEncryptedExtensions(int bodyLength, TlsOutput output)
    {
        byte[] message = Message(HandshakeType.EncryptedExtensions, bodyLength);
        if (!HandshakeMessages.TryReadEncryptedExtensions(message, out ReadOnlySpan<byte> extensions, out HandshakeAlert alert))
        {
            Fail(output, (byte)alert);
            return;
        }

        if (!ResponseExtensionsAreOffered(extensions, false, out byte extensionAlert))
        {
            Fail(output, extensionAlert);
            return;
        }

        RememberSelectedProtocol(extensions);
        _transcript!.Append(message);
        State = _pskSelected ? TlsEngineState.WaitFinished : TlsEngineState.WaitCertificateRequestOrCertificate;
    }

    private void OnCertificateRequest(int bodyLength, TlsOutput output)
    {
        byte[] message = Message(HandshakeType.CertificateRequest, bodyLength);
        if (!HandshakeMessages.TryReadCertificateRequest(message, out ReadOnlySpan<byte> context, out ReadOnlySpan<byte> extensions, out HandshakeAlert alert))
        {
            Fail(output, (byte)alert);
            return;
        }

        if (!context.IsEmpty)
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        if (!TryExtension(extensions, ExtensionType.SignatureAlgorithms, out ReadOnlySpan<byte> schemes))
        {
            Fail(output, AlertMissingExtension);
            return;
        }

        if (!CertificateRequestExtensionsAreAcceptable(extensions, out byte extensionAlert))
        {
            Fail(output, extensionAlert);
            return;
        }

        if (!TryListedScheme(schemes, 0x0403, out bool listed))
        {
            Fail(output, (byte)HandshakeAlert.DecodeError);
            return;
        }

        _certificateRequested = true;
        _certificateAllowsP256 = listed;
        _transcript!.Append(message);
        State = TlsEngineState.WaitCertificate;
    }

    private bool CertificateRequestExtensionsAreAcceptable(ReadOnlySpan<byte> extensions, out byte alert)
    {
        alert = 0;
        TlsReader reader = new(extensions);
        while (reader.Remaining > 0)
        {
            if (!reader.TryReadUInt16(out ushort type) || !reader.TryReadVector16(out _))
            {
                alert = (byte)HandshakeAlert.DecodeError;
                return false;
            }

            // These are the server's request, not a response to the ClientHello.
            if (type is ExtensionType.SignatureAlgorithms
                or ExtensionType.SignatureAlgorithmsCert
                or ExtensionType.CertificateAuthorities
                or ExtensionType.OidFilters)
            {
                continue;
            }

            if (!Offered(type))
            {
                alert = (byte)HandshakeAlert.UnsupportedExtension;
                return false;
            }
        }

        return true;
    }

    private bool ResponseExtensionsAreOffered(ReadOnlySpan<byte> extensions, bool allowCookie, out byte alert)
    {
        alert = 0;
        TlsReader reader = new(extensions);
        while (reader.Remaining > 0)
        {
            if (!reader.TryReadUInt16(out ushort type) || !reader.TryReadVector16(out _))
            {
                alert = (byte)HandshakeAlert.DecodeError;
                return false;
            }

            if (allowCookie && type == ExtensionType.Cookie)
            {
                continue;
            }

            if (!Offered(type))
            {
                alert = (byte)HandshakeAlert.UnsupportedExtension;
                return false;
            }
        }

        return true;
    }

    private bool CertificateEntriesAreOffered(ReadOnlySpan<byte> list, out byte alert)
    {
        alert = 0;
        TlsReader reader = new(list);
        while (reader.Remaining > 0)
        {
            if (!reader.TryReadVector24(out _) || !reader.TryReadVector16(out ReadOnlySpan<byte> extensions))
            {
                alert = (byte)HandshakeAlert.DecodeError;
                return false;
            }

            if (!ResponseExtensionsAreOffered(extensions, false, out alert))
            {
                return false;
            }
        }

        return true;
    }

    private bool Offered(ushort type)
    {
        for (int i = 0; i < _offeredCount; i++)
        {
            if (_offered[i] == type)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryListedScheme(ReadOnlySpan<byte> body, ushort scheme, out bool listed)
    {
        listed = false;
        TlsReader reader = new(body);
        if (!reader.TryReadVector16(out ReadOnlySpan<byte> list) || reader.Remaining != 0 || (list.Length & 1) != 0)
        {
            return false;
        }

        for (int i = 0; i + 1 < list.Length; i += 2)
        {
            if (((list[i] << 8) | list[i + 1]) == scheme)
            {
                listed = true;
            }
        }

        return true;
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
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
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

    private void WriteOffered(ref TlsWriter writer, ushort type, ReadOnlySpan<byte> body)
    {
        if (_offeredCount == _offered.Length)
        {
            throw new TlsProtocolViolation("The ClientHello offers more extensions than the engine records.");
        }

        _offered[_offeredCount++] = type;
        WriteExtension(ref writer, type, body);
    }

    private static void WriteExtension(ref TlsWriter writer, ushort type, ReadOnlySpan<byte> body)
    {
        writer.TryWriteUInt16(type);
        writer.TryWriteVector16(body);
    }

    private void WriteAlpn(ref TlsWriter writer, string[] protocols)
    {
        int list = 0;
        foreach (string protocol in protocols)
        {
            list += 1 + System.Text.Encoding.ASCII.GetByteCount(protocol);
        }

        var body = new byte[2 + list];
        body[0] = (byte)(list >> 8);
        body[1] = (byte)list;
        int at = 2;
        foreach (string protocol in protocols)
        {
            byte[] name = System.Text.Encoding.ASCII.GetBytes(protocol);
            body[at++] = (byte)name.Length;
            name.CopyTo(body.AsSpan(at));
            at += name.Length;
        }

        WriteOffered(ref writer, ExtensionType.ApplicationLayerProtocolNegotiation, body);
    }

    private void WriteServerName(ref TlsWriter writer, string name)
    {
        byte[] ascii = System.Text.Encoding.ASCII.GetBytes(name);
        var body = new byte[2 + 1 + 2 + ascii.Length];
        int list = 1 + 2 + ascii.Length;
        body[0] = (byte)(list >> 8);
        body[1] = (byte)list;
        body[3] = (byte)(ascii.Length >> 8);
        body[4] = (byte)ascii.Length;
        ascii.CopyTo(body.AsSpan(5));
        WriteOffered(ref writer, ExtensionType.ServerName, body);
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
