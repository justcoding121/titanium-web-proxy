using System.Net;
using System.Net.Sockets;

namespace Titanium.Web.Proxy.Exceptions;

/// <summary>
///     A listening socket could not be bound. The message names the endpoint and the reason.
/// </summary>
public sealed class ProxyListenException : ProxyException
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ProxyListenException"/> class.
    /// </summary>
    public ProxyListenException(string endpointKind, IPAddress ipAddress, int port, SocketException inner)
        : base(BuildMessage(endpointKind, ipAddress, port, inner), inner)
    {
        EndpointKind = endpointKind;
        IpAddress = ipAddress;
        Port = port;
        SocketError = inner.SocketErrorCode;
        NativeErrorCode = inner.NativeErrorCode;
        Data["ipAddress"] = ipAddress;
        Data["port"] = port;
    }

    /// <summary>Listener kind, such as <c>explicit</c> or <c>transparent</c>.</summary>
    public string EndpointKind { get; }

    /// <summary>Address that could not be bound.</summary>
    public IPAddress IpAddress { get; }

    /// <summary>Port that could not be bound.</summary>
    public int Port { get; }

    /// <summary>Socket error reported by the bind.</summary>
    public SocketError SocketError { get; }

    /// <summary>Platform error code (WSAEADDRINUSE is 10048, EADDRINUSE is 98).</summary>
    public int NativeErrorCode { get; }

    /// <summary>True when the port is already taken.</summary>
    public bool IsAddressInUse =>
        SocketError == SocketError.AddressAlreadyInUse || NativeErrorCode is 10048 or 98;

    private static string BuildMessage(string endpointKind, IPAddress ipAddress, int port, SocketException inner)
    {
        var where = $"{endpointKind} listener {FormatAddress(ipAddress)}:{port}";
        if (inner.SocketErrorCode == SocketError.AddressAlreadyInUse || inner.NativeErrorCode is 10048 or 98)
        {
            return $"Failed to bind {where}: address already in use.";
        }

        if (inner.SocketErrorCode == SocketError.AccessDenied || inner.NativeErrorCode is 10013 or 13)
        {
            return $"Failed to bind {where}: access denied (the port is privileged, reserved, or excluded by the operating system).";
        }

        return $"Failed to bind {where}: {inner.Message} ({inner.SocketErrorCode}).";
    }

    private static string FormatAddress(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
}
