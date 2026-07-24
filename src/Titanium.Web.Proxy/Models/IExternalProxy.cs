namespace Titanium.Web.Proxy.Models;

public interface IExternalProxy
{
    /// <summary>
    ///     Use default windows credentials?
    /// </summary>
    bool UseDefaultCredentials { get; set; }

    /// <summary>
    ///     Bypass this proxy for connections to localhost?
    /// </summary>
    bool BypassLocalhost { get; set; }

    ExternalProxyType ProxyType { get; set; }

    bool ProxyDnsRequests { get; set; }

    /// <summary>
    ///     Username.
    /// </summary>
    string? UserName { get; set; }

    /// <summary>
    ///     Password.
    /// </summary>
    string? Password { get; set; }

    /// <summary>
    ///     Host name.
    /// </summary>
    string HostName { get; set; }

    /// <summary>
    ///     Port.
    /// </summary>
    int Port { get; set; }

    /// <summary>
    ///     Optional next hop for an ordered two-hop HTTP upstream chain (issue #909).
    ///     When set, the proxy TCP-connects to this proxy, CONNECTs to
    ///     <c>NextHop.HostName:NextHop.Port</c>, then CONNECTs to the origin through that tunnel.
    ///     Only HTTP hops are supported; SOCKS chaining is not implemented.
    /// </summary>
    IExternalProxy? NextHop { get; set; }

    string ToString();
}