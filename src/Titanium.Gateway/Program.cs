using System.Collections;
using Microsoft.Extensions.Logging;
using Titanium.Gateway;
using Titanium.Gateway.Control;
using Titanium.Gateway.Kube;

GatewayOptions options;
try
{
    var environment = Environment.GetEnvironmentVariables()
        .Cast<DictionaryEntry>()
        .ToDictionary(e => (string)e.Key, e => e.Value as string, StringComparer.Ordinal);
    options = GatewayOptions.Parse(args, environment);
}
catch (ArgumentException ex)
{
    await Console.Error.WriteLineAsync($"titanium-gateway: {ex.Message}").ConfigureAwait(false);
    return 2;
}

using var loggers = LoggerFactory.Create(builder => builder
    .SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Information)
    .AddSimpleConsole(c =>
    {
        c.SingleLine = true;
        c.TimestampFormat = "HH:mm:ss ";
    }));
var log = loggers.CreateLogger("titanium-gateway");
if (options.IgnoredEnvironment.Count > 0)
{
    log.LogWarning("Ignoring environment variables that name no option (Kubernetes service links?): {Names}",
        string.Join(", ", options.IgnoredEnvironment));
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();

try
{
    using var api = KubernetesClientApi.Create();
    var cache = new ClusterCache(api, options.ToCacheOptions(), log);
    var controllerOptions = options.ToControllerOptions();
    var leader = options.LeaderElection ? new LeaderElector(api, options.ToLeaderOptions(), log) : null;
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    var observer = new HttpDataPlaneObserver(
        http,
        options.AdminPort,
        Environment.GetEnvironmentVariable(options.AdminTokenEnv),
        log);
    var controller = new GatewayController(
        cache,
        new OutputWriter(api, controllerOptions.Output),
        new StatusWriter(api, options.ControllerName, log),
        observer,
        leader,
        controllerOptions,
        log,
        events: new EventWriter(api, options.IngressControllerName, log));

    using var health = new HealthServer(options.HealthPort, () => controller.Ready, log);
    if (log.IsEnabled(LogLevel.Information))
    {
        log.LogInformation(
            "titanium-gateway starting as {Identity}: controller {Controller}, data plane {Namespace}/{Service}.",
            options.Identity,
            options.ControllerName,
            options.DataPlaneNamespace,
            options.DataPlaneService);
    }
    await Task.WhenAll(controller.RunAsync(stop.Token), health.RunAsync(stop.Token)).ConfigureAwait(false);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception ex)
{
    log.LogCritical(ex, "titanium-gateway stopped: {Message}", ex.Message);
    return 1;
}
