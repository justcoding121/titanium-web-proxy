using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Tls;

internal sealed partial class TlsEngine
{
    private const byte AlertHandshakeFailure = 40;
    private const byte AlertProtocolVersion = 70;
    private const byte AlertInternalError = 80;
    private const byte AlertNoApplicationProtocol = 120;
    private const int DefaultTicketLifetime = 6 * 60 * 60;
    private const int MaxTicketLifetime = 7 * 24 * 60 * 60;

    private static readonly ushort[] DefaultCiphers = [0x1301, 0x1303, 0x1302];
    private static readonly NamedGroup[] DefaultGroups =
    [
        NamedGroup.X25519MlKem768,
        NamedGroup.X25519,
        NamedGroup.Secp256r1,
        NamedGroup.Secp384r1,
    ];

    private static ReadOnlySpan<byte> HelloRetryRandom =>
    [
        0xCF, 0x21, 0xAD, 0x74, 0xE5, 0x9A, 0x61, 0x11, 0xBE, 0x1D, 0x8C, 0x02, 0x1E, 0x65, 0xB8, 0x91,
        0xC2, 0xA2, 0x11, 0x16, 0x7A, 0xBB, 0x8C, 0x5E, 0x07, 0x9E, 0x09, 0xE2, 0xC8, 0xA8, 0x33, 0x9C,
    ];

    private TlsServerSelection? _selection;
    private TlsClientHelloInfo? _clientHelloInfo;
    private byte[]? _firstHello;
    private byte[]? _sessionId;
    private ushort _requestedGroup;
    private bool _resumed;

    internal bool AcceptedResumption => _resumed;
    private byte[]? _clientLeaf;
    private TlsCertificateCheck? _clientCheck;

    internal SslPolicyErrors ClientCertificateErrors => _clientCheck?.Errors ?? SslPolicyErrors.None;

    internal X509Chain? ClientChain => _clientCheck?.Chain;

    internal void ReleaseClientCheck()
    {
        _clientCheck?.Dispose();
        _clientCheck = null;
    }

    private string? _selectedAlpn;
    private SecretBuffer? _clientApplication;
    private SecretBuffer? _serverApplication;

    internal TlsClientHelloInfo? ClientHello => _clientHelloInfo;

    internal string? SelectedApplicationProtocol => _selectedAlpn;

    internal bool IsResumption => _resumed;

    internal void Resume(Func<TlsClientHelloInfo, TlsServerSelection> callback, TlsOutput output)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(output);
        if (!_paused || _clientHelloInfo is null)
        {
            throw new InvalidOperationException("The engine is not waiting for a decision.");
        }

        try
        {
            _selection = callback(_clientHelloInfo);
        }
        catch (Exception)
        {
            output.Clear();
            Fail(output, AlertInternalError);
            return;
        }

