namespace Titanium.Cli.Service;

internal static class ServiceDefaults
{
    public const string DefaultServiceName = "titanium";
    public const string DisplayName = "Titanium Web Proxy";
    public const string Description = "Titanium Web Proxy reverse / edge proxy";
    public const string MacOsLabelPrefix = "com.justcoding121.";

    public static string ResolveMacOsLabel(string serviceName) =>
        serviceName.StartsWith("com.", StringComparison.OrdinalIgnoreCase)
            ? serviceName
            : MacOsLabelPrefix + serviceName;

    public static string ResolveExePath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException(
                "Unable to resolve the titanium executable path (Environment.ProcessPath).");
        }

        return Path.GetFullPath(path);
    }
}

internal enum ServiceStatusKind
{
    NotInstalled,
    Stopped,
    Running,
    Other,
}

internal sealed record ServiceStatusResult(
    ServiceStatusKind Kind,
    string Name,
    string? Detail = null);

internal interface IOsServiceManager
{
    Task InstallAsync(ServiceInstallRequest request);
    Task UninstallAsync(string name, bool user);
    Task StartAsync(string name, bool user);
    Task StopAsync(string name, bool user);
    Task RestartAsync(string name, bool user);
    Task<ServiceStatusResult> StatusAsync(string name, bool user);
}

internal sealed record ServiceInstallRequest(
    string Name,
    string ConfigPath,
    bool User,
    bool StartAfterInstall,
    string ExePath,
    string WorkingDirectory);
