using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Titanium.Tls;

namespace Titanium.Quic;

internal sealed partial class QuicConnectionCore : IDisposable
{
    private readonly byte[] _secretScratch = new byte[32];

    private readonly QuicLossRecovery _loss = new(new QuicNewReno());

    private DateTimeOffset _now;

    private TlsEngine? _engine;

    private X509Certificate2? _certificate;

    private QuicPacketProtector? _initialSend;

    private QuicPacketProtector? _initialRecv;

    private QuicPacketProtector? _handshakeSend;

    private QuicPacketProtector? _handshakeRecv;

    private QuicPacketProtector? _applicationSend;

    private QuicPacketProtector? _applicationRecv;

    private readonly QuicIdentity _ids = new();

    private readonly QuicFlow _flow = new();

    private readonly QuicPathMtu _mtu = new();

    private ulong? _probeNumber;

    private readonly List<(PathInfo Path, byte[] Data)> _challenges = [];

    private byte[] _localId = [];

    private byte[] _remoteId = [];

    private byte[] _originalDestinationId = [];

    private byte[] _initialToken = [];

    private byte[]? _initialFlight;

    private byte[]? _expectedRetryToken;

    private byte[]? _peerSourceId;

    private bool _retryArmed;

    private bool _retryAccepted;

    private byte[] _retryToken = [];

    private PathInfo _path;

    private bool _hasPath;

    private bool _client;

    private bool _limitAmplification = true;

    private bool _parametersChecked;

    private bool _disposed;

    private int _recvInitial;

    private int _recvHandshake;

    private int _sentInitial;

    private int _sentHandshake;

    private ulong _pnInitial;

    private ulong _pnHandshake;

    private ulong _pnApplication;

    private ulong? _largestInitial;

    private ulong? _largestHandshake;

    private ulong? _largestApplication;

    internal bool IsClosed { get; private set; }

    internal bool ClosedByStatelessReset { get; private set; }

    internal ulong CloseError { get; private set; }

    internal byte[]? ReceivedInitialToken { get; private set; }

    internal byte[]? IssuedNewToken { get; private set; }

    internal int PeerConnectionIds => _ids.PeerActive;

    internal bool HandshakeComplete => _engine?.State == TlsEngineState.Connected;

    internal byte? ReceivedStreamByte { get; private set; }

    internal bool CorruptNextHandshakePayload { get; set; }

    internal void StartClient(in PathInfo path, ReadOnlySpan<byte> localId, ReadOnlySpan<byte> remoteId, ReadOnlySpan<byte> transportParameters, ReadOnlySpan<byte> token = default)
    {
        _client = true;
        _limitAmplification = false;
        _localId = localId.ToArray();
        _remoteId = remoteId.ToArray();
        _originalDestinationId = remoteId.ToArray();
        _initialToken = token.ToArray();
        _ids.AddIssued(localId);
        _ids.AddPeer(remoteId);
        _path = path;
        _hasPath = true;
        AddReceived(path, 0);
        InstallInitial(remoteId, clientSend: true);
        RememberLocalLimits(transportParameters, fromServer: false);
        _engine = new TlsEngine(TlsRole.Client, serverName: "localhost")
        {
            DatagramTransport = true,
            LocalTransportParameters = transportParameters.ToArray(),
        };
        var output = new TlsOutput();
        _engine.AdvanceDatagram([], output);
        if (_engine.State == TlsEngineState.Closed)
        {
            MarkClosed(CryptoError(_engine.LastLocalAlert), send: false);
            return;
        }

        Emit(output);
    }

    internal void StartServer(ReadOnlySpan<byte> localId, X509Certificate2 certificate, ReadOnlySpan<byte> transportParameters)
    {
        _localId = localId.ToArray();
        _originalDestinationId = localId.ToArray();
        _ids.AddIssued(localId);
        _certificate = certificate;
        InstallInitial(localId, clientSend: false);
        RememberLocalLimits(transportParameters, fromServer: true);
        _engine = new TlsEngine(TlsRole.Server)
        {
            DatagramTransport = true,
            LocalTransportParameters = transportParameters.ToArray(),
        };
    }

    internal void SendStreamByte(byte value)
    {
        if (!HandshakeComplete || IsClosed || !_hasPath || _applicationSend is null)
        {
            return;
        }

        SendStream(0, [value]);
    }

    internal int SendStream(ulong streamId, ReadOnlySpan<byte> data)
    {
        if (!HandshakeComplete || IsClosed || !_hasPath || _applicationSend is null)
        {
            return 0;
        }

        int queued = _flow.Queue(streamId, data, out _);
        StageReady();
        return queued;
    }

