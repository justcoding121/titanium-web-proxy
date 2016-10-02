using System.Net;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Tcp
{
    /// <summary>
    /// Represents a managed interface of IP Helper API TcpRow struct
    /// <see cref="http://msdn2.microsoft.com/en-us/library/aa366913.aspx"/>
    /// </summary>
    internal class TcpRow
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TcpRow"/> class.
        /// </summary>
        /// <param name="tcpRow">TcpRow struct.</param>
        public TcpRow(NativeMethods.TcpRow tcpRow)
        {
            ProcessId = tcpRow.owningPid;

            int localPort = (tcpRow.localPort1 << 8) + (tcpRow.localPort2) + (tcpRow.localPort3 << 24) + (tcpRow.localPort4 << 16);
            long localAddress = tcpRow.localAddr;
            LocalEndPoint = new IPEndPoint(localAddress, localPort);
        }

        /// <summary>
        /// Gets the local end point.
        /// </summary>
        public IPEndPoint LocalEndPoint { get; private set; }

        /// <summary>
        /// Gets the process identifier.
        /// </summary>
        public int ProcessId { get; private set; }
    }
}