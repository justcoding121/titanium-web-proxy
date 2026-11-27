using System;

namespace Titanium.Tls;

internal enum TlsRole
{
    Client = 0,
    Server = 1,
}

internal enum TlsEngineState
{
    Start = 0,
    WaitServerHello = 1,
    WaitEncryptedExtensions = 2,
    WaitCertificate = 3,
    WaitCertificateDecision = 4,
    WaitCertificateVerify = 5,
    WaitClientHello = 6,
    WaitClientHelloDecision = 7,
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

/// <summary>Bytes to write and events from one <see cref="TlsEngine.Advance"/> or <see cref="TlsEngine.Resume"/>.</summary>
internal sealed class TlsOutput
{
    private readonly byte[] _bytes = new byte[1024];
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
/// Sans-IO handshake driver. The flight bytes are a stub until the client and server machines replace them.
/// The transition table is the one those machines plug into.
/// </summary>
internal sealed class TlsEngine
{
    internal const int MaxEmptyRecords = 32;
    internal const int DefaultMaxBufferedBytes = HandshakeFraming.DefaultMaxMessageBytes + TlsRecordProtector.HeaderLength;

    private readonly TlsRole _role;
    private readonly int _maxBufferedBytes;
    private readonly byte[] _stash;
    private readonly byte[] _body;
    private readonly HandshakeReassembler _handshake;
    private int _stashLength;
    private int _pendingHandshake;
    private int _emptyRecords;
    private int _acceptedChangeCipherSpec;
    private bool _paused;

    internal TlsEngine(TlsRole role, int maxHandshakeMessage = HandshakeFraming.DefaultMaxMessageBytes, int maxBufferedBytes = DefaultMaxBufferedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferedBytes);
        _role = role;
        _maxBufferedBytes = maxBufferedBytes;
        _stash = new byte[Math.Max(maxBufferedBytes, TlsRecordProtector.HeaderLength + 1)];
        _body = new byte[maxHandshakeMessage];
        _handshake = new HandshakeReassembler(maxHandshakeMessage);
        State = role == TlsRole.Client ? TlsEngineState.Start : TlsEngineState.WaitClientHello;
    }

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
            WriteMessages(output, [1]);
            State = TlsEngineState.WaitServerHello;
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

            if (recordLength == 0)
            {
                _emptyRecords++;
                Consume(recordLength);
                if (_emptyRecords > MaxEmptyRecords)
                {
                    Fail(output, (byte)TlsRecordAlert.UnexpectedMessage);
                }

                continue;
            }

            _emptyRecords = 0;
            byte contentType = _stash[0];
            if (contentType == 20)
            {
                bool accepted = State != TlsEngineState.Connected && TlsPlaintextRecord.TryAcceptChangeCipherSpec(_stash.AsSpan(0, TlsRecordProtector.HeaderLength + recordLength), ref _acceptedChangeCipherSpec);
                Consume(recordLength);
                if (!accepted)
                {
                    Fail(output, (byte)TlsRecordAlert.UnexpectedMessage);
                }

                continue;
            }

            if (contentType == 21)
            {
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
                Consume(recordLength);
                if (State != TlsEngineState.Connected)
                {
                    Fail(output, (byte)TlsRecordAlert.UnexpectedMessage);
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
            WriteMessages(output, [2, 8, 11, 15, 20]);
            State = TlsEngineState.WaitFinished;
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

        if (!TlsPlaintextRecord.TryReadHeader(_stash, out _, out _, out recordLength, out TlsRecordAlert headerAlert))
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

            Enter(type, output);
        }

        return State != TlsEngineState.Closed;
    }

    private bool Accepts(HandshakeType type)
    {
        if (_role == TlsRole.Client)
        {
            return State switch
            {
                TlsEngineState.WaitServerHello => type == HandshakeType.ServerHello,
                TlsEngineState.WaitEncryptedExtensions => type == HandshakeType.EncryptedExtensions,
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
            TlsEngineState.WaitFinished => type == HandshakeType.Finished,
            TlsEngineState.Connected => type == HandshakeType.KeyUpdate,
            _ => false,
        };
    }

    private void Enter(HandshakeType type, TlsOutput output)
    {
        if (_role == TlsRole.Server && type == HandshakeType.ClientHello)
        {
            State = TlsEngineState.WaitClientHelloDecision;
            _paused = true;
            output.AddEvent(TlsEventKind.ClientHelloReceived);
            return;
        }

        if (type == HandshakeType.ServerHello)
        {
            State = TlsEngineState.WaitEncryptedExtensions;
        }
        else if (type == HandshakeType.EncryptedExtensions)
        {
            State = TlsEngineState.WaitCertificate;
        }
        else if (type == HandshakeType.Certificate)
        {
            State = TlsEngineState.WaitCertificateDecision;
            _paused = true;
            output.AddEvent(TlsEventKind.ServerCertificateReceived);
        }
        else if (type == HandshakeType.CertificateVerify)
        {
            State = TlsEngineState.WaitFinished;
        }
        else if (type == HandshakeType.Finished)
        {
            if (_role == TlsRole.Client)
            {
                WriteMessages(output, [20]);
            }

            State = TlsEngineState.Connected;
            output.AddEvent(TlsEventKind.HandshakeCompleted);
        }
        else if (type == HandshakeType.NewSessionTicket)
        {
            output.AddEvent(TlsEventKind.TicketReceived);
        }
        else if (type == HandshakeType.KeyUpdate)
        {
            output.AddEvent(TlsEventKind.KeyUpdateRequired);
        }
    }

    private void Fail(TlsOutput output, byte description)
    {
        State = TlsEngineState.Closed;
        _paused = false;
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

    private static void WriteMessages(TlsOutput output, ReadOnlySpan<byte> types)
    {
        int payload = types.Length * 4;
        Span<byte> record = stackalloc byte[64];
        record[0] = 22;
        record[1] = 3;
        record[2] = 3;
        record[3] = (byte)(payload >> 8);
        record[4] = (byte)payload;
        for (int i = 0; i < types.Length; i++)
        {
            int at = 5 + (i * 4);
            record[at] = types[i];
        }

        output.AddBytes(record[..(5 + payload)]);
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
