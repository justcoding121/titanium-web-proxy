using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.Helpers
{
    internal enum IpVersion
    {
        Ipv4 = 1,
        Ipv6 = 2,
    }

    internal class TcpHelper
    {
        /// <summary>
        /// Gets the extended TCP table.
        /// </summary>
        /// <returns>Collection of <see cref="TcpRow"/>.</returns>
        internal static TcpTable GetExtendedTcpTable(IpVersion ipVersion)
        {
            var tcpRows = new List<TcpRow>();

            var tcpTable = IntPtr.Zero;
            int tcpTableLength = 0;

            int ipVersionValue = ipVersion == IpVersion.Ipv4 ? NativeMethods.AfInet : NativeMethods.AfInet6;

            if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, false, ipVersionValue, (int)NativeMethods.TcpTableType.OwnerPidAll, 0) != 0)
            {
                try
                {
                    tcpTable = Marshal.AllocHGlobal(tcpTableLength);
                    if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, true, ipVersionValue, (int)NativeMethods.TcpTableType.OwnerPidAll, 0) == 0)
                    {
                        var table = (NativeMethods.TcpTable)Marshal.PtrToStructure(tcpTable, typeof(NativeMethods.TcpTable));

                        var rowPtr = (IntPtr)((long)tcpTable + Marshal.SizeOf(table.length));

                        for (int i = 0; i < table.length; ++i)
                        {
                            tcpRows.Add(new TcpRow((NativeMethods.TcpRow)Marshal.PtrToStructure(rowPtr, typeof(NativeMethods.TcpRow))));
                            rowPtr = (IntPtr)((long)rowPtr + Marshal.SizeOf(typeof(NativeMethods.TcpRow)));
                        }
                    }
                }
                finally
                {
                    if (tcpTable != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(tcpTable);
                    }
                }
            }

            return new TcpTable(tcpRows);
        }

        /// <summary>
        /// Gets the TCP row by local port number.
        /// </summary>
        /// <returns><see cref="TcpRow"/>.</returns>
        internal static TcpRow GetTcpRowByLocalPort(IpVersion ipVersion, int localPort)
        {
            var tcpTable = IntPtr.Zero;
            int tcpTableLength = 0;

            int ipVersionValue = ipVersion == IpVersion.Ipv4 ? NativeMethods.AfInet : NativeMethods.AfInet6;

            if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, false, ipVersionValue, (int)NativeMethods.TcpTableType.OwnerPidAll, 0) != 0)
            {
                try
                {
                    tcpTable = Marshal.AllocHGlobal(tcpTableLength);
                    if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, true, ipVersionValue, (int)NativeMethods.TcpTableType.OwnerPidAll, 0) == 0)
                    {
                        var table = (NativeMethods.TcpTable)Marshal.PtrToStructure(tcpTable, typeof(NativeMethods.TcpTable));

                        var rowPtr = (IntPtr)((long)tcpTable + Marshal.SizeOf(table.length));

                        for (int i = 0; i < table.length; ++i)
                        {
                            var tcpRow = (NativeMethods.TcpRow)Marshal.PtrToStructure(rowPtr, typeof(NativeMethods.TcpRow));
                            if (tcpRow.GetLocalPort() == localPort)
                            {
                                return new TcpRow(tcpRow);
                            }

                            rowPtr = (IntPtr)((long)rowPtr + Marshal.SizeOf(typeof(NativeMethods.TcpRow)));
                        }
                    }
                }
                finally
                {
                    if (tcpTable != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(tcpTable);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// relays the input clientStream to the server at the specified host name and port with the given httpCmd and headers as prefix
        /// Usefull for websocket requests
        /// </summary>
        /// <param name="clientStream"></param>
        /// <param name="serverStream"></param>
        /// <param name="onDataSend"></param>
        /// <param name="onDataReceive"></param>
        /// <returns></returns>
        internal static async Task SendRaw(Stream clientStream, Stream serverStream, int bufferSize,
            Action<byte[], int, int> onDataSend, Action<byte[], int, int> onDataReceive)
        {
            //Now async relay all server=>client & client=>server data
            var sendRelay = clientStream.CopyToAsync(serverStream, onDataSend, bufferSize);

            var receiveRelay = serverStream.CopyToAsync(clientStream, onDataReceive, bufferSize);

            await Task.WhenAll(sendRelay, receiveRelay);
        }
    }
}