using System;
using System.Collections.Generic;

namespace Titanium.Quic;

/// <summary>
/// Stream and connection flow control, plus reassembly for CRYPTO and STREAM.
/// A limit that does not increase is ignored. Reset and stop-sending are reported once.
/// </summary>
internal sealed class QuicFlow
{
    internal const ulong FlowControlError = 0x03;

    internal const ulong StreamLimitError = 0x04;

    internal const ulong StreamStateError = 0x05;

    internal const ulong FinalSizeError = 0x06;

    internal const ulong CryptoBufferExceeded = 0x0d;

    internal const int ReassemblyCap = 16384;

    private readonly QuicReassembly _initialCrypto = new(ReassemblyCap);

    private readonly QuicReassembly _handshakeCrypto = new(ReassemblyCap);

    private readonly Dictionary<ulong, StreamFlow> _streams = [];

    private readonly List<byte> _received = [];

    private bool _client;

    private ulong _peerMaxData;

    private ulong _peerBidiLocal;

    private ulong _peerBidiRemote;

    private ulong _peerUni;

    private ulong _peerMaxBidi;

    private ulong _peerMaxUni;

    private ulong _localMaxData;

    private ulong _localBidiLocal;

    private ulong _localBidiRemote;

    private ulong _localUni;

    private ulong _localMaxBidi;

    private ulong _localMaxUni;

    private ulong _dataSent;

    private ulong _dataReceived;

    internal bool SendBlocked { get; private set; }

    internal int ResetsDelivered { get; private set; }

    internal int StopsDelivered { get; private set; }

    internal byte[] Received => _received.ToArray();

    internal void SetLocal(QuicTransportParameters parameters, bool client)
    {
        _client = client;
        _localMaxData = parameters.InitialMaxData;
        _localBidiLocal = parameters.InitialMaxStreamDataBidiLocal;
        _localBidiRemote = parameters.InitialMaxStreamDataBidiRemote;
        _localUni = parameters.InitialMaxStreamDataUni;
        _localMaxBidi = parameters.InitialMaxStreamsBidi;
        _localMaxUni = parameters.InitialMaxStreamsUni;
    }

    internal void SetPeer(QuicTransportParameters parameters)
    {
        _peerMaxData = parameters.InitialMaxData;
        _peerBidiLocal = parameters.InitialMaxStreamDataBidiLocal;
        _peerBidiRemote = parameters.InitialMaxStreamDataBidiRemote;
        _peerUni = parameters.InitialMaxStreamDataUni;
        _peerMaxBidi = parameters.InitialMaxStreamsBidi;
        _peerMaxUni = parameters.InitialMaxStreamsUni;
        Flush();
    }

    internal int Queue(ulong streamId, ReadOnlySpan<byte> data, out bool blocked)
    {
        blocked = false;
        if (data.IsEmpty || !CanOpen(streamId, peer: true))
        {
            blocked = true;
            SendBlocked = true;
            return 0;
        }

        StreamFlow stream = Open(streamId);
        int before = stream.Pending.Count;
        foreach (byte value in data)
        {
            stream.Pending.Add(value);
        }

        Flush();
        blocked = stream.Pending.Count > 0;
        SendBlocked = blocked;
        return before + data.Length - stream.Pending.Count;
    }

    internal bool RaiseSend(ulong? maxData, ulong? streamId, ulong? maxStreamData)
    {
        if (maxData is ulong data && data > _peerMaxData)
        {
            _peerMaxData = data;
        }

        if (streamId is ulong id && maxStreamData is ulong streamLimit)
        {
            if (ReceiveOnly(id) || (LocallyInitiated(id) && !_streams.ContainsKey(id)))
            {
                return false;
            }

            StreamFlow stream = Open(id);
            if (streamLimit > stream.SendLimit)
            {
                stream.SendLimit = streamLimit;
            }
        }

        Flush();
        SendBlocked = PendingBytes() > 0;
        return true;
    }

    internal bool Grant(ulong streamId, ulong streamLimit, ulong connectionLimit)
    {
        if (connectionLimit > _localMaxData)
        {
            _localMaxData = connectionLimit;
        }

        StreamFlow stream = Open(streamId);
        if (streamLimit > stream.ReceiveLimit)
        {
            stream.ReceiveLimit = streamLimit;
        }

        return true;
    }

    internal bool PushCrypto(QuicPacketKind kind, ulong offset, ReadOnlySpan<byte> data, out byte[] ready, out ulong error)
    {
        QuicReassembly buffer = kind == QuicPacketKind.Initial ? _initialCrypto : _handshakeCrypto;
        return buffer.Push(offset, data, CryptoBufferExceeded, out ready, out error);
    }

