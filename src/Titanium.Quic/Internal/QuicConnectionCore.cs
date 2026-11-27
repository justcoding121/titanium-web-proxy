using System;
using System.Buffers;
using System.Collections.Generic;

namespace Titanium.Quic;

/// <summary>One network path. Amplification and validation are per path.</summary>
internal readonly struct PathInfo : IEquatable<PathInfo>
{
    private readonly ushort _localPort;

    private readonly ushort _remotePort;

    private readonly byte _localLength;

    private readonly byte _remoteLength;

    private readonly ulong _localHigh;

    private readonly ulong _localLow;

    private readonly ulong _remoteHigh;

    private readonly ulong _remoteLow;

    internal PathInfo(ReadOnlySpan<byte> localAddress, int localPort, ReadOnlySpan<byte> remoteAddress, int remotePort)
    {
        if (localAddress.Length is < 1 or > 16 || remoteAddress.Length is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(localAddress));
        }

        if ((uint)localPort > ushort.MaxValue || (uint)remotePort > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(localPort));
        }

        _localPort = (ushort)localPort;
        _remotePort = (ushort)remotePort;
        _localLength = (byte)localAddress.Length;
        _remoteLength = (byte)remoteAddress.Length;
        _localHigh = Pack(localAddress, 0);
        _localLow = Pack(localAddress, 8);
        _remoteHigh = Pack(remoteAddress, 0);
        _remoteLow = Pack(remoteAddress, 8);
    }

    public bool Equals(PathInfo other)
    {
        return _localPort == other._localPort
            && _remotePort == other._remotePort
            && _localLength == other._localLength
            && _remoteLength == other._remoteLength
            && _localHigh == other._localHigh
            && _localLow == other._localLow
            && _remoteHigh == other._remoteHigh
            && _remoteLow == other._remoteLow;
    }

    public override bool Equals(object? obj) => obj is PathInfo other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_localPort, _remotePort, _localHigh, _remoteLow);

    private static ulong Pack(ReadOnlySpan<byte> address, int offset)
    {
        ulong value = 0;
        int end = Math.Min(address.Length, offset + 8);
        for (int i = offset; i < end; i++)
        {
            value |= (ulong)address[i] << ((i - offset) * 8);
        }

        return value;
    }
}

internal readonly struct QuicConnectionId : IEquatable<QuicConnectionId>
{
    private readonly byte _length;

    private readonly ulong _first;

    private readonly ulong _second;

    private readonly uint _tail;

    internal QuicConnectionId(ReadOnlySpan<byte> bytes)
    {
        if ((uint)bytes.Length > QuicDatagramReader.MaxConnectionIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        _length = (byte)bytes.Length;
        _first = Pack(bytes, 0, 8);
        _second = Pack(bytes, 8, 8);
        _tail = (uint)Pack(bytes, 16, 4);
    }

    internal int Length => _length;

    internal bool Matches(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length == _length && new QuicConnectionId(bytes).Equals(this);
    }

    public bool Equals(QuicConnectionId other)
    {
        return _length == other._length && _first == other._first && _second == other._second && _tail == other._tail;
    }

    public override bool Equals(object? obj) => obj is QuicConnectionId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_length, _first, _second, _tail);

    private static ulong Pack(ReadOnlySpan<byte> bytes, int offset, int width)
    {
        ulong value = 0;
        int end = Math.Min(bytes.Length, offset + width);
        for (int i = offset; i < end; i++)
        {
            value |= (ulong)bytes[i] << ((i - offset) * 8);
        }

        return value;
    }
}

/// <summary>
/// Routes a datagram to a connection that already owns the destination connection ID.
/// An unknown ID does not allocate a connection.
/// </summary>
internal sealed class QuicConnectionTable
{
    private readonly List<QuicConnectionId> _ids = [];

    private readonly List<QuicConnectionCore> _cores = [];

    internal int Count => _cores.Count;

    internal void Add(QuicConnectionId id, QuicConnectionCore core)
    {
        _ids.Add(id);
        _cores.Add(core);
    }

    internal bool TryDispatch(DateTimeOffset now, ReadOnlySpan<byte> datagram, in PathInfo path)
    {
        if (!TryFind(datagram, out QuicConnectionCore? core) || core is null)
        {
            return false;
        }

        core.HandleDatagram(now, datagram, path);
        return true;
    }

