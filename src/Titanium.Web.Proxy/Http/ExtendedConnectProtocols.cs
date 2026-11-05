using System;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     Extended CONNECT <c>:protocol</c> tokens. Classification is per connection:
///     <see cref="ConnectionAdvertisesConnect"/> is the bit passed into one
///     <c>SendHttp2</c> call. It is not an OR of every listener on
///     <c>ProxyServer.EnableRfc8441</c>.
/// </summary>
internal static class ExtendedConnectProtocols
{
    public const string WebSocket = "websocket";

    public const string WebTransport = "webtransport";

    public const string ConnectTcp = "connect-tcp";

    public const string ConnectUdp = "connect-udp";

    public static bool IsWebSocket(string? protocol) =>
        string.Equals(protocol, WebSocket, StringComparison.OrdinalIgnoreCase);

    public static bool IsWebTransport(string? protocol) =>
        string.Equals(protocol, WebTransport, StringComparison.OrdinalIgnoreCase);

    public static bool IsConnectTcp(string? protocol) =>
        string.Equals(protocol, ConnectTcp, StringComparison.OrdinalIgnoreCase);

    public static bool IsConnectUdp(string? protocol) =>
        string.Equals(protocol, ConnectUdp, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     True when <paramref name="enableRfc8441"/> or <paramref name="advertisesConnectProtocol"/>
    ///     is set for this connection. Compressed-relay topology stays eligible only when both are false.
    /// </summary>
    public static bool ConnectionAdvertisesConnect(bool enableRfc8441, bool advertisesConnectProtocol) =>
        enableRfc8441 || advertisesConnectProtocol;

    /// <summary>
    ///     Token allow-list for this connection. <c>websocket</c> and <c>connect-tcp</c> follow
    ///     their support flags. <c>webtransport</c> and <c>connect-udp</c> stay refused.
    /// </summary>
    public static bool IsSupported(string? protocol, ExtendedConnectSupport support)
    {
        if (IsWebSocket(protocol))
            return support.WebSocket;
        if (IsWebTransport(protocol))
            return support.WebTransport;
        if (IsConnectTcp(protocol))
            return support.ConnectTcp;

        return false;
    }

    /// <summary>
    ///     501 body for a token this connection does not accept. The text matches the historical
    ///     websocket-only message. Reserved tokens are classified here so the allow-list stays one place.
    /// </summary>
    public static string UnsupportedMessage(string? protocol, ExtendedConnectSupport support, string rfc = "8441")
    {
        var shown = protocol ?? "unknown";
        if (shown.Length == 0
            && (IsWebTransport(protocol) || IsConnectTcp(protocol) || IsConnectUdp(protocol) || support.HasReservedTokens))
            shown = "unknown";

        return $"RFC {rfc} extended CONNECT (protocol: {shown}) " +
               "is not supported by this proxy. Only 'websocket' is implemented.";
    }
}

/// <summary>
///     Which extended CONNECT tokens this connection may accept.
///     Each flag is set by the listener that owns the connection, not by a process-wide OR.
/// </summary>
internal readonly struct ExtendedConnectSupport
{
    public ExtendedConnectSupport(bool webSocket, bool webTransport = false, bool connectTcp = false,
        bool connectUdp = false)
    {
        WebSocket = webSocket;
        WebTransport = webTransport;
        ConnectTcp = connectTcp;
        ConnectUdp = connectUdp;
    }

    public bool WebSocket { get; }

    /// <summary>Reserved for the WebTransport slice. Not accepted yet.</summary>
    public bool WebTransport { get; }

    /// <summary>Accept <c>connect-tcp</c> on this connection.</summary>
    public bool ConnectTcp { get; }

    /// <summary>Reserved for CONNECT-UDP on an existing listener. Not accepted yet.</summary>
    public bool ConnectUdp { get; }

    /// <summary>
    ///     Support derived from the process RFC 8441 flag. A masque listener does not set that flag;
    ///     it passes <c>enableRfc8441: true</c> only into its own <c>SendHttp2</c> call.
    /// </summary>
    public static ExtendedConnectSupport ForProcess(bool enableRfc8441) => new(webSocket: enableRfc8441);

    /// <summary>
    ///     True when a later-slice flag is set. Those tokens are still refused; the read keeps the
    ///     reserved flags observable for the characterization tests and for the next slices.
    /// </summary>
    public bool HasReservedTokens => WebTransport || ConnectTcp || ConnectUdp;
}
