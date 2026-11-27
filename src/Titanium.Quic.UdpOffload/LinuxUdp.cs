using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Titanium.Quic.UdpOffload;

internal static unsafe partial class LinuxUdp
{
    internal const int SolUdp = 17;

    internal const int UdpSegment = 103;

    private const int MessageDontWait = 0x40;

    private const int AddressLength = 16;

    [LibraryImport("libc", EntryPoint = "sendmmsg", SetLastError = true)]
    private static partial int SendMany(int descriptor, MMsgHdr* messages, uint length, int flags);

    [LibraryImport("libc", EntryPoint = "recvmmsg", SetLastError = true)]
    private static partial int ReceiveMany(int descriptor, MMsgHdr* messages, uint length, int flags, IntPtr timeout);

    internal static bool Send(Socket socket, ReadOnlyMemory<ReadOnlyMemory<byte>> datagrams, IPEndPoint destination, bool segment, out int datagramsSent, out int bytes)
    {
        datagramsSent = 0;
        bytes = 0;
        if (!TryAddress(destination, out SockAddrIn address))
        {
            return false;
        }

        using DescriptorLease lease = DescriptorLease.Open(socket);
        if (!lease.Held)
        {
            return false;
        }

        return Send(lease.Value, datagrams, address, segment, out datagramsSent, out bytes);
    }

    internal static int Receive(Socket socket, Span<byte> buffer, Span<int> counts)
    {
        if (counts.Length == 0 || buffer.Length < counts.Length)
        {
            return 0;
        }

        using DescriptorLease lease = DescriptorLease.Open(socket);
        if (!lease.Held)
        {
            return 0;
        }

        return Receive(lease.Value, buffer, counts);
    }

    private static bool Send(int descriptor, ReadOnlyMemory<ReadOnlyMemory<byte>> datagrams, SockAddrIn address, bool segment, out int datagramsSent, out int bytes)
    {
        datagramsSent = 0;
        bytes = 0;
        int count = datagrams.Length;
        if (count == 0 || count > 16)
        {
            return false;
        }

        int total = 0;
        int width = datagrams.Span[0].Length;
        bool uniform = width > 0;
        for (int i = 0; i < count; i++)
        {
            int length = datagrams.Span[i].Length;
            if (length == 0)
            {
                return false;
            }

            if (length != width)
            {
                uniform = false;
            }

            total += length;
        }

        byte[] storage = new byte[total];
        int offset = 0;
        for (int i = 0; i < count; i++)
        {
            datagrams.Span[i].Span.CopyTo(storage.AsSpan(offset));
            offset += datagrams.Span[i].Length;
        }

        if (segment && uniform && count > 1 && SendSegment(descriptor, storage, width, address))
        {
            datagramsSent = count;
            bytes = total;
            return true;
        }

        var headers = new MMsgHdr[count];
        var vectors = new IoVector[count];
        var names = new SockAddrIn[1];
        names[0] = address;
        fixed (byte* raw = storage)
        fixed (MMsgHdr* header = headers)
        fixed (IoVector* vector = vectors)
        fixed (SockAddrIn* name = names)
        {
            int cursor = 0;
            for (int i = 0; i < count; i++)
            {
                int length = datagrams.Span[i].Length;
                vector[i] = new IoVector { Base = (IntPtr)(raw + cursor), Length = (nuint)length };
                header[i].Header.Name = (IntPtr)name;
                header[i].Header.NameLength = AddressLength;
                header[i].Header.Iov = (IntPtr)(vector + i);
                header[i].Header.IovLength = 1;
                cursor += length;
            }

            int sent = SendMany(descriptor, header, (uint)count, 0);
            if (sent <= 0)
            {
                return false;
            }

            for (int i = 0; i < sent; i++)
            {
                bytes += datagrams.Span[i].Length;
            }

            datagramsSent = sent;
            return true;
        }
    }

