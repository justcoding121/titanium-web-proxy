namespace Titanium.Web.Proxy.Models;

/// <summary>
///     Sends a TCP passthrough connection to a cluster when the client's TLS server name matches.
///     <see cref="ServerName" /> is an exact host or <c>*.example.com</c> (any host below that suffix).
/// </summary>
public sealed class PassthroughSniRoute
{
    public PassthroughSniRoute(string serverName, string clusterId)
    {
        ServerName = serverName;
        ClusterId = clusterId;
    }

    /// <summary>Exact host name, or one leading <c>*.</c> label followed by a suffix.</summary>
    public string ServerName { get; }

    /// <summary>Id of the cluster that receives matching connections.</summary>
    public string ClusterId { get; }
}

/// <summary>PROXY protocol header a passthrough listener writes to the upstream before client bytes.</summary>
public enum PassthroughProxyProtocolMode
{
    /// <summary>Send nothing extra.</summary>
    None = 0,

    /// <summary>Send a PROXY protocol v2 header carrying the client and listener addresses.</summary>
    V2 = 1,
}
