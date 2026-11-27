using System;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Tls;

internal enum TlsRole
{
    Client = 0,
    Server = 1,
}

internal enum TlsEngineState
{
    Start = 0,
    WaitServerHelloOrHrr = 1,
    WaitCertificateRequestOrCertificate = 11,
    WaitEncryptedExtensions = 2,
    WaitCertificate = 3,
    WaitCertificateDecision = 4,
    WaitCertificateVerify = 5,
    WaitClientHello = 6,
    WaitClientHelloDecision = 7,
    WaitClientCertificate = 12,
    WaitClientCertificateVerify = 13,
    WaitFinished = 8,
    Connected = 9,
    Closed = 10,
}

internal enum TlsEventKind
{
    ClientHelloReceived = 1,
    ServerCertificateReceived = 2,
    HandshakeCompleted = 3,
    PeerAlert = 4,
    NeedMoreData = 5,
    TicketReceived = 6,
    KeyUpdateRequired = 7,
}

internal readonly struct TlsEvent
{
    internal TlsEventKind Kind { get; init; }

    internal byte Alert { get; init; }
}

/// <summary>Bytes to write and events from one <see cref="TlsEngine.Advance"/> or Resume call.</summary>
internal sealed class TlsOutput
{
    private readonly byte[] _bytes = new byte[16384];
    private readonly TlsEvent[] _events = new TlsEvent[8];
    private int _length;
    private int _eventCount;

    internal ReadOnlySpan<byte> Bytes => _bytes.AsSpan(0, _length);

    internal int EventCount => _eventCount;

    internal TlsEvent Event(int index) => _events[index];

    internal void Clear()
    {
        _length = 0;
        _eventCount = 0;
    }

    internal void AddBytes(ReadOnlySpan<byte> bytes)
    {
        if (_length + bytes.Length > _bytes.Length)
        {
            throw new TlsProtocolViolation("The engine produced more output than the stub buffer holds.");
        }

        bytes.CopyTo(_bytes.AsSpan(_length));
        _length += bytes.Length;
    }

    internal void AddEvent(TlsEventKind kind, byte alert = 0)
    {
        if (_eventCount == _events.Length)
        {
            throw new TlsProtocolViolation("The engine produced more events than the stub buffer holds.");
        }

        _events[_eventCount++] = new TlsEvent { Kind = kind, Alert = alert };
    }
}

/// <summary>
/// Sans-IO handshake driver. The client completes a TLS 1.3 handshake. The server answers one after
/// <see cref="TlsEventKind.ClientHelloReceived"/>.
/// </summary>
internal sealed partial class TlsEngine : IDisposable
{
    internal const int MaxEmptyRecords = 32;

    internal const int MaxKeyUpdates = 32;
    internal const int DefaultMaxBufferedBytes = HandshakeFraming.DefaultMaxMessageBytes + TlsRecordProtector.HeaderLength;

    private readonly TlsRole _role;
    private readonly int _maxBufferedBytes;
    private readonly byte[] _stash;
    private readonly byte[] _body;
    private readonly HandshakeReassembler _handshake;
    private int _stashLength;
    private int _pendingHandshake;
    private int _emptyRecords;

    private int _keyUpdates;
    private int _acceptedChangeCipherSpec;
    private bool _paused;
    private bool _endOfRecord;
    private bool _alertsEncrypted;
    private bool _ignoreEarlyData;
    private int _earlyDataSkipped;
    private const int MaxIgnoredEarlyData = 16384;
    private byte[]? _plaintext;
    private int _plaintextLength;
    private int _plaintextOffset;
    private SecretBuffer? _exporter;
    private TlsCertificateCheck? _serverCheck;
    private X509Certificate2? _remoteCertificate;
    private bool _sentClientCertificate;
    private bool _clientAuthenticated;
    private byte[]? _loggedRandom;
    internal bool KeyUpdateResponseNeeded;

    internal bool SendKeyUpdate;

    internal byte LastLocalAlert { get; private set; }

    private TlsAlgorithmPolicy _algorithmPolicy;

    internal TlsAlgorithmPolicy AlgorithmPolicy
    {
        get => _algorithmPolicy;
        set
        {
            _algorithmPolicy = value;
            if (value == TlsAlgorithmPolicy.FipsApprovedOnly && _shareGroup == (ushort)NamedGroup.X25519)
            {
                _shareGroup = (ushort)NamedGroup.Secp256r1;
            }
        }
    }
    private readonly IEntropySource _entropy;
    private readonly string? _serverName;
    private readonly ITlsSessionCache? _cache;
    private TranscriptStore? _transcript;
    private TlsKeySchedule? _schedule;
    private bool _disposed;

