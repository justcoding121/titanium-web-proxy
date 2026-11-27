using System;
using System.Net.Sockets;

namespace Titanium.Quic;

/// <summary>
/// Datagram packetization-layer path MTU search. The confirmed size starts at 1200 and never drops below that except by returning to it.
/// </summary>
internal sealed class QuicPathMtu
{
    internal const int Base = 1200;

    private int _confirmed = Base;

    private int _limit = 65527;

    private int? _probe;

    private bool _confirming;

    internal int Ceiling { get; private set; } = Base;

    internal void SetMaximum(int bytes)
    {
        if (bytes < Base)
        {
            bytes = Base;
        }

        if (bytes < _limit)
        {
            _limit = bytes;
        }

        if (Ceiling > bytes)
        {
            Ceiling = bytes;
        }

        if (_confirmed > bytes)
        {
            _confirmed = bytes;
        }
    }

    internal int BeginProbe(bool confirm)
    {
        if (_probe is not null)
        {
            return 0;
        }

        int size;
        if (confirm)
        {
            size = Ceiling;
            _confirming = true;
        }
        else
        {
            int span = _limit - _confirmed;
            size = _confirmed + (span / 2);
            if (size <= _confirmed || size > _limit)
            {
                return 0;
            }

            _confirming = false;
        }

        _probe = size;
        return size;
    }

    internal void Acknowledge()
    {
        if (_probe is not int size)
        {
            return;
        }

        _probe = null;
        _confirming = false;
        if (size > _confirmed)
        {
            _confirmed = size;
        }

        if (_confirmed > Ceiling)
        {
            Ceiling = _confirmed;
        }

        if (Ceiling > _limit)
        {
            Ceiling = _limit;
        }
    }

    internal void Lose()
    {
        if (_probe is not int size)
        {
            return;
        }

        _probe = null;
        if (_confirming || size <= Ceiling)
        {
            _confirmed = Base;
            Ceiling = Base;
            _confirming = false;
            return;
        }

        if (size - 1 < _limit)
        {
            _limit = size - 1;
        }

        if (_limit < _confirmed)
        {
            _limit = _confirmed;
        }

        if (Ceiling > _limit)
        {
            Ceiling = _limit;
        }
    }

    internal void Cancel()
    {
        _probe = null;
        _confirming = false;
    }
}

/// <summary>Sets the socket flag used by path MTU probes, where the platform allows it.</summary>
internal static class QuicDatagramSocket
{
    internal static bool TryEnableDontFragment(Socket socket)
    {
        try
        {
            socket.DontFragment = true;
            return socket.DontFragment;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
