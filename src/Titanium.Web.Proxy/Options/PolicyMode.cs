namespace Titanium.Web.Proxy.Options;

/// <summary>
///     How a resource-bound policy family is applied once its numeric limit is breached, per the
///     plan's "Rollout, profiles and documentation" section.
///     <para>
///         Not every family supports every mode. Framing, chunk parsing and
///         <c>Content-Length</c>/<c>Transfer-Encoding</c> resolution have no <see cref="Observe" />
///         mode at all: an ambiguous chunk size or a conflicting length can only be forwarded (a
///         desync) or rejected, so those call sites are unconditionally enforced and never consult a
///         <see cref="PolicyMode" />. <see cref="ProxyPolicyModes" /> exists for the families where a
///         safe "detect but let it through" middle ground is actually possible.
///     </para>
/// </summary>
public enum PolicyMode
{
    /// <summary>
    ///     The family's numeric limit, if any is configured, is not consulted at all: no check runs,
    ///     no metric is recorded. Distinct from a family whose limit is itself <see langword="null" />
    ///     (which still runs a check that trivially never breaches) - <see cref="Disabled" /> is a
    ///     single switch that turns the whole family off regardless of what numeric bound is
    ///     configured underneath it, so re-enabling later does not require rediscovering and
    ///     restoring every individual limit value.
    /// </summary>
    Disabled,

    /// <summary>
    ///     The family's check still runs and a breach is still recorded (logged and counted in the
    ///     typed metrics), but the request/connection/stream is never rejected or torn down because
    ///     of it. Useful for measuring what a stricter profile's limits would have caught against
    ///     real traffic before switching that profile to <see cref="Enforce" />.
    /// </summary>
    Observe,

    /// <summary>
    ///     The family's check runs and a breach rejects the request, closes the connection, or resets
    ///     the stream, per that family's own defined breach behavior. Today's shipped behavior for
    ///     every family that has one.
    /// </summary>
    Enforce
}
