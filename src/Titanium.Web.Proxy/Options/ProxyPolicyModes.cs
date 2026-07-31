using System;
using System.Collections.Generic;

namespace Titanium.Web.Proxy.Options;

/// <summary>
///     Immutable snapshot of the <see cref="PolicyMode" /> selected for each <see cref="PolicyFamily" />,
///     plus the one deliberately-separate <see cref="AllowAmbiguousFraming" /> escape hatch. Read live by
///     enforcement call sites through <see cref="ProxyServer.PolicyModes" />, which the plan's rollout
///     section requires to be a runtime switch: replacing the whole snapshot (see
///     <see cref="ProxyServer.PolicyModes" />'s setter) rather than mutating a field lets an operator
///     drop every family to <see cref="PolicyMode.Observe" /> without redeploying, while every
///     in-flight request that already read the previous snapshot keeps behaving consistently with
///     whichever snapshot it observed.
///     <para>
///         <see cref="AllowAmbiguousFraming" /> is not a <see cref="PolicyFamily" /> member and has no
///         corresponding <see cref="PolicyMode" />: framing, chunk parsing and
///         <c>Content-Length</c>/<c>Transfer-Encoding</c> resolution have no safe "detect but let it
///         through" middle ground, so this is a single named, binary, off-by-default flag that
///         relays malformed framing instead of rejecting it - useful only for security research that
///         needs to observe how a client or origin reacts to smuggling-shaped input through the
///         proxy. No profile sets it: <see cref="Create" /> never accepts it as a parameter, and the
///         only way to turn it on is the explicit <see cref="WithAllowAmbiguousFramingEnabled" />
///         call, so enabling it can never be a side effect of selecting a profile.
///     </para>
/// </summary>
public sealed class ProxyPolicyModes
{
    private readonly Dictionary<PolicyFamily, PolicyMode> modes;

    private ProxyPolicyModes(Dictionary<PolicyFamily, PolicyMode> modes, bool allowAmbiguousFraming)
    {
        this.modes = modes;
        AllowAmbiguousFraming = allowAmbiguousFraming;
    }

    /// <summary>
    ///     Off by default and absent from every profile - see the type-level remarks. Never
    ///     <see langword="true" /> unless <see cref="WithAllowAmbiguousFramingEnabled" /> was called
    ///     explicitly.
    /// </summary>
    public bool AllowAmbiguousFraming { get; }

    /// <summary>Every family enforced - today's shipped behavior, and the starting point every profile builds from.</summary>
    public static ProxyPolicyModes AllEnforce { get; } = Create(
        bodyBudget: PolicyMode.Enforce,
        decompressionRatio: PolicyMode.Enforce,
        headerLimits: PolicyMode.Enforce,
        admissionControl: PolicyMode.Enforce,
        http2AbuseBudget: PolicyMode.Enforce);

    /// <summary>Returns the mode selected for <paramref name="family" />.</summary>
    public PolicyMode this[PolicyFamily family] => modes[family];

    /// <summary>Builds a snapshot with an explicit mode for every family. <see cref="AllowAmbiguousFraming" /> starts <see langword="false" />.</summary>
    public static ProxyPolicyModes Create(
        PolicyMode bodyBudget,
        PolicyMode decompressionRatio,
        PolicyMode headerLimits,
        PolicyMode admissionControl,
        PolicyMode http2AbuseBudget)
    {
        var dict = new Dictionary<PolicyFamily, PolicyMode>
        {
            [PolicyFamily.BodyBudget] = bodyBudget,
            [PolicyFamily.DecompressionRatio] = decompressionRatio,
            [PolicyFamily.HeaderLimits] = headerLimits,
            [PolicyFamily.AdmissionControl] = admissionControl,
            [PolicyFamily.Http2AbuseBudget] = http2AbuseBudget
        };
        return new ProxyPolicyModes(dict, false);
    }

    /// <summary>Returns a snapshot identical to this one but with <paramref name="mode" /> for <paramref name="family" />.</summary>
    public ProxyPolicyModes With(PolicyFamily family, PolicyMode mode)
    {
        var dict = new Dictionary<PolicyFamily, PolicyMode>(modes) { [family] = mode };
        return new ProxyPolicyModes(dict, AllowAmbiguousFraming);
    }

    /// <summary>
    ///     Returns a snapshot identical to this one but with every family dropped to
    ///     <see cref="PolicyMode.Observe" />, except families already <see cref="PolicyMode.Disabled" />
    ///     (which stay disabled - Observe would silently turn a family back on). This is the "drop to
    ///     Observe without redeploying" runtime switch the plan's rollout section requires.
    /// </summary>
    public ProxyPolicyModes WithAllObservedExceptDisabled()
    {
        var dict = new Dictionary<PolicyFamily, PolicyMode>(modes);
        foreach (var family in dict.Keys)
        {
            if (dict[family] != PolicyMode.Disabled)
                dict[family] = PolicyMode.Observe;
        }

        return new ProxyPolicyModes(dict, AllowAmbiguousFraming);
    }

    /// <summary>
    ///     The single, explicit, isolated act of relaying ambiguous HTTP/1 framing instead of
    ///     rejecting it. See the type-level remarks; never call this as a side effect of applying a
    ///     profile.
    /// </summary>
    public ProxyPolicyModes WithAllowAmbiguousFramingEnabled()
    {
        return new ProxyPolicyModes(new Dictionary<PolicyFamily, PolicyMode>(modes), true);
    }

    internal IReadOnlyDictionary<PolicyFamily, PolicyMode> AsReadOnlyDictionary() => modes;
}
