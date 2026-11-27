using System;
using System.Buffers;
using System.Buffers.Binary;
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

    private byte[] _localId = [];

    private byte[] _remoteId = [];

    private byte[]? _peerSourceId;

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

    internal ulong CloseError { get; private set; }

    internal bool HandshakeComplete => _engine?.State == TlsEngineState.Connected;

    internal byte? ReceivedStreamByte { get; private set; }

    internal bool CorruptNextHandshakePayload { get; set; }

    internal void StartClient(in PathInfo path, ReadOnlySpan<byte> localId, ReadOnlySpan<byte> remoteId, ReadOnlySpan<byte> transportParameters)
    {
        _client = true;
        _limitAmplification = false;
        _localId = localId.ToArray();
        _remoteId = remoteId.ToArray();
        _path = path;
        _hasPath = true;
        AddReceived(path, 0);
        _initialSend = QuicPacketProtector.FromInitial(remoteId, client: true);
        _initialRecv = QuicPacketProtector.FromInitial(remoteId, client: false);
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
        _certificate = certificate;
        _initialSend = QuicPacketProtector.FromInitial(localId, client: false);
        _initialRecv = QuicPacketProtector.FromInitial(localId, client: true);
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

        Span<byte> data = [value];
        Span<byte> frame = stackalloc byte[16];
        var stream = new QuicFrame
        {
            Kind = QuicFrameKind.Stream,
            HasLength = true,
            Data = data,
        };
        int written = QuicFrameWriter.Write(in stream, default, frame);
        if (written > 0)
        {
            SealAndStage(QuicPacketKind.OneRtt, _applicationSend, frame[..written], 0);
        }
    }

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

        if (!TryLocate(datagram, out QuicPacketKind kind, out int packetLength, out int packetNumberOffset, out int sourceOffset, out int sourceLength))
        {
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(packetLength);
        byte[] plain = ArrayPool<byte>.Shared.Rent(packetLength);
        try
        {
            datagram[..packetLength].CopyTo(rented);
            if (!TryOpen(kind, rented.AsSpan(0, packetLength), packetNumberOffset, plain, out ulong packetNumber, out int plainLength))
            {
                return;
            }

            if (kind == QuicPacketKind.Initial && _peerSourceId is null)
            {
                _peerSourceId = datagram.Slice(sourceOffset, sourceLength).ToArray();
            }

            Remember(kind, packetNumber);
            if (kind == QuicPacketKind.Handshake)
            {
                MarkAddressValidated(_path);
            }

            TakePayload(kind, plain.AsSpan(0, plainLength));
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
        out int sourceLength)
    {
        kind = default;
        packetLength = 0;
        packetNumberOffset = 0;
        sourceOffset = 0;
        sourceLength = 0;
        if ((datagram[0] & 0x40) == 0)
        {
            return false;
        }

        if ((datagram[0] & 0x80) == 0)
        {
            if (datagram.Length < 1 + _localId.Length || !_localId.AsSpan().SequenceEqual(datagram.Slice(1, _localId.Length)))
            {
                return false;
            }

            kind = QuicPacketKind.OneRtt;
            packetNumberOffset = 1 + _localId.Length;
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

        if (!_localId.AsSpan().SequenceEqual(datagram.Slice(6, destinationLength)))
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
            if (!QuicVarInt.TryDecode(datagram[at..], out ulong tokenLength, out int tokenUsed)
                || tokenLength > (ulong)(datagram.Length - at - tokenUsed))
            {
                return false;
            }

            at += tokenUsed + (int)tokenLength;
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
                if (!_loss.OnAck(kind, frame.LargestAcknowledged, delay, ranges, frame.AckRangeCount, _now))
                {
                    MarkClosed(QuicFrameReader.ProtocolViolation, send: true);
                    return;
                }

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

            if (frame.Kind == QuicFrameKind.Stream && frame.Data.Length > 0 && ReceivedStreamByte is null)
            {
                ReceivedStreamByte = frame.Data[0];
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

        int cursor = kind == QuicPacketKind.Initial ? _recvInitial : _recvHandshake;
        if (offset != (ulong)cursor)
        {
            MarkClosed(QuicFrameReader.ProtocolViolation, send: true);
            return false;
        }

        if (kind == QuicPacketKind.Initial)
        {
            _recvInitial = cursor + data.Length;
        }
        else
        {
            _recvHandshake = cursor + data.Length;
        }

        var output = new TlsOutput();
        _engine.AdvanceDatagram(data, output);
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
            ? Same(parsed.OriginalDestinationConnectionId, _remoteId) && Same(parsed.InitialSourceConnectionId, _peerSourceId)
            : Same(parsed.InitialSourceConnectionId, _peerSourceId);
        _parametersChecked = true;
        if (match)
        {
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

    private int SealAndStage(QuicPacketKind kind, QuicPacketProtector protector, ReadOnlySpan<byte> payload, int minimumDatagram, bool probe = false)
    {
        byte[] packet = ArrayPool<byte>.Shared.Rent(Math.Max(minimumDatagram, payload.Length) + 128);
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
                : QuicPackets.SealLong(packet, kind, DestinationId(), _localId, number, payload, protector, minimumDatagram);
            byte[] copy = payload.ToArray();
            if (written == 0 || !TryStageTransmit(_path, packet.AsSpan(0, written), track: true, kind, number, copy, probe))
            {
                return 0;
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

    private byte[] DestinationId() => _client ? _remoteId : _peerSourceId ?? [];

    private static bool Same(byte[]? value, byte[]? expected)
    {
        return value is not null && expected is not null && value.AsSpan().SequenceEqual(expected);
    }

    private static ulong CryptoError(byte alert) => 0x0100UL + alert;

    private static ulong Max(ulong? seen, ulong packetNumber) => seen is ulong value && value > packetNumber ? value : packetNumber;
}
