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

/// <summary>
/// One entry of <c>plugins</c> in the configuration. <see cref="Kind"/> picks the loader: <c>dotnet</c> (the default) uses
/// <see cref="Assembly"/> and <see cref="TypeName"/>; <c>lua</c> and <c>wasm</c> use <see cref="File"/> and are built by a
/// registered <see cref="IPluginEngine"/>.
/// </summary>
public sealed class PluginConfig
{
    /// <summary>Unique name that <c>routes[].middleware</c> and the plugin metrics use. Letters, digits, <c>.</c>, <c>_</c> and <c>-</c>.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// How the plugin is loaded: <c>dotnet</c>, <c>lua</c>, or <c>wasm</c> (see <see cref="PluginKinds"/>). Null or empty means
    /// <c>dotnet</c>, so a configuration written before script engines existed keeps working.
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>Path of the plugin assembly (<c>kind: dotnet</c>). Relative paths resolve against the configuration directory.</summary>
    public string? Assembly { get; init; }

    /// <summary>Path of the script (<c>kind: lua</c>) or module (<c>kind: wasm</c>). Not used by <c>kind: dotnet</c>.</summary>
    public string? File { get; init; }

    /// <summary>Optional name of a VM shared by plugins of the same engine. Letters, digits, <c>.</c>, <c>_</c> and <c>-</c>. Script kinds only.</summary>
    public string? VmId { get; init; }

    /// <summary>How the script sees request and response bodies. The engine defines the accepted values. Script kinds only.</summary>
    public string? BodyMode { get; init; }

    /// <summary>Hosts the script may call out to over HTTP. Script kinds only.</summary>
    public IReadOnlyList<string>? CalloutHosts { get; init; }

    /// <summary>Resource limits the engine enforces on the script. Script kinds only.</summary>
    public PluginLimitsConfig? Limits { get; init; }

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

/// <summary>
/// Resource limits for a script plugin (<c>plugins[].limits</c>). A property left null takes the engine's default. Each engine
/// reads the ones that apply to it and ignores the rest.
/// </summary>
public sealed class PluginLimitsConfig
{
    /// <summary>Memory ceiling of one VM, in MiB.</summary>
    public int? MemoryMb { get; init; }

    /// <summary>Most VM instructions one call may run (Lua).</summary>
    public long? MaxInstructions { get; init; }

    /// <summary>Most fuel one call may burn (WebAssembly).</summary>
    public long? Fuel { get; init; }

    /// <summary>How many VM instances serve calls at once.</summary>
    public int? PoolSize { get; init; }

    /// <summary>How many HTTP callouts one plugin may have in flight.</summary>
    public int? CalloutConcurrency { get; init; }

    /// <summary>Largest body, in bytes, the engine buffers for a script.</summary>
    public long? BufferedBodyBytes { get; init; }

    /// <summary>Size, in bytes, of the data area shared between the host and a VM.</summary>
    public long? SharedDataBytes { get; init; }

    /// <summary>Size, in bytes, of the queue of commands a script may return from one call.</summary>
    public long? QueueBytes { get; init; }

    /// <summary>Epoch deadline of one call, in milliseconds (WebAssembly).</summary>
    public int? EpochDeadlineMs { get; init; }
}
