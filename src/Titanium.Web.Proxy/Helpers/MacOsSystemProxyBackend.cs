using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     macOS system proxy via <c>networksetup</c>, with optional admin elevation on auth failure.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("osx")]
internal sealed class MacOsSystemProxyBackend : ISystemProxyBackend
{
    private const string NetworkSetupCommand = "networksetup";
    private const string EmptyBypass = "Empty";

    private readonly IProcessRunner _runner;
    private readonly IElevationPrompt _elevation;
    private readonly List<ServiceSnapshot> _original = new();
    private bool _hasSnapshot;
    private bool _disposed;
    private readonly EventHandler _processExitHandler;
    private readonly UnhandledExceptionEventHandler _unhandledExceptionHandler;

    public MacOsSystemProxyBackend(IProcessRunner? runner = null, IElevationPrompt? elevation = null)
    {
        _runner = runner ?? new ProcessRunner();
        _elevation = elevation ?? new OsElevationPrompt(_runner);
        _processExitHandler = (_, _) => RestoreOriginalSettings();
        _unhandledExceptionHandler = (_, _) => RestoreOriginalSettings();
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
        AppDomain.CurrentDomain.UnhandledException += _unhandledExceptionHandler;
    }

    public void SetProxy(string hostname, int port, ProxyProtocolType protocolType, string? proxyOverride)
    {
        try
        {
            EnsureSnapshot();
            var services = ListNetworkServices();
            if (services.Count == 0)
            {
                throw new InvalidOperationException(
                    "networksetup listed no network services; macOS system proxy was not changed");
            }

            var bypass = UnixProxyBypassMapper.ToCommaSeparated(proxyOverride);
            foreach (var service in services)
            {
                if ((protocolType & ProxyProtocolType.Http) != 0)
                {
                    RunNetworkSetup($"-setwebproxy \"{Escape(service)}\" {hostname} {port}", true);
                    RunNetworkSetup($"-setwebproxystate \"{Escape(service)}\" on", true);
                }

                if ((protocolType & ProxyProtocolType.Https) != 0)
                {
                    RunNetworkSetup($"-setsecurewebproxy \"{Escape(service)}\" {hostname} {port}", true);
                    RunNetworkSetup($"-setsecurewebproxystate \"{Escape(service)}\" on", true);
                }

                if (proxyOverride != null)
                    RunNetworkSetup($"-setproxybypassdomains \"{Escape(service)}\" {FormatBypassArgs(bypass)}", true);
            }

            VerifyMacApplied(services, hostname, port, protocolType);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to apply macOS system proxy: " + ex.Message, ex);
        }
    }

