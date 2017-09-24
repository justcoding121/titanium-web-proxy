using System;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Helpers
{
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
}