    private bool TryFind(ReadOnlySpan<byte> datagram, out QuicConnectionCore? core)
    {
        core = null;
        if (datagram.IsEmpty)
        {
            return false;
        }

        if ((datagram[0] & 0x80) != 0)
        {
            if (datagram.Length < 6)
            {
                return false;
            }

            int length = datagram[5];
            if (length > QuicDatagramReader.MaxConnectionIdLength || datagram.Length < 6 + length)
            {
                return false;
            }

            var id = new QuicConnectionId(datagram.Slice(6, length));
            for (int i = 0; i < _ids.Count; i++)
            {
                if (_ids[i].Equals(id))
                {
                    core = _cores[i];
                    return true;
                }
            }

            return false;
        }

        int best = -1;
        for (int i = 0; i < _ids.Count; i++)
        {
            int length = _ids[i].Length;
            if (datagram.Length >= 1 + length && _ids[i].Matches(datagram.Slice(1, length)) && (best < 0 || length > _ids[best].Length))
            {
                best = i;
            }
        }

        if (best < 0)
        {
            return false;
        }

        core = _cores[best];
        return true;
    }
}

/// <summary>
/// Sans-IO connection. Time and datagrams come from the caller. There is no socket and no timer here.
/// </summary>
internal sealed class QuicConnectionCore
{
    /// <summary>Stand-in deadline until loss recovery supplies the real one.</summary>
    internal static readonly TimeSpan ScheduleHook = TimeSpan.FromSeconds(1);

    private readonly List<PathState> _paths = [];

    private readonly List<Staged> _staged = [];

    private DateTimeOffset? _timeout;

    internal DateTimeOffset? NextTimeout() => _timeout;

    internal void HandleDatagram(DateTimeOffset now, ReadOnlySpan<byte> datagram, in PathInfo path)
    {
        AddReceived(path, datagram.Length);
        _timeout ??= now + ScheduleHook;
    }

    internal bool HandleTimeout(DateTimeOffset now)
    {
        if (_timeout is not DateTimeOffset due || now < due)
        {
            return false;
        }

        _timeout = null;
        return true;
    }

    internal void MarkAddressValidated(in PathInfo path)
    {
        int index = IndexOf(path);
        if (index < 0)
        {
            _paths.Add(new PathState { Path = path, Validated = true });
            return;
        }

        PathState state = _paths[index];
        state.Validated = true;
        _paths[index] = state;
    }

    internal bool TryStageTransmit(in PathInfo path, ReadOnlySpan<byte> datagram)
    {
        if (datagram.IsEmpty || datagram.Length > 65535)
        {
            return false;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(datagram.Length);
        datagram.CopyTo(rented);
        _staged.Add(new Staged { Buffer = rented, Length = datagram.Length, Path = path });
        return true;
    }

    internal int PollTransmit(DateTimeOffset now, Span<byte> destination, out PathInfo path)
    {
        _ = now;
        path = default;
        for (int i = 0; i < _staged.Count; i++)
        {
            Staged staged = _staged[i];
            if (destination.Length < staged.Length || !Fits(staged.Path, staged.Length, out int pathIndex))
            {
                continue;
            }

            staged.Buffer.AsSpan(0, staged.Length).CopyTo(destination);
            ArrayPool<byte>.Shared.Return(staged.Buffer, clearArray: true);
            _staged.RemoveAt(i);
            PathState state = _paths[pathIndex];
            state.Sent += staged.Length;
            _paths[pathIndex] = state;
            path = staged.Path;
            return staged.Length;
        }

        return 0;
    }

    internal long BytesReceived(in PathInfo path)
    {
        int index = IndexOf(path);
        return index < 0 ? 0 : _paths[index].Received;
    }

    internal long BytesSent(in PathInfo path)
    {
        int index = IndexOf(path);
        return index < 0 ? 0 : _paths[index].Sent;
    }

    private void AddReceived(in PathInfo path, int length)
    {
        int index = IndexOf(path);
        if (index < 0)
        {
            _paths.Add(new PathState { Path = path, Received = length });
            return;
        }

        PathState state = _paths[index];
        state.Received += length;
        _paths[index] = state;
    }

    private bool Fits(in PathInfo path, int length, out int pathIndex)
    {
        pathIndex = IndexOf(path);
        if (pathIndex < 0)
        {
            return false;
        }

        PathState state = _paths[pathIndex];
        if (state.Validated)
        {
            return true;
        }

        long budget = state.Received > long.MaxValue / 3 ? long.MaxValue : state.Received * 3;
        return state.Sent <= budget - length;
    }

    private int IndexOf(in PathInfo path)
    {
        for (int i = 0; i < _paths.Count; i++)
        {
            if (_paths[i].Path.Equals(path))
            {
                return i;
            }
        }

        return -1;
    }

    private struct PathState
    {
        internal PathInfo Path;

        internal long Received;

        internal long Sent;

        internal bool Validated;
    }

    private struct Staged
    {
        internal byte[] Buffer;

        internal int Length;

        internal PathInfo Path;
    }
}
