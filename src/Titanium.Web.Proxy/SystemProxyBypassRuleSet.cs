using System.Collections.Generic;
namespace Titanium.Web.Proxy;

/// <inheritdoc />
/// <summary>
///     This class is used to control when system should skip or use (in case of implicitly ignored addresses) system proxy.
///     By default for secutiry reasons proxy is ignored on local networks considered as trusted, you can disable it by using <see cref="SubtractImplicitBypassRules"/>.
///     Be aware that rules are check in order they are added, it's important when they are contradicting.
/// </summary>
public class SystemProxyBypassRuleSet
{
    private const string IgnoreImplicitBypassRules = "<-loopback>";
    private const string BypassSimpleHostnamesRule = "<local>";

    private readonly List<string> _bypassRules;

    /// <summary>
    ///     Initialize new system proxy rule set.
    /// </summary>
    public SystemProxyBypassRuleSet()
    {
        _bypassRules = new List<string>();
    }

    /// <summary>
    /// Adds user defined rule to bypass system proxy.
    /// </summary>
    /// <param name="rule">Rule specifying which urls should not go though system proxy.</param>
    /// <returns>Itself.</returns>
    /// <example>
    /// <code>
    /// var ruleSet = 
    ///     new SystemProxyBypassRuleSet()
    ///         .AddRule("https://x.*.y.com:99")
    ///         .AddRule("*foobar.com")
    ///         .AddRule("*.org:443")
    ///         .AddRule("192.168.1.1/16");
    /// </code>
    /// </example>
    public SystemProxyBypassRuleSet AddRule(string rule)
    {
        _bypassRules.Add(rule);
        return this;
    }

    /// <summary>
    /// Adds rule for bypassing hostnames without a period in them, and that are not IP literals.
    /// </summary>
    /// <returns>Itself.</returns>
    public SystemProxyBypassRuleSet AddSimpleHostnames()
    {
        _bypassRules.Add(BypassSimpleHostnamesRule);
        return this;
    }

    /// <summary>
    /// Subtracts the implicit proxy bypass rules (localhost and link local addresses).
    /// This is generally only needed for test setups. Beware of the security implications to proxying localhost.
    /// Ordering may matter when using a subtractive rule, as rules will be evaluated in a left-to-right order.
    /// </summary>
    /// <returns>Itself.</returns>
    public SystemProxyBypassRuleSet SubtractImplicitBypassRules()
    {
        _bypassRules.Add(IgnoreImplicitBypassRules);
        return this;
    }

    public override string ToString()
    {
        return string.Join(";", _bypassRules);
    }
}
