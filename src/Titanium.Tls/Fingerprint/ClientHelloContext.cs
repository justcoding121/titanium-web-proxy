using System;
using System.Collections.Generic;

namespace Titanium.Tls;

/// <summary>
/// Inputs for one ClientHello. Pass the same <see cref="HelloRetryState"/> on the
/// ClientHello that follows a HelloRetryRequest so the extension order and GREASE values stay put.
/// </summary>
public sealed class ClientHelloContext
{
    /// <summary>The DNS name to put in the server_name extension. A null name omits that extension.</summary>
    public string? ServerName { get; set; }

    /// <summary>Protocols for the ALPN extension. Null uses the profile's own list.</summary>
    public IReadOnlyList<string>? ApplicationProtocols { get; set; }

    /// <summary>
    /// Key-share bytes keyed by the named group. The profile places these bytes; it does not invent them.
    /// A GREASE share is the exception and is filled from the entropy source.
    /// </summary>
    public IReadOnlyDictionary<ushort, byte[]>? KeyShares { get; set; }

    /// <summary>A session ticket. When set, the profile appends <c>pre_shared_key</c> as the last extension.</summary>
    public byte[]? SessionTicket { get; set; }

    /// <summary>The PSK binder that matches <see cref="SessionTicket"/>.</summary>
    public byte[]? PskBinder { get; set; }

    /// <summary>Opaque state written by the profile on the first ClientHello and read back after a HelloRetryRequest.</summary>
    public byte[]? HelloRetryState { get; set; }
}
