using System;
using System.Net;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Quic;

/// <summary>
/// Chooses options for one inbound connection.
/// The platform connection type is sealed, so this callback takes <see cref="ManagedQuicConnection"/>.
/// </summary>
/// <param name="connection">The connection that received the ClientHello.</param>
/// <param name="clientHello">The server name and protocol from that ClientHello.</param>
/// <param name="cancellationToken">A token that cancels the selection.</param>
/// <returns>The options used to continue the handshake.</returns>
public delegate ValueTask<ManagedQuicServerConnectionOptions> ManagedQuicConnectionOptionsCallback(
    ManagedQuicConnection connection,
    SslClientHelloInfo clientHello,
    CancellationToken cancellationToken);

/// <summary>Options for <see cref="ManagedQuicListener.ListenAsync"/>.</summary>
public sealed class ManagedQuicListenerOptions
{
    /// <summary>Gets or sets the local endpoint. Port 0 asks the operating system for a port.</summary>
    public IPEndPoint? ListenEndPoint { get; set; }

    /// <summary>Gets or sets the callback that supplies the certificate for each connection.</summary>
    public ManagedQuicConnectionOptionsCallback? ConnectionOptionsCallback { get; set; }

    /// <summary>Gets or sets the limit on how long a handshake may run. The default is 10 seconds.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets whether this listener may run while FIPS mode is on.</summary>
    public bool AllowManagedStackUnderFips { get; set; }
}

/// <summary>Options for <see cref="ManagedQuicConnection.ConnectAsync"/>.</summary>
public sealed class ManagedQuicClientConnectionOptions
{
    /// <summary>Gets or sets the server endpoint.</summary>
    public IPEndPoint? RemoteEndPoint { get; set; }

    /// <summary>Gets or sets the local endpoint. Port 0 asks the operating system for a port.</summary>
    public IPEndPoint? LocalEndPoint { get; set; }

    /// <summary>Gets or sets the TLS options. <see cref="SslClientAuthenticationOptions.TargetHost"/> is the server name.</summary>
    public SslClientAuthenticationOptions? ClientAuthenticationOptions { get; set; }

    /// <summary>Gets or sets the limit on how long the handshake may run. The default is 10 seconds.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets whether this connection may run while FIPS mode is on.</summary>
    public bool AllowManagedStackUnderFips { get; set; }
}

/// <summary>Options returned by <see cref="ManagedQuicConnectionOptionsCallback"/>.</summary>
public sealed class ManagedQuicServerConnectionOptions
{
    /// <summary>Gets or sets the certificate presented to the client.</summary>
    public SslServerAuthenticationOptions? ServerAuthenticationOptions { get; set; }

    /// <summary>Gets or sets whether this connection may run while FIPS mode is on.</summary>
    public bool AllowManagedStackUnderFips { get; set; }
}
