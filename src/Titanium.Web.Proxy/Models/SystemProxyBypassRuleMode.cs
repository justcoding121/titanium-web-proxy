namespace Titanium.Web.Proxy;

/// <summary>
///     Controls how configured bypass rules are combined with the current Windows system proxy bypass list.
/// </summary>
public enum SystemProxyBypassRuleMode
{
    /// <summary>
    ///     Preserve the current bypass rules and add the configured rules.
    /// </summary>
    Merge,

    /// <summary>
    ///     Replace the current bypass rules with the configured rules.
    /// </summary>
    Replace
}
