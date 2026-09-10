namespace Titanium.Web.Proxy;

/// <summary>
///     How factory MITM exclusion defaults interact with caller-supplied host lists.
/// </summary>
public enum MitmExclusionMode
{
    /// <summary>
    ///     Factory OS-bypass and tunnel/SSO decrypt skips are always applied, then caller lists add more
    ///     (and optional decrypt-only allowlist). Default for back-compat.
    /// </summary>
    Merge = 0,

    /// <summary>
    ///     Caller lists are authoritative. Factory defaults are not re-injected — use them only as a
    ///     seed when building the lists you pass in.
    /// </summary>
    Replace = 1,
}