    internal bool AcceptStream(ulong streamId, ulong offset, ReadOnlySpan<byte> data, out ulong error)
    {
        error = 0;
        if (SendOnly(streamId) || (LocallyInitiated(streamId) && !_streams.ContainsKey(streamId)))
        {
            error = StreamStateError;
            return false;
        }

        if (!CanOpen(streamId, peer: false))
        {
            error = StreamLimitError;
            return false;
        }

        ulong end = offset + (ulong)data.Length;
        if (end < offset)
        {
            error = QuicFrameReader.ProtocolViolation;
            return false;
        }

        StreamFlow stream = Open(streamId);
        if (end > stream.ReceiveLimit || _dataReceived - stream.ReceivedEnd + end > _localMaxData)
        {
            error = FlowControlError;
            return false;
        }

        if (!stream.Incoming.Push(offset, data, FlowControlError, out byte[] ready, out error))
        {
            return false;
        }

        if (end > stream.ReceivedEnd)
        {
            _dataReceived = _dataReceived - stream.ReceivedEnd + end;
            stream.ReceivedEnd = end;
        }

        if (ready.Length > 0)
        {
            _received.AddRange(ready);
        }

        return true;
    }

    internal bool NoteReset(ulong streamId, ulong finalSize, out ulong error)
    {
        error = 0;
        if (SendOnly(streamId))
        {
            error = StreamStateError;
            return false;
        }

        StreamFlow stream = Open(streamId);
        if (stream.Reset)
        {
            if (stream.FinalSize != finalSize)
            {
                error = FinalSizeError;
            }

            return error == 0;
        }

        if (finalSize < stream.ReceivedEnd)
        {
            error = FinalSizeError;
            return false;
        }

        stream.Reset = true;
        stream.FinalSize = finalSize;
        ResetsDelivered++;
        return true;
    }

    internal bool NoteStopSending(ulong streamId, out ulong error)
    {
        error = 0;
        if (ReceiveOnly(streamId) || (LocallyInitiated(streamId) && !_streams.ContainsKey(streamId)))
        {
            error = StreamStateError;
            return false;
        }

        StreamFlow stream = Open(streamId);
        if (stream.StopSending)
        {
            return true;
        }

        stream.StopSending = true;
        StopsDelivered++;
        return true;
    }

    private bool LocallyInitiated(ulong streamId) => _client ? (streamId & 1) == 0 : (streamId & 1) == 1;

    private bool SendOnly(ulong streamId) => (streamId & 2) != 0 && LocallyInitiated(streamId);

    private bool ReceiveOnly(ulong streamId) => (streamId & 2) != 0 && !LocallyInitiated(streamId);

    internal void ReturnReady(ulong streamId, ulong offset, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        StreamFlow stream = Open(streamId);
        stream.Ready.InsertRange(0, data.ToArray());
        stream.ReadyOffset = offset;
    }

    internal IReadOnlyList<Outgoing> TakeReady()
    {
        var ready = new List<Outgoing>();
        foreach (KeyValuePair<ulong, StreamFlow> pair in _streams)
        {
            if (pair.Value.Ready.Count == 0)
            {
                continue;
            }

            ready.Add(new Outgoing(pair.Key, pair.Value.ReadyOffset, pair.Value.Ready.ToArray()));
            pair.Value.ReadyOffset += (ulong)pair.Value.Ready.Count;
            pair.Value.Ready.Clear();
        }

        return ready;
    }

    private void Flush()
    {
        foreach (KeyValuePair<ulong, StreamFlow> pair in _streams)
        {
            StreamFlow stream = pair.Value;
            if (stream.SendLimit == 0)
            {
                stream.SendLimit = PeerStreamLimit(pair.Key);
            }

            while (stream.Pending.Count > 0)
            {
                ulong streamRoom = stream.SendLimit - stream.Sent;
                ulong connectionRoom = _peerMaxData - _dataSent;
                if (streamRoom == 0 || connectionRoom == 0)
                {
                    break;
                }

                ulong room = streamRoom < connectionRoom ? streamRoom : connectionRoom;
                if ((ulong)stream.Pending.Count < room)
                {
                    room = (ulong)stream.Pending.Count;
                }

                int take = (int)room;
                stream.Ready.AddRange(stream.Pending.GetRange(0, take));
                stream.Pending.RemoveRange(0, take);
                stream.Sent += (ulong)take;
                _dataSent += (ulong)take;
            }
        }
    }

    private int PendingBytes()
    {
        int count = 0;
        foreach (StreamFlow stream in _streams.Values)
        {
            count += stream.Pending.Count;
        }

        return count;
    }

