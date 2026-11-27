using System;
using System.Buffers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Tls;

internal sealed partial class TlsEngine
{
    internal int PlaintextAvailable => _plaintextLength - _plaintextOffset;

    internal ushort SelectedCipher => _selectedCipher;

    internal bool IsMutuallyAuthenticated => _sentClientCertificate || _clientAuthenticated;

    internal X509Certificate2? RemoteCertificate
    {
        get
        {
            byte[]? encoded = _role == TlsRole.Server ? _clientLeaf : _leaf;
            if (_remoteCertificate is null && encoded is not null)
            {
                _remoteCertificate = X509CertificateLoader.LoadCertificate(encoded);
            }

            return _remoteCertificate;
        }
    }

    internal X509Chain? ServerChain => _serverCheck?.Chain;

    internal void ReleaseServerCheck()
    {
        _serverCheck?.Dispose();
        _serverCheck = null;
    }

    internal int ReadPlaintext(Span<byte> destination)
    {
        int available = PlaintextAvailable;
        int count = available < destination.Length ? available : destination.Length;
        if (count == 0 || _plaintext is null)
        {
            return 0;
        }

        _plaintext.AsSpan(_plaintextOffset, count).CopyTo(destination);
        _plaintextOffset += count;
        if (_plaintextOffset == _plaintextLength)
        {
            ArrayPool<byte>.Shared.Return(_plaintext, clearArray: true);
            _plaintext = null;
            _plaintextLength = 0;
            _plaintextOffset = 0;
        }

        return count;
    }

    internal int ProtectApplication(ReadOnlySpan<byte> plaintext, Span<byte> destination)
    {
        if (State != TlsEngineState.Connected || _write is null)
        {
            throw new InvalidOperationException("The handshake is not complete.");
        }

        if (plaintext.Length > TlsRecordProtector.MaxPlaintext)
        {
            throw new TlsRecordException(TlsRecordAlert.RecordOverflow);
        }

        return _write.Protect(23, plaintext, destination);
    }

    internal int ProtectAlert(byte level, byte description, Span<byte> destination)
    {
        if (_write is null)
        {
            Span<byte> alert = stackalloc byte[7];
            alert[0] = 21;
            alert[1] = 3;
            alert[2] = 3;
            alert[3] = 0;
            alert[4] = 2;
            alert[5] = level;
            alert[6] = description;
            alert.CopyTo(destination);
            return alert.Length;
        }

        Span<byte> plain = stackalloc byte[2];
        plain[0] = level;
        plain[1] = description;
        return _write.Protect(21, plain, destination);
    }

    internal int ProtectKeyUpdate(byte request, Span<byte> destination)
    {
        Span<byte> message = stackalloc byte[5];
        message[0] = (byte)HandshakeType.KeyUpdate;
        message[3] = 1;
        message[4] = request;
        int written = _write!.Protect(22, message, destination);
        RotateTraffic(read: false);
        KeyUpdateResponseNeeded = false;
        return written;
    }

    internal bool WriteKeyUpdateDue => _write is not null && _write.KeyUpdateDue;

    internal void WriteKeyLog(ITlsKeyLogSink sink)
    {
        ReadOnlySpan<byte> random = _role == TlsRole.Client ? _clientRandom : _loggedRandom;
        if (random.IsEmpty || _clientApplication is null || _serverApplication is null || _exporter is null)
        {
            return;
        }

        sink.Write("CLIENT_TRAFFIC_SECRET_0 " + ToHex(random) + " " + ToHex(_clientApplication.Span));
        sink.Write("SERVER_TRAFFIC_SECRET_0 " + ToHex(random) + " " + ToHex(_serverApplication.Span));
        sink.Write("EXPORTER_SECRET " + ToHex(random) + " " + ToHex(_exporter.Span));
    }

    internal void ExportKeyingMaterial(string label, ReadOnlySpan<byte> context, Span<byte> destination)
    {
        if (_exporter is null || State != TlsEngineState.Connected)
        {
            throw new InvalidOperationException("The handshake is not complete.");
        }

        HashAlgorithmName hash = _selectedCipher == 0 ? HashAlgorithmName.SHA256 : HashOf(_selectedCipher);
        TlsKeySchedule.ExportKeyingMaterial(hash, _exporter.Span, System.Text.Encoding.ASCII.GetBytes(label), context, destination);
    }

    private void OnKeyUpdate(int bodyLength, TlsOutput output)
    {
        if (!HandshakeMessages.TryReadKeyUpdate(Message(HandshakeType.KeyUpdate, bodyLength), out byte request, out HandshakeAlert alert))
        {
            Fail(output, (byte)alert);
            return;
        }

        if (_keyUpdates >= MaxKeyUpdates)
        {
            Fail(output, (byte)TlsRecordAlert.UnexpectedMessage);
            return;
        }

        _keyUpdates++;
        RotateTraffic(read: true);
        if (request == 1)
        {
            KeyUpdateResponseNeeded = true;
        }

        _endOfRecord = true;
        output.AddEvent(TlsEventKind.KeyUpdateRequired, request);
    }

