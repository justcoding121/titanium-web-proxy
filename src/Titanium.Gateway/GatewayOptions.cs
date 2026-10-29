using System.Globalization;
using Titanium.Gateway.Control;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Model;
using Titanium.Gateway.Translation;

namespace Titanium.Gateway;

/// <summary>
///     Command-line and environment settings of <c>titanium-gateway</c>. Every option is a <c>--name value</c> flag or,
///     for the chart, an environment variable (<c>TITANIUM_GATEWAY_</c> plus the upper-case name with underscores).
/// </summary>
public sealed class GatewayOptions
{
    public string ControllerName { get; private set; } = GatewayTranslatorOptions.DefaultControllerName;

    public string DataPlaneNamespace { get; private set; } = string.Empty;

    public string DataPlaneService { get; private set; } = string.Empty;

    public string ConfigMapName { get; private set; } = string.Empty;

    public string TlsSecretName { get; private set; } = string.Empty;

    public string ConfigKey { get; private set; } = "twp.yaml";

    /// <summary>The data-plane Deployment to roll when its listener set changes. Empty leaves restarts to the operator.</summary>
    public string DataPlaneDeployment { get; private set; } = string.Empty;

    public string LeaseName { get; private set; } = "titanium-gateway";

    public string LeaseNamespace { get; private set; } = string.Empty;

    public string Identity { get; private set; } = string.Empty;

    public bool LeaderElection { get; private set; } = true;

    public int AdminPort { get; private set; } = 9901;

    public string AdminTokenEnv { get; private set; } = "TITANIUM_ADMIN_TOKEN";

    public int HealthPort { get; private set; } = 8081;

    /// <summary>Where the data-plane pods see the generated PFX files. The chart projects the Secret next to <c>twp.yaml</c>, so a certificate rotation also triggers the pods' config watch.</summary>
    public string TlsMountPath { get; private set; } = "/etc/titanium";

    public IReadOnlyDictionary<int, int> PortMap { get; private set; } = new Dictionary<int, int> { [80] = 8080, [443] = 8443 };

    public IReadOnlyList<string> SecretNamespaces { get; private set; } = [];

    /// <summary><c>IngressClass.spec.controller</c> this controller owns.</summary>
    public string IngressControllerName { get; private set; } = IngressApiNames.DefaultControllerName;

    /// <summary>Also treat <c>kubernetes.io/ingress.class</c> as a class selector (deprecated upstream). Off by default.</summary>
    public bool WatchLegacyAnnotation { get; private set; }

    public int MinReconcileMilliseconds { get; private set; } = 500;

    public bool Verbose { get; private set; }

