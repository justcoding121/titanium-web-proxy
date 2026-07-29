namespace Titanium.Web.Proxy.Exceptions;

/// <summary>
///     Identifies which configured proxy timeout elapsed.
/// </summary>
public enum ProxyTimeoutKind
{
    /// <summary>
    ///     TCP connect race against <see cref="ProxyServer.ConnectTimeOutSeconds" />.
    /// </summary>
    Connect = 0,

    /// <summary>
    ///     Waiting for the origin response status line / headers.
    /// </summary>
    ResponseHeader = 1,

    /// <summary>
    ///     Idle window while reading (stalled body / header read).
    /// </summary>
    IdleRead = 2,

    /// <summary>
    ///     Idle window while writing (stalled body / header write).
    /// </summary>
    IdleWrite = 3,

    /// <summary>
    ///     Total per-request / per-session deadline.
    /// </summary>
    Request = 4,

    /// <summary>
    ///     Waiting for a client to finish sending the request line and headers. <c>Socket.ReceiveTimeout</c>
    ///     does not bound this: it only fails a single blocking <c>Receive</c> call, not the asynchronous
    ///     reads this proxy actually issues, so without this deadline a client that opens a connection and
    ///     trickles the request line/headers arbitrarily slowly (or not at all) ties up a server-side
    ///     read loop indefinitely.
    /// </summary>
    ClientHeader = 5
}
