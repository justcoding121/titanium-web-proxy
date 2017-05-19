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
            List<TcpRow> tcpRows = new List<TcpRow>();

            IntPtr tcpTable = IntPtr.Zero;
            int tcpTableLength = 0;

            var ipVersionValue = ipVersion == IpVersion.Ipv4 ? NativeMethods.AfInet : NativeMethods.AfInet6;

            if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, false, ipVersionValue, (int)NativeMethods.TcpTableType.OwnerPidAll, 0) != 0)
            {
                try
                {
                    tcpTable = Marshal.AllocHGlobal(tcpTableLength);
                    if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, true, ipVersionValue, (int)NativeMethods.TcpTableType.OwnerPidAll, 0) == 0)
                    {
                        NativeMethods.TcpTable table = (NativeMethods.TcpTable)Marshal.PtrToStructure(tcpTable, typeof(NativeMethods.TcpTable));

                        IntPtr rowPtr = (IntPtr)((long)tcpTable + Marshal.SizeOf(table.length));

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
            IntPtr tcpTable = IntPtr.Zero;
            int tcpTableLength = 0;

            var ipVersionValue = ipVersion == IpVersion.Ipv4 ? NativeMethods.AfInet : NativeMethods.AfInet6;

            if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, false, ipVersionValue, (int)NativeMethods.TcpTableType.OwnerPidAll, 0) != 0)
            {
                try
                {
                    tcpTable = Marshal.AllocHGlobal(tcpTableLength);
                    if (NativeMethods.GetExtendedTcpTable(tcpTable, ref tcpTableLength, true, ipVersionValue, (int)NativeMethods.TcpTableType.OwnerPidAll, 0) == 0)
                    {
                        NativeMethods.TcpTable table = (NativeMethods.TcpTable)Marshal.PtrToStructure(tcpTable, typeof(NativeMethods.TcpTable));

                        IntPtr rowPtr = (IntPtr)((long)tcpTable + Marshal.SizeOf(table.length));

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
        /// <param name="server"></param>
        /// <param name="remoteHostName"></param>
        /// <param name="remotePort"></param>
        /// <param name="httpCmd"></param>
        /// <param name="httpVersion"></param>
        /// <param name="requestHeaders"></param>
        /// <param name="isHttps"></param>
        /// <param name="clientStream"></param>
        /// <param name="tcpConnectionFactory"></param>
        /// <returns></returns>
        internal static async Task SendRaw(ProxyServer server,
            string remoteHostName, int remotePort,
            string httpCmd, Version httpVersion, Dictionary<string, HttpHeader> requestHeaders,
            bool isHttps,
            Stream clientStream, TcpConnectionFactory tcpConnectionFactory)
        {
            //prepare the prefix content
            StringBuilder sb = null;
            if (httpCmd != null || requestHeaders != null)
            {
                sb = new StringBuilder();

                if (httpCmd != null)
                {
                    sb.Append(httpCmd);
                    sb.Append(ProxyConstants.NewLine);
                }

                if (requestHeaders != null)
                {
                    foreach (var header in requestHeaders.Select(t => t.Value.ToString()))
                    {
                        sb.Append(header);
                        sb.Append(ProxyConstants.NewLine);
                    }
                }

                sb.Append(ProxyConstants.NewLine);
            }

            var tcpConnection = await tcpConnectionFactory.CreateClient(server,
                remoteHostName, remotePort,
                httpVersion, isHttps,
                null, null, clientStream);

            try
            {
                Stream tunnelStream = tcpConnection.Stream;

                //Now async relay all server=>client & client=>server data
                var sendRelay = clientStream.CopyToAsync(sb?.ToString() ?? string.Empty, tunnelStream);

                var receiveRelay = tunnelStream.CopyToAsync(string.Empty, clientStream);

                await Task.WhenAll(sendRelay, receiveRelay);
            }
            finally
            {
                tcpConnection.Dispose();
                Interlocked.Decrement(ref server.serverConnectionCount);
            }
        }
    }
}
