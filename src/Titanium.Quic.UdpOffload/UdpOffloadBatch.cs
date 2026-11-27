using System;
using System.Net;
using System.Net.Sockets;

namespace Titanium.Quic.UdpOffload;

/// <summary>Batch send and receive. The portable socket path remains when this is off.</summary>
internal static class UdpOffloadBatch
{
    private static readonly object Gate = new();

    private static bool _probed;

    private static bool _send;

    private static bool _segment;

    internal static bool IsEnabled
    {
        get
        {
            if (string.Equals(Environment.GetEnvironmentVariable("TITANIUM_UDP_OFFLOAD"), "0", StringComparison.Ordinal))
            {
                return false;
            }

            Probe();
            return _send;
        }
    }

    internal static bool SegmentSupported
    {
        get
        {
            if (!IsEnabled)
            {
                return false;
            }

            return _segment;
        }
    }

    internal static bool TrySend(Socket socket, ReadOnlyMemory<ReadOnlyMemory<byte>> datagrams, IPEndPoint destination, out int datagramsSent, out int bytes)
    {
        datagramsSent = 0;
        bytes = 0;
        if (!IsEnabled || !OperatingSystem.IsLinux())
        {
            return false;
        }

        return LinuxUdp.Send(socket, datagrams, destination, SegmentSupported, out datagramsSent, out bytes);
    }

    internal static int TryReceive(Socket socket, Span<byte> buffer, Span<int> counts)
    {
        if (!IsEnabled || !OperatingSystem.IsLinux())
        {
            return 0;
        }

        return LinuxUdp.Receive(socket, buffer, counts);
    }

    private static void Probe()
    {
        if (_probed || !OperatingSystem.IsLinux())
        {
            _probed = true;
            return;
        }

        lock (Gate)
        {
            if (_probed)
            {
                return;
            }

            _probed = true;
            try
            {
                using var left = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                using var right = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                left.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                right.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                var first = new byte[] { 1, 2 };
                var second = new byte[] { 3, 4 };
                ReadOnlyMemory<byte>[] messages = [first, second];
                if (!LinuxUdp.Send(left, messages, (IPEndPoint)right.LocalEndPoint!, false, out int sent, out _) || sent != 2)
                {
                    return;
                }

                var buffer = new byte[64];
                var counts = new int[2];
                int slice = buffer.Length / counts.Length;
                if (LinuxUdp.Receive(right, buffer, counts) != 2 || counts[0] != 2 || counts[1] != 2 || buffer[0] != 1 || buffer[1] != 2 || buffer[slice] != 3 || buffer[slice + 1] != 4)
                {
                    return;
                }

                _send = true;
                var same = new byte[] { 5, 6 };
                ReadOnlyMemory<byte>[] pair = [same, same];
                if (!LinuxUdp.Send(left, pair, (IPEndPoint)right.LocalEndPoint!, true, out int segmented, out _) || segmented != 2)
                {
                    return;
                }

                Array.Clear(buffer);
                Array.Clear(counts);
                if (LinuxUdp.Receive(right, buffer, counts) == 2 && counts[0] == 2 && counts[1] == 2 && buffer[0] == 5 && buffer[slice] == 5)
                {
                    _segment = true;
                }
            }
            catch (SocketException)
            {
            }
        }
    }
}