        Resume(true, output);
    }

    private void HandleServer(HandshakeType type, int bodyLength, TlsOutput output)
    {
        if (type == HandshakeType.ClientHello)
        {
            OnClientHello(bodyLength, output);
            // Stryker disable once statement: the following handshake type does not match
            return;
        }

        if (type == HandshakeType.Certificate)
        {
            OnClientCertificate(bodyLength, output);
            // Stryker disable once statement: the following handshake type does not match
            return;
        }

        if (type == HandshakeType.CertificateVerify)
        {
            OnClientCertificateVerify(bodyLength, output);
            // Stryker disable once statement: the following handshake type does not match
            return;
        }

        if (type == HandshakeType.Finished)
        {
            OnClientFinished(bodyLength, output);
            // Stryker disable once statement: the following handshake type does not match
            return;
        }

        if (type == HandshakeType.KeyUpdate)
        {
            OnKeyUpdate(bodyLength, output);
        }
    }

    private void OnClientHello(int bodyLength, TlsOutput output)
    {
        byte[] message = Message(HandshakeType.ClientHello, bodyLength);
        if (!HandshakeMessages.TryReadClientHello(message, out ClientHelloView hello, out HandshakeAlert alert))
        {
            Fail(output, (byte)alert);
            return;
        }

        _loggedRandom = hello.Random.ToArray();
        _ignoreEarlyData = TryExtension(hello.Extensions, ExtensionType.EarlyData, out _);
        if (_hrrSeen)
        {
            ContinueAfterRetry(message, hello, output);
            if (State != TlsEngineState.Closed)
            {
                _endOfRecord = true;
            }

            return;
        }

        if (!OffersVersion(hello.Extensions, 0x0304))
        {
            Fail(output, AlertProtocolVersion);
            return;
        }

        if (!OffersCompressionNull(hello.CompressionMethods))
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        _firstHello = message;
        _sessionId = hello.SessionId.ToArray();
        _clientHelloInfo = new TlsClientHelloInfo(ReadServerName(hello.Extensions), message);
        State = TlsEngineState.WaitClientHelloDecision;
        _paused = true;
        _endOfRecord = true;
        output.AddEvent(TlsEventKind.ClientHelloReceived);
    }

    private void SendServerFlight(TlsOutput output)
    {
        if (_selection is null || _firstHello is null || _sessionId is null)
        {
            Fail(output, AlertInternalError);
            return;
        }

        if (!HandshakeMessages.TryReadClientHello(_firstHello, out ClientHelloView hello, out _))
        {
            Fail(output, AlertInternalError);
            return;
        }

        if (!RequireGroups(hello.Extensions, output))
        {
            return;
        }

        if (!TrySelectCipher(hello.CipherSuites, out ushort cipher, out HashAlgorithmName hash, out TlsAeadSuite aead, out int keyLength))
        {
            Fail(output, AlertHandshakeFailure);
            return;
        }

        _selectedCipher = cipher;
        _transcript = new TranscriptStore(hash);
        _schedule = new TlsKeySchedule(hash);
        if (!TrySelectShare(hello, out ushort group, out byte[]? share))
        {
            Fail(output, AlertHandshakeFailure);
            return;
        }

        if (!TrySelectAlpn(hello.Extensions, out byte alpnAlert))
        {
            Fail(output, alpnAlert);
            return;
        }

        if (share is null)
        {
            _transcript.Append(_firstHello);
            SendHelloRetry(group, output);
            return;
        }

        if (!TryAcceptTicket(hello, _firstHello, out byte[]? psk, out byte pskAlert))
        {
            if (pskAlert != 0)
            {
                Fail(output, pskAlert);
                return;
            }

            psk = null;
        }

        _schedule.InitializeEarly(psk ?? ReadOnlySpan<byte>.Empty);
        if (psk is not null)
        {
            // Stryker disable once statement: the temporary is not read after the bytes are copied
            CryptographicOperations.ZeroMemory(psk);
        }

        _transcript.Append(_firstHello);
        EmitFlight(group, share, hash, aead, keyLength, output);
    }

    private void ContinueAfterRetry(byte[] message, ClientHelloView hello, TlsOutput output)
    {
        if (_schedule is null || _transcript is null || _selection is null || _firstHello is null)
        {
            Fail(output, AlertInternalError);
            return;
        }

        if (!OffersVersion(hello.Extensions, 0x0304))
        {
            Fail(output, AlertProtocolVersion);
            return;
        }

        if (!OffersCompressionNull(hello.CompressionMethods))
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        if (!HandshakeMessages.TryReadClientHello(_firstHello, out ClientHelloView first, out _) || !RetryHelloMatches(first, hello))
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        if (!RequireGroups(hello.Extensions, output))
        {
            return;
        }

        if (!TryKeyShare(hello.Extensions, _requestedGroup, out byte[]? share) || share is null)
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        Suite(_selectedCipher, out HashAlgorithmName hash, out TlsAeadSuite aead, out int keyLength);
        if (!TryAcceptTicket(hello, message, out byte[]? psk, out byte pskAlert))
        {
            if (pskAlert != 0)
            {
                Fail(output, pskAlert);
                return;
            }

            psk = null;
        }

        _schedule.InitializeEarly(psk ?? ReadOnlySpan<byte>.Empty);
        if (psk is not null)
        {
            CryptographicOperations.ZeroMemory(psk);
        }

        _transcript.Append(message);
        EmitFlight(_requestedGroup, share, hash, aead, keyLength, output);
    }

    private void SendHelloRetry(ushort group, TlsOutput output)
    {
        var random = new byte[32];
        HelloRetryRandom.CopyTo(random);
        byte[] message = ServerHelloMessage(random, _sessionId!, _selectedCipher, VersionExtension(), GroupOnly(group), psk: false);
        _transcript!.ReplaceWithMessageHash();
        _transcript.Append(message);
        WriteServerRecord(output, message);
        _hrrSeen = true;
        _requestedGroup = group;
        State = TlsEngineState.WaitClientHello;
    }

    private void EmitFlight(ushort group, byte[] clientShare, HashAlgorithmName hash, TlsAeadSuite aead, int keyLength, TlsOutput output)
    {
        Span<byte> serverShare = stackalloc byte[1216];
        Span<byte> secret = stackalloc byte[64];
        if (!TryAgree(group, clientShare, serverShare, secret, out int serverWritten, out int secretWritten))
        {
            Fail(output, AlertIllegalParameter);
            return;
        }

        var random = new byte[32];
        _entropy.Fill(random);
        byte[] serverHello = ServerHelloMessage(random, _sessionId!, _selectedCipher, VersionExtension(), KeyShareEntry(group, serverShare[..serverWritten]), _resumed);
        _transcript!.Append(serverHello);
        int hashLength = TlsHkdf.HashLength(hash);
        Span<byte> transcriptHash = stackalloc byte[48];
        _transcript.CurrentHash(transcriptHash);
        _schedule!.AdvanceToHandshake(secret[..secretWritten]);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(secret[..secretWritten]);
        InstallServerHandshakeKeys(hash, aead, keyLength, hashLength, transcriptHash);
        byte[] ee = EncryptedExtensionsMessage(_selectedAlpn);
        _transcript.Append(ee);
        var flight = new System.Collections.Generic.List<byte>(ee.Length + 2048);
        flight.AddRange(ee);
        if (!_resumed && _selection!.RequestClientCertificate)
        {
            byte[] request = CertificateRequestMessage();
            _transcript.Append(request);
            flight.AddRange(request);
            _certificateRequested = true;
        }

        if (!_resumed)
        {
            byte[] certificate = CertificateMessage(_selection!.Certificate.RawData);
            _transcript.Append(certificate);
            _transcript.CurrentHash(transcriptHash);
        if (!TrySign(_selection.Certificate, hash, transcriptHash, out ushort scheme, out byte[] signature, out byte signAlert))
        {
            Fail(output, signAlert);
            return;
        }

            byte[] verify = CertificateVerifyMessage(scheme, signature);
            _transcript.Append(verify);
            flight.AddRange(certificate);
            flight.AddRange(verify);
        }

        _transcript.CurrentHash(transcriptHash);
        Span<byte> finishedKey = stackalloc byte[48];
        Span<byte> verifyData = stackalloc byte[48];
        TlsKeySchedule.FinishedKey(hash, _serverHandshakeSecret!.Span, finishedKey[..hashLength]);
        Mac(hash, finishedKey[..hashLength], transcriptHash[..hashLength], verifyData[..hashLength]);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(finishedKey);
        var finished = new byte[4 + hashLength];
        finished[0] = (byte)HandshakeType.Finished;
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        finished[1] = (byte)(hashLength >> 16);
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        finished[2] = (byte)(hashLength >> 8);
        finished[3] = (byte)hashLength;
        verifyData[..hashLength].CopyTo(finished.AsSpan(4));
        _transcript.Append(finished);
        flight.AddRange(finished);
        _transcript.CurrentHash(transcriptHash);
        _schedule.AdvanceToMaster();
        _clientApplication = new SecretBuffer(hashLength);
        _serverApplication = new SecretBuffer(hashLength);
        _exporter?.Dispose();
        _exporter = new SecretBuffer(hashLength);
        _schedule.ApplicationTrafficSecret(true, transcriptHash[..hashLength], _clientApplication.Span);
        _schedule.ApplicationTrafficSecret(false, transcriptHash[..hashLength], _serverApplication.Span);
        _schedule.ExporterMaster(transcriptHash[..hashLength], _exporter.Span);
        WriteServerRecord(output, serverHello);
        if (_sessionId!.Length > 0)
        {
            output.AddBytes([20, 3, 3, 0, 1, 1]);
        }

        ProtectHandshake(output, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(flight));
        _alertsEncrypted = true;
        State = _certificateRequested ? TlsEngineState.WaitClientCertificate : TlsEngineState.WaitFinished;
    }

    private void OnClientCertificate(int bodyLength, TlsOutput output)
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
            if (_selection?.RequireClientCertificate == true)
            {
                Fail(output, 116);
                return;
            }

            _transcript!.Append(message);
            State = TlsEngineState.WaitFinished;
            return;
        }

        TlsCertificateCheck check = TlsChainValidator.Evaluate(certificate.CertificateList, targetHost: null, _selection?.ClientTrustAnchors, serverCertificate: false);
        if (!TryCopyClientLeaf(certificate.CertificateList))
        {
            check.Dispose();
            Fail(output, 42);
            return;
        }

        _transcript!.Append(message);
        if (_selection?.ClientCertificateCallback is not null)
        {
            _clientCheck = check;
            _paused = true;
            State = TlsEngineState.WaitClientCertificateDecision;
            output.AddEvent(TlsEventKind.ClientCertificateReceived);
            return;
        }

        if (check.Errors != SslPolicyErrors.None)
        {
            check.Dispose();
            Fail(output, 42);
            return;
        }

        check.Dispose();
        State = TlsEngineState.WaitClientCertificateVerify;
    }

    private void OnClientCertificateVerify(int bodyLength, TlsOutput output)
    {
        byte[] message = Message(HandshakeType.CertificateVerify, bodyLength);
        if (!HandshakeMessages.TryReadCertificateVerify(message, out ushort scheme, out ReadOnlySpan<byte> signature, out HandshakeAlert alert))
        {
            Fail(output, (byte)alert);
            return;
        }

        if (scheme != 0x0403 || _clientLeaf is null)
        {
            Fail(output, 47);
            return;
        }

        HashAlgorithmName hash = HashOf(_selectedCipher);
        int hashLength = TlsHkdf.HashLength(hash);
        Span<byte> transcriptHash = stackalloc byte[48];
        _transcript!.CurrentHash(transcriptHash);
        Span<byte> content = stackalloc byte[64 + 34 + 48];
        content[..64].Fill(0x20);
        "TLS 1.3, client CertificateVerify"u8.CopyTo(content[64..]);
        content[64 + 33] = 0;
        transcriptHash[..hashLength].CopyTo(content[(64 + 34)..]);
        using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(_clientLeaf);
        if (!TlsCertificateVerifier.Verify(certificate, scheme, content[..(64 + 34 + hashLength)], signature))
        {
            Fail(output, AlertDecryptError);
            return;
        }

        _transcript.Append(message);
        _clientAuthenticated = true;
        State = TlsEngineState.WaitFinished;
    }

    private bool TryCopyClientLeaf(ReadOnlySpan<byte> list)
    {
        TlsReader reader = new(list);
        if (!reader.TryReadVector24(out ReadOnlySpan<byte> encoded) || encoded.IsEmpty)
        {
            return false;
        }

        _clientLeaf = encoded.ToArray();
        return true;
    }

    private void OnClientFinished(int bodyLength, TlsOutput output)
    {
        byte[] message = Message(HandshakeType.Finished, bodyLength);
        if (!HandshakeMessages.TryReadFinished(message, out ReadOnlySpan<byte> verifyData, out HandshakeAlert alert))
        {
            Fail(output, (byte)alert);
            return;
        }

        HashAlgorithmName hash = HashOf(_selectedCipher);
        int hashLength = TlsHkdf.HashLength(hash);
        Span<byte> transcriptHash = stackalloc byte[48];
        Span<byte> finishedKey = stackalloc byte[48];
        Span<byte> expected = stackalloc byte[48];
        _transcript!.CurrentHash(transcriptHash);
        TlsKeySchedule.FinishedKey(hash, _clientHandshakeSecret!.Span, finishedKey[..hashLength]);
        Mac(hash, finishedKey[..hashLength], transcriptHash[..hashLength], expected[..hashLength]);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(finishedKey);
        if (!CryptographicOperations.FixedTimeEquals(expected[..hashLength], verifyData))
        {
            Fail(output, AlertDecryptError);
            return;
        }

        _transcript.Append(message);
        _transcript.CurrentHash(transcriptHash);
        _resumptionMaster = new SecretBuffer(hashLength);
        _schedule!.ResumptionMaster(transcriptHash[..hashLength], _resumptionMaster.Span);
        InstallApplicationKeys();
        if (_selection?.Tickets is not null)
        {
            SendTicket(hash, output);
        }

        State = TlsEngineState.Connected;
        _endOfRecord = true;
        output.AddEvent(TlsEventKind.HandshakeCompleted);
    }

    private void InstallServerHandshakeKeys(HashAlgorithmName hash, TlsAeadSuite aead, int keyLength, int hashLength, ReadOnlySpan<byte> transcriptHash)
    {
        _clientHandshakeSecret?.Dispose();
        _serverHandshakeSecret?.Dispose();
        _clientHandshakeSecret = new SecretBuffer(hashLength);
        _serverHandshakeSecret = new SecretBuffer(hashLength);
        _schedule!.HandshakeTrafficSecret(true, transcriptHash[..hashLength], _clientHandshakeSecret.Span);
        _schedule.HandshakeTrafficSecret(false, transcriptHash[..hashLength], _serverHandshakeSecret.Span);
        Span<byte> key = stackalloc byte[32];
        Span<byte> iv = stackalloc byte[12];
        TlsKeySchedule.DeriveTrafficKeys(hash, _clientHandshakeSecret.Span, key[..keyLength], iv);
        _read?.Dispose();
        _read = new TlsRecordProtector(aead, key[..keyLength], iv);
        TlsKeySchedule.DeriveTrafficKeys(hash, _serverHandshakeSecret.Span, key[..keyLength], iv);
        _write?.Dispose();
        _write = new TlsRecordProtector(aead, key[..keyLength], iv);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(key);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(iv);
    }

    private void InstallApplicationKeys()
    {
        HashAlgorithmName hash = HashOf(_selectedCipher);
        Suite(_selectedCipher, out _, out TlsAeadSuite aead, out int keyLength);
        Span<byte> key = stackalloc byte[32];
        Span<byte> iv = stackalloc byte[12];
        TlsKeySchedule.DeriveTrafficKeys(hash, _clientApplication!.Span, key[..keyLength], iv);
        _read?.Dispose();
        _read = new TlsRecordProtector(aead, key[..keyLength], iv);
        TlsKeySchedule.DeriveTrafficKeys(hash, _serverApplication!.Span, key[..keyLength], iv);
        _write?.Dispose();
        _write = new TlsRecordProtector(aead, key[..keyLength], iv);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(key);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(iv);
    }

    private void ProtectHandshake(TlsOutput output, ReadOnlySpan<byte> handshake)
    {
        var encrypted = new byte[handshake.Length + 64];
        int written = _write!.Protect(22, handshake, encrypted);
        output.AddBytes(encrypted.AsSpan(0, written));
    }

    private bool TrySign(X509Certificate2 leaf, HashAlgorithmName transcriptHash, ReadOnlySpan<byte> hash, out ushort scheme, out byte[] signature, out byte alert)
    {
        scheme = 0;
        signature = [];
        alert = AlertHandshakeFailure;
        if (!TryOfferedSchemes(_firstHello!, out ushort[] offered, out alert))
        {
            return false;
        }

        int hashLength = TlsHkdf.HashLength(transcriptHash);
        Span<byte> content = stackalloc byte[64 + 34 + 48];
        content[..64].Fill(0x20);
        "TLS 1.3, server CertificateVerify"u8.CopyTo(content[64..]);
        hash[..hashLength].CopyTo(content[(64 + 34)..]);
        ReadOnlySpan<byte> signedContent = content[..(64 + 34 + hashLength)];
        ECDsa? ecdsa = CertificateKeys.Ecdsa(leaf);
        if (ecdsa is not null)
        {
            ECParameters parameters = ecdsa.ExportParameters(false);
            string? oid = parameters.Curve.Oid.Value;
            if (oid == "1.2.840.10045.3.1.7")
            {
                scheme = 0x0403;
            }
            else if (oid == "1.3.132.0.34")
            {
                scheme = 0x0503;
            }

            if (scheme == 0 || !Contains(offered, scheme) || !TlsAlgorithmSet.AllowsSignature(_algorithmPolicy, scheme))
            {
                alert = AlertHandshakeFailure;
                return false;
            }

            signature = ecdsa.SignData(signedContent, scheme == 0x0403 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA384, DSASignatureFormat.Rfc3279DerSequence);
            alert = 0;
            return true;
        }

        RSA? rsa = CertificateKeys.Rsa(leaf);
        if (rsa is null || rsa.KeySize < 2048 || !TlsAlgorithmSet.AllowsRsa(_algorithmPolicy, rsa.KeySize) || !Contains(offered, 0x0804))
        {
            alert = AlertHandshakeFailure;
            return false;
        }

        scheme = 0x0804;
        signature = rsa.SignData(signedContent, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        alert = 0;
        return true;
    }

    private bool TryAcceptTicket(ClientHelloView hello, byte[] message, out byte[]? psk, out byte alert)
    {
        psk = null;
        alert = 0;
        _resumed = false;
        // A requested client certificate is not stored in the ticket. Resuming would skip CertificateRequest.
        if (_selection?.RequestClientCertificate == true)
        {
            return false;
        }

        if (_selection?.Tickets is null || !TryExtension(hello.Extensions, ExtensionType.PreSharedKey, out ReadOnlySpan<byte> body))
        {
            return false;
        }

        if (!TryPskKeyExchange(hello.Extensions, out bool dhe, out alert) ||
            !TryReadFirstTicket(body, out ReadOnlySpan<byte> identity, out ReadOnlySpan<byte> binder, out int binderTail, out alert))
        {
            return false;
        }

        if (!dhe)
        {
            return false;
        }

        Span<byte> plain = stackalloc byte[1024];
        if (!_selection.Tickets.TryUnprotect(identity, plain, out int plainLength) || !TryReadTicket(plain[..plainLength], out TicketView ticket))
        {
            return false;
        }

        long now = Now();
        bool acceptable = ticket.Cipher == _selectedCipher &&
            ticket.Lifetime is > 0 and <= MaxTicketLifetime &&
            now <= ticket.Issued + ticket.Lifetime &&
            NamesMatch(ticket.ServerName, _clientHelloInfo?.ServerName) &&
            string.Equals(ticket.Alpn, _selectedAlpn, StringComparison.Ordinal) &&
            ticket.LeafHash.SequenceEqual(SHA256.HashData(_selection.Certificate.RawData));
        Span<byte> expected = stackalloc byte[48];
        int hashLength = binder.Length;
        if (hashLength != TlsHkdf.HashLength(HashOf(_selectedCipher)))
        {
            CryptographicOperations.ZeroMemory(ticket.Psk);
            alert = AlertIllegalParameter;
            return false;
        }

        Span<byte> transcriptHash = stackalloc byte[48];
        _transcript!.HashWith(message.AsSpan(0, message.Length - binderTail), transcriptHash);
        using var early = new TlsKeySchedule(HashOf(_selectedCipher));
        early.InitializeEarly(ticket.Psk);
        Span<byte> empty = stackalloc byte[48];
        empty[..hashLength].Clear();
        if (HashOf(_selectedCipher) == HashAlgorithmName.SHA256)
        {
            SHA256.HashData(ReadOnlySpan<byte>.Empty, empty[..hashLength]);
        }
        else
        {
            SHA384.HashData(ReadOnlySpan<byte>.Empty, empty[..hashLength]);
        }

        Span<byte> binderKey = stackalloc byte[48];
        Span<byte> finishedKey = stackalloc byte[48];
        early.DeriveEarly("res binder"u8, empty[..hashLength], binderKey[..hashLength]);
        TlsKeySchedule.FinishedKey(HashOf(_selectedCipher), binderKey[..hashLength], finishedKey[..hashLength]);
        Mac(HashOf(_selectedCipher), finishedKey[..hashLength], transcriptHash[..hashLength], expected);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(binderKey);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(finishedKey);
        bool binderMatches = CryptographicOperations.FixedTimeEquals(expected[..hashLength], binder);
        if (!acceptable)
        {
            CryptographicOperations.ZeroMemory(ticket.Psk);
            return false;
        }

        if (!binderMatches)
        {
            CryptographicOperations.ZeroMemory(ticket.Psk);
            alert = AlertDecryptError;
            return false;
        }

        psk = ticket.Psk;
        _resumed = true;
        return true;
    }

    private void SendTicket(HashAlgorithmName hash, TlsOutput output)
    {
        var nonce = new byte[8];
        _entropy.Fill(nonce);
        var psk = new byte[TlsHkdf.HashLength(hash)];
        TlsKeySchedule.ResumptionPsk(hash, _resumptionMaster!.Span, nonce, psk);
        byte[] plain = WriteTicket(_selectedCipher, psk, _clientHelloInfo?.ServerName, _selectedAlpn, Now(), DefaultTicketLifetime, SHA256.HashData(_selection!.Certificate.RawData));
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(psk);
        byte[] identity = _selection.Tickets!.Protect(plain);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(plain);
        var age = new byte[4];
        _entropy.Fill(age);
        byte[] message = NewSessionTicketMessage(DefaultTicketLifetime, age, nonce, identity);
        ProtectHandshake(output, message);
    }

    private bool TrySelectCipher(ReadOnlySpan<byte> offered, out ushort cipher, out HashAlgorithmName hash, out TlsAeadSuite aead, out int keyLength)
    {
        cipher = 0;
        hash = default;
        aead = default;
        keyLength = 0;
        ushort[] supported = _selection!.CipherSuites ?? DefaultCiphers;
        if (_selection.PreferServerCipherOrder)
        {
            foreach (ushort candidate in supported)
            {
                if (ContainsCipher(offered, candidate) && TlsAlgorithmSet.AllowsCipher(_algorithmPolicy, candidate) && Suite(candidate, out hash, out aead, out keyLength))
                {
                    cipher = candidate;
                    return true;
                }
            }

            return false;
        }

        for (int i = 0; i + 1 < offered.Length; i += 2)
        {
            ushort candidate = (ushort)((offered[i] << 8) | offered[i + 1]);
            if (Contains(supported, candidate) && TlsAlgorithmSet.AllowsCipher(_algorithmPolicy, candidate) && Suite(candidate, out hash, out aead, out keyLength))
            {
                cipher = candidate;
                return true;
            }
        }

        return false;
    }

    private bool TrySelectShare(ClientHelloView hello, out ushort group, out byte[]? share)
    {
        group = 0;
        share = null;
        NamedGroup[] preference = _selection!.Groups ?? DefaultGroups;
        foreach (NamedGroup candidate in preference)
        {
            if (!TlsAlgorithmSet.AllowsGroup(_algorithmPolicy, (ushort)candidate))
            {
                continue;
            }

            if (TryKeyShare(hello.Extensions, (ushort)candidate, out byte[]? found) && found is not null && Advertised(hello.Extensions, (ushort)candidate))
            {
                group = (ushort)candidate;
                share = found;
                return true;
            }
        }

        if (_hrrSeen)
        {
            return false;
        }

        foreach (NamedGroup candidate in preference)
        {
            if (!TlsAlgorithmSet.AllowsGroup(_algorithmPolicy, (ushort)candidate))
            {
                continue;
            }

            if (Advertised(hello.Extensions, (ushort)candidate))
            {
                group = (ushort)candidate;
                return true;
            }
        }

        return false;
    }

    private bool TrySelectAlpn(ReadOnlySpan<byte> extensions, out byte alert)
    {
        alert = 0;
        _selectedAlpn = null;
        if (!TryExtension(extensions, ExtensionType.ApplicationLayerProtocolNegotiation, out ReadOnlySpan<byte> body))
        {
            return true;
        }

        string[]? ours = _selection!.ApplicationProtocols;
        if (!TryReadAlpn(body, out string[] theirs) || theirs.Length == 0)
        {
            alert = (byte)HandshakeAlert.DecodeError;
            return false;
        }

        if (ours is not null)
        {
            foreach (string protocol in ours)
            {
                foreach (string offered in theirs)
                {
                    if (string.Equals(protocol, offered, StringComparison.Ordinal))
                    {
                        _selectedAlpn = protocol;
                        return true;
                    }
                }
            }
        }

        if (_selection.RequireApplicationProtocol)
        {
            alert = AlertNoApplicationProtocol;
            return false;
        }

        return true;
    }

    private bool TryAgree(ushort group, ReadOnlySpan<byte> clientShare, Span<byte> serverShare, Span<byte> secret, out int serverWritten, out int secretWritten)
    {
        serverWritten = 0;
        secretWritten = 0;
        IKeyExchangeGroup exchange = KeyExchangeGroups.Create((NamedGroup)group);
        if (group == (ushort)NamedGroup.X25519MlKem768)
        {
            return exchange.TryEncapsulate(clientShare, _entropy, serverShare, secret, out serverWritten, out secretWritten);
        }

        using IPrivateKeyShare mine = exchange.GenerateKeyShare(_entropy);
        serverWritten = exchange.PublicKeyLength;
        mine.WritePublicKey(serverShare[..serverWritten]);
        return mine.TryComputeSharedSecret(clientShare, secret, out secretWritten);
    }

    private static bool Suite(ushort cipher, out HashAlgorithmName hash, out TlsAeadSuite aead, out int keyLength)
    {
        switch (cipher)
        {
            case 0x1301:
                hash = HashAlgorithmName.SHA256;
                aead = TlsAeadSuite.Aes128Gcm;
                keyLength = 16;
                return true;
            case 0x1302:
                hash = HashAlgorithmName.SHA384;
                aead = TlsAeadSuite.Aes256Gcm;
                keyLength = 32;
                return true;
            case 0x1303:
                hash = HashAlgorithmName.SHA256;
                aead = TlsAeadSuite.ChaCha20Poly1305;
                keyLength = 32;
                return true;
            default:
                hash = default;
                aead = default;
                keyLength = 0;
                return false;
        }
    }

    private static HashAlgorithmName HashOf(ushort cipher)
    {
        Suite(cipher, out HashAlgorithmName hash, out _, out _);
        return hash;
    }

    private static void Mac(HashAlgorithmName hash, ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (hash == HashAlgorithmName.SHA256)
        {
            HMACSHA256.HashData(key, data, destination);
            return;
        }

        HMACSHA384.HashData(key, data, destination);
    }

    private static bool OffersVersion(ReadOnlySpan<byte> extensions, ushort version)
    {
        if (!TryExtension(extensions, ExtensionType.SupportedVersions, out ReadOnlySpan<byte> body) || body.IsEmpty)
        {
            return false;
        }

        int length = body[0];
        if ((length & 1) != 0 || body.Length != 1 + length)
        {
            return false;
        }

        for (int i = 0; i < length; i += 2)
        {
            if (((body[1 + i] << 8) | body[2 + i]) == version)
            {
                return true;
            }
        }

        return false;
    }

    private static bool OffersCompressionNull(ReadOnlySpan<byte> methods)
    {
        return methods.Length == 1 && methods[0] == 0;
    }

    private bool RequireGroups(ReadOnlySpan<byte> extensions, TlsOutput output)
    {
        if (!TryExtension(extensions, ExtensionType.SupportedGroups, out ReadOnlySpan<byte> body))
        {
            Fail(output, AlertMissingExtension);
            return false;
        }

        TlsReader reader = new(body);
        if (!reader.TryReadVector16(out ReadOnlySpan<byte> groups) || reader.Remaining != 0 || groups.Length == 0 || (groups.Length & 1) != 0)
        {
            Fail(output, (byte)HandshakeAlert.DecodeError);
            return false;
        }

        return true;
    }

    private static bool Advertised(ReadOnlySpan<byte> extensions, ushort group)
    {
        if (!TryExtension(extensions, ExtensionType.SupportedGroups, out ReadOnlySpan<byte> body))
        {
            return false;
        }

        TlsReader reader = new(body);
        if (!reader.TryReadVector16(out ReadOnlySpan<byte> groups))
        {
            return false;
        }

        for (int i = 0; i + 1 < groups.Length; i += 2)
        {
            if (((groups[i] << 8) | groups[i + 1]) == group)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryKeyShare(ReadOnlySpan<byte> extensions, ushort group, out byte[]? share)
    {
        share = null;
        if (!TryExtension(extensions, ExtensionType.KeyShare, out ReadOnlySpan<byte> body))
        {
            return false;
        }

        TlsReader reader = new(body);
        if (!reader.TryReadVector16(out ReadOnlySpan<byte> entries))
        {
            return false;
        }

        TlsReader entriesReader = new(entries);
        while (entriesReader.Remaining > 0)
        {
            if (!entriesReader.TryReadUInt16(out ushort found) || !entriesReader.TryReadVector16(out ReadOnlySpan<byte> key))
            {
                return false;
            }

            if (found == group)
            {
                share = key.ToArray();
                return true;
            }
        }

        return false;
    }

    private static string? ReadServerName(ReadOnlySpan<byte> extensions)
    {
        if (!TryExtension(extensions, ExtensionType.ServerName, out ReadOnlySpan<byte> body))
        {
            return null;
        }

        TlsReader reader = new(body);
        if (!reader.TryReadVector16(out ReadOnlySpan<byte> list))
        {
            return null;
        }

        TlsReader names = new(list);
        if (!names.TryReadUInt8(out byte kind) || kind != 0 || !names.TryReadVector16(out ReadOnlySpan<byte> host))
        {
            return null;
        }

        return System.Text.Encoding.ASCII.GetString(host);
    }

    private static bool TryPskKeyExchange(ReadOnlySpan<byte> extensions, out bool dhe, out byte alert)
    {
        dhe = false;
        alert = 0;
        if (!TryExtension(extensions, ExtensionType.PskKeyExchangeModes, out ReadOnlySpan<byte> body))
        {
            alert = AlertMissingExtension;
            return false;
        }

        if (body.IsEmpty)
        {
            alert = (byte)HandshakeAlert.DecodeError;
            return false;
        }

        int count = body[0];
        if (body.Length != 1 + count)
        {
            alert = (byte)HandshakeAlert.DecodeError;
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (body[1 + i] == 1)
            {
                dhe = true;
            }
        }

        return true;
    }

    private static bool TryReadFirstTicket(ReadOnlySpan<byte> body, out ReadOnlySpan<byte> identity, out ReadOnlySpan<byte> binder, out int binderTail, out byte alert)
    {
        identity = default;
        binder = default;
        binderTail = 0;
        alert = (byte)HandshakeAlert.DecodeError;
        TlsReader reader = new(body);
        if (!reader.TryReadVector16(out ReadOnlySpan<byte> identities) || !reader.TryReadVector16(out ReadOnlySpan<byte> binders) || reader.Remaining != 0)
        {
            return false;
        }

        if (!CountVectors(identities, 4, out int identityCount) || !CountBinders(binders, out int binderCount))
        {
            return false;
        }

        if (identityCount != binderCount || identityCount == 0)
        {
            alert = AlertIllegalParameter;
            return false;
        }

        TlsReader idReader = new(identities);
        if (!idReader.TryReadVector16(out identity) || identity.IsEmpty || !idReader.TryReadUInt32(out _))
        {
            return false;
        }

        TlsReader binderReader = new(binders);
        if (!binderReader.TryReadVector8(out binder) || binder.Length is not (32 or 48))
        {
            return false;
        }

        binderTail = 2 + binders.Length;
        alert = 0;
        return true;
    }

    private static bool CountBinders(ReadOnlySpan<byte> body, out int count)
    {
        count = 0;
        TlsReader reader = new(body);
        while (reader.Remaining > 0)
        {
            if (!reader.TryReadVector8(out ReadOnlySpan<byte> value) || value.IsEmpty)
            {
                return false;
            }

            count++;
        }

        return true;
    }

    private static bool CountVectors(ReadOnlySpan<byte> body, int trailing, out int count)
    {
        count = 0;
        TlsReader reader = new(body);
        while (reader.Remaining > 0)
        {
            if (!reader.TryReadVector16(out ReadOnlySpan<byte> value) || value.IsEmpty)
            {
                return false;
            }

            if (trailing > 0 && !reader.TryReadBytes(trailing, out _))
            {
                return false;
            }

            count++;
        }

        return true;
    }

    private static bool TryOfferedSchemes(byte[] hello, out ushort[] schemes, out byte alert)
    {
        schemes = [];
        alert = AlertMissingExtension;
        if (!HandshakeMessages.TryReadClientHello(hello, out ClientHelloView view, out _))
        {
            alert = AlertInternalError;
            return false;
        }

        if (!TryExtension(view.Extensions, ExtensionType.SignatureAlgorithms, out ReadOnlySpan<byte> body))
        {
            return false;
        }

        TlsReader reader = new(body);
        if (!reader.TryReadVector16(out ReadOnlySpan<byte> list) || reader.Remaining != 0 || (list.Length & 1) != 0 || list.Length == 0)
        {
            alert = (byte)HandshakeAlert.DecodeError;
            return false;
        }

        schemes = new ushort[list.Length / 2];
        for (int i = 0; i < schemes.Length; i++)
        {
            schemes[i] = (ushort)((list[i * 2] << 8) | list[(i * 2) + 1]);
        }

        alert = 0;
        return true;
    }

    private static bool RetryHelloMatches(ClientHelloView first, ClientHelloView second)
    {
        if (first.LegacyVersion != second.LegacyVersion ||
            !first.Random.SequenceEqual(second.Random) ||
            !first.SessionId.SequenceEqual(second.SessionId) ||
            !first.CipherSuites.SequenceEqual(second.CipherSuites) ||
            !first.CompressionMethods.SequenceEqual(second.CompressionMethods))
        {
            return false;
        }

        return ExtensionsKept(first.Extensions, second.Extensions, fromFirst: true) &&
            ExtensionsKept(second.Extensions, first.Extensions, fromFirst: false);
    }

    private static bool ExtensionsKept(ReadOnlySpan<byte> source, ReadOnlySpan<byte> other, bool fromFirst)
    {
        TlsReader reader = new(source);
        while (reader.Remaining > 0)
        {
            if (!reader.TryReadUInt16(out ushort type) || !reader.TryReadVector16(out ReadOnlySpan<byte> body))
            {
                return false;
            }

            if (type is ExtensionType.KeyShare or ExtensionType.Cookie or ExtensionType.Padding)
            {
                continue;
            }

            if (type == ExtensionType.EarlyData)
            {
                if (fromFirst || TryExtension(other, type, out _))
                {
                    continue;
                }

                return false;
            }

            if (type == ExtensionType.PreSharedKey)
            {
                if (!TryExtension(other, type, out ReadOnlySpan<byte> otherBody) || !SamePskIdentities(body, otherBody))
                {
                    return false;
                }

                continue;
            }

            if (!TryExtension(other, type, out ReadOnlySpan<byte> match) || !body.SequenceEqual(match))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SamePskIdentities(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length < 2 || right.Length < 2)
        {
            return false;
        }

        int leftLength = (left[0] << 8) | left[1];
        int rightLength = (right[0] << 8) | right[1];
        return leftLength == rightLength &&
            left.Length >= 2 + leftLength &&
            right.Length >= 2 + rightLength &&
            left[..(2 + leftLength)].SequenceEqual(right[..(2 + rightLength)]);
    }

    private static bool TryReadAlpn(ReadOnlySpan<byte> body, out string[] protocols)
    {
        protocols = [];
        TlsReader reader = new(body);
        if (!reader.TryReadVector16(out ReadOnlySpan<byte> list))
        {
            return false;
        }

        var found = new System.Collections.Generic.List<string>();
        TlsReader items = new(list);
        while (items.Remaining > 0)
        {
            if (!items.TryReadVector8(out ReadOnlySpan<byte> protocol) || protocol.IsEmpty)
            {
                return false;
            }

            found.Add(System.Text.Encoding.ASCII.GetString(protocol));
        }

        protocols = found.ToArray();
        return true;
    }

    private static bool ContainsCipher(ReadOnlySpan<byte> offered, ushort cipher)
    {
        for (int i = 0; i + 1 < offered.Length; i += 2)
        {
            if (((offered[i] << 8) | offered[i + 1]) == cipher)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(ReadOnlySpan<ushort> values, ushort wanted)
    {
        foreach (ushort value in values)
        {
            if (value == wanted)
            {
                return true;
            }
        }

        return false;
    }

    private static bool NamesMatch(string? left, string? right)
    {
        return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);
    }

    private static byte[] VersionExtension() => [0x03, 0x04];

    // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
    private static byte[] GroupOnly(ushort group) => [(byte)(group >> 8), (byte)group];

    private static byte[] KeyShareEntry(ushort group, ReadOnlySpan<byte> share)
    {
        var body = new byte[4 + share.Length];
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        body[0] = (byte)(group >> 8);
        body[1] = (byte)group;
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        body[2] = (byte)(share.Length >> 8);
        body[3] = (byte)share.Length;
        share.CopyTo(body.AsSpan(4));
        return body;
    }

    private static byte[] ServerHelloMessage(byte[] random, byte[] session, ushort cipher, byte[] version, byte[] keyShare, bool psk)
    {
        var extensions = new System.Collections.Generic.List<byte>();
        AddExtension(extensions, ExtensionType.SupportedVersions, version);
        AddExtension(extensions, ExtensionType.KeyShare, keyShare);
        if (psk)
        {
            AddExtension(extensions, ExtensionType.PreSharedKey, [0, 0]);
        }

        var body = new System.Collections.Generic.List<byte> { 2, 0, 0, 0, 3, 3 };
        body.AddRange(random);
        body.Add((byte)session.Length);
        body.AddRange(session);
        body.Add((byte)(cipher >> 8));
        body.Add((byte)cipher);
        body.Add(0);
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        body.Add((byte)(extensions.Count >> 8));
        body.Add((byte)extensions.Count);
        body.AddRange(extensions);
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        body[1] = (byte)((body.Count - 4) >> 16);
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        body[2] = (byte)((body.Count - 4) >> 8);
        body[3] = (byte)(body.Count - 4);
        return body.ToArray();
    }

    private static byte[] EncryptedExtensionsMessage(string? alpn)
    {
        if (alpn is null)
        {
            return [8, 0, 0, 2, 0, 0];
        }

        byte[] name = System.Text.Encoding.ASCII.GetBytes(alpn);
        var extensions = new System.Collections.Generic.List<byte>();
        var body = new byte[2 + 1 + name.Length];
        body[1] = (byte)(1 + name.Length);
        body[2] = (byte)name.Length;
        name.CopyTo(body.AsSpan(3));
        AddExtension(extensions, ExtensionType.ApplicationLayerProtocolNegotiation, body);
        var message = new System.Collections.Generic.List<byte> { 8, 0, 0, 0 };
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        message.Add((byte)(extensions.Count >> 8));
        message.Add((byte)extensions.Count);
        message.AddRange(extensions);
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        message[1] = (byte)((message.Count - 4) >> 16);
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        message[2] = (byte)((message.Count - 4) >> 8);
        message[3] = (byte)(message.Count - 4);
        return message.ToArray();
    }

    private static byte[] CertificateRequestMessage()
    {
        return
        [
            13, 0, 0, 11,
            0,
            0, 8,
            0, 13, 0, 4, 0, 2, 4, 3,
        ];
    }

    private static byte[] CertificateMessage(byte[] der)
    {
        var entry = new byte[3 + der.Length + 2];
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        entry[0] = (byte)(der.Length >> 16);
        entry[1] = (byte)(der.Length >> 8);
        entry[2] = (byte)der.Length;
        der.CopyTo(entry, 3);
        var message = new byte[8 + entry.Length];
        message[0] = 11;
        int body = 4 + entry.Length;
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        message[1] = (byte)(body >> 16);
        message[2] = (byte)(body >> 8);
        message[3] = (byte)body;
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        message[5] = (byte)(entry.Length >> 16);
        message[6] = (byte)(entry.Length >> 8);
        message[7] = (byte)entry.Length;
        entry.CopyTo(message, 8);
        return message;
    }

    private static byte[] CertificateVerifyMessage(ushort scheme, byte[] signature)
    {
        var message = new byte[8 + signature.Length];
        message[0] = 15;
        int body = 4 + signature.Length;
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        message[2] = (byte)(body >> 8);
        message[3] = (byte)body;
        message[4] = (byte)(scheme >> 8);
        message[5] = (byte)scheme;
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        message[6] = (byte)(signature.Length >> 8);
        message[7] = (byte)signature.Length;
        signature.CopyTo(message.AsSpan(8));
        return message;
    }

    private static byte[] NewSessionTicketMessage(uint lifetime, byte[] ageAdd, byte[] nonce, byte[] ticket)
    {
        int body = 4 + 4 + 1 + nonce.Length + 2 + ticket.Length + 2;
        var message = new byte[4 + body];
        message[0] = 4;
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        message[1] = (byte)(body >> 16);
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        message[2] = (byte)(body >> 8);
        message[3] = (byte)body;
        WriteUint(message, 4, lifetime);
        ageAdd.CopyTo(message.AsSpan(8));
        message[12] = (byte)nonce.Length;
        nonce.CopyTo(message.AsSpan(13));
        int at = 13 + nonce.Length;
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        message[at] = (byte)(ticket.Length >> 8);
        message[at + 1] = (byte)ticket.Length;
        ticket.CopyTo(message.AsSpan(at + 2));
        return message;
    }

    private static byte[] WriteTicket(ushort cipher, byte[] psk, string? serverName, string? alpn, long issued, uint lifetime, byte[] leafHash)
    {
        byte[] name = System.Text.Encoding.ASCII.GetBytes(serverName ?? string.Empty);
        byte[] protocol = System.Text.Encoding.ASCII.GetBytes(alpn ?? string.Empty);
        var plain = new byte[2 + 1 + psk.Length + 2 + name.Length + 1 + protocol.Length + 8 + 4 + 32];
        int at = 0;
        plain[at++] = (byte)(cipher >> 8);
        plain[at++] = (byte)cipher;
        plain[at++] = (byte)psk.Length;
        psk.CopyTo(plain.AsSpan(at));
        at += psk.Length;
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        plain[at++] = (byte)(name.Length >> 8);
        plain[at++] = (byte)name.Length;
        name.CopyTo(plain.AsSpan(at));
        at += name.Length;
        plain[at++] = (byte)protocol.Length;
        protocol.CopyTo(plain.AsSpan(at));
        at += protocol.Length;
        for (int i = 7; i >= 0; i--)
        {
            plain[at++] = (byte)(issued >> (8 * i));
        }

        WriteUint(plain, at, lifetime);
        at += 4;
        leafHash.CopyTo(plain.AsSpan(at));
        return plain;
    }

    private static bool TryReadTicket(ReadOnlySpan<byte> plain, out TicketView ticket)
    {
        ticket = default;
        TlsReader reader = new(plain);
        if (!reader.TryReadUInt16(out ushort cipher) || !reader.TryReadVector8(out ReadOnlySpan<byte> psk) || psk.IsEmpty)
        {
            return false;
        }

        if (!reader.TryReadVector16(out ReadOnlySpan<byte> name) || !reader.TryReadVector8(out ReadOnlySpan<byte> alpn))
        {
            return false;
        }

        if (!reader.TryReadBytes(8, out ReadOnlySpan<byte> issuedBytes) || !reader.TryReadUInt32(out uint lifetime) || !reader.TryReadBytes(32, out ReadOnlySpan<byte> leaf) || reader.Remaining != 0)
        {
            return false;
        }

        long issued = 0;
        foreach (byte part in issuedBytes)
        {
            issued = (issued << 8) | part;
        }

        ticket = new TicketView(cipher, psk.ToArray(), name.IsEmpty ? null : System.Text.Encoding.ASCII.GetString(name), alpn.IsEmpty ? null : System.Text.Encoding.ASCII.GetString(alpn), issued, lifetime, leaf.ToArray());
        return true;
    }

    private static void WriteServerRecord(TlsOutput output, ReadOnlySpan<byte> handshake)
    {
        var record = new byte[5 + handshake.Length];
        record[0] = 22;
        record[1] = 3;
        record[2] = 3;
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        record[3] = (byte)(handshake.Length >> 8);
        record[4] = (byte)handshake.Length;
        handshake.CopyTo(record.AsSpan(5));
        output.AddBytes(record);
    }

    private static void AddExtension(System.Collections.Generic.List<byte> destination, ushort type, byte[] body)
    {
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        destination.Add((byte)(type >> 8));
        destination.Add((byte)type);
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        destination.Add((byte)(body.Length >> 8));
        destination.Add((byte)body.Length);
        destination.AddRange(body);
    }

    private static void WriteUint(byte[] destination, int at, uint value)
    {
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        destination[at] = (byte)(value >> 24);
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        destination[at + 1] = (byte)(value >> 16);
        // Stryker disable once bitwise: this byte matches for a non-negative value under the shift width
        destination[at + 2] = (byte)(value >> 8);
        destination[at + 3] = (byte)value;
    }

    private readonly struct TicketView
    {
        internal TicketView(ushort cipher, byte[] psk, string? serverName, string? alpn, long issued, uint lifetime, byte[] leafHash)
        {
            Cipher = cipher;
            Psk = psk;
            ServerName = serverName;
            Alpn = alpn;
            Issued = issued;
            Lifetime = lifetime;
            LeafHash = leafHash;
        }

        internal ushort Cipher { get; }

        internal byte[] Psk { get; }

        internal string? ServerName { get; }

        internal string? Alpn { get; }

        internal long Issued { get; }

        internal uint Lifetime { get; }

        internal byte[] LeafHash { get; }
    }
}

internal static class CertificateKeys
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<X509Certificate2, ECDsa> EcdsaKeys = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<X509Certificate2, RSA> RsaKeys = new();

    internal static ECDsa? Ecdsa(X509Certificate2 certificate)
    {
        if (EcdsaKeys.TryGetValue(certificate, out ECDsa? cached))
        {
            return cached;
        }

        ECDsa? key = certificate.GetECDsaPrivateKey();
        if (key is null)
        {
            return null;
        }

        EcdsaKeys.Add(certificate, key);
        return key;
    }

    internal static RSA? Rsa(X509Certificate2 certificate)
    {
        if (RsaKeys.TryGetValue(certificate, out RSA? cached))
        {
            return cached;
        }

        RSA? key = certificate.GetRSAPrivateKey();
        if (key is null)
        {
            return null;
        }

        RsaKeys.Add(certificate, key);
        return key;
    }
}
