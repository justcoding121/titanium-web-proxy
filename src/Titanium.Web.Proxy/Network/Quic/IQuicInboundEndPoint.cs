#pragma warning disable CA1416 // QUIC APIs are platform-specific; runtime check guards usage
using System;
using System.Net;
using System.Net.Quic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Network.Quic;

/// <summary>
///     Internal surface shared by UDP-only <see cref="TransparentQuicProxyEndPoint" /> and
///     dual-listen <see cref="TransparentProxyEndPoint" /> (when <c>EnableHttp3</c> is on).
/// </summary>
internal interface IQuicInboundEndPoint
{
    IPAddress IpAddress { get; }

    int Port { get; }

    /// <summary>
    ///     Assigns the local listen port after the OS binds an ephemeral port.
    /// </summary>
    void AssignPort(int port);

    string GenericCertificateName { get; }

    string? ForwardHost { get; }

    int? ForwardPort { get; }

    int MaxInboundBidirectionalStreams { get; }

    int MaxInboundUnidirectionalStreams { get; }

    TimeSpan HandshakeTimeout { get; }

    TimeSpan IdleTimeout { get; }

    QuicListener? QuicListener { get; set; }

    ConditionalWeakTable<QuicConnection, BeforeQuicAuthenticateEventArgs> PendingQuicAuthArgs { get; }

    /// <summary>
    ///     Optional NAT original-destination resolver. Dual-listen reverse endpoints return null and
    ///     rely on <see cref="ForwardHost" />.
    /// </summary>
    IOriginalDestinationResolver? OriginalDestinationResolver { get; }

    /// <summary>
    ///     The <see cref="ProxyEndPoint" /> instance passed into session event args.
    /// </summary>
    ProxyEndPoint ProxyEndPoint { get; }

    Task InvokeBeforeQuicAuthenticate(ProxyServer proxyServer, BeforeQuicAuthenticateEventArgs args,
        ILogger logger);
}
