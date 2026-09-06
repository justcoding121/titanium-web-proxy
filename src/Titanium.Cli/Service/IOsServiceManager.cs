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
        var prefix = ResolveProgramPrefix();
        return prefix[0];
    }

    /// <summary>
    /// Command prefix written into the OS service unit. A published apphost is
    /// <c>titanium</c>/<c>titanium.exe</c>. <c>dotnet titanium.dll</c> must not become
    /// <c>dotnet run -c …</c> (that looks for a project in the config directory).
    /// </summary>
    public static string[] ResolveProgramPrefix() =>
        ResolveProgramPrefix(
            Environment.ProcessPath,
            Environment.GetCommandLineArgs(),
            AppContext.BaseDirectory);

    internal static string[] ResolveProgramPrefix(
        string? processPath,
        string[] commandLineArgs,
        string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            throw new InvalidOperationException(
                "Unable to resolve the titanium executable path (Environment.ProcessPath).");
        }

        var host = Path.GetFullPath(processPath);
        if (!IsDotnetMuxer(host))
            return [host];

        var entry = ResolveEntryDll(commandLineArgs, baseDirectory);
        var dllDir = Path.GetDirectoryName(entry) ?? baseDirectory;
        var apphost = Path.Combine(
            dllDir,
            OperatingSystem.IsWindows() ? "titanium.exe" : "titanium");
        if (File.Exists(apphost))
            return [Path.GetFullPath(apphost)];

        return [host, entry];
    }

    /// <summary>
    /// Process + optional <c>titanium.dll</c> args when relaunching under UAC/sudo
    /// while still hosted as <c>dotnet titanium.dll</c> (no adjacent apphost).
    /// </summary>
    internal static (string FileName, string[] PrefixArgs) ResolveRelaunchTarget()
    {
        var prefix = ResolveProgramPrefix();
        return (prefix[0], prefix.Length > 1 ? prefix[1..] : []);
    }

    public static Dictionary<string, string> ResolveServiceEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        };

        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (string.IsNullOrEmpty(root) &&
            !string.IsNullOrWhiteSpace(Environment.ProcessPath) &&
            IsDotnetMuxer(Environment.ProcessPath))
        {
            root = Path.GetDirectoryName(Path.GetFullPath(Environment.ProcessPath));
        }

        if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
            env["DOTNET_ROOT"] = root;

        return env;
    }

    internal static bool IsDotnetMuxer(string processPath)
    {
        var name = Path.GetFileNameWithoutExtension(processPath);
        return name.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Alias for <see cref="IsDotnetMuxer"/> used by elevation relaunch paths.</summary>
    internal static bool IsDotnetHostPath(string path) => IsDotnetMuxer(path);

    private static string ResolveEntryDll(string[] commandLineArgs, string baseDirectory)
    {
        if (commandLineArgs.Length > 0)
        {
            var candidate = commandLineArgs[0];
            if (!Path.IsPathRooted(candidate))
                candidate = Path.Combine(baseDirectory, candidate);
            candidate = Path.GetFullPath(candidate);
            if (candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                return candidate;
        }

        var fallback = Path.GetFullPath(Path.Combine(baseDirectory, "titanium.dll"));
        if (File.Exists(fallback))
            return fallback;

        throw new InvalidOperationException(
            "Unable to resolve titanium.dll while hosted by the dotnet muxer.");
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
    IReadOnlyList<string> ProgramPrefix,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);
