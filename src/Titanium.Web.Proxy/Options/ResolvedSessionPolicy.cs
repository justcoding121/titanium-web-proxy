using System;

namespace Titanium.Web.Proxy.Options;

/// <summary>
///     Read-only snapshot combining <see cref="ProxyResourceLimits" /> and <see cref="ProxyTimeoutOptions" />
///     into the single object H1/H2/H3/WebSocket subsystems are handed, so runtime mutation of either
///     half cannot produce inconsistent enforcement partway through a request that started under a
///     different combination of the two.
///     <para>
///         Per the plan's "Constraints on the policy layer" section, this type is deliberately inert:
///         no back-pointer to <see cref="ProxyServer" />, no service-locator lookup, no mutable fields,
///         no static ambient accessor. Subsystems receive it as a constructor argument or method
///         parameter only, so the dependency graph among consumers stays acyclic - the opposite of how
///         <see cref="ProxyServer" /> itself is reached today.
///     </para>
///     <para>
///         Per the plan's "Two-phase policy resolution" section, a single resolution per connection is
///         not correct: <c>SessionEventArgs.MaxBufferedBodyBytes</c> is contractually settable from
///         <c>BeforeRequest</c>, and HTTP/3 already reads the request body before <c>BeforeRequest</c>
///         fires. This type does not itself perform either resolution phase - that is the
///         responsibility of the call sites introduced in later hardening-plan items, once there is a
///         real per-session override path to resolve against - but it is deliberately shaped so a
///         caller can hold one instance at headers-complete time (<see cref="ResourceLimits" />'s
///         framing/header-shape fields, which are never overridable) and, if a session lowers a body or
///         streaming budget in <c>BeforeRequest</c>, build a second instance via <see cref="Create" />
///         that shares the same <see cref="Timeouts" /> but substitutes a <see cref="ProxyResourceLimits" />
///         reflecting the override, rather than mutating the first instance in place.
///     </para>
/// </summary>
public sealed class ResolvedSessionPolicy
{
    private ResolvedSessionPolicy()
    {
    }

    /// <summary>Header shape, body/decompression budgets, and concurrency/abuse-rate ceilings.</summary>
    public ProxyResourceLimits ResourceLimits { get; private init; } = null!;

    /// <summary>Every deadline composed for a request's lifetime.</summary>
    public ProxyTimeoutOptions Timeouts { get; private init; } = null!;

    /// <summary>
    ///     The <c>Balanced</c> profile: today's shipped resource limits and timeout defaults, unchanged
    ///     for existing traffic, per the plan's release-posture section.
    /// </summary>
    public static ResolvedSessionPolicy Default { get; } = Create(ProxyResourceLimits.Default,
        ProxyTimeoutOptions.Default);

    /// <summary>
    ///     Combines an already-validated <see cref="ProxyResourceLimits" /> and
    ///     <see cref="ProxyTimeoutOptions" /> pair into one snapshot. Both halves are validated by their
    ///     own constructors already; this constructor only rejects a missing half so a snapshot can
    ///     never be built with one side implicitly defaulted to <see langword="null" />.
    /// </summary>
    public static ResolvedSessionPolicy Create(ProxyResourceLimits resourceLimits, ProxyTimeoutOptions timeouts)
    {
        ArgumentNullException.ThrowIfNull(resourceLimits);
        ArgumentNullException.ThrowIfNull(timeouts);

        return new ResolvedSessionPolicy { ResourceLimits = resourceLimits, Timeouts = timeouts };
    }

    /// <summary>
    ///     Returns a snapshot sharing this instance's <see cref="Timeouts" /> but with
    ///     <see cref="ResourceLimits" /> replaced - the shape a post-<c>BeforeRequest</c> per-session
    ///     override takes: a new immutable snapshot, never a mutation of the headers-complete one that
    ///     framing decisions upstream may still be holding a reference to.
    /// </summary>
    public ResolvedSessionPolicy WithResourceLimits(ProxyResourceLimits resourceLimits)
    {
        ArgumentNullException.ThrowIfNull(resourceLimits);
        return new ResolvedSessionPolicy { ResourceLimits = resourceLimits, Timeouts = Timeouts };
    }
}
