using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Helpers
{
    internal enum IpVersion
    {
        Ipv4 = 1,
        Ipv6 = 2,
    }

    internal partial class NativeMethods
    {
        internal const int AfInet = 2;
        internal const int AfInet6 = 23;

        internal enum TcpTableType
        {
            BasicListener,
            BasicConnections,
            BasicAll,
            OwnerPidListener,
            OwnerPidConnections,
            OwnerPidAll,
            OwnerModuleListener,
            OwnerModuleConnections,
            OwnerModuleAll,
        }

        /// <summary>
        /// <see href="http://msdn2.microsoft.com/en-us/library/aa366921.aspx"/>
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct TcpTable
        {
            public uint length;
            public TcpRow row;
        }

        /// <summary>
        /// <see href="http://msdn2.microsoft.com/en-us/library/aa366913.aspx"/>
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct TcpRow
        {
            public TcpState state;
            public uint localAddr;
            public byte localPort1;
            public byte localPort2;
            public byte localPort3;
            public byte localPort4;
            public uint remoteAddr;
            public byte remotePort1;
            public byte remotePort2;
            public byte remotePort3;
            public byte remotePort4;
            public int owningPid;
        }

        /// <summary>
        /// <see href="http://msdn2.microsoft.com/en-us/library/aa365928.aspx"/>
        /// </summary>
        [DllImport("iphlpapi.dll", SetLastError = true)]
        internal static extern uint GetExtendedTcpTable(IntPtr tcpTable, ref int size, bool sort, int ipVersion, int tableClass, int reserved);
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
        /// <param name="httpCmd"></param>
        /// <param name="requestHeaders"></param>
        /// <param name="clientStream"></param>
        /// <param name="connection"></param>
        /// <param name="onDataSend"></param>
        /// <param name="onDataReceive"></param>
        /// <returns></returns>
        internal static async Task SendRaw(string httpCmd, IEnumerable<HttpHeader> requestHeaders, Stream clientStream, TcpConnection connection, Action<byte[], int, int> onDataSend, Action<byte[], int, int> onDataReceive)
        {
            //prepare the prefix content
            if (httpCmd != null || requestHeaders != null)
            {
                using (var ms = new MemoryStream())
                using (var writer = new StreamWriter(ms, Encoding.ASCII) { NewLine = ProxyConstants.NewLine })
                {
                    if (httpCmd != null)
                    {
                        writer.WriteLine(httpCmd);
                    }

                    if (requestHeaders != null)
                    {
                        foreach (string header in requestHeaders.Select(t => t.ToString()))
                        {
                            writer.WriteLine(header);
                        }
                    }

                    writer.WriteLine();
                    writer.Flush();

                    var data = ms.ToArray();
                    await ms.WriteAsync(data, 0, data.Length);
                    onDataSend?.Invoke(data, 0, data.Length);
                }
            }

            var tunnelStream = connection.Stream;

            //Now async relay all server=>client & client=>server data
            var sendRelay = clientStream.CopyToAsync(tunnelStream, onDataSend);

            var receiveRelay = tunnelStream.CopyToAsync(clientStream, onDataReceive);

            await Task.WhenAll(sendRelay, receiveRelay);
        }
    }
}
