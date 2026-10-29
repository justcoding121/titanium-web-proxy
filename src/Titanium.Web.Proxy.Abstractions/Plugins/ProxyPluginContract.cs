namespace Titanium.Web.Proxy.Abstractions.Plugins;

/// <summary>
/// Version of the per-route .NET plugin contract. A plugin is a class that implements
/// <see cref="Middleware.IProxyMiddleware"/>; the host passes a <see cref="Middleware.ProxyMiddlewareContext"/> whose
/// <see cref="Middleware.ProxyMiddlewareContext.Session"/> is the request session. The version moves only when that
/// surface changes in a way an existing plugin binary cannot survive.
/// </summary>
public static class ProxyPluginContract
{
    /// <summary>The contract version this host implements.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The oldest contract version this host still loads.</summary>
    public const int MinimumSupportedVersion = 1;

    /// <summary>The <c>failMode</c> value that denies the request (503) when a plugin throws or times out. The default.</summary>
    public const string FailClosed = "closed";

    /// <summary>The <c>failMode</c> value that lets the request continue when a plugin throws or times out.</summary>
    public const string FailOpen = "open";

    /// <summary>Default per-call plugin timeout in milliseconds.</summary>
    public const int DefaultTimeoutMilliseconds = 1000;

    /// <summary>Largest per-call plugin timeout in milliseconds.</summary>
    public const int MaxTimeoutMilliseconds = 60_000;
}

/// <summary>
/// Declares the contract version a plugin class was written against. A class without the attribute is treated as
/// <see cref="ProxyPluginContract.MinimumSupportedVersion"/>. A host refuses a plugin that declares a version it does not
/// support, before it constructs anything.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ProxyPluginAttribute : Attribute
{
    public ProxyPluginAttribute(int contractVersion) => ContractVersion = contractVersion;

    /// <summary>The <see cref="ProxyPluginContract.CurrentVersion"/> the plugin was built against.</summary>
    public int ContractVersion { get; }
}

/// <summary>One entry of <c>plugins</c> in the configuration: an assembly, the plugin class in it, and its settings.</summary>
public sealed class PluginConfig
{
    /// <summary>Unique name that <c>routes[].middleware</c> and the plugin metrics use. Letters, digits, <c>.</c>, <c>_</c> and <c>-</c>.</summary>
    public string? Name { get; init; }

    /// <summary>Path of the plugin assembly. Relative paths resolve against the configuration directory.</summary>
    public string? Assembly { get; init; }

    /// <summary>
    /// Full name of the plugin class. Optional when the assembly has exactly one public class that implements
    /// <see cref="Middleware.IProxyMiddleware"/>.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>String settings handed to the plugin constructor.</summary>
    public IReadOnlyDictionary<string, string>? Settings { get; init; }

    /// <summary><c>closed</c> (default) answers 503 when the plugin throws or times out. <c>open</c> lets the request continue.</summary>
    public string? FailMode { get; init; }

    /// <summary>Longest one call may run, in milliseconds (1 to 60000). Default 1000.</summary>
    public int? TimeoutMs { get; init; }
}