    internal TlsEngine(
        TlsRole role,
        int maxHandshakeMessage = HandshakeFraming.DefaultMaxMessageBytes,
        int maxBufferedBytes = DefaultMaxBufferedBytes,
        IEntropySource? entropy = null,
        string? serverName = null,
        ITlsSessionCache? cache = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferedBytes);
        _role = role;
        _maxBufferedBytes = maxBufferedBytes;
        _entropy = entropy ?? new SystemEntropy();
        _serverName = serverName;
        _cache = cache;
        _stash = new byte[Math.Max(maxBufferedBytes, TlsRecordProtector.HeaderLength + 1)];
        _body = new byte[maxHandshakeMessage];
        _handshake = new HandshakeReassembler(maxHandshakeMessage);
        if (role == TlsRole.Client)
        {
            _transcript = new TranscriptStore(System.Security.Cryptography.HashAlgorithmName.SHA256);
            _schedule = new TlsKeySchedule(System.Security.Cryptography.HashAlgorithmName.SHA256);
            State = TlsEngineState.Start;
        }
        else
        {
            State = TlsEngineState.WaitClientHello;
        }
    }

    internal long UnixTimeSeconds { get; set; }

    internal TlsEngineState State { get; private set; }

    internal void Advance(ReadOnlySpan<byte> input, out int consumed, TlsOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();
        consumed = 0;
        if (State == TlsEngineState.Closed)
        {
            return;
        }

        if (_paused)
        {
            return;
        }

        if (_role == TlsRole.Client && State == TlsEngineState.Start)
        {
            WriteClientHello(output, helloRetry: false);
            State = TlsEngineState.WaitServerHelloOrHrr;
        }

        bool needMore = false;
        int offset = 0;
        while (State != TlsEngineState.Closed)
        {
            if (!TryFillRecord(input, ref offset, out int recordLength, out bool incomplete, out byte alert))
            {
                Fail(output, alert);
                break;
            }

            if (incomplete)
            {
                needMore = _stashLength > 0 || offset < input.Length;
                break;
            }

            byte contentType = _stash[0];
            if (recordLength == 0)
            {
                Consume(recordLength);
                if (contentType != 23)
                {
                    Fail(output, (byte)TlsRecordAlert.UnexpectedMessage);
                    break;
                }

                _emptyRecords++;
                if (_emptyRecords > MaxEmptyRecords)
                {
                    Fail(output, (byte)TlsRecordAlert.UnexpectedMessage);
                    break;
                }

                continue;
            }

            _emptyRecords = 0;
            if (contentType == 20)
            {
                bool beforeHello = _role == TlsRole.Server && _firstHello is null;
                bool accepted = !beforeHello &&
                    _pendingHandshake == 0 &&
                    State != TlsEngineState.Connected &&
                    TlsPlaintextRecord.TryAcceptChangeCipherSpec(_stash.AsSpan(0, TlsRecordProtector.HeaderLength + recordLength), ref _acceptedChangeCipherSpec);
                Consume(recordLength);
                if (!accepted)
                {
                    Fail(output, (byte)TlsRecordAlert.UnexpectedMessage);
                    break;
                }

                continue;
            }

            if (contentType == 21)
            {
                if (_read != null)
                {
                    Consume(recordLength);
                    Fail(output, (byte)TlsRecordAlert.UnexpectedMessage);
                    break;
                }

                byte description = recordLength == 2 ? _stash[6] : (byte)0;
                Consume(recordLength);
                if (recordLength != 2)
                {
                    Fail(output, (byte)HandshakeAlert.DecodeError);
                    break;
                }

                output.AddEvent(TlsEventKind.PeerAlert, description);
                State = TlsEngineState.Closed;
                break;
            }

            if (contentType == 22)
            {
                if (_read != null)
                {
                    Consume(recordLength);
                    Fail(output, (byte)TlsRecordAlert.UnexpectedMessage);
                    break;
                }

                bool ok = AppendHandshake(_stash.AsSpan(TlsRecordProtector.HeaderLength, recordLength), output) && DrainHandshake(output);
                Consume(recordLength);
                if (!ok || _paused || State == TlsEngineState.Closed)
                {
                    break;
                }

                continue;
            }

            if (contentType == 23)
            {
                if (_read == null)
                {
                    Consume(recordLength);
                    if (State != TlsEngineState.Connected)
                    {
                        Fail(output, (byte)TlsRecordAlert.UnexpectedMessage);
                    }

                    continue;
                }

                RecordResult result = _read.TryUnprotect(_stash.AsSpan(0, TlsRecordProtector.HeaderLength + recordLength));
                if (!result.Success)
                {
                    if (result.Alert == TlsRecordAlert.BadRecordMac &&
                        _ignoreEarlyData &&
                        State != TlsEngineState.Connected &&
                        _earlyDataSkipped + recordLength <= MaxIgnoredEarlyData)
                    {
                        _earlyDataSkipped += recordLength;
                        Consume(recordLength);
                        continue;
                    }

                    Consume(recordLength);
                    Fail(output, (byte)result.Alert);
                    break;
                }

                _ignoreEarlyData = false;
                if (result.ContentLength == 0 && result.ContentType is 20 or 21 or 22)
                {
                    Consume(recordLength);
                    Fail(output, (byte)TlsRecordAlert.UnexpectedMessage);
                    break;
                }

                if (result.ContentType == 21)
                {
                    if (result.ContentLength != 2)
                    {
                        Consume(recordLength);
                        Fail(output, (byte)HandshakeAlert.DecodeError);
                        break;
                    }

                    byte description = _stash[TlsRecordProtector.HeaderLength + 1];
                    Consume(recordLength);
                    output.AddEvent(TlsEventKind.PeerAlert, description);
                    State = TlsEngineState.Closed;
                    _paused = false;
                    break;
                }

                if (result.ContentType == 22)
                {
                    bool ok = AppendHandshake(_stash.AsSpan(TlsRecordProtector.HeaderLength, result.ContentLength), output) && DrainHandshake(output);
                    Consume(recordLength);
                    if (!ok || _paused || State == TlsEngineState.Closed)
                    {
                        break;
                    }

                    continue;
                }

                if (result.ContentType != 23 || State != TlsEngineState.Connected)
                {
                    Consume(recordLength);
                    Fail(output, (byte)TlsRecordAlert.UnexpectedMessage);
                    break;
                }

                AppendPlaintext(_stash.AsSpan(TlsRecordProtector.HeaderLength, result.ContentLength));
                Consume(recordLength);
                if (result.ContentLength > 0)
                {
                    break;
                }

                continue;
            }

            Consume(recordLength);
            Fail(output, (byte)TlsRecordAlert.UnexpectedMessage);
        }

        if (needMore && State != TlsEngineState.Closed && !_paused)
        {
            output.AddEvent(TlsEventKind.NeedMoreData);
        }

        consumed = offset;
    }

    internal void Resume(bool accepted, TlsOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();
        if (!_paused)
        {
            throw new InvalidOperationException("The engine is not waiting for a decision.");
        }

        _paused = false;
        if (!accepted)
        {
            byte alert = State == TlsEngineState.WaitClientHelloDecision ? (byte)40 : (byte)42;
            Fail(output, alert);
            return;
        }

        if (State == TlsEngineState.WaitClientHelloDecision)
        {
            if (_selection is null)
            {
                Fail(output, 80);
                return;
            }

            SendServerFlight(output);
            return;
        }

        State = TlsEngineState.WaitCertificateVerify;
        DrainHandshake(output);
    }

    private bool TryFillRecord(ReadOnlySpan<byte> input, ref int offset, out int recordLength, out bool incomplete, out byte alert)
    {
        recordLength = 0;
        incomplete = false;
        alert = 0;
        if (!Pull(input, ref offset, TlsRecordProtector.HeaderLength))
        {
            incomplete = true;
            return true;
        }

        int maxLength = _read is null ? TlsRecordProtector.MaxPlaintext : TlsRecordProtector.MaxCiphertext;
        if (!TlsPlaintextRecord.TryReadHeader(_stash, out _, out _, out recordLength, out TlsRecordAlert headerAlert, maxLength))
        {
            alert = (byte)headerAlert;
            return false;
        }

        int total = TlsRecordProtector.HeaderLength + recordLength;
        if (total > _maxBufferedBytes)
        {
            alert = (byte)TlsRecordAlert.RecordOverflow;
            return false;
        }

        if (!Pull(input, ref offset, total))
        {
            incomplete = true;
            return true;
        }

        return true;
    }

    private bool Pull(ReadOnlySpan<byte> input, ref int offset, int total)
    {
        if (total > _stash.Length)
        {
            return false;
        }

        int need = total - _stashLength;
        if (need <= 0)
        {
            return true;
        }

        int available = input.Length - offset;
        int take = need < available ? need : available;
        if (take > 0)
        {
            input.Slice(offset, take).CopyTo(_stash.AsSpan(_stashLength));
            _stashLength += take;
            offset += take;
        }

        return _stashLength >= total;
    }

    private bool AppendHandshake(ReadOnlySpan<byte> payload, TlsOutput output)
    {
        if (_pendingHandshake + payload.Length > _maxBufferedBytes)
        {
            Fail(output, (byte)TlsRecordAlert.RecordOverflow);
            return false;
        }

        if (!_handshake.TryAppend(payload, out HandshakeAlert alert))
        {
            Fail(output, (byte)alert);
            return false;
        }

        _pendingHandshake += payload.Length;
        return true;
    }

    private bool DrainHandshake(TlsOutput output)
    {
        while (State != TlsEngineState.Closed && !_paused)
        {
            if (!_handshake.TryTake(_body, out HandshakeType type, out int bodyLength, out bool needMore, out HandshakeAlert alert))
            {
                Fail(output, (byte)alert);
                return false;
            }

            if (needMore)
            {
                return true;
            }

            _pendingHandshake -= HandshakeFraming.HeaderLength + bodyLength;
            if (_pendingHandshake < 0)
            {
                _pendingHandshake = 0;
            }

            if (!Accepts(type))
            {
                Fail(output, (byte)TlsRecordAlert.UnexpectedMessage);
                return false;
            }

            Enter(type, bodyLength, output);
            if (_endOfRecord)
            {
                _endOfRecord = false;
                if (State != TlsEngineState.Closed && _handshake.Pending > 0)
                {
                    Fail(output, (byte)TlsRecordAlert.UnexpectedMessage);
                    return false;
                }
            }

            if (State == TlsEngineState.Closed || _paused)
            {
                return State != TlsEngineState.Closed;
            }
        }

        return State != TlsEngineState.Closed;
    }

    private bool Accepts(HandshakeType type)
    {
        if (_role == TlsRole.Client)
        {
            return State switch
            {
                TlsEngineState.WaitServerHelloOrHrr => type == HandshakeType.ServerHello,
                TlsEngineState.WaitEncryptedExtensions => type == HandshakeType.EncryptedExtensions,
                TlsEngineState.WaitCertificateRequestOrCertificate => type is HandshakeType.CertificateRequest or HandshakeType.Certificate,
                TlsEngineState.WaitCertificate => type == HandshakeType.Certificate,
                TlsEngineState.WaitCertificateVerify => type == HandshakeType.CertificateVerify,
                TlsEngineState.WaitFinished => type == HandshakeType.Finished,
                TlsEngineState.Connected => type is HandshakeType.NewSessionTicket or HandshakeType.KeyUpdate,
                _ => false,
            };
        }

        return State switch
        {
            TlsEngineState.WaitClientHello => type == HandshakeType.ClientHello,
            TlsEngineState.WaitClientCertificate => type == HandshakeType.Certificate,
            TlsEngineState.WaitClientCertificateVerify => type == HandshakeType.CertificateVerify,
            TlsEngineState.WaitFinished => type == HandshakeType.Finished,
            TlsEngineState.Connected => type == HandshakeType.KeyUpdate,
            _ => false,
        };
    }

    private void Enter(HandshakeType type, int bodyLength, TlsOutput output)
    {
        if (_role == TlsRole.Client)
        {
            HandleClient(type, bodyLength, output);
            return;
        }

        HandleServer(type, bodyLength, output);
    }

    private void Fail(TlsOutput output, byte description)
    {
        State = TlsEngineState.Closed;
        _paused = false;
        LastLocalAlert = description;
        if (_alertsEncrypted && _write != null)
        {
            Span<byte> plain = stackalloc byte[2];
            plain[0] = 2;
            plain[1] = description;
            Span<byte> encrypted = stackalloc byte[32];
            int written = _write.Protect(21, plain, encrypted);
            output.AddBytes(encrypted[..written]);
            return;
        }

        Span<byte> alert = stackalloc byte[7];
        alert[0] = 21;
        alert[1] = 3;
        alert[2] = 3;
        alert[3] = 0;
        alert[4] = 2;
        alert[5] = 2;
        alert[6] = description;
        output.AddBytes(alert);
    }

    private void Consume(int recordLength)
    {
        int total = TlsRecordProtector.HeaderLength + recordLength;
        int tail = _stashLength - total;
        if (tail > 0)
        {
            Buffer.BlockCopy(_stash, total, _stash, 0, tail);
            _stashLength = tail;
            return;
        }

        _stashLength = 0;
    }
}
