using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Titanium.Quic;

/// <summary>
/// Connection IDs this endpoint has issued and the ones its peer has issued.
/// Reset tokens are compared without an early exit.
/// </summary>
internal sealed class QuicIdentity
{
    internal const ulong ConnectionIdLimitError = 0x09;

    internal const int ActiveLimit = 8;

    private readonly List<Issued> _issued = [];

    private readonly List<Peer> _peers = [];

    private readonly List<byte[]> _used = [];

    private ulong _retirePriorTo;

    internal int IssuedActive
    {
        get
        {
            int count = 0;
            foreach (Issued issued in _issued)
            {
                if (!issued.Retired)
                {
                    count++;
                }
            }

            return count;
        }
    }

    internal int PeerActive
    {
        get
        {
            int count = 0;
            foreach (Peer peer in _peers)
            {
                if (!peer.Retired && !peer.ResetOnly)
                {
                    count++;
                }
            }

            return count;
        }
    }

    internal byte[]? ActivePeer()
    {
        Peer? chosen = null;
        foreach (Peer peer in _peers)
        {
            if (peer.Retired || peer.ResetOnly)
            {
                continue;
            }

            if (chosen is null || peer.Sequence >= chosen.Value.Sequence)
            {
                chosen = peer;
            }
        }

        return chosen?.Id;
    }

    internal void AddIssued(ReadOnlySpan<byte> id)
    {
        _issued.Add(new Issued { Sequence = (ulong)_issued.Count, Id = id.ToArray() });
    }

    internal void AddPeer(ReadOnlySpan<byte> id)
    {
        if (id.IsEmpty || FindPeer(id) is not null)
        {
            return;
        }

        _peers.Add(new Peer { Sequence = (ulong)_peers.Count, Id = id.ToArray() });
    }

    internal bool Owns(ReadOnlySpan<byte> id)
    {
        foreach (Issued issued in _issued)
        {
            if (!issued.Retired && issued.Id.AsSpan().SequenceEqual(id))
            {
                return true;
            }
        }

        return false;
    }

    internal byte[]? LongestOwnedPrefix(ReadOnlySpan<byte> datagram)
    {
        byte[]? match = null;
        foreach (Issued issued in _issued)
        {
            if (issued.Retired || datagram.Length < issued.Id.Length)
            {
                continue;
            }

            if (!datagram[..issued.Id.Length].SequenceEqual(issued.Id))
            {
                continue;
            }

            if (match is null || issued.Id.Length > match.Length)
            {
                match = issued.Id;
            }
        }

        return match;
    }

    internal (ulong Sequence, byte[] Id, byte[] Token) Issue()
    {
        byte[] id = new byte[8];
        byte[] token = new byte[16];
        RandomNumberGenerator.Fill(id);
        RandomNumberGenerator.Fill(token);
        ulong sequence = (ulong)_issued.Count;
        _issued.Add(new Issued { Sequence = sequence, Id = id });
        return (sequence, id, token);
    }

    internal bool TryRetire(ulong sequence, out ulong error)
    {
        error = 0;
        Issued? found = null;
        ulong highest = 0;
        bool any = false;
        foreach (Issued issued in _issued)
        {
            any = true;
            if (issued.Sequence > highest)
            {
                highest = issued.Sequence;
            }

            if (issued.Sequence == sequence)
            {
                found = issued;
            }
        }

        if (!any || sequence > highest || found is null)
        {
            error = QuicFrameReader.ProtocolViolation;
            return false;
        }

        if (found.Retired)
        {
            return true;
        }

        if (IssuedActive <= 1)
        {
            error = QuicFrameReader.ProtocolViolation;
            return false;
        }

        found.Retired = true;
        RetireReset(found.Id);
        return true;
    }

