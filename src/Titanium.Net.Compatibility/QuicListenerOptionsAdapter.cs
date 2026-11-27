#pragma warning disable CA1416 // The platform option type is the translation source. This adapter does not start that stack.
using System;
using System.Collections.Generic;
using System.Net.Quic;
using Titanium.Quic;

namespace Titanium.Net.Compatibility;

/// <summary>
/// Copies a platform <see cref="QuicListenerOptions"/> onto <see cref="ManagedQuicListenerOptions"/>.
/// The platform listener does not carry a handshake timeout, so the managed default stays.
/// Application protocols stay on the server authentication options the callback returns.
/// </summary>
public static class QuicListenerOptionsAdapter
{
    /// <summary>
    /// Builds managed listener options from <paramref name="options"/> and <paramref name="connectionOptionsCallback"/>.
    /// </summary>
    /// <param name="options">The platform options.</param>
    /// <param name="connectionOptionsCallback">Selects the certificate and protocols for each connection.</param>
    /// <returns>Options the managed listener can bind with.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="connectionOptionsCallback"/> is null.</exception>
    /// <exception cref="ArgumentException">A setting cannot be applied.</exception>
    public static ManagedQuicListenerOptions ToManaged(
        QuicListenerOptions options,
        ManagedQuicConnectionOptionsCallback connectionOptionsCallback)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connectionOptionsCallback);
        IReadOnlyList<string> unsupported = UnsupportedSettings(options);
        if (unsupported.Count > 0)
        {
            throw new ArgumentException(
                "The managed listener cannot apply these settings yet: " + string.Join(", ", unsupported) + ".");
        }

        return new ManagedQuicListenerOptions
        {
            ListenEndPoint = options.ListenEndPoint,
            ConnectionOptionsCallback = connectionOptionsCallback,
        };
    }

    /// <summary>
    /// Names the settings on <paramref name="options"/> that <see cref="ToManaged"/> will refuse.
    /// <see cref="QuicListenerOptions.ApplicationProtocols"/> is refused because the managed listener
    /// reads protocols from the server authentication options.
    /// </summary>
    /// <param name="options">The platform options.</param>
    /// <returns>The setting names, in a stable order. Empty when <see cref="ToManaged"/> can copy the options.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static IReadOnlyList<string> UnsupportedSettings(QuicListenerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var defaults = new QuicListenerOptions();
        var names = new List<string>();
        if (options.ApplicationProtocols is { Count: > 0 })
        {
            names.Add(nameof(QuicListenerOptions.ApplicationProtocols));
        }

        if (options.ListenBacklog != defaults.ListenBacklog)
        {
            names.Add(nameof(QuicListenerOptions.ListenBacklog));
        }

        return names;
    }
}
#pragma warning restore CA1416