    public void RemoveProxy(ProxyProtocolType protocolType, bool saveOriginalConfig = true)
    {
        try
        {
            if (saveOriginalConfig) EnsureSnapshot();
            foreach (var service in ListNetworkServices())
            {
                if ((protocolType & ProxyProtocolType.Http) != 0)
                    RunNetworkSetup($"-setwebproxystate \"{Escape(service)}\" off", true);

                if ((protocolType & ProxyProtocolType.Https) != 0)
                    RunNetworkSetup($"-setsecurewebproxystate \"{Escape(service)}\" off", true);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to disable macOS system proxy: " + ex.Message, ex);
        }
    }

    public void DisableAllProxy()
    {
        EnsureSnapshot();
        RemoveProxy(ProxyProtocolType.AllHttp, saveOriginalConfig: false);
    }

    public void RestoreOriginalSettings()
    {
        if (!_hasSnapshot) return;

        try
        {
        foreach (var snap in _original)
        {
            if (snap.HttpEnabled)
            {
                RunNetworkSetup($"-setwebproxy \"{Escape(snap.Service)}\" {snap.HttpHost} {snap.HttpPort}", true);
                RunNetworkSetup($"-setwebproxystate \"{Escape(snap.Service)}\" on", true);
            }
            else
            {
                RunNetworkSetup($"-setwebproxystate \"{Escape(snap.Service)}\" off", true);
            }

            if (snap.HttpsEnabled)
            {
                RunNetworkSetup($"-setsecurewebproxy \"{Escape(snap.Service)}\" {snap.HttpsHost} {snap.HttpsPort}",
                    true);
                RunNetworkSetup($"-setsecurewebproxystate \"{Escape(snap.Service)}\" on", true);
            }
            else
            {
                RunNetworkSetup($"-setsecurewebproxystate \"{Escape(snap.Service)}\" off", true);
            }

            RunNetworkSetup(
                $"-setproxybypassdomains \"{Escape(snap.Service)}\" {FormatBypassArgs(snap.BypassDomains)}", true);
        }

        _original.Clear();
        _hasSnapshot = false;
        }
        catch
        {
            // process-exit restore must not throw
        }
    }

    public string? GetCurrentProxyOverride()
    {
        var service = ListNetworkServices().FirstOrDefault();
        if (service is null) return null;
        var result = _runner.Run(NetworkSetupCommand, $"-getproxybypassdomains \"{Escape(service)}\"");
        if (result is null || !result.Succeeded) return null;
        var domains = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("There aren't any", StringComparison.OrdinalIgnoreCase));
        return string.Join(";", domains);
    }

    public ProxyProtocolType GetStaleLocalProxyProtocols(IReadOnlyCollection<int> ownedPorts)
    {
        var stale = ProxyProtocolType.None;
        foreach (var service in ListNetworkServices())
        {
            var http = ParseProxyState(_runner.Run(NetworkSetupCommand, $"-getwebproxy \"{Escape(service)}\""));
            if (http.Enabled && UnixProxyBypassMapper.IsLocalHost(http.Host) && ownedPorts.Contains(http.Port))
                stale |= ProxyProtocolType.Http;

            var https = ParseProxyState(_runner.Run(NetworkSetupCommand, $"-getsecurewebproxy \"{Escape(service)}\""));
            if (https.Enabled && UnixProxyBypassMapper.IsLocalHost(https.Host) && ownedPorts.Contains(https.Port))
                stale |= ProxyProtocolType.Https;
        }

        return stale;
    }

    public void Dispose()
    {
        if (_disposed) return;
        AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
        AppDomain.CurrentDomain.UnhandledException -= _unhandledExceptionHandler;
        _disposed = true;
    }

    private void EnsureSnapshot()
    {
        if (_hasSnapshot) return;
        _original.Clear();
        foreach (var service in ListNetworkServices())
        {
            var http = ParseProxyState(_runner.Run(NetworkSetupCommand, $"-getwebproxy \"{Escape(service)}\""));
            var https = ParseProxyState(_runner.Run(NetworkSetupCommand, $"-getsecurewebproxy \"{Escape(service)}\""));
            var bypassResult = _runner.Run(NetworkSetupCommand, $"-getproxybypassdomains \"{Escape(service)}\"");
            var bypass = bypassResult?.StandardOutput ?? string.Empty;
            if (bypass.Contains("There aren't any", StringComparison.OrdinalIgnoreCase))
                bypass = EmptyBypass;

            _original.Add(new ServiceSnapshot(
                service,
                http.Enabled, http.Host, http.Port,
                https.Enabled, https.Host, https.Port,
                string.Join(",",
                    bypass.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())
                        .Where(x => x.Length > 0))));
        }

