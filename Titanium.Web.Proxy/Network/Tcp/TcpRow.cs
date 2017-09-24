using System.Net;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Network.Tcp
{
    /// <summary>
    /// Represents a managed interface of IP Helper API TcpRow struct
    /// <see href="http://msdn2.microsoft.com/en-us/library/aa366913.aspx"/>
    /// </summary>
    internal class TcpRow
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TcpRow"/> class.
        /// </summary>
        /// <param name="tcpRow">TcpRow struct.</param>
        internal TcpRow(NativeMethods.TcpRow tcpRow)
        {
            ProcessId = tcpRow.owningPid;

            LocalPort = tcpRow.GetLocalPort();
            LocalAddress = tcpRow.localAddr;

            RemotePort = tcpRow.GetRemotePort();
            RemoteAddress = tcpRow.remoteAddr;
        }

        /// <summary>
        /// Gets the local end point address.
        /// </summary>
        internal long LocalAddress { get; }

        /// <summary>
        /// Gets the local end point port.
        /// </summary>
        internal int LocalPort { get; }

        /// <summary>
        /// Gets the local end point.
        /// </summary>
        internal IPEndPoint LocalEndPoint => new IPEndPoint(LocalAddress, LocalPort);

        /// <summary>
        /// Gets the remote end point address.
        /// </summary>
        internal long RemoteAddress { get; }

        /// <summary>
        /// Gets the remote end point port.
        /// </summary>
        internal int RemotePort { get; }

        /// <summary>
        /// Gets the remote end point.
        /// </summary>
        internal IPEndPoint RemoteEndPoint => new IPEndPoint(RemoteAddress, RemotePort);

        /// <summary>
        /// Gets the process identifier.
        /// </summary>
        internal int ProcessId { get; }
    }
}