    internal bool TryCopyTrafficSecret(bool application, bool client, Span<byte> destination)
    {
        SecretBuffer? source = application
            ? client ? _clientApplication : _serverApplication
            : client ? _clientHandshakeSecret : _serverHandshakeSecret;
        if (source is null || destination.Length < source.Length)
        {
            return false;
        }

        source.Span.CopyTo(destination[..source.Length]);
        return true;
    }

    private void RememberTrafficSecrets(ReadOnlySpan<byte> client, ReadOnlySpan<byte> server, ReadOnlySpan<byte> exporter)
    {
        _clientApplication?.Dispose();
        _serverApplication?.Dispose();
        _exporter?.Dispose();
        _clientApplication = new SecretBuffer(client.Length);
        _serverApplication = new SecretBuffer(server.Length);
        _exporter = new SecretBuffer(exporter.Length);
        client.CopyTo(_clientApplication.Span);
        server.CopyTo(_serverApplication.Span);
        exporter.CopyTo(_exporter.Span);
    }

    private bool TryRememberSelectedProtocol(ReadOnlySpan<byte> extensions, out byte alert)
    {
        alert = 0;
        if (!TryExtension(extensions, ExtensionType.ApplicationLayerProtocolNegotiation, out ReadOnlySpan<byte> body))
        {
            return true;
        }

        // protocol_name_list is one uint16 length and exactly one name. A second name, or a name
        // this client did not offer, is illegal_parameter (RFC 7301).
        if (body.Length < 4)
        {
            alert = (byte)HandshakeAlert.DecodeError;
            return false;
        }

        int listLength = (body[0] << 8) | body[1];
        int nameLength = body[2];
        if (listLength != body.Length - 2 || nameLength == 0 || nameLength != listLength - 1)
        {
            alert = (byte)HandshakeAlert.IllegalParameter;
            return false;
        }

        string selected = System.Text.Encoding.ASCII.GetString(body.Slice(3, nameLength));
        if (ApplicationProtocols is not { Length: > 0 })
        {
            alert = (byte)HandshakeAlert.UnsupportedExtension;
            return false;
        }

        foreach (string offered in ApplicationProtocols)
        {
            if (offered == selected)
            {
                _selectedAlpn = selected;
                return true;
            }
        }

        alert = (byte)HandshakeAlert.IllegalParameter;
        return false;
    }

    private void AppendPlaintext(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.IsEmpty)
        {
            return;
        }

        if (_plaintext is null || _plaintextLength + plaintext.Length > _plaintext.Length)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(TlsRecordProtector.MaxPlaintext);
            if (_plaintext is not null)
            {
                _plaintext.AsSpan(_plaintextOffset, PlaintextAvailable).CopyTo(rented);
                _plaintextLength = PlaintextAvailable;
                _plaintextOffset = 0;
                ArrayPool<byte>.Shared.Return(_plaintext, clearArray: true);
            }

            _plaintext = rented;
        }

        plaintext.CopyTo(_plaintext.AsSpan(_plaintextLength));
        _plaintextLength += plaintext.Length;
    }

    private void RotateTraffic(bool read)
    {
        SecretBuffer current = read
            ? (_role == TlsRole.Client ? _serverApplication! : _clientApplication!)
            : (_role == TlsRole.Client ? _clientApplication! : _serverApplication!);
        HashAlgorithmName hash = _selectedCipher == 0 ? HashAlgorithmName.SHA256 : HashOf(_selectedCipher);
        int hashLength = TlsHkdf.HashLength(hash);
        Span<byte> next = stackalloc byte[48];
        TlsKeySchedule.NextTrafficSecret(hash, current.Span, next[..hashLength]);
        next[..hashLength].CopyTo(current.Span);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(next);
        Suite(_selectedCipher == 0 ? (ushort)0x1301 : _selectedCipher, out _, out TlsAeadSuite aead, out int keyLength);
        Span<byte> key = stackalloc byte[32];
        Span<byte> iv = stackalloc byte[12];
        TlsKeySchedule.DeriveTrafficKeys(hash, current.Span, key[..keyLength], iv);
        if (read)
        {
            _read?.Dispose();
            _read = new TlsRecordProtector(aead, key[..keyLength], iv);
        }
        else
        {
            _write?.Dispose();
            _write = new TlsRecordProtector(aead, key[..keyLength], iv);
        }

        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(key);
        // Stryker disable once statement: the temporary is not read after the bytes are copied
        CryptographicOperations.ZeroMemory(iv);
    }

    private static string ToHex(ReadOnlySpan<byte> value)
    {
        return Convert.ToHexString(value);
    }
}
