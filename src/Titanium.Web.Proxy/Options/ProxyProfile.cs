namespace Titanium.Web.Proxy.Options;

/// <summary>
///     The three shipped profiles, per the plan's "Rollout, profiles and documentation" section.
///     Selecting a profile via <see cref="ProxyServer.Profile" /> applies its
///     <see cref="ProxyProfileSettings" /> to <see cref="ProxyServer.ResourceLimits" />,
///     <see cref="ProxyServer.PolicyModes" />, <see cref="ProxyServer.SupportedSslProtocols" /> and
///     <see cref="ProxyServer.BlockPrivateNetworkDestinations" /> as one atomic assignment, so a
///     caller can never observe a half-applied profile.
/// </summary>
public enum ProxyProfile
{
    /// <summary>
    ///     The default. Lenient toward user traffic - it does not block outbound destinations, does
    ///     not add rejections beyond the always-enforced framing family, and keeps today's shipped
    ///     limit values - but strict wherever leniency would let a peer exhaust the proxy: resource
    ///     families are enforced rather than observed, and TLS is restricted to 1.2/1.3.
    /// </summary>
    Balanced,

    /// <summary>
    ///     Opt-in for 4.x migrators. Permits legacy TLS (down to SSL 3.0) and relaxes the non-memory
    ///     families to <see cref="PolicyMode.Observe" />. The memory-bounding families
    ///     (<see cref="PolicyFamily.BodyBudget" />, <see cref="PolicyFamily.DecompressionRatio" />)
    ///     stay enforced even here, since Observe cannot protect against exhaustion. Framing remains
    ///     enforce-only, as it always is.
    /// </summary>
    LegacyCompatible,

    /// <summary>
    ///     Opt-in for deployments that accept requests from untrusted clients.
    ///     <see cref="Balanced" /> plus outbound destination blocking for private, link-local and
    ///     metadata addresses (<see cref="ProxyServer.BlockPrivateNetworkDestinations" />) and tighter
    ///     admission and deadline values (a finite global connection cap and bounded client-header,
    ///     response-header, idle and total-request deadlines, none of which are bounded by default
    ///     under <see cref="Balanced" />). Header-name hygiene (control-character/CRLF rejection) is
    ///     already unconditionally enforced under every profile - see the framing family's
    ///     always-enforce rule - so this profile does not add a separate header-validation mode.
    /// </summary>
    PublicFacing
}