    private ulong PeerStreamLimit(ulong streamId)
    {
        int low = (int)(streamId & 3);
        bool localBidi = _client ? low == 0 : low == 1;
        bool remoteBidi = _client ? low == 1 : low == 0;
        if (localBidi)
        {
            return _peerBidiRemote;
        }

        if (remoteBidi)
        {
            return _peerBidiLocal;
        }

        return _peerUni;
    }

    private bool CanOpen(ulong streamId, bool peer)
    {
        int low = (int)(streamId & 3);
        bool uni = low >= 2;
        ulong sequence = streamId >> 2;
        ulong limit = uni
            ? (peer ? _peerMaxUni : _localMaxUni)
            : (peer ? _peerMaxBidi : _localMaxBidi);
        return sequence < limit;
    }

    private StreamFlow Open(ulong streamId)
    {
        if (_streams.TryGetValue(streamId, out StreamFlow? stream))
        {
            return stream;
        }

        stream = new StreamFlow { ReceiveLimit = LocalStreamLimit(streamId), SendLimit = PeerStreamLimit(streamId) };
        _streams.Add(streamId, stream);
        return stream;
    }

    private ulong LocalStreamLimit(ulong streamId)
    {
        int low = (int)(streamId & 3);
        bool localBidi = _client ? low == 0 : low == 1;
        bool remoteBidi = _client ? low == 1 : low == 0;
        if (localBidi)
        {
            return _localBidiLocal;
        }

        if (remoteBidi)
        {
            return _localBidiRemote;
        }

        return _localUni;
    }

    private sealed class StreamFlow
    {
        internal ulong SendLimit;

        internal ulong ReceiveLimit;

        internal ulong Sent;

        internal ulong ReceivedEnd;

        internal ulong FinalSize;

        internal bool Reset;

        internal bool StopSending;

        internal List<byte> Pending { get; } = [];

        internal List<byte> Ready { get; } = [];

        internal ulong ReadyOffset;

        internal QuicReassembly Incoming { get; } = new(ReassemblyCap);
    }
}

internal readonly struct Outgoing
{
    internal Outgoing(ulong streamId, ulong offset, byte[] data)
    {
        StreamId = streamId;
        Offset = offset;
        Data = data;
    }

    internal ulong StreamId { get; }

    internal ulong Offset { get; }

    internal byte[] Data { get; }
}

internal sealed class QuicReassembly
{
    private readonly List<Piece> _pieces = [];

    private readonly int _cap;

    private ulong _cursor;

    private int _held;

    internal QuicReassembly(int cap) => _cap = cap;

    internal bool Push(ulong offset, ReadOnlySpan<byte> data, ulong overflowError, out byte[] ready, out ulong error)
    {
        ready = [];
        error = 0;
        if (data.IsEmpty)
        {
            return true;
        }

        ulong end = offset + (ulong)data.Length;
        if (end < offset)
        {
            error = QuicFrameReader.ProtocolViolation;
            return false;
        }

        if (end <= _cursor)
        {
            return true;
        }

        if (offset < _cursor)
        {
            int skip = (int)(_cursor - offset);
            data = data[skip..];
            offset = _cursor;
        }

        if (offset > _cursor && offset - _cursor > (ulong)_cap)
        {
            error = overflowError;
            return false;
        }

        if (offset != _cursor && _held + data.Length > _cap)
        {
            error = overflowError;
            return false;
        }

        if (offset == _cursor)
        {
            var delivered = new List<byte>(data.Length);
            foreach (byte value in data)
            {
                delivered.Add(value);
            }

            _cursor = end;
            Drain(delivered);
            ready = delivered.ToArray();
            return true;
        }

        _pieces.Add(new Piece { Offset = offset, Data = data.ToArray() });
        _held += data.Length;
        return true;
    }

    private void Drain(List<byte> delivered)
    {
        bool moved = true;
        while (moved)
        {
            moved = false;
            for (int i = 0; i < _pieces.Count; i++)
            {
                Piece piece = _pieces[i];
                ulong end = piece.Offset + (ulong)piece.Data.Length;
                if (end <= _cursor)
                {
                    _held -= piece.Data.Length;
                    _pieces.RemoveAt(i);
                    moved = true;
                    break;
                }

                if (piece.Offset > _cursor)
                {
                    continue;
                }

                int skip = (int)(_cursor - piece.Offset);
                for (int b = skip; b < piece.Data.Length; b++)
                {
                    delivered.Add(piece.Data[b]);
                }
                _cursor = end;
                _held -= piece.Data.Length;
                _pieces.RemoveAt(i);
                moved = true;
                break;
            }
        }
    }

    private sealed class Piece
    {
        internal ulong Offset;

        internal byte[] Data = [];
    }
}
