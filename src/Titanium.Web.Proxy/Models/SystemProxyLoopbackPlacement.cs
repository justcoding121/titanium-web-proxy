namespace Titanium.Web.Proxy;

/// <summary>
///     Controls where the <c>&lt;-loopback&gt;</c> rule is placed within the Windows system proxy bypass list.
/// </summary>
public enum SystemProxyLoopbackPlacement
{
    /// <summary>
    ///     Place the <c>&lt;-loopback&gt;</c> rule before all other bypass rules.
    /// </summary>
    First,

    /// <summary>
    ///     Place the <c>&lt;-loopback&gt;</c> rule after all other bypass rules.
    /// </summary>
    Last
}
