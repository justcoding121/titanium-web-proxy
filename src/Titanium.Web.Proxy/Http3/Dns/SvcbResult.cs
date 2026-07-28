using System;

namespace Titanium.Web.Proxy.Http3.Dns;

/// <summary>
///     Represents the H3-relevant information extracted from an HTTPS/SVCB DNS RR for a given host:port.
/// </summary>
/// <param name="AltPort">
///     Alternative port at which the origin serves HTTP/3. Use the original port when this equals
///     the queried port (or when the SVCB record does not contain a <c>port</c> SvcParam).
/// </param>
/// <param name="Ttl">DNS TTL to use as the capability-cache lifetime.</param>
/// <param name="TargetName">
///     The ServiceMode <c>TargetName</c> from the HTTPS RR, or <see langword="null" /> when the
///     record uses <c>.</c> (root label) meaning the owner name.  When non-null, the QUIC connection
///     must be established to this hostname while the original origin host is retained for TLS SNI
///     and the HTTP/3 <c>:authority</c> pseudo-header.
/// </param>
internal sealed record SvcbResult(int AltPort, TimeSpan Ttl, string? TargetName = null);