    private static bool SendSegment(int descriptor, byte[] storage, int width, SockAddrIn address)
    {
        var control = new SegmentControl
        {
            Length = 18,
            Level = SolUdp,
            Type = UdpSegment,
            Size = (ushort)width,
        };
        var headers = new MMsgHdr[1];
        var vectors = new IoVector[1];
        var names = new SockAddrIn[1];
        names[0] = address;
        fixed (byte* raw = storage)
        fixed (MMsgHdr* header = headers)
        fixed (IoVector* vector = vectors)
        fixed (SockAddrIn* name = names)
        {
            vector[0] = new IoVector { Base = (IntPtr)raw, Length = (nuint)storage.Length };
            header[0].Header.Name = (IntPtr)name;
            header[0].Header.NameLength = AddressLength;
            header[0].Header.Iov = (IntPtr)vector;
            header[0].Header.IovLength = 1;
            header[0].Header.Control = (IntPtr)(&control);
            header[0].Header.ControlLength = 24;
            return SendMany(descriptor, header, 1, 0) == 1;
        }
    }

    private static int Receive(int descriptor, Span<byte> buffer, Span<int> counts)
    {
        int count = counts.Length;
        int slice = buffer.Length / count;
        if (slice < 1)
        {
            return 0;
        }

        byte[] storage = new byte[slice * count];
        var headers = new MMsgHdr[count];
        var vectors = new IoVector[count];
        fixed (byte* raw = storage)
        fixed (MMsgHdr* header = headers)
        fixed (IoVector* vector = vectors)
        {
            for (int i = 0; i < count; i++)
            {
                vector[i] = new IoVector { Base = (IntPtr)(raw + (i * slice)), Length = (nuint)slice };
                header[i].Header.Iov = (IntPtr)(vector + i);
                header[i].Header.IovLength = 1;
            }

            int got = ReceiveMany(descriptor, header, (uint)count, MessageDontWait, IntPtr.Zero);
            if (got <= 0)
            {
                return 0;
            }

            for (int i = 0; i < got; i++)
            {
                int length = (int)headers[i].Length;
                if (length < 0 || length > slice)
                {
                    return 0;
                }

                storage.AsSpan(i * slice, length).CopyTo(buffer[(i * slice)..]);
                counts[i] = length;
            }

            return got;
        }
    }

    private static bool TryAddress(IPEndPoint destination, out SockAddrIn address)
    {
        address = default;
        if (destination.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] ip = destination.Address.GetAddressBytes();
        address.Family = 2;
        address.Port = (ushort)((destination.Port >> 8) | (destination.Port << 8));
        address.Address = (uint)ip[0] | ((uint)ip[1] << 8) | ((uint)ip[2] << 16) | ((uint)ip[3] << 24);
        return true;
    }

    private readonly struct DescriptorLease : IDisposable
    {
        private readonly SafeSocketHandle _handle;

        private readonly bool _added;

        private DescriptorLease(SafeSocketHandle handle, bool added, int value)
        {
            _handle = handle;
            _added = added;
            Value = value;
            Held = added && value > 0;
        }

        internal int Value { get; }

        internal bool Held { get; }

        internal static DescriptorLease Open(Socket socket)
        {
            SafeSocketHandle handle = socket.SafeHandle;
            bool added = false;
            handle.DangerousAddRef(ref added);
            long raw = handle.DangerousGetHandle().ToInt64();
            int value = raw > 0 && raw <= int.MaxValue ? (int)raw : 0;
            return new DescriptorLease(handle, added, value);
        }

        public void Dispose()
        {
            if (_added)
            {
                _handle.DangerousRelease();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVector
    {
        internal IntPtr Base;
        internal nuint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsgHdr
    {
        internal IntPtr Name;
        internal uint NameLength;
        internal uint Pad0;
        internal IntPtr Iov;
        internal nuint IovLength;
        internal IntPtr Control;
        internal nuint ControlLength;
        internal int Flags;
        internal int Pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MMsgHdr
    {
        internal MsgHdr Header;
        internal uint Length;
        internal uint Pad;
    }

    [StructLayout(LayoutKind.Sequential, Size = 24)]
    private struct SegmentControl
    {
        internal nuint Length;
        internal int Level;
        internal int Type;
        internal ushort Size;
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct SockAddrIn
    {
        internal ushort Family;
        internal ushort Port;
        internal uint Address;
        internal ulong Zero;
    }
}