    internal void GrantStreamCredit(ulong streamId, ulong limit)
    {
        if (_applicationSend is null)
        {
            return;
        }

        _flow.Grant(streamId, limit, limit);
        Span<byte> frame = stackalloc byte[32];
        var maxData = new QuicFrame { Kind = QuicFrameKind.MaxData, Limit = limit };
        int count = QuicFrameWriter.Write(in maxData, default, frame);
        if (count > 0)
        {
            SealAndStage(QuicPacketKind.OneRtt, _applicationSend, frame[..count], 0);
        }

        var maxStream = new QuicFrame { Kind = QuicFrameKind.MaxStreamData, StreamId = streamId, Limit = limit };
        count = QuicFrameWriter.Write(in maxStream, default, frame);
        if (count > 0)
        {
            SealAndStage(QuicPacketKind.OneRtt, _applicationSend, frame[..count], 0);
        }
    }

    internal void InjectApplicationPayload(ReadOnlySpan<byte> payload) => TakePayload(QuicPacketKind.OneRtt, payload);

    internal bool StreamSendBlocked => _flow.SendBlocked;

    internal byte[] ReceivedStream => _flow.Received;

    internal int ResetsDelivered => _flow.ResetsDelivered;

    internal int StopsDelivered => _flow.StopsDelivered;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine?.Dispose();
        _engine = null;
        _initialSend?.Dispose();
        _initialRecv?.Dispose();
        _handshakeSend?.Dispose();
        _handshakeRecv?.Dispose();
        _applicationSend?.Dispose();
        _applicationRecv?.Dispose();
        CryptographicOperations.ZeroMemory(_secretScratch);
        for (int i = 0; i < _staged.Count; i++)
        {
            ArrayPool<byte>.Shared.Return(_staged[i].Buffer, clearArray: true);
        }

