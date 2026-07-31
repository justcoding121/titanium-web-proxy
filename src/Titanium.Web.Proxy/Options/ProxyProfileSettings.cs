using System.Security.Authentication;

namespace Titanium.Web.Proxy.Options;

/// <summary>
///     The full bundle of settings one <see cref="ProxyProfile" /> applies to a
///     <see cref="ProxyServer" /> as a single atomic assignment via <see cref="ProxyServer.Profile" />.
///     Deliberately a plain data bundle, not a type with behavior: applying it is
///     <see cref="ProxyServer" />'s job (see <see cref="ProxyServer.Profile" />'s setter), so this type
///     has no back-reference and no side effects of its own, per the plan's "Constraints on the
///     policy layer" section.
/// </summary>
public sealed class ProxyProfileSettings
{
    private ProxyProfileSettings()
    {
    }

    /// <summary>Header shape, body/decompression budgets, and concurrency/abuse-rate ceilings.</summary>
    public ProxyResourceLimits ResourceLimits { get; private init; } = null!;

    /// <summary>Which resource-bound families are enforced, observed, or disabled.</summary>
    public ProxyPolicyModes PolicyModes { get; private init; } = null!;

    /// <summary>TLS protocol versions negotiated with clients and, unless overridden separately, origins.</summary>
    public SslProtocols SupportedSslProtocols { get; private init; }

    /// <summary>Whether outbound connections to private/link-local/metadata addresses are blocked.</summary>
    public bool BlockPrivateNetworkDestinations { get; private init; }

    /// <summary>
    ///     Global admission cap applied to <see cref="ProxyServer.MaxConcurrentClientConnections" />.
    ///     <see langword="null" /> disables the global admission gate, matching today's shipped
    ///     default.
    /// </summary>
    public int? MaxConcurrentClientConnections { get; private init; }

    /// <summary>Seconds to wait for a client to finish sending the request line and headers. 0 disables the deadline.</summary>
    public int ClientHeaderTimeoutSeconds { get; private init; }

    /// <summary>Seconds to wait for the origin to send response status line and headers. 0 disables the deadline.</summary>
    public int ResponseHeaderTimeoutSeconds { get; private init; }

    /// <summary>Seconds of idle time allowed while reading from the origin. 0 disables the deadline.</summary>
    public int IdleReadTimeoutSeconds { get; private init; }

    /// <summary>Seconds of idle time allowed while writing to the origin. 0 disables the deadline.</summary>
    public int IdleWriteTimeoutSeconds { get; private init; }

    /// <summary>Total seconds allowed for a single request/response exchange after <c>BeforeRequest</c> returns. 0 disables the deadline.</summary>
    public int RequestTimeoutSeconds { get; private init; }

    /// <summary>
    ///     <c>Balanced</c>: today's shipped values, unchanged for existing traffic. Every resource
    ///     family is enforced (not observed) because Observe still allocates and would leave the OOM
    ///     paths live by default; TLS 1.2/1.3 only; no outbound destination blocking; no admission cap;
    ///     no deadlines beyond what already ships disabled today.
    /// </summary>
    public static ProxyProfileSettings Balanced { get; } = new()
    {
        ResourceLimits = ProxyResourceLimits.Default,
        PolicyModes = ProxyPolicyModes.AllEnforce,
        SupportedSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        BlockPrivateNetworkDestinations = false,
        MaxConcurrentClientConnections = null,
        ClientHeaderTimeoutSeconds = 0,
        ResponseHeaderTimeoutSeconds = 0,
        IdleReadTimeoutSeconds = 0,
        IdleWriteTimeoutSeconds = 0,
        RequestTimeoutSeconds = 0
    };

    /// <summary>
    ///     Opt-in for 4.x migrators: permits legacy TLS and relaxes the non-memory-bounding families
    ///     to <see cref="PolicyMode.Observe" />. <see cref="PolicyFamily.BodyBudget" /> and
    ///     <see cref="PolicyFamily.DecompressionRatio" /> stay enforced even here, since Observe
    ///     cannot protect against exhaustion; framing remains enforce-only, as it always is,
    ///     independent of any profile.
    ///     <para>
    ///         "Legacy TLS" here means TLS 1.0/1.1 in addition to 1.2/1.3: SSL 2.0/3.0 are not offered
    ///         because modern .NET's <see cref="System.Net.Security.SslStream" /> no longer negotiates
    ///         them regardless of what <see cref="SslProtocols" /> flags are requested.
    ///     </para>
    /// </summary>
    public static ProxyProfileSettings LegacyCompatible { get; } = new()
    {
        ResourceLimits = ProxyResourceLimits.Default,
        PolicyModes = ProxyPolicyModes.Create(
            bodyBudget: PolicyMode.Enforce,
            decompressionRatio: PolicyMode.Enforce,
            headerLimits: PolicyMode.Observe,
            admissionControl: PolicyMode.Observe,
            http2AbuseBudget: PolicyMode.Observe),
#pragma warning disable SYSLIB0039 // Deliberate legacy-TLS opt-in for 4.x migrators, per this profile's purpose.
        SupportedSslProtocols = SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12 | SslProtocols.Tls13,
#pragma warning restore SYSLIB0039
        BlockPrivateNetworkDestinations = false,
        MaxConcurrentClientConnections = null,
        ClientHeaderTimeoutSeconds = 0,
        ResponseHeaderTimeoutSeconds = 0,
        IdleReadTimeoutSeconds = 0,
        IdleWriteTimeoutSeconds = 0,
        RequestTimeoutSeconds = 0
    };

    /// <summary>
    ///     Opt-in for deployments exposed to untrusted clients: <see cref="Balanced" /> plus outbound
    ///     destination blocking and finite admission/deadline values, none of which are bounded by
    ///     default under <see cref="Balanced" />.
    /// </summary>
    public static ProxyProfileSettings PublicFacing { get; } = new()
    {
        ResourceLimits = ProxyResourceLimits.Default,
        PolicyModes = ProxyPolicyModes.AllEnforce,
        SupportedSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        BlockPrivateNetworkDestinations = true,
        MaxConcurrentClientConnections = 10_000,
        ClientHeaderTimeoutSeconds = 30,
        ResponseHeaderTimeoutSeconds = 60,
        IdleReadTimeoutSeconds = 60,
        IdleWriteTimeoutSeconds = 60,
        RequestTimeoutSeconds = 120
    };

    /// <summary>Returns the shipped bundle for <paramref name="profile" />.</summary>
    public static ProxyProfileSettings For(ProxyProfile profile)
    {
        return profile switch
        {
            ProxyProfile.Balanced => Balanced,
            ProxyProfile.LegacyCompatible => LegacyCompatible,
            ProxyProfile.PublicFacing => PublicFacing,
            _ => throw new System.ArgumentOutOfRangeException(nameof(profile), profile, "Unknown profile.")
        };
    }
}