    /// <summary>
    ///     Environment variables with the <c>TITANIUM_GATEWAY_</c> prefix that name no option. Kubernetes injects
    ///     service-link variables (for a Service called <c>titanium-gateway-data</c>: <c>TITANIUM_GATEWAY_DATA_PORT</c> and
    ///     friends) that share the prefix, so these are reported, not fatal. Unknown flags stay fatal.
    /// </summary>
    public IReadOnlyList<string> IgnoredEnvironment { get; private set; } = [];

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "controller-name", "data-plane-namespace", "data-plane-service", "data-plane-deployment", "config-map", "tls-secret",
        "config-key", "lease-name", "lease-namespace", "identity", "no-leader-election", "admin-port", "admin-token-env",
        "health-port", "tls-mount-path", "port-map", "secret-namespaces", "min-reconcile-ms", "verbose", "pod-namespace", "pod-name",
        "ingress-controller-name", "watch-legacy-annotation",
    };

    private static string KnownList => string.Join(", ", Known.Order(StringComparer.Ordinal));

    public static GatewayOptions Parse(IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> environment)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var ignoredEnvironment = new List<string>();
        foreach (var pair in environment)
        {
            const string prefix = "TITANIUM_GATEWAY_";
            if (pair.Key.StartsWith(prefix, StringComparison.Ordinal) && !string.IsNullOrEmpty(pair.Value))
            {
                var name = pair.Key[prefix.Length..].ToLowerInvariant().Replace('_', '-');
                if (Known.Contains(name))
                {
                    values[name] = pair.Value;
                }
                else
                {
                    ignoredEnvironment.Add(pair.Key);
                }
            }
        }

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{arg}'.");
            }

            var name = arg[2..];
            var eq = name.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                values[name[..eq]] = name[(eq + 1)..];
            }
            else if (name is "verbose" or "no-leader-election" or "watch-legacy-annotation")
            {
                values[name] = "true";
            }
            else if (i + 1 < args.Count)
            {
                values[name] = args[++i];
            }
            else
            {
                throw new ArgumentException($"Option '{arg}' needs a value.");
            }
        }

        foreach (var name in values.Keys)
        {
            if (!Known.Contains(name))
            {
                throw new ArgumentException($"Unknown option '{name}'. Known options: {KnownList}.");
            }
        }

        string? Get(string name) => values.TryGetValue(name, out var value) ? value : null;
        string Fallback(string name, string key) => Get(name) ?? environment.GetValueOrDefault(key) ?? string.Empty;

        var options = new GatewayOptions { IgnoredEnvironment = ignoredEnvironment.Order(StringComparer.Ordinal).ToList() };
        var podNamespace = Fallback("pod-namespace", "POD_NAMESPACE");
        options.ControllerName = Get("controller-name") ?? options.ControllerName;
        options.DataPlaneService = Get("data-plane-service") ?? string.Empty;
        options.DataPlaneNamespace = Get("data-plane-namespace") ?? podNamespace;
        options.ConfigMapName = Get("config-map") ?? options.DataPlaneService;
        options.TlsSecretName = Get("tls-secret") ?? (options.DataPlaneService.Length == 0 ? string.Empty : options.DataPlaneService + "-tls");
        options.ConfigKey = Get("config-key") ?? options.ConfigKey;
        options.DataPlaneDeployment = Get("data-plane-deployment") ?? string.Empty;
        options.LeaseName = Get("lease-name") ?? options.LeaseName;
        options.LeaseNamespace = Get("lease-namespace") ?? podNamespace;
        options.Identity = Get("identity") ?? Fallback("pod-name", "POD_NAME");
        if (options.Identity.Length == 0)
        {
            options.Identity = Environment.MachineName + "-" + Guid.NewGuid().ToString("N")[..6];
        }

        options.LeaderElection = Get("no-leader-election") is not "true";
        options.AdminPort = Int(Get("admin-port"), options.AdminPort, "admin-port");
        options.AdminTokenEnv = Get("admin-token-env") ?? options.AdminTokenEnv;
        options.HealthPort = Int(Get("health-port"), options.HealthPort, "health-port");
        options.TlsMountPath = Get("tls-mount-path") ?? options.TlsMountPath;
        options.MinReconcileMilliseconds = Int(Get("min-reconcile-ms"), options.MinReconcileMilliseconds, "min-reconcile-ms");
        options.Verbose = Get("verbose") is "true";
        options.IngressControllerName = Get("ingress-controller-name") ?? options.IngressControllerName;
        options.WatchLegacyAnnotation = Get("watch-legacy-annotation") is "true";
        if (Get("port-map") is { } map)
        {
            options.PortMap = ParsePortMap(map);
        }

        if (Get("secret-namespaces") is { } namespaces)
        {
            options.SecretNamespaces = namespaces.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        options.Validate();
        return options;
    }

    /// <summary>Parses <c>80=8080,443=8443</c>.</summary>
    public static IReadOnlyDictionary<int, int> ParsePortMap(string text)
    {
        var map = new Dictionary<int, int>();
        foreach (var entry in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('=');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var gateway) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var dataPlane) ||
                gateway is < 1 or > 65535 || dataPlane is < 1 or > 65535)
            {
                throw new ArgumentException($"--port-map entry '{entry}' must look like 80=8080.");
            }

            map[gateway] = dataPlane;
        }

        return map;
    }

    public GatewayControllerOptions ToControllerOptions() => new()
    {
        Translator = new GatewayTranslatorOptions
        {
            ControllerName = ControllerName,
            ListenerPortMap = PortMap,
            AdminPort = AdminPort,
            AdminTokenEnv = AdminTokenEnv,
            TlsMountPath = TlsMountPath,
            DataPlaneServiceNamespace = DataPlaneNamespace,
            DataPlaneServiceName = DataPlaneService,
            IngressControllerName = IngressControllerName,
            WatchLegacyAnnotation = WatchLegacyAnnotation,
        },
        Output = new OutputOptions
        {
            Namespace = DataPlaneNamespace,
            ConfigMapName = ConfigMapName,
            ConfigKey = ConfigKey,
            TlsSecretName = TlsSecretName,
            DeploymentName = DataPlaneDeployment.Length == 0 ? null : DataPlaneDeployment,
        },
        MinReconcileInterval = TimeSpan.FromMilliseconds(MinReconcileMilliseconds),
    };

    public LeaderElectionOptions ToLeaderOptions() => new()
    {
        Namespace = LeaseNamespace,
        Name = LeaseName,
        Identity = Identity,
    };

    public ClusterCacheOptions ToCacheOptions() => new() { SecretNamespaces = SecretNamespaces };

    private void Validate()
    {
        if (DataPlaneService.Length == 0)
        {
            throw new ArgumentException("--data-plane-service is required.");
        }

        if (DataPlaneNamespace.Length == 0)
        {
            throw new ArgumentException("--data-plane-namespace is required when POD_NAMESPACE is not set.");
        }

        if (LeaderElection && LeaseNamespace.Length == 0)
        {
            throw new ArgumentException("--lease-namespace is required when POD_NAMESPACE is not set.");
        }

        if (MinReconcileMilliseconds < 0)
        {
            throw new ArgumentException("--min-reconcile-ms must not be negative.");
        }
    }

    private static int Int(string? text, int fallback, string name)
    {
        if (text is null)
        {
            return fallback;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException($"--{name} must be a number.");
    }
}
