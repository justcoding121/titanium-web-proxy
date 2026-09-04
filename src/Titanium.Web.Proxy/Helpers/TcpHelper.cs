using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.Helpers;

internal static class TcpHelper
{
    /// <summary>
    ///     Gets the process id by local port number on the current OS.
    /// </summary>
    /// <returns>Process id, or 0 when not found.</returns>
    internal static int GetProcessIdByLocalPort(AddressFamily addressFamily, int localPort)
    {
        if (RunTime.IsWindows)
        {
            return GetProcessIdByLocalPortWindows(addressFamily, localPort);
        }

        if (RunTime.IsLinux)
        {
            return GetProcessIdByLocalPortLinux(addressFamily, localPort);
        }

        if (RunTime.IsMac)
        {
            return GetProcessIdByLocalPortMac(addressFamily, localPort);
        }

        return 0;
    }

    private static unsafe int GetProcessIdByLocalPortWindows(AddressFamily addressFamily, int localPort) // NOSONAR S6640
    {
        var tcpTable = IntPtr.Zero;
        var tcpTableLength = 0;

        var addressFamilyValue =
            addressFamily == AddressFamily.InterNetwork ? NativeMethods.AfInet : NativeMethods.AfInet6;
        const int allPid = (int)NativeMethods.TcpTableType.OwnerPidAll;

        if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, false, addressFamilyValue, allPid, 0) != 0)
            try
            {
                tcpTable = Marshal.AllocHGlobal(tcpTableLength);
                if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, true, addressFamilyValue, allPid,
                        0) == 0)
                {
                    var rowCount = *(int*)tcpTable;
                    var portInNetworkByteOrder = ToNetworkByteOrder((uint)localPort);

                    if (addressFamily == AddressFamily.InterNetwork)
                    {
                        var rowPtr = (NativeMethods.TcpRow*)(tcpTable + 4);

                        for (var i = 0; i < rowCount; ++i)
                        {
                            if (rowPtr->localPort == portInNetworkByteOrder) return rowPtr->owningPid;

                            rowPtr++;
                        }
                    }
                    else
                    {
                        var rowPtr = (NativeMethods.Tcp6Row*)(tcpTable + 4);

                        for (var i = 0; i < rowCount; ++i)
                        {
                            if (rowPtr->localPort == portInNetworkByteOrder) return rowPtr->owningPid;

                            rowPtr++;
                        }
                    }
                }
            }
            finally
            {
                if (tcpTable != IntPtr.Zero) Marshal.FreeHGlobal(tcpTable);
            }

        return 0;
    }

    private static int GetProcessIdByLocalPortLinux(AddressFamily addressFamily, int localPort)
    {
        var path = addressFamily == AddressFamily.InterNetwork ? "/proc/net/tcp" : "/proc/net/tcp6";
        string contents;
        try
        {
            contents = File.ReadAllText(path);
        }
        catch
        {
            return 0;
        }

        if (!LinuxProcNetTcp.TryFindInodeForLocalPort(contents, localPort, out var inode))
        {
            return 0;
        }

        return LinuxProcNetTcp.FindProcessIdByInode(inode);
    }

    [SupportedOSPlatform("macos")]
    private static int GetProcessIdByLocalPortMac(AddressFamily addressFamily, int localPort)
    {
        var expectedFamily = addressFamily == AddressFamily.InterNetwork
            ? NativeMethods.DarwinAfInet
            : NativeMethods.DarwinAfInet6;

        int listBytes;
        try
        {
            listBytes = NativeMethods.ProcListPids(NativeMethods.ProcAllPids, 0, null, 0);
        }
        catch
        {
            return 0;
        }

        if (listBytes <= 0)
        {
            return 0;
        }

        var pids = new int[listBytes / sizeof(int)];
        listBytes = NativeMethods.ProcListPids(NativeMethods.ProcAllPids, 0, pids, listBytes);
        if (listBytes <= 0)
        {
            return 0;
        }

        var pidCount = listBytes / sizeof(int);
        var socketInfo = new byte[NativeMethods.SocketFdInfoSize];

        for (var i = 0; i < pidCount; i++)
        {
            var pid = pids[i];
            if (pid <= 0)
            {
                continue;
            }

            int fdBytes;
            try
            {
                fdBytes = NativeMethods.ProcPidInfo(pid, NativeMethods.ProcPidListFds, 0, null, 0);
            }
            catch
            {
                continue;
            }

            if (fdBytes <= 0)
            {
                continue;
            }

            var fdBuffer = new byte[fdBytes];
            fdBytes = NativeMethods.ProcPidInfo(pid, NativeMethods.ProcPidListFds, 0, fdBuffer, fdBytes);
            if (fdBytes <= 0)
            {
                continue;
            }

            var fdCount = fdBytes / NativeMethods.ProcFdInfoSize;
            for (var f = 0; f < fdCount; f++)
            {
                var offset = f * NativeMethods.ProcFdInfoSize;
                var fd = BinaryPrimitives.ReadInt32LittleEndian(fdBuffer.AsSpan(offset, 4));
                var fdType = BinaryPrimitives.ReadUInt32LittleEndian(fdBuffer.AsSpan(offset + 4, 4));
                if (fdType != NativeMethods.ProxFdTypeSocket)
                {
                    continue;
                }

                int written;
                try
                {
                    written = NativeMethods.ProcPidFdInfo(pid, fd, NativeMethods.ProcPidFdSocketInfo, socketInfo,
                        NativeMethods.SocketFdInfoSize);
                }
                catch
                {
                    continue;
                }

                if (written < NativeMethods.SocketFdInfoSize)
                {
                    continue;
                }

                var soiKind = BinaryPrimitives.ReadInt32LittleEndian(
                    socketInfo.AsSpan(NativeMethods.SocketFdInfoOffsetSoiKind, 4));
                if (soiKind != NativeMethods.SockInfoTcp)
                {
                    continue;
                }

                var soiProtocol = BinaryPrimitives.ReadInt32LittleEndian(
                    socketInfo.AsSpan(NativeMethods.SocketFdInfoOffsetSoiProtocol, 4));
                if (soiProtocol != NativeMethods.IpProtoTcp)
                {
                    continue;
                }

                var soiFamily = BinaryPrimitives.ReadInt32LittleEndian(
                    socketInfo.AsSpan(NativeMethods.SocketFdInfoOffsetSoiFamily, 4));
                if (soiFamily != expectedFamily)
                {
                    continue;
                }

                var lportNet = BinaryPrimitives.ReadInt32LittleEndian(
                    socketInfo.AsSpan(NativeMethods.SocketFdInfoOffsetInsiLport, 4));
                var port = System.Net.IPAddress.NetworkToHostOrder(unchecked((short)lportNet)) & 0xFFFF;
                if (port == localPort)
                {
                    return pid;
                }
            }
        }

        return 0;
    }

    /// <summary>
    ///     Converts 32-bit integer from native byte order (little-endian)
    ///     to network byte order for port,
    ///     switches 0th and 1st bytes, and 2nd and 3rd bytes
    /// </summary>
    private static uint ToNetworkByteOrder(uint port)
    {
        return ((port >> 8) & 0x00FF00FFu) | ((port << 8) & 0xFF00FF00u);
    }

    /// <summary>
    ///     relays the input clientStream to the server at the specified host name and port with the given httpCmd and headers
    ///     as prefix
    ///     Useful for websocket requests
    ///     Task-based Asynchronous Pattern
    /// </summary>
    private static async Task SendRawTap(Stream clientStream, Stream serverStream, IBufferPool bufferPool,
        Action<byte[], int, int>? onDataSend, Action<byte[], int, int>? onDataReceive,
        CancellationTokenSource cancellationTokenSource)
    {
        // Now async relay all server=>client & client=>server data
        var sendRelay =
            clientStream.CopyToAsync(serverStream, onDataSend, bufferPool, cancellationTokenSource.Token);
        var receiveRelay =
            serverStream.CopyToAsync(clientStream, onDataReceive, bufferPool, cancellationTokenSource.Token);

        await Task.WhenAny(sendRelay, receiveRelay);
        await cancellationTokenSource.CancelAsync();

        await Task.WhenAll(sendRelay, receiveRelay);
    }

    /// <summary>
    ///     relays the input clientStream to the server at the specified host name and port with the given httpCmd and headers
    ///     as prefix
    ///     Useful for websocket requests
    /// </summary>
    internal static Task SendRaw(Stream clientStream, Stream serverStream, IBufferPool bufferPool,
        Action<byte[], int, int>? onDataSend, Action<byte[], int, int>? onDataReceive,
        CancellationTokenSource cancellationTokenSource,
        ILogger logger)
    {
        // Preserve the legacy APM callback path for callers that still use Begin/End methods.
        return SendRawTap(clientStream, serverStream, bufferPool, onDataSend, onDataReceive,
            cancellationTokenSource);
    }
}