    internal bool TryAddPeer(ulong sequence, ulong retirePriorTo, ReadOnlySpan<byte> id, ReadOnlySpan<byte> token, List<ulong> retire, out ulong error)
    {
        error = 0;
        if (retirePriorTo > _retirePriorTo)
        {
            _retirePriorTo = retirePriorTo;
        }

        for (int i = 0; i < _peers.Count; i++)
        {
            Peer peer = _peers[i];
            if (peer.Retired || peer.Sequence >= _retirePriorTo)
            {
                continue;
            }

            peer.Retired = true;
            _peers[i] = peer;
            RetireReset(peer.Id);
            retire.Add(peer.Sequence);
        }

        for (int i = 0; i < _peers.Count; i++)
        {
            Peer peer = _peers[i];
            if (peer.Sequence != sequence)
            {
                continue;
            }

            bool sameId = peer.Id.AsSpan().SequenceEqual(id);
            if (!sameId || (peer.Token is not null && !peer.Token.AsSpan().SequenceEqual(token)))
            {
                error = QuicFrameReader.ProtocolViolation;
                return false;
            }

            if (peer.Token is null)
            {
                peer.Token = token.ToArray();
                _peers[i] = peer;
            }

            return true;
        }

        var added = new Peer
        {
            Sequence = sequence,
            Id = id.ToArray(),
            Token = token.ToArray(),
            Retired = sequence < _retirePriorTo,
        };
        _peers.Add(added);
        if (added.Retired)
        {
            retire.Add(sequence);
        }

        if (PeerActive > ActiveLimit)
        {
            error = ConnectionIdLimitError;
            return false;
        }

        return true;
    }

    internal void MarkUsed(ReadOnlySpan<byte> connectionId)
    {
        if (connectionId.IsEmpty || Used(connectionId))
        {
            return;
        }

        _used.Add(connectionId.ToArray());
    }

    internal void RememberResetToken(ReadOnlySpan<byte> connectionId, ReadOnlySpan<byte> token)
    {
        if (connectionId.IsEmpty || token.Length != 16)
        {
            return;
        }

        for (int i = 0; i < _peers.Count; i++)
        {
            if (!_peers[i].Id.AsSpan().SequenceEqual(connectionId))
            {
                continue;
            }

            if (_peers[i].Token is not null)
            {
                return;
            }

            Peer peer = _peers[i];
            peer.Token = token.ToArray();
            _peers[i] = peer;
            return;
        }

        // The token belongs to a connection ID this endpoint chose, not one the
        // peer asked us to send to. Keep it for reset checks only.
        _peers.Add(new Peer
        {
            Sequence = ulong.MaxValue,
            Id = connectionId.ToArray(),
            Token = token.ToArray(),
            ResetOnly = true,
        });
    }

    internal bool MatchesReset(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < 21)
        {
            return false;
        }

        ReadOnlySpan<byte> tail = datagram[^16..];
        bool match = false;
        foreach (Peer peer in _peers)
        {
            if (peer.Token is null || peer.Retired || !Used(peer.Id))
            {
                continue;
            }

            match |= CryptographicOperations.FixedTimeEquals(peer.Token, tail);
        }

        return match;
    }

    private void RetireReset(byte[] id)
    {
        for (int i = 0; i < _peers.Count; i++)
        {
            if (_peers[i].Id.AsSpan().SequenceEqual(id))
            {
                Peer peer = _peers[i];
                peer.Retired = true;
                _peers[i] = peer;
            }
        }
    }

    private bool Used(ReadOnlySpan<byte> connectionId)
    {
        foreach (byte[] used in _used)
        {
            if (used.AsSpan().SequenceEqual(connectionId))
            {
                return true;
            }
        }

        return false;
    }

    private Peer? FindPeer(ReadOnlySpan<byte> id)
    {
        foreach (Peer peer in _peers)
        {
            if (peer.Id.AsSpan().SequenceEqual(id))
            {
                return peer;
            }
        }

        return null;
    }

    private sealed class Issued
    {
        internal ulong Sequence;

        internal byte[] Id = [];

        internal bool Retired;
    }

    private struct Peer
    {
        internal ulong Sequence;

        internal byte[] Id;

        internal byte[]? Token;

        internal bool Retired;

        internal bool ResetOnly;
    }
}
