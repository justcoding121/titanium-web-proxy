using System.Text;

namespace Titanium.Web.Proxy.Shared
{
    /// <summary>
    /// Literals shared by Proxy Server
    /// </summary>
    internal class ProxyConstants
    {
        internal static readonly char[] SpaceSplit = { ' ' };
        internal static readonly char[] ColonSplit = { ':' };
        internal static readonly char[] SemiColonSplit = { ';' };

        internal static readonly byte[] NewLineBytes = Encoding.ASCII.GetBytes(NewLine);

        internal static readonly byte[] ChunkEnd =
            Encoding.ASCII.GetBytes(0.ToString("x2") + NewLine + NewLine);

        internal const string NewLine = "\r\n";
    }
}