        _hasSnapshot = true;
    }

    private List<string> ListNetworkServices()
    {
        var result = _runner.Run(NetworkSetupCommand, "-listallnetworkservices");
        if (result is null || !result.Succeeded) return new List<string>();

        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("An asterisk", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.StartsWith('*'))
            .ToList();
    }

    private void RunNetworkSetup(string arguments, bool elevateOnAuthFailure)
    {
        var result = _runner.Run(NetworkSetupCommand, arguments);
        if (result is { Succeeded: true }) return;
        if (!elevateOnAuthFailure)
        {
            throw new InvalidOperationException(
                "networksetup failed: " + (result?.StandardError ?? result?.StandardOutput ?? "unknown error"));
        }

        var err = (result?.StandardError ?? string.Empty) + (result?.StandardOutput ?? string.Empty);
        if (result is not null && !LooksLikeAuthFailure(err))
        {
            throw new InvalidOperationException(
                "networksetup failed: " + (string.IsNullOrWhiteSpace(err) ? "non-zero exit" : err.Trim()));
        }

        var elevated = _elevation.RunElevated("/usr/sbin/networksetup", arguments);
        if (elevated is not { Succeeded: true })
        {
            throw new InvalidOperationException(
                "Elevated networksetup failed: " +
                (elevated?.StandardError ?? elevated?.StandardOutput ?? "cancelled or denied"));
        }
    }

    private void VerifyMacApplied(
        IReadOnlyList<string> services, string hostname, int port, ProxyProtocolType protocolType)
    {
        Exception? last = null;
        foreach (var service in services)
        {
            try
            {
                if ((protocolType & ProxyProtocolType.Http) != 0)
                {
                    var http = ParseProxyState(_runner.Run(NetworkSetupCommand, $"-getwebproxy \"{Escape(service)}\""));
                    if (!http.Enabled ||
                        !http.Host.Equals(hostname, StringComparison.OrdinalIgnoreCase) ||
                        http.Port != port)
                    {
                        last = new InvalidOperationException(
                            $"HTTP proxy on '{service}' did not stick (enabled={http.Enabled}, {http.Host}:{http.Port})");
                        continue;
                    }
                }

                if ((protocolType & ProxyProtocolType.Https) != 0)
                {
                    var https = ParseProxyState(
                        _runner.Run(NetworkSetupCommand, $"-getsecurewebproxy \"{Escape(service)}\""));
                    if (!https.Enabled ||
                        !https.Host.Equals(hostname, StringComparison.OrdinalIgnoreCase) ||
                        https.Port != port)
                    {
                        last = new InvalidOperationException(
                            $"HTTPS proxy on '{service}' did not stick (enabled={https.Enabled}, {https.Host}:{https.Port})");
                        continue;
                    }
                }

                return; // at least one service verified
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException("macOS system proxy could not be verified after apply");
    }

    private static bool LooksLikeAuthFailure(string text) =>
        text.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("authoriz", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("admin", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("privileged", StringComparison.OrdinalIgnoreCase);

    private static (bool Enabled, string Host, int Port) ParseProxyState(ProcessRunResult? result)
    {
        if (result is null) return (false, "127.0.0.1", 0);
        var enabled = false;
        var host = "127.0.0.1";
        var port = 0;
        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (key.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                enabled = value.Equals("Yes", StringComparison.OrdinalIgnoreCase);
            else if (key.Equals("Server", StringComparison.OrdinalIgnoreCase))
                host = value;
            else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var p))
                port = p;
        }

        return (enabled, host, port);
    }

    private static string FormatBypassArgs(string bypassCsv)
    {
        if (string.IsNullOrWhiteSpace(bypassCsv) ||
            bypassCsv.Equals(EmptyBypass, StringComparison.OrdinalIgnoreCase))
            return EmptyBypass;

        var sb = new StringBuilder();
        foreach (var part in bypassCsv.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append('"').Append(Escape(part.Trim())).Append('"');
        }

        return sb.Length == 0 ? EmptyBypass : sb.ToString();
    }

    private static string Escape(string value) => value.Replace("\"", "\\\"");

    private sealed record ServiceSnapshot(
        string Service,
        bool HttpEnabled, string HttpHost, int HttpPort,
        bool HttpsEnabled, string HttpsHost, int HttpsPort,
        string BypassDomains);
}
