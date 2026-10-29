using Titanium.Plus.Audit;
using Titanium.Plus.ControlPlane;
using Titanium.Plus.Dashboard;
using Titanium.Plus.Discovery;
using Titanium.Plus.Grpc;
using Titanium.Plus.Observability;
using Titanium.Plus.Operations;
using Titanium.Plus.Resilience;
using Titanium.Plus.Security;
using Titanium.Plus.Security.Oidc;
using Titanium.Plus.State;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus;

/// <summary>
/// Plus plugin entry loaded by Cli via ALC.
/// Sticky sessions: set <c>affinityCookie</c> / <c>affinityHeader</c> on cluster JSON
/// (<see cref="Titanium.Web.Proxy.Abstractions.Clusters.ClusterConfig"/>); Core LoadBalancer honors them.
/// Least-time LB: <c>algorithm</c> / loadBalancingPolicy <c>leastTime</c>, <c>leasttime</c>, or <c>least_time</c>.
/// </summary>
public sealed class TitaniumPlusModule : ITitaniumPlusModule
{
    public Version RequiredAbstractionsVersion { get; } = new(7, 0, 1);

    public void Apply(PlusActivationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = context.Options ?? new Dictionary<string, string>();
        var secret = options.GetValueOrDefault("controlPlane.sharedSecret")
                     ?? options.GetValueOrDefault("sharedSecret")
                     ?? "changeme";
        var host = options.GetValueOrDefault("controlPlane.host") ?? "127.0.0.1";
        var port = int.TryParse(options.GetValueOrDefault("controlPlane.port"), out var p) ? p : 9080;
        int? dashboardPort = int.TryParse(options.GetValueOrDefault("controlPlane.dashboardPort"), out var dp) &&
                             dp is > 0 and < 65536
            ? dp
            : null;
        var allowDev = string.Equals(
            Environment.GetEnvironmentVariable("TITANIUM_PLUS_ALLOW_DEV_SECRET"), "1",
            StringComparison.Ordinal);

        ControlPlaneServer.ValidateSecret(host, secret, allowDev);
        var viewerSecret = options.GetValueOrDefault("controlPlane.dashboardViewerSecret");
        ValidateViewerSecret(viewerSecret, secret);
        var usersFile = First(options, "controlPlane.dashboardUsersFile", "dashboard.usersFile");
        var breakGlass = !IsFalse(First(options, "controlPlane.dashboardBreakGlass", "dashboard.breakGlass"));
        var oidc = ParseDashboardOidc(options);

        var tls = ParseTls(options);
        var allowInsecure = IsTrue(options.GetValueOrDefault("controlPlane.allowInsecureNonLoopback"));
        ControlPlaneServer.ValidateTransport(host, tls, allowInsecure);

        var audit = new AuditLog(
            options.GetValueOrDefault("audit.path"),
            long.TryParse(options.GetValueOrDefault("audit.maxBytes"), out var maxBytes) ? maxBytes : AuditLog.DefaultMaxFileBytes,
            int.TryParse(options.GetValueOrDefault("audit.maxFiles"), out var maxFiles) ? maxFiles : AuditLog.DefaultMaxFiles,
            onError: message => PlusLog.Warn(context, message));
        if (audit.FilePath is not null)
        {
            PlusLog.Info(context, $"Plus audit log: {audit.FilePath}");
        }

        var controlPlane = new ControlPlaneServer(
            context.ClusterManager,
            host,
            port,
            secret,
            context.Routes,
            context.RefreshReverseProxy,
            context.ResponseCache,
            new ControlPlaneSecurity(audit),
            tls,
            allowInsecure);
        controlPlane.Start();
        if (!controlPlane.IsTls && !ControlPlaneServer.IsLoopbackHost(host))
        {
            PlusLog.Warn(context,
                $"Plus control plane listens on {host}:{port} without TLS (allowInsecureNonLoopback). The shared secret crosses the network unencrypted.");
        }

        var operations = new DrainOperations(context.ClusterManager);
        var metrics = new PrometheusMetricsExporter(context.ClusterManager, context.LatencyRecorder);
        context.RegisterMetricsAppender?.Invoke(metrics.AppendTo);
        var dashboard = new DashboardHost(
            controlPlane,
            operations,
            metrics,
            context.ClusterManager,
            dashboardPort,
            new DashboardOptions
            {
                ViewerSecret = viewerSecret,
                Routes = context.Routes,
                HostStatus = context.HostStatus,
                Metrics = new DashboardMetrics(),
                HealthEvents = new HealthEventLog(context.ClusterManager),
                BreakGlass = breakGlass,
                UsersFile = usersFile,
                Oidc = oidc,
            });
        dashboard.Start();
        PlusLog.Info(context, $"Plus dashboard listening on {dashboard.Prefix}");

        // Stretch modules — activate only when configured.
        _ = ServiceDiscovery.TryStart(context, options);
        _ = SharedStateStore.TryStart(context, options);
        _ = RedisFleetRateStore.TryStart(context, options);
        _ = AccessSecurity.TryStart(context, options);
        _ = WafGuard.TryStart(context, options);
        _ = ResilienceController.TryStart(context, options);
        _ = CircuitBreakerController.TryStart(context, options);
        _ = IdempotentRetryGuard.TryStart(context, options);
        _ = GrpcTranscodeGuard.TryStart(context, options);
        _ = OidcLogin.TryStart(context);
    }

