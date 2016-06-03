using System;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;

namespace Titanium.Web.Proxy.Shared
{
    /// <summary>
    /// Literals shared by Proxy Server
    /// </summary>
    internal class Constants
    {
        internal static readonly int BUFFER_SIZE = 8192;

        internal static readonly char[] SpaceSplit = { ' ' };
        internal static readonly char[] ColonSplit = { ':' };
        internal static readonly char[] SemiColonSplit = { ';' };

        internal static readonly byte[] NewLineBytes = Encoding.ASCII.GetBytes(Environment.NewLine);

        internal static readonly byte[] ChunkEnd =
            Encoding.ASCII.GetBytes(0.ToString("x2") + Environment.NewLine + Environment.NewLine);

        internal static SslProtocols SupportedProtocols = SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12 | SslProtocols.Ssl3;
    }
}
