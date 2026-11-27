#pragma warning disable CA1416 // The platform option type is the translation source. This adapter does not start that stack.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using Titanium.Quic;

namespace Titanium.Net.Compatibility;

/// <summary>
/// Copies a platform <see cref="QuicClientConnectionOptions"/> onto <see cref="ManagedQuicClientConnectionOptions"/>.
/// A setting the managed client cannot apply is refused.
/// </summary>
public static class QuicClientOptionsAdapter
{
    /// <summary>
    /// Builds managed client options from <paramref name="options"/>.
    /// </summary>
    /// <param name="options">The platform options.</param>
    /// <returns>Options the managed client can connect with.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The remote endpoint is not an IP endpoint, or a setting cannot be applied.</exception>
    public static ManagedQuicClientConnectionOptions ToManaged(QuicClientConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<string> unsupported = UnsupportedSettings(options);
        if (unsupported.Count > 0)
        {
            throw new ArgumentException(
                "The managed client cannot apply these settings yet: " + string.Join(", ", unsupported) + ".");
        }

        if (options.RemoteEndPoint is not IPEndPoint remote)
        {
            throw new ArgumentException("The managed client takes an IP endpoint. Resolve the name first.", nameof(options));
        }

        return new ManagedQuicClientConnectionOptions
        {
            RemoteEndPoint = remote,
            LocalEndPoint = options.LocalEndPoint,
            ClientAuthenticationOptions = options.ClientAuthenticationOptions,
            HandshakeTimeout = options.HandshakeTimeout,
            KeepAliveInterval = options.KeepAliveInterval,
            IdleTimeout = options.IdleTimeout,
            MaxInboundBidirectionalStreams = options.MaxInboundBidirectionalStreams,
            MaxInboundUnidirectionalStreams = options.MaxInboundUnidirectionalStreams,
            DefaultStreamErrorCode = options.DefaultStreamErrorCode,
        };
    }

    /// <summary>
    /// Names the settings on <paramref name="options"/> that <see cref="ToManaged"/> will refuse.
    /// A host name in <see cref="QuicClientConnectionOptions.RemoteEndPoint"/> is one of them.
    /// </summary>
    /// <param name="options">The platform options.</param>
    /// <returns>The setting names, in a stable order. Empty when <see cref="ToManaged"/> can copy the options.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static IReadOnlyList<string> UnsupportedSettings(QuicClientConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var defaults = new QuicClientConnectionOptions();
        var names = new List<string>();
        if (options.RemoteEndPoint is not null and not IPEndPoint)
        {
            names.Add(nameof(QuicClientConnectionOptions.RemoteEndPoint));
        }

        AddIfDifferent(names, nameof(QuicClientConnectionOptions.DefaultCloseErrorCode), options.DefaultCloseErrorCode, defaults.DefaultCloseErrorCode);
        if (options.StreamCapacityCallback is not null)
        {
            names.Add(nameof(QuicClientConnectionOptions.StreamCapacityCallback));
        }

        AddWindowIfDifferent(names, options.InitialReceiveWindowSizes, defaults.InitialReceiveWindowSizes);
        return names;
    }

    private static void AddWindowIfDifferent(List<string> names, QuicReceiveWindowSizes? value, QuicReceiveWindowSizes? baseline)
    {
        if (value is null && baseline is null)
        {
            return;
        }

        if (value is null || baseline is null
            || value.Connection != baseline.Connection
            || value.LocallyInitiatedBidirectionalStream != baseline.LocallyInitiatedBidirectionalStream
            || value.RemotelyInitiatedBidirectionalStream != baseline.RemotelyInitiatedBidirectionalStream
            || value.UnidirectionalStream != baseline.UnidirectionalStream)
        {
            names.Add(nameof(QuicClientConnectionOptions.InitialReceiveWindowSizes));
        }
    }

    private static void AddIfDifferent<T>(List<string> names, string name, T value, T baseline)
        where T : IEquatable<T>
    {
        if (!value.Equals(baseline))
        {
            names.Add(name);
        }
    }
}
#pragma warning restore CA1416
