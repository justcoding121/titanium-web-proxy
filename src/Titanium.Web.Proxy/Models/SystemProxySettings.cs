using System;
using System.Collections.Generic;

namespace Titanium.Web.Proxy;

/// <summary>
///     Options applied when configuring an explicit endpoint as the Windows system proxy.
/// </summary>
public class SystemProxySettings
{
    private const string SubtractImplicitLoopbackRule = "<-loopback>";

    /// <summary>
    ///     Gets or sets whether loopback requests should use the proxy.
    /// </summary>
    /// <remarks>
    ///     This adds the WinINET <c>&lt;-loopback&gt;</c> rule. It only affects applications that honor compatible
    ///     Windows system proxy settings and can expose otherwise trusted local traffic to the proxy.
    /// </remarks>
    public bool ProxyLoopback { get; set; }

    /// <summary>
    ///     Gets or sets where the <c>&lt;-loopback&gt;</c> rule is placed in the bypass list.
    /// </summary>
    /// <remarks>
    ///     Ordering matters because rules are evaluated left-to-right; a subtractive rule such as
    ///     <c>&lt;-loopback&gt;</c> has a different effect before versus after a contradicting bypass rule.
    /// </remarks>
    public SystemProxyLoopbackPlacement ProxyLoopbackPlacement { get; set; } = SystemProxyLoopbackPlacement.First;

    /// <summary>
    ///     Gets additional WinINET host patterns that should bypass the proxy.
    /// </summary>
    public IList<string> BypassRules { get; } = new List<string>();

    /// <summary>
    ///     Gets or sets how <see cref="BypassRules"/> are combined with the current Windows system proxy bypass list.
    /// </summary>
    public SystemProxyBypassRuleMode BypassRuleMode { get; set; } = SystemProxyBypassRuleMode.Merge;

    /// <summary>
    ///     Validates the configured bypass rules, throwing when any rule is malformed.
    /// </summary>
    internal void Validate()
    {
        foreach (var rule in BypassRules)
        {
            if (string.IsNullOrWhiteSpace(rule))
                throw new ArgumentException("System proxy bypass rules cannot be null or empty.");

            if (rule.Contains(";"))
                throw new ArgumentException(
                    "Add each system proxy bypass rule separately; rules cannot contain semicolons.");
        }
    }

    internal string BuildProxyOverride(string? currentProxyOverride)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (ProxyLoopback && ProxyLoopbackPlacement == SystemProxyLoopbackPlacement.First)
            AddRule(result, seen, SubtractImplicitLoopbackRule);

        if (BypassRuleMode == SystemProxyBypassRuleMode.Merge && !string.IsNullOrWhiteSpace(currentProxyOverride))
            foreach (var rule in currentProxyOverride.Split(';'))
                AddRule(result, seen, rule);

        foreach (var rule in BypassRules) AddRule(result, seen, rule);

        if (ProxyLoopback && ProxyLoopbackPlacement == SystemProxyLoopbackPlacement.Last)
            AddRule(result, seen, SubtractImplicitLoopbackRule);

        return string.Join(";", result);
    }

    private static void AddRule(List<string> result, HashSet<string> seen, string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return;

        var normalizedRule = rule.Trim();
        if (seen.Add(normalizedRule)) result.Add(normalizedRule);
    }
}