        _staged.Clear();
    }

    private void AcceptDatagram(ReadOnlySpan<byte> datagram)
    {
        if (!_hasPath || datagram.IsEmpty || IsClosed || _engine is null)
        {
            return;
        }

        if (_client && datagram[0] == 0xF0)
        {
            AcceptRetry(datagram);
            return;
        }

        if (!TryLocate(datagram, out QuicPacketKind kind, out int packetLength, out int packetNumberOffset, out int sourceOffset, out int sourceLength, out int tokenOffset, out int tokenLength))
        {
            if (_ids.MatchesReset(datagram))
            {
                DrainReset();
            }

            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(packetLength);
        byte[] plain = ArrayPool<byte>.Shared.Rent(packetLength);
        try
        {
            datagram[..packetLength].CopyTo(rented);
            if (!TryOpen(kind, rented.AsSpan(0, packetLength), packetNumberOffset, plain, out ulong packetNumber, out int plainLength))
            {
                if (_ids.MatchesReset(datagram))
                {
                    DrainReset();
                }

                return;
            }

            if (kind == QuicPacketKind.Initial && _peerSourceId is null)
            {
                _peerSourceId = datagram.Slice(sourceOffset, sourceLength).ToArray();
                _ids.AddPeer(_peerSourceId);
            }

            if (kind == QuicPacketKind.Initial && _retryArmed && tokenLength == 0)
            {
                SendRetry();
                return;
            }

            if (kind == QuicPacketKind.Initial && _expectedRetryToken is not null)
            {
                ReadOnlySpan<byte> token = datagram.Slice(tokenOffset, tokenLength);
                if (token.Length != _expectedRetryToken.Length || !CryptographicOperations.FixedTimeEquals(token, _expectedRetryToken))
                {
                    return;
                }

                ReceivedInitialToken = token.ToArray();
                _expectedRetryToken = null;
            }

            Remember(kind, packetNumber);
            if (kind == QuicPacketKind.Handshake)
            {
                MarkAddressValidated(_path);
            }

            TakePayload(kind, plain.AsSpan(0, plainLength));
            if (!IsClosed)
            {
                NotePeerPacket();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            ArrayPool<byte>.Shared.Return(plain, clearArray: true);
        }
    }

    private bool TryLocate(
        ReadOnlySpan<byte> datagram,
        out QuicPacketKind kind,
        out int packetLength,
        out int packetNumberOffset,
        out int sourceOffset,
        out int sourceLength,
        out int tokenOffset,
        out int tokenLength)
    {
        kind = default;
        packetLength = 0;
        packetNumberOffset = 0;
        sourceOffset = 0;
        sourceLength = 0;
        tokenOffset = 0;
        tokenLength = 0;
        if ((datagram[0] & 0x40) == 0)
        {
            return false;
        }

        if ((datagram[0] & 0x80) == 0)
        {
            byte[]? owned = _ids.LongestOwnedPrefix(datagram[1..]);
            if (owned is null)
            {
                return false;
            }

            kind = QuicPacketKind.OneRtt;
            packetNumberOffset = 1 + owned.Length;
            packetLength = datagram.Length;
            return packetLength > packetNumberOffset;
        }

        if (datagram.Length < 7)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(datagram[1..]) != 1)
        {
            return false;
        }

        int destinationLength = datagram[5];
        if (destinationLength > QuicDatagramReader.MaxConnectionIdLength || datagram.Length < 6 + destinationLength)
        {
            return false;
        }

        if (!_ids.Owns(datagram.Slice(6, destinationLength)))
        {
            return false;
        }

        int sourceIndex = 6 + destinationLength;
        if (datagram.Length <= sourceIndex)
        {
            return false;
        }

        sourceLength = datagram[sourceIndex];
        sourceOffset = sourceIndex + 1;
        if (sourceLength > QuicDatagramReader.MaxConnectionIdLength || datagram.Length < sourceOffset + sourceLength)
        {
            return false;
        }

        int at = sourceOffset + sourceLength;
        kind = (QuicPacketKind)((datagram[0] >> 4) & 0x03);
        if (kind == QuicPacketKind.Initial)
        {
            if (!QuicVarInt.TryDecode(datagram[at..], out ulong encodedToken, out int tokenUsed)
                || encodedToken > (ulong)(datagram.Length - at - tokenUsed))
            {
                return false;
            }

            tokenOffset = at + tokenUsed;
            tokenLength = (int)encodedToken;
            at += tokenUsed + tokenLength;
        }
        else if (kind != QuicPacketKind.Handshake)
        {
            return false;
        }

        if (!QuicVarInt.TryDecode(datagram[at..], out ulong bodyLength, out int lengthUsed)
            || bodyLength > (ulong)(datagram.Length - at - lengthUsed))
        {
            return false;
        }

        at += lengthUsed;
        packetNumberOffset = at;
        packetLength = at + (int)bodyLength;
        return packetLength <= datagram.Length;
    }

    private bool TryOpen(QuicPacketKind kind, Span<byte> packet, int packetNumberOffset, Span<byte> plaintext, out ulong packetNumber, out int plaintextLength)
    {
        QuicPacketProtector? protector = Protector(kind, send: false);
        if (protector is null
            || !protector.Open(packet, packetNumberOffset, Largest(kind), out packetNumber, plaintext, out plaintextLength))
        {
            packetNumber = 0;
            plaintextLength = 0;
            return false;
        }

        ulong? seen = Largest(kind);
        return seen is null || packetNumber > seen.Value;
    }

    private void TakePayload(QuicPacketKind kind, ReadOnlySpan<byte> plaintext)
    {
        var reader = new QuicFrameReader(plaintext, kind);
        Span<QuicAckRange> ranges = new QuicAckRange[QuicFrameReader.MaxAckRanges];
        while (reader.TryNext(ranges, out QuicFrame frame))
        {
            if (IsClosed)
            {
                return;
            }

            if (frame.Kind == QuicFrameKind.Ack)
            {
                TimeSpan delay = TimeSpan.FromTicks((long)frame.AckDelay * 80);
                if (!_loss.OnAck(kind, frame.LargestAcknowledged, delay, ranges, frame.AckRangeCount, _now, IsPathValidated(_path)))
                {
                    MarkClosed(QuicFrameReader.ProtocolViolation, send: true);
                    return;
                }

                ObservePathMtu();
            foreach (QuicRetransmit item in _loss.Pending)
                {
                    Retransmit(item.Kind, item.Payload);
                }

                continue;
            }

            if (frame.Kind == QuicFrameKind.ConnectionClose && !frame.ApplicationClose)
            {
                MarkClosed(frame.ErrorCode, send: false);
                return;
            }

            if (frame.Kind == QuicFrameKind.Stream)
            {
                if (!_flow.AcceptStream(frame.StreamId, frame.Offset, frame.Data, out ulong error))
                {
                    MarkClosed(error, send: true);
                    return;
                }

                byte[] received = _flow.Received;
                if (ReceivedStreamByte is null && received.Length > 0)
                {
                    ReceivedStreamByte = received[0];
                }

                continue;
            }

            if (frame.Kind == QuicFrameKind.ResetStream)
            {
                if (!_flow.NoteReset(frame.StreamId, frame.Limit, out ulong error))
                {
                    MarkClosed(error, send: true);
                    return;
                }

                continue;
            }

            if (frame.Kind == QuicFrameKind.StopSending)
            {
                _flow.NoteStopSending(frame.StreamId);
                continue;
            }

            if (frame.Kind == QuicFrameKind.MaxData)
            {
                _flow.RaiseSend(frame.Limit, null, null);
                StageReady();
                continue;
            }

            if (frame.Kind == QuicFrameKind.MaxStreamData)
            {
                _flow.RaiseSend(null, frame.StreamId, frame.Limit);
                StageReady();
                continue;
            }

            if (frame.Kind == QuicFrameKind.NewConnectionId)
            {
                var retire = new List<ulong>();
                if (!_ids.TryAddPeer(frame.Sequence, frame.RetirePriorTo, frame.Data, frame.Token, retire, out ulong error))
                {
                    MarkClosed(error, send: true);
                    return;
                }

                foreach (ulong sequence in retire)
                {
                    StageRetire(sequence);
                }

                continue;
            }

            if (frame.Kind == QuicFrameKind.RetireConnectionId)
            {
                if (!_ids.TryRetire(frame.Sequence, out ulong error))
                {
                    MarkClosed(error, send: true);
                    return;
                }

                continue;
            }

            if (frame.Kind == QuicFrameKind.NewToken && _client)
            {
                IssuedNewToken = frame.Data.ToArray();
                continue;
            }

            if (frame.Kind == QuicFrameKind.PathChallenge)
            {
                StagePathResponse(frame.Data);
                continue;
            }

            if (frame.Kind == QuicFrameKind.PathResponse && MatchesChallenge(frame.Data))
            {
                MarkAddressValidated(_path);
                continue;
            }

            if (frame.Kind == QuicFrameKind.Crypto && !TakeCrypto(kind, frame.Offset, frame.Data))
            {
                return;
            }
        }

        if (reader.Error != 0)
        {
            MarkClosed(reader.Error, send: true);
        }
    }

    private bool TakeCrypto(QuicPacketKind kind, ulong offset, ReadOnlySpan<byte> data)
    {
        if (kind is not (QuicPacketKind.Initial or QuicPacketKind.Handshake) || _engine is null)
        {
            return true;
        }

        if (offset > (ulong)HandshakeFraming.DefaultMaxMessageBytes
            || (ulong)data.Length > (ulong)HandshakeFraming.DefaultMaxMessageBytes
            || offset + (ulong)data.Length > (ulong)HandshakeFraming.DefaultMaxMessageBytes)
        {
            MarkClosed(CryptoError(50), send: true);
            return false;
        }

        if (!_flow.PushCrypto(kind, offset, data, out byte[] ready, out ulong flowError))
        {
            MarkClosed(flowError, send: true);
            return false;
        }

        if (ready.Length == 0)
        {
            return true;
        }

        if (kind == QuicPacketKind.Initial)
        {
            _recvInitial += ready.Length;
        }
        else
        {
            _recvHandshake += ready.Length;
        }

        var output = new TlsOutput();
        _engine.AdvanceDatagram(ready, output);
        if (_engine.State == TlsEngineState.Closed)
        {
            MarkClosed(CryptoError(_engine.LastLocalAlert), send: true);
            return false;
        }

        bool hello = _engine.State == TlsEngineState.WaitClientHelloDecision;
        bool certificate = _engine.State == TlsEngineState.WaitCertificateDecision;
        if (hello || certificate)
        {
            if (!AcceptParameters())
            {
                return false;
            }

            if (hello)
            {
                _engine.Resume(_ => new TlsServerSelection { Certificate = _certificate! }, output);
            }
            else
            {
                _engine.Resume(true, output);
            }

            if (_engine.State == TlsEngineState.Closed)
            {
                MarkClosed(CryptoError(_engine.LastLocalAlert), send: true);
                return false;
            }
        }
        else if (!AcceptParameters())
        {
            return false;
        }

        if (!IsClosed)
        {
            Emit(output);
        }

        return !IsClosed;
    }

    private bool AcceptParameters()
    {
        if (_parametersChecked || _engine?.PeerTransportParameters is not byte[] encoded)
        {
            return true;
        }

        if (!QuicTransportParametersCodec.TryRead(encoded, fromServer: _client, retryReceived: false, out QuicTransportParameters parsed, out ulong error))
        {
            MarkClosed(error, send: true);
            return false;
        }

        bool match = _client
            ? Same(parsed.OriginalDestinationConnectionId, _originalDestinationId) && Same(parsed.InitialSourceConnectionId, _peerSourceId)
            : Same(parsed.InitialSourceConnectionId, _peerSourceId);
        _parametersChecked = true;
        if (match)
        {
            _flow.SetPeer(parsed);
            NotePeerIdle(parsed.MaxIdleTimeout);
            if (parsed.MaxUdpPayloadSize <= int.MaxValue)
            {
                _mtu.SetMaximum((int)parsed.MaxUdpPayloadSize);
            }
        }

        if (match)
        {
            if (_client && parsed.StatelessResetToken is byte[] token)
            {
                _ids.RememberResetToken(_originalDestinationId, token);
            }

            return true;
        }

        MarkClosed(QuicTransportParametersCodec.TransportParameterError, send: true);
        return false;
    }

    private void Emit(TlsOutput output)
    {
        InstallSecrets();
        for (int i = 0; i < output.SegmentCount && !IsClosed; i++)
        {
            (int level, int offset, int length) = output.Segment(i);
            ReadOnlySpan<byte> bytes = output.Bytes.Slice(offset, length);
            if (level == 0)
            {
                StageCrypto(QuicPacketKind.Initial, _initialSend, ref _sentInitial, bytes, _client && _pnInitial == 0 ? 4096 : 0, corrupt: false);
            }
            else if (level == 2)
            {
                StageCrypto(QuicPacketKind.Handshake, _handshakeSend, ref _sentHandshake, bytes, 0, CorruptNextHandshakePayload);
            }
        }
    }

    private void StageCrypto(QuicPacketKind kind, QuicPacketProtector? protector, ref int cursor, ReadOnlySpan<byte> handshake, int minimumDatagram, bool corrupt)
    {
        if (protector is null || handshake.IsEmpty)
        {
            return;
        }

        byte[] frame = ArrayPool<byte>.Shared.Rent(handshake.Length + 32);
        try
        {
            var crypto = new QuicFrame
            {
                Kind = QuicFrameKind.Crypto,
                Offset = (ulong)cursor,
                Data = handshake,
            };
            int written = QuicFrameWriter.Write(in crypto, default, frame);
            if (written == 0)
            {
                return;
            }

            Span<byte> payload = frame.AsSpan(0, written);
            if (corrupt)
            {
                payload[^1] ^= 0xFF;
                CorruptNextHandshakePayload = false;
            }

            if (SealAndStage(kind, protector, payload, minimumDatagram) > 0)
            {
                cursor += handshake.Length;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame, clearArray: true);
        }
    }

    private void Retransmit(QuicPacketKind kind, byte[] payload)
    {
        QuicPacketProtector? protector = Protector(kind, send: true);
        if (protector is null || payload.Length == 0 || !_hasPath || IsClosed)
        {
            return;
        }

        SealAndStage(kind, protector, payload, 0, probe: true);
    }

    private int SealAndStage(QuicPacketKind kind, QuicPacketProtector protector, ReadOnlySpan<byte> payload, int minimumDatagram, bool probe = false, PathInfo? path = null, bool pmtu = false)
    {
        byte[] packet = ArrayPool<byte>.Shared.Rent(Math.Max(minimumDatagram, payload.Length) + 128 + _initialToken.Length);
        try
        {
            ulong number = kind switch
            {
                QuicPacketKind.Handshake => _pnHandshake,
                QuicPacketKind.OneRtt => _pnApplication,
                _ => _pnInitial,
            };
            int written = kind == QuicPacketKind.OneRtt
                ? QuicPackets.SealShort(packet, DestinationId(), number, payload, protector)
                : QuicPackets.SealLong(packet, kind, DestinationId(), _localId, number, payload, protector, minimumDatagram, _initialToken);
            byte[] copy = payload.ToArray();
            if (written == 0 || !TryStageTransmit(path ?? _path, packet.AsSpan(0, written), track: true, kind, number, copy, probe, pmtu))
            {
                return 0;
            }

            if (_client && kind == QuicPacketKind.Initial && _initialFlight is null)
            {
                _initialFlight = copy;
            }

            if (kind == QuicPacketKind.Handshake)
            {
                _pnHandshake++;
            }
            else if (kind == QuicPacketKind.OneRtt)
            {
                _pnApplication++;
            }
            else
            {
                _pnInitial++;
            }

            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet, clearArray: true);
        }
    }

    private void InstallSecrets()
    {
        Span<byte> secret = _secretScratch;
        if (_handshakeSend is null && Copy(application: false, client: _client, secret))
        {
            _handshakeSend = QuicPacketProtector.FromSecret(secret);
        }

        if (_handshakeRecv is null && Copy(application: false, client: !_client, secret))
        {
            _handshakeRecv = QuicPacketProtector.FromSecret(secret);
        }

        if (_applicationSend is null && Copy(application: true, client: _client, secret))
        {
            _applicationSend = QuicPacketProtector.FromSecret(secret);
        }

        if (_applicationRecv is null && Copy(application: true, client: !_client, secret))
        {
            _applicationRecv = QuicPacketProtector.FromSecret(secret);
        }

        CryptographicOperations.ZeroMemory(secret);
    }

    private bool Copy(bool application, bool client, Span<byte> secret)
    {
        secret.Clear();
        return _engine is not null && _engine.TryCopyTrafficSecret(application, client, secret);
    }

    private void MarkClosed(ulong error, bool send)
    {
        if (IsClosed)
        {
            return;
        }

        IsClosed = true;
        CloseError = error;
        if (!send || !_hasPath)
        {
            return;
        }

        InstallSecrets();
        QuicPacketProtector? protector;
        QuicPacketKind kind;
        if (_applicationSend is not null)
        {
            protector = _applicationSend;
            kind = QuicPacketKind.OneRtt;
        }
        else if (_handshakeSend is not null)
        {
            protector = _handshakeSend;
            kind = QuicPacketKind.Handshake;
        }
        else if (_initialSend is not null)
        {
            protector = _initialSend;
            kind = QuicPacketKind.Initial;
        }
        else
        {
            return;
        }

        Span<byte> frame = stackalloc byte[24];
        var close = new QuicFrame { Kind = QuicFrameKind.ConnectionClose, ErrorCode = error };
        int written = QuicFrameWriter.Write(in close, default, frame);
        if (written > 0)
        {
            SealAndStage(kind, protector, frame[..written], 0);
        }
    }

    private void Remember(QuicPacketKind kind, ulong packetNumber)
    {
        if (kind == QuicPacketKind.Handshake)
        {
            _largestHandshake = Max(_largestHandshake, packetNumber);
        }
        else if (kind == QuicPacketKind.OneRtt)
        {
            _largestApplication = Max(_largestApplication, packetNumber);
        }
        else
        {
            _largestInitial = Max(_largestInitial, packetNumber);
        }
    }

    private ulong? Largest(QuicPacketKind kind)
    {
        if (kind == QuicPacketKind.Handshake)
        {
            return _largestHandshake;
        }

        if (kind == QuicPacketKind.OneRtt)
        {
            return _largestApplication;
        }

        return _largestInitial;
    }

    private QuicPacketProtector? Protector(QuicPacketKind kind, bool send)
    {
        if (kind == QuicPacketKind.Handshake)
        {
            return send ? _handshakeSend : _handshakeRecv;
        }

        if (kind == QuicPacketKind.OneRtt)
        {
            return send ? _applicationSend : _applicationRecv;
        }

        return send ? _initialSend : _initialRecv;
    }

    internal void ArmRetry(ReadOnlySpan<byte> token)
    {
        _retryToken = token.ToArray();
        _retryArmed = true;
    }

    internal void SendNewToken(ReadOnlySpan<byte> token)
    {
        if (token.IsEmpty || _applicationSend is null)
        {
            return;
        }

        Span<byte> frame = stackalloc byte[token.Length + 8];
        var written = new QuicFrame { Kind = QuicFrameKind.NewToken, Data = token };
        int count = QuicFrameWriter.Write(in written, default, frame);
        if (count > 0)
        {
            SealAndStage(QuicPacketKind.OneRtt, _applicationSend, frame[..count], 0);
        }
    }

    internal void IssueConnectionId()
    {
        if (_applicationSend is null)
        {
            return;
        }

        (ulong sequence, byte[] id, byte[] token) = _ids.Issue();
        Span<byte> frame = stackalloc byte[64];
        var written = new QuicFrame
        {
            Kind = QuicFrameKind.NewConnectionId,
            Sequence = sequence,
            Data = id,
            Token = token,
        };
        int count = QuicFrameWriter.Write(in written, default, frame);
        if (count > 0)
        {
            SealAndStage(QuicPacketKind.OneRtt, _applicationSend, frame[..count], 0);
        }
    }

    internal void RetireConnectionId(ulong sequence)
    {
        if (_applicationSend is null)
        {
            return;
        }

        Span<byte> frame = stackalloc byte[16];
        var written = new QuicFrame { Kind = QuicFrameKind.RetireConnectionId, Sequence = sequence };
        int count = QuicFrameWriter.Write(in written, default, frame);
        if (count > 0)
        {
            SealAndStage(QuicPacketKind.OneRtt, _applicationSend, frame[..count], 0);
        }
    }

    internal void SendPathChallenge(in PathInfo path)
    {
        if (_applicationSend is null)
        {
            return;
        }

        byte[] data = new byte[8];
        RandomNumberGenerator.Fill(data);
        _challenges.Add((path, data));
        AddReceived(path, 0);
        Span<byte> frame = stackalloc byte[16];
        var challenge = new QuicFrame { Kind = QuicFrameKind.PathChallenge, Data = data };
        int count = QuicFrameWriter.Write(in challenge, default, frame);
        if (count == 0)
        {
            return;
        }

        int overhead = 1 + DestinationId().Length + 2 + 16;
        byte[] body = new byte[Math.Max(count, 1200 - overhead)];
        frame[..count].CopyTo(body);
        SealAndStage(QuicPacketKind.OneRtt, _applicationSend, body, 1200, probe: true, path);
    }

    private void RememberLocalLimits(ReadOnlySpan<byte> encoded, bool fromServer)
    {
        if (QuicTransportParametersCodec.TryRead(encoded, fromServer, retryReceived: false, out QuicTransportParameters parsed, out _))
        {
            _flow.SetLocal(parsed, _client);
            _localIdleMs = parsed.MaxIdleTimeout;
            ArmIdle(_now);
        }
    }

    internal void ReportTunnelMaximum(int bytes) => _mtu.SetMaximum(bytes);

    internal bool SendPathMtuProbe(bool confirm = false)
    {
        if (IsClosed || _applicationSend is null)
        {
            return false;
        }

        int size = _mtu.BeginProbe(confirm);
        if (size < QuicPathMtu.Base)
        {
            return false;
        }

        int overhead = 1 + DestinationId().Length + 2 + 16;
        if (size <= overhead + 1)
        {
            _mtu.Cancel();
            return false;
        }

        byte[] body = new byte[size - overhead];
        body[0] = 0x01;
        ulong number = _pnApplication;
        if (SealAndStage(QuicPacketKind.OneRtt, _applicationSend, body, 0, probe: true, pmtu: true) == 0)
        {
            _mtu.Cancel();
            return false;
        }

        _probeNumber = number;
        return true;
    }

    internal void AcknowledgeApplication(ulong number, DateTimeOffset now)
    {
        _now = now;
        if (HandshakeComplete)
        {
            _loss.HandshakeConfirmed = true;
        }

        Span<byte> frame = stackalloc byte[16];
        var ranges = new QuicAckRange[1];
        var ack = new QuicFrame
        {
            Kind = QuicFrameKind.Ack,
            LargestAcknowledged = number,
            AckRangeCount = 1,
        };
        int written = QuicFrameWriter.Write(in ack, ranges, frame);
        if (written > 0)
        {
            TakePayload(QuicPacketKind.OneRtt, frame[..written]);
        }
    }

    private void ObservePathMtu()
    {
        if (_probeNumber is ulong number)
        {
            bool acked = false;
            bool lost = false;
            foreach (ulong item in _loss.AckedPmtu)
            {
                if (item == number)
                {
                    acked = true;
                }
            }

            foreach (ulong item in _loss.LostPmtu)
            {
                if (item == number)
                {
                    lost = true;
                }
            }

            if (acked)
            {
                _mtu.Acknowledge();
                _probeNumber = null;
            }
            else if (lost)
            {
                _mtu.Lose();
                _probeNumber = null;
            }
        }

        _loss.AckedPmtu.Clear();
        _loss.LostPmtu.Clear();
    }

    internal void SendKeepAlive()
    {
        if (IsClosed || _applicationSend is null)
        {
            return;
        }

        Span<byte> frame = stackalloc byte[4];
        var ping = new QuicFrame { Kind = QuicFrameKind.Ping };
        int count = QuicFrameWriter.Write(in ping, default, frame);
        if (count > 0)
        {
            SealAndStage(QuicPacketKind.OneRtt, _applicationSend, frame, 0, probe: true);
        }
    }

    private ulong _localIdleMs;

    private ulong _peerIdleMs;

    private bool _havePeerIdle;

    private bool _sentSinceReceive;

    private DateTimeOffset? _idleDeadline;

    internal bool IdleTimedOut { get; private set; }

    internal int PathMtu => _mtu.Ceiling;

    internal ulong LastSentApplication { get; private set; }

    private void NotePeerIdle(ulong milliseconds)
    {
        _peerIdleMs = milliseconds;
        _havePeerIdle = true;
        ArmIdle(_now);
    }

    private void NotePeerPacket()
    {
        _sentSinceReceive = false;
        ArmIdle(_now);
    }

    private void NoteAckElicitingSend()
    {
        if (_sentSinceReceive)
        {
            return;
        }

        _sentSinceReceive = true;
        ArmIdle(_now);
    }

    private void ArmIdle(DateTimeOffset now)
    {
        ulong chosen = _localIdleMs;
        if (_havePeerIdle && _peerIdleMs != 0 && (_localIdleMs == 0 || _peerIdleMs < chosen))
        {
            chosen = _peerIdleMs;
        }
        if (chosen == 0 || now == default)
        {
            _idleDeadline = null;
            return;
        }

        TimeSpan period = TimeSpan.FromMilliseconds(chosen);
        TimeSpan floor = TimeSpan.FromTicks(_loss.CurrentProbeTimeout().Ticks * 3);
        if (period < floor)
        {
            period = floor;
        }

        _idleDeadline = now + period;
    }

    private void StageReady()
    {
        if (_applicationSend is null || IsClosed)
        {
            return;
        }

        foreach (Outgoing outgoing in _flow.TakeReady())
        {
            Span<byte> frame = new byte[outgoing.Data.Length + 32];
            var stream = new QuicFrame
            {
                Kind = QuicFrameKind.Stream,
                StreamId = outgoing.StreamId,
                Offset = outgoing.Offset,
                HasLength = true,
                Data = outgoing.Data,
            };
            int count = QuicFrameWriter.Write(in stream, default, frame);
            if (count > 0)
            {
                SealAndStage(QuicPacketKind.OneRtt, _applicationSend, frame[..count], 0);
            }
        }
    }

    private void InstallInitial(ReadOnlySpan<byte> destinationId, bool clientSend)
    {
        _initialSend?.Dispose();
        _initialRecv?.Dispose();
        _initialSend = QuicPacketProtector.FromInitial(destinationId, clientSend);
        _initialRecv = QuicPacketProtector.FromInitial(destinationId, !clientSend);
    }

    private void AcceptRetry(ReadOnlySpan<byte> datagram)
    {
        if (_retryAccepted || _peerSourceId is not null || datagram.Length < 23 || BinaryPrimitives.ReadUInt32BigEndian(datagram[1..]) != 1)
        {
            return;
        }

        int destinationLength = datagram[5];
        if (destinationLength != _localId.Length || !datagram.Slice(6, destinationLength).SequenceEqual(_localId))
        {
            return;
        }

        int sourceIndex = 6 + destinationLength;
        if (datagram.Length <= sourceIndex)
        {
            return;
        }

        int sourceLength = datagram[sourceIndex];
        int sourceOffset = sourceIndex + 1;
        if (sourceLength is < 1 or > QuicDatagramReader.MaxConnectionIdLength || datagram.Length < sourceOffset + sourceLength + 16)
        {
            return;
        }

        if (!QuicRetry.TryVerify(_originalDestinationId, datagram))
        {
            return;
        }

        _retryAccepted = true;
        int tokenStart = sourceOffset + sourceLength;
        _initialToken = datagram[tokenStart..^16].ToArray();
        _remoteId = datagram.Slice(sourceOffset, sourceLength).ToArray();
        _ids.AddPeer(_remoteId);
        InstallInitial(_remoteId, clientSend: true);
        _largestInitial = null;
        _pnInitial = 0;
        _loss.DiscardInitial();
        if (_initialFlight is not null && _initialSend is not null)
        {
            SealAndStage(QuicPacketKind.Initial, _initialSend, _initialFlight, 4096, probe: true);
        }
    }

    private void SendRetry()
    {
        if (_peerSourceId is null)
        {
            return;
        }

        byte[] source = new byte[8];
        RandomNumberGenerator.Fill(source);
        int body = 6 + _peerSourceId.Length + 1 + source.Length + _retryToken.Length;
        byte[] packet = new byte[body + 16];
        packet[0] = 0xF0;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1), 1);
        int at = 5;
        packet[at++] = (byte)_peerSourceId.Length;
        _peerSourceId.CopyTo(packet.AsSpan(at));
        at += _peerSourceId.Length;
        packet[at++] = (byte)source.Length;
        source.CopyTo(packet.AsSpan(at));
        at += source.Length;
        _retryToken.CopyTo(packet.AsSpan(at));
        if (!QuicRetry.TryWriteTag(_originalDestinationId, packet.AsSpan(0, body), packet.AsSpan(body)))
        {
            return;
        }

        TryStageTransmit(_path, packet);
        _expectedRetryToken = _retryToken;
        _retryArmed = false;
        _ids.AddIssued(source);
        InstallInitial(source, clientSend: false);
        _largestInitial = null;
        _pnInitial = 0;
        _loss.DiscardInitial();
    }

    private void DrainReset()
    {
        if (ClosedByStatelessReset)
        {
            return;
        }

        IsClosed = true;
        ClosedByStatelessReset = true;
        for (int i = 0; i < _staged.Count; i++)
        {
            ArrayPool<byte>.Shared.Return(_staged[i].Buffer, clearArray: true);
        }

        _staged.Clear();
    }

    private void StageRetire(ulong sequence)
    {
        if (_applicationSend is null)
        {
            return;
        }

        Span<byte> frame = stackalloc byte[16];
        var written = new QuicFrame { Kind = QuicFrameKind.RetireConnectionId, Sequence = sequence };
        int count = QuicFrameWriter.Write(in written, default, frame);
        if (count > 0)
        {
            SealAndStage(QuicPacketKind.OneRtt, _applicationSend, frame[..count], 0);
        }
    }

    private void StagePathResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length != 8 || _applicationSend is null)
        {
            return;
        }

        byte[] copy = data.ToArray();
        Span<byte> frame = stackalloc byte[16];
        var written = new QuicFrame { Kind = QuicFrameKind.PathResponse, Data = copy };
        int count = QuicFrameWriter.Write(in written, default, frame);
        if (count > 0)
        {
            SealAndStage(QuicPacketKind.OneRtt, _applicationSend, frame[..count], 0);
        }
    }

    private bool MatchesChallenge(ReadOnlySpan<byte> data)
    {
        bool match = false;
        for (int i = 0; i < _challenges.Count; i++)
        {
            if (!_challenges[i].Path.Equals(_path))
            {
                continue;
            }

            match |= CryptographicOperations.FixedTimeEquals(_challenges[i].Data, data);
        }

        if (!match)
        {
            return false;
        }

        for (int i = _challenges.Count - 1; i >= 0; i--)
        {
            if (_challenges[i].Path.Equals(_path) && CryptographicOperations.FixedTimeEquals(_challenges[i].Data, data))
            {
                _challenges.RemoveAt(i);
            }
        }

        return true;
    }

    private byte[] DestinationId() => _client ? _remoteId : _peerSourceId ?? [];

    private static bool Same(byte[]? value, byte[]? expected)
    {
        return value is not null && expected is not null && value.AsSpan().SequenceEqual(expected);
    }

    private static ulong CryptoError(byte alert) => 0x0100UL + alert;

    private static ulong Max(ulong? seen, ulong packetNumber) => seen is ulong value && value > packetNumber ? value : packetNumber;
}