    /// <summary>
    ///     The viewer secret opens the dashboard read-only, so it must be its own secret: the same value as the shared
    ///     secret would sign every viewer in as an operator, and the default one is public.
    /// </summary>
    internal static void ValidateViewerSecret(string? viewerSecret, string sharedSecret)
    {
        if (string.IsNullOrEmpty(viewerSecret))
        {
            return;
        }

        if (viewerSecret.Equals("changeme", StringComparison.OrdinalIgnoreCase) ||
            ConstantTime.StringEquals(viewerSecret, sharedSecret))
        {
            throw new InvalidOperationException(
                "controlPlane.dashboardViewerSecret must be set to its own value: not 'changeme' and not the shared secret.");
        }
    }

    private static ControlPlaneTlsOptions? ParseTls(IReadOnlyDictionary<string, string> options)
    {
        var cert = options.GetValueOrDefault("controlPlane.tls.certificatePath");
        var key = options.GetValueOrDefault("controlPlane.tls.keyPath");
        if (string.IsNullOrWhiteSpace(cert) && string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(cert) || string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("controlPlane.tls needs both certificatePath and keyPath.");
        }

        return new ControlPlaneTlsOptions(cert, key);
    }

    internal static DashboardOidcOptions? ParseDashboardOidc(IReadOnlyDictionary<string, string> options)
    {
        var issuer = First(options, "controlPlane.dashboardOidc.issuer", "dashboard.oidc.issuer");
        var clientId = First(options, "controlPlane.dashboardOidc.clientId", "dashboard.oidc.clientId");
        if (string.IsNullOrWhiteSpace(issuer) && string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("controlPlane.dashboardOidc needs both issuer and clientId.");
        }

        var cookieSecret = First(options, "controlPlane.dashboardOidc.cookieSecret", "dashboard.oidc.cookieSecret");
        if (string.IsNullOrWhiteSpace(cookieSecret))
        {
            throw new InvalidOperationException(
                "controlPlane.dashboardOidc needs cookieSecret (or cookieSecretEnv / cookieSecretFile resolved by the host).");
        }

        return new DashboardOidcOptions
        {
            Issuer = issuer,
            ClientId = clientId,
            ClientSecret = First(options, "controlPlane.dashboardOidc.clientSecret", "dashboard.oidc.clientSecret"),
            CookieSecret = cookieSecret,
            RedirectUri = First(options, "controlPlane.dashboardOidc.redirectUri", "dashboard.oidc.redirectUri"),
            GroupsClaim = First(options, "controlPlane.dashboardOidc.groupsClaim", "dashboard.oidc.groupsClaim"),
            OperatorGroups = SplitList(First(options, "controlPlane.dashboardOidc.operatorGroups", "dashboard.oidc.operatorGroups")),
            ViewerGroups = SplitList(First(options, "controlPlane.dashboardOidc.viewerGroups", "dashboard.oidc.viewerGroups")),
            AllowedEmails = SplitList(First(options, "controlPlane.dashboardOidc.allowedEmails", "dashboard.oidc.allowedEmails")),
            AllowedEmailDomains = SplitList(First(options, "controlPlane.dashboardOidc.allowedEmailDomains", "dashboard.oidc.allowedEmailDomains")),
        };
    }

    private static string? First(IReadOnlyDictionary<string, string> options, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static IReadOnlyList<string>? SplitList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var list = value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static s => s.Length > 0)
            .ToArray();
        return list.Length == 0 ? null : list;
    }

    private static bool IsTrue(string? value) =>
        value is not null &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static bool IsFalse(string? value) =>
        value is not null &&
        (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0" ||
         value.Equals("no", StringComparison.OrdinalIgnoreCase));
}
