using System.Reflection;
using System.Runtime.Loader;
using Titanium.Web.Proxy.Abstractions.Middleware;

namespace Titanium.Web.Proxy.Abstractions.Plugins;

/// <summary>
/// The collectible <see cref="AssemblyLoadContext"/> one plugin lives in. The contract assemblies (this one, the proxy,
/// and the logging abstractions) always come from the host, so the plugin and the host agree on every type that crosses
/// the boundary. Everything else resolves from the plugin's own folder, through its <c>.deps.json</c> when present.
/// Unloading the context lets the runtime reclaim the plugin assembly once nothing references it.
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string[] SharedAssemblies =
    [
        "Titanium.Web.Proxy.Abstractions",
        "Titanium.Web.Proxy",
        "Microsoft.Extensions.Logging.Abstractions",
    ];

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string name, string assemblyPath)
        : base(name, isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        AssemblyPath = Path.GetFullPath(assemblyPath);
        _resolver = new AssemblyDependencyResolver(AssemblyPath);
    }

    /// <summary>Full path of the plugin assembly.</summary>
    public string AssemblyPath { get; }

    /// <summary>
    /// Loads the plugin assembly from a copy of its bytes, so the file stays free to replace while the plugin runs
    /// (a reload then picks the new file up).
    /// </summary>
    public Assembly LoadPluginAssembly()
    {
        using var stream = new FileStream(AssemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return LoadFromStream(stream);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var simple = assemblyName.Name;
        if (simple is null)
        {
            return null;
        }

        if (Array.IndexOf(SharedAssemblies, simple) >= 0)
        {
            return null;
        }

        var loaded = Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, simple, StringComparison.Ordinal));
        if (loaded is not null)
        {
            return loaded;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}

/// <summary>Finds and builds the plugin class in a loaded assembly, and checks a plugin assembly without running it.</summary>
public static class PluginTypeResolver
{
    /// <summary>
    /// Finds the plugin class: the named type, or the only public concrete class that implements
    /// <see cref="IProxyMiddleware"/>. Nothing in the assembly runs. On failure <paramref name="error"/> says why.
    /// </summary>
    public static bool TryResolve(Assembly assembly, string? typeName, out Type? type, out string? error)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        type = null;
        error = null;

        Type[] candidates;
        try
        {
            candidates = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            error = "its types could not be loaded: " + (ex.LoaderExceptions.FirstOrDefault()?.Message ?? ex.Message);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(typeName))
        {
            var named = candidates.FirstOrDefault(t => string.Equals(t.FullName, typeName, StringComparison.Ordinal));
            if (named is null)
            {
                error = $"the assembly has no type '{typeName}'";
                return false;
            }

            return Accept(named, out type, out error);
        }

        var found = candidates
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && typeof(IProxyMiddleware).IsAssignableFrom(t))
            .ToList();
        if (found.Count == 0)
        {
            error = $"the assembly has no public class that implements {nameof(IProxyMiddleware)}";
            return false;
        }

        if (found.Count > 1)
        {
            error = "the assembly has several plugin classes (" +
                    string.Join(", ", found.Select(t => t.FullName)) + "); set typeName";
            return false;
        }

        return Accept(found[0], out type, out error);
    }

    /// <summary>Creates the plugin with its settings, through <c>(IReadOnlyDictionary&lt;string,string&gt;)</c> or a parameterless constructor.</summary>
    public static IProxyMiddleware Create(Type type, IReadOnlyDictionary<string, string>? settings)
    {
        ArgumentNullException.ThrowIfNull(type);
        var map = settings ?? new Dictionary<string, string>(0);
        var withSettings = type.GetConstructor([typeof(IReadOnlyDictionary<string, string>)]);
        var instance = withSettings is not null
            ? withSettings.Invoke([map])
            : Activator.CreateInstance(type);
        return instance as IProxyMiddleware
               ?? throw new InvalidOperationException($"{type.FullName} did not produce an {nameof(IProxyMiddleware)}.");
    }

    /// <summary>
    /// Checks a plugin assembly the way the configuration validator does: the file exists, loads, and holds a class that
    /// implements the contract at a supported version. The assembly is loaded into a throwaway context that is
    /// unloaded again, and no plugin code runs. Returns null when the plugin is usable, else the reason.
    /// </summary>
    public static string? Probe(string assemblyPath, string? typeName)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            return "no assembly path is set";
        }

        var full = Path.GetFullPath(assemblyPath);
        if (!File.Exists(full))
        {
            return $"assembly '{assemblyPath}' does not exist";
        }

        var context = new PluginLoadContext("probe:" + Path.GetFileName(full), full);
        try
        {
            var assembly = context.LoadPluginAssembly();
            return TryResolve(assembly, typeName, out _, out var error) ? null : error;
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException or UnauthorizedAccessException)
        {
            return $"assembly '{assemblyPath}' cannot be loaded: {ex.Message}";
        }
        finally
        {
            context.Unload();
        }
    }

    private static bool Accept(Type candidate, out Type? type, out string? error)
    {
        type = null;
        error = null;
        if (!candidate.IsClass || candidate.IsAbstract)
        {
            error = $"'{candidate.FullName}' is not a concrete class";
            return false;
        }

        if (!typeof(IProxyMiddleware).IsAssignableFrom(candidate))
        {
            error = $"'{candidate.FullName}' does not implement {nameof(IProxyMiddleware)}";
            return false;
        }

        if (candidate.GetConstructor([typeof(IReadOnlyDictionary<string, string>)]) is null &&
            candidate.GetConstructor(Type.EmptyTypes) is null)
        {
            error = $"'{candidate.FullName}' needs a public constructor that takes IReadOnlyDictionary<string,string> or no arguments";
            return false;
        }

        var version = DeclaredVersion(candidate);
        if (version < ProxyPluginContract.MinimumSupportedVersion || version > ProxyPluginContract.CurrentVersion)
        {
            error = $"'{candidate.FullName}' declares plugin contract version {version}; this host supports " +
                    $"{ProxyPluginContract.MinimumSupportedVersion} to {ProxyPluginContract.CurrentVersion}";
            return false;
        }

        type = candidate;
        return true;
    }

    private static int DeclaredVersion(Type candidate)
    {
        // CustomAttributeData reads the metadata without constructing the attribute.
        foreach (var data in candidate.GetCustomAttributesData())
        {
            if (string.Equals(data.AttributeType.FullName, typeof(ProxyPluginAttribute).FullName, StringComparison.Ordinal) &&
                data.ConstructorArguments.Count == 1 &&
                data.ConstructorArguments[0].Value is int version)
            {
                return version;
            }
        }

        return ProxyPluginContract.MinimumSupportedVersion;
    }
}
