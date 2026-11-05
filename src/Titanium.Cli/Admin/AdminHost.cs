using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Admin;

/// <summary>What <c>server.admin</c> asks for, after the token has been read.</summary>
internal sealed record AdminSetup(
    IPAddress Address,
    int Port,
    bool RequireHealthyClusters,
    int PreStopDelaySeconds,
    string? AuthToken);

/// <summary>Glue between <c>server.admin</c>, <see cref="RunCommand" />, and <see cref="AdminServer" />.</summary>
internal static class AdminHost
{
    /// <summary>
    ///     Returns null when the admin listener is off. The bearer token is read from
    ///     <see cref="AdminConfig.AuthToken" />, which <see cref="SecretResolver" /> has already filled from
    ///     <c>authTokenEnv</c> or <c>authTokenFile</c> (a reference that cannot be read stops start-up there).
    /// </summary>
    public static AdminSetup? TryPrepare(AdminConfig? config)
    {
        if (config is not { Enabled: true })
        {
            return null;
        }

        var token = config.AuthToken;
        var host = string.IsNullOrWhiteSpace(config.Host) ? AdminServer.DefaultHost : config.Host.Trim();
        return new AdminSetup(
            IPAddress.Parse(host),
            config.Port ?? 0,
            config.Readiness?.RequireHealthyClusters ?? false,
            config.PreStopDelaySeconds ?? 0,
            token);
    }

    public static AdminServer? TryStart(
        AdminSetup? setup,
        IReadinessProbe readiness,
        MetricsExposition exposition,
        Func<AdminVersionInfo> version)
    {
        if (setup is null)
        {
            return null;
        }

        var server = new AdminServer(setup.Address, setup.Port, readiness, exposition, version, setup.AuthToken);
        try
        {
            server.Start();
        }
        catch
        {
            server.Dispose();
            throw;
        }

        AsyncConsole.WriteLine(
            $"Admin: http://{setup.Address}:{server.Port} (/healthz /readyz /metrics /version){(server.RequiresToken ? " token required for /metrics and /version" : string.Empty)}");
        return server;
    }

    public static ClusterHealthView? ClusterView(ClusterManager clusters)
    {
        var snapshot = clusters.Snapshot;
        return new ClusterHealthView(snapshot.Clusters, snapshot.DestinationStates);
    }

    /// <summary>SHA-256 of the config file bytes, lowercase hex. Empty when the file cannot be read.</summary>
    public static string ComputeConfigHash(string configPath)
    {
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(configPath)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public static AdminVersionInfo BuildVersionInfo(string configHash, bool plusActive)
    {
        var informational = typeof(AdminHost).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = "unknown";
        var commit = "unknown";
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            version = plus < 0 ? informational : informational[..plus];
            if (plus >= 0 && plus + 1 < informational.Length)
            {
                commit = informational[(plus + 1)..];
            }
        }

        return new AdminVersionInfo(
            version,
            commit,
            RuntimeInformation.FrameworkDescription,
            plusActive ? "plus" : "community",
            configHash);
    }
}
