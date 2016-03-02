using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.EventArguments
{
    /// <summary>
    /// This class wraps Tcp connection to Server
    /// </summary>
    public class ProxyClient
    {
        /// <summary>
        /// TcpClient used to communicate with server
        /// </summary>
        internal TcpClient TcpClient { get; set; }

        /// <summary>
        /// holds the stream to server
        /// </summary>
        internal Stream ClientStream { get; set; }

        /// <summary>
        /// Used to read line by line from server
        /// </summary>
        internal CustomBinaryReader ClientStreamReader { get; set; }

        /// <summary>
        /// used to write line by line to server
        /// </summary>
        internal StreamWriter ClientStreamWriter { get; set; }

    }
}
