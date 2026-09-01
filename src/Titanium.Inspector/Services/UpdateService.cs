using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Titanium.Inspector.Services;

public enum UpdateApplyKind
{
    Msi,
    Zip,
}

public sealed class UpdateCheckResult
{
    public bool UpdateAvailable { get; init; }
    public string Message { get; init; } = "";
    public string? RemoteVersion { get; init; }
    public string ChannelDisplay { get; init; } = "Stable";
    public string? AssetUrl { get; init; }
    public string? AssetSha256 { get; init; }
    public UpdateApplyKind ApplyKind { get; init; } = UpdateApplyKind.Zip;
}

/// <summary>GitHub Releases + release-manifest updater for Stable/Beta channels.</summary>
public sealed class UpdateService
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string GitHubReleasesUrl =
        "https://api.github.com/repos/justcoding121/titanium-web-proxy/releases"; // NOSONAR S1075

    private const string GitHubLatestReleaseUrl =
        "https://api.github.com/repos/justcoding121/titanium-web-proxy/releases/latest"; // NOSONAR S1075

    private readonly SettingsService _settings;
    private readonly Func<HttpClient> _httpFactory;

    public UpdateService(SettingsService settings, Func<HttpClient>? httpFactory = null)
    {
        _settings = settings;
        _httpFactory = httpFactory ?? (() => new HttpClient { Timeout = TimeSpan.FromMinutes(5) });
    }

    public string ChannelDisplayName =>
        _settings.Current.UpdateChannel.Equals("Beta", StringComparison.OrdinalIgnoreCase)
            ? "Beta"
            : "Stable";

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        _settings.Current.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        _settings.Save();

        var channelDisplay = ChannelDisplayName;
        var local = AssemblyVersion();

        try
        {
            using var http = _httpFactory();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TitaniumInspector/7.0");

            var manifest = await TryGetManifestAsync(http, channelDisplay, cancellationToken);
            if (manifest is null)
            {
                return new UpdateCheckResult
                {
                    ChannelDisplay = channelDisplay,
                    Message = channelDisplay.Equals("Beta", StringComparison.OrdinalIgnoreCase)
                        ? "No beta release found."
                        : "Update check failed: no release manifest.",
                };
            }

            var remoteText = manifest.Version?.TrimStart('v') ?? "0.0.0";
            if (!Version.TryParse(remoteText.Split('-')[0], out var remote))
            {
                remote = new Version(0, 0);
            }

            if (remote <= local)
            {
                return new UpdateCheckResult
                {
                    RemoteVersion = remoteText,
                    ChannelDisplay = channelDisplay,
                    Message = $"Titanium Inspector is up to date ({channelDisplay}).",
                };
            }

            var (kind, asset) = ResolveAsset(manifest);
            if (asset?.Url is null)
            {
                return new UpdateCheckResult
                {
                    UpdateAvailable = true,
                    RemoteVersion = remoteText,
                    ChannelDisplay = channelDisplay,
                    Message =
                        $"Update {remoteText} ({channelDisplay}) is available, but no package was found for this install.",
                };
            }

            return new UpdateCheckResult
            {
                UpdateAvailable = true,
                RemoteVersion = remoteText,
                ChannelDisplay = channelDisplay,
                AssetUrl = asset.Url,
                AssetSha256 = asset.Sha256,
                ApplyKind = kind,
                Message = $"Update available: {remoteText} ({channelDisplay})",
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult
            {
                ChannelDisplay = channelDisplay,
                Message = $"Update check failed: {ex.Message}",
            };
        }
    }

    /// <summary>Download package, verify SHA256, spawn apply helper, return true if helper started.</summary>
    public async Task<(bool Ok, string Message)> DownloadAndStartApplyAsync(
        UpdateCheckResult check,
        CancellationToken cancellationToken = default)
    {
        if (!check.UpdateAvailable || string.IsNullOrEmpty(check.AssetUrl))
        {
            return (false, "No update package to install.");
        }

        var workDir = Path.Combine(Path.GetTempPath(), "TitaniumInspector-update");
        Directory.CreateDirectory(workDir);
        var fileName = check.ApplyKind == UpdateApplyKind.Msi
            ? $"TitaniumInspector-{check.RemoteVersion}.msi"
            : $"TitaniumInspector-{check.RemoteVersion}.zip";
        var packagePath = Path.Combine(workDir, fileName);

        try
        {
            using var http = _httpFactory();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TitaniumInspector/7.0");
            var bytes = await http.GetByteArrayAsync(check.AssetUrl, cancellationToken);
            if (!string.IsNullOrEmpty(check.AssetSha256))
            {
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!hash.Equals(check.AssetSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "SHA256 mismatch — aborting update.");
                }
            }

            await File.WriteAllBytesAsync(packagePath, bytes, cancellationToken);

            var installDir = AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var exeName = OperatingSystem.IsWindows() ? "TitaniumInspector.exe" : "TitaniumInspector";
            var relaunchPath = Path.Combine(installDir, exeName);
            if (OperatingSystem.IsMacOS() && TryFindMacAppBundle(installDir, out var appBundle))
            {
                relaunchPath = appBundle;
            }

            UpdateApplyHelper.StartDetached(
                Process.GetCurrentProcess().Id,
                check.ApplyKind,
                packagePath,
                installDir,
                relaunchPath,
                check.RemoteVersion ?? "",
                check.ChannelDisplay);

            return (true, $"Installing {check.RemoteVersion} ({check.ChannelDisplay})…");
        }
        catch (Exception ex)
        {
            return (false, $"Update failed: {ex.Message}");
        }
    }

    public static Version AssemblyVersion() =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    public static bool IsMsiInstall(string baseDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var full = Path.GetFullPath(baseDirectory);
            if ((!string.IsNullOrEmpty(pf) && full.StartsWith(Path.GetFullPath(pf), StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(pf86) && full.StartsWith(Path.GetFullPath(pf86), StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\justcoding121\TitaniumInspector");
                if (key?.GetValue("installed") is not null)
                {
                    return true;
                }
            }
            catch
            {
                // ignore registry access issues
            }
        }

        return false;
    }

    public static string SuggestRid()
    {
        var arm = RuntimeInformation.OSArchitecture is Architecture.Arm64 or Architecture.Arm;
        if (OperatingSystem.IsWindows())
        {
            return "win-x64";
        }

        if (OperatingSystem.IsMacOS())
        {
            return arm ? "osx-arm64" : "osx-x64";
        }

        if (File.Exists("/etc/alpine-release"))
        {
            return arm ? "linux-musl-arm64" : "linux-musl-x64";
        }

        return arm ? "linux-arm64" : "linux-x64";
    }

    public (UpdateApplyKind Kind, ManifestAsset? Asset) ResolveAsset(InspectorReleaseManifest manifest)
    {
        var assets = manifest.Products?.Inspector?.Assets;
        if (assets is null)
        {
            return (UpdateApplyKind.Zip, null);
        }

        if (IsMsiInstall(AppContext.BaseDirectory)
            && assets.TryGetValue("win-x64-msi", out var msi)
            && !string.IsNullOrEmpty(msi.Url))
        {
            return (UpdateApplyKind.Msi, msi);
        }

        var rid = SuggestRid();
        if (assets.TryGetValue(rid, out var zip) && !string.IsNullOrEmpty(zip.Url))
        {
            return (UpdateApplyKind.Zip, zip);
        }

        // Portable Windows without MSI asset key may still ship win-x64 zip.
        if (OperatingSystem.IsWindows()
            && assets.TryGetValue("win-x64", out var winZip)
            && !string.IsNullOrEmpty(winZip.Url))
        {
            return (UpdateApplyKind.Zip, winZip);
        }

        return (UpdateApplyKind.Zip, null);
    }

    private async Task<InspectorReleaseManifest?> TryGetManifestAsync(
        HttpClient http,
        string channelDisplay,
        CancellationToken cancellationToken)
    {
        var feed = Environment.GetEnvironmentVariable("TITANIUM_UPDATE_FEED");
        if (feed == string.Empty)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(feed))
        {
            var json = await http.GetStringAsync(feed, cancellationToken);
            return JsonSerializer.Deserialize<InspectorReleaseManifest>(json, ManifestJson);
        }

        var beta = channelDisplay.Equals("Beta", StringComparison.OrdinalIgnoreCase);
        var api = beta ? GitHubReleasesUrl : GitHubLatestReleaseUrl;
        var payload = await http.GetStringAsync(api, cancellationToken);
        using var doc = JsonDocument.Parse(payload);
        if (!TrySelectRelease(doc.RootElement, beta, out var release))
        {
            return null;
        }

        var version = release.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "0.0.0";
        if (!release.TryGetProperty("assets", out var assets))
        {
            return new InspectorReleaseManifest { Version = version, Channel = channelDisplay.ToLowerInvariant() };
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.Equals("release-manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = asset.GetProperty("browser_download_url").GetString();
            if (url is null)
            {
                continue;
            }

            var manifestJson = await http.GetStringAsync(url, cancellationToken);
            var manifest = JsonSerializer.Deserialize<InspectorReleaseManifest>(manifestJson, ManifestJson);
            if (manifest is null)
            {
                continue;
            }

            manifest.Version ??= version;
            manifest.Channel ??= channelDisplay.ToLowerInvariant();
            return manifest;
        }

        return new InspectorReleaseManifest { Version = version, Channel = channelDisplay.ToLowerInvariant() };
    }

    private static bool TrySelectRelease(JsonElement root, bool beta, out JsonElement release)
    {
        release = default;
        if (beta)
        {
            foreach (var el in root.EnumerateArray())
            {
                if (el.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
                {
                    release = el;
                    return true;
                }
            }

            return false;
        }

        release = root;
        return true;
    }

    private static bool TryFindMacAppBundle(string installDir, out string appPath)
    {
        appPath = "";
        var dir = new DirectoryInfo(installDir);
        while (dir is not null)
        {
            if (dir.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                appPath = dir.FullName;
                return true;
            }

            dir = dir.Parent;
        }

        return false;
    }
}

public sealed class InspectorReleaseManifest
{
    public string? Version { get; set; }
    public string? Channel { get; set; }
    public InspectorProductsBlock? Products { get; set; }
}

public sealed class InspectorProductsBlock
{
    public InspectorProductAssets? Inspector { get; set; }
}

public sealed class InspectorProductAssets
{
    public Dictionary<string, ManifestAsset>? Assets { get; set; }
}

public sealed class ManifestAsset
{
    public string? Url { get; set; }
    public string? Sha256 { get; set; }
}

/// <summary>Spawns a detached helper that applies an update after this process exits.</summary>
public static class UpdateApplyHelper
{
    public static void StartDetached(
        int pid,
        UpdateApplyKind kind,
        string packagePath,
        string installDir,
        string relaunchPath,
        string version,
        string channel)
    {
        var workDir = Path.GetDirectoryName(packagePath) ?? Path.GetTempPath();
        if (OperatingSystem.IsWindows())
        {
            var ps1 = Path.Combine(workDir, "apply-update.ps1");
            File.WriteAllText(ps1, BuildWindowsScript(pid, kind, packagePath, installDir, relaunchPath, version, channel), Encoding.UTF8);
            Process.Start(new ProcessStartInfo
            {
                // Absolute path: Sonar S4036 (PATH lookup for powershell.exe is a vulnerability).
                FileName = ResolveWindowsPowerShellPath(),
                Arguments =
                    $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WorkingDirectory = workDir,
            });
            return;
        }

        var sh = Path.Combine(workDir, "apply-update.sh");
        File.WriteAllText(sh, BuildUnixScript(pid, packagePath, installDir, relaunchPath, version, channel), new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"\"{sh}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
        });
    }

    /// <summary>Absolute Windows PowerShell path — avoids PATH-based Process.Start (Sonar S4036).</summary>
    private static string ResolveWindowsPowerShellPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

    public static string BuildWindowsScript(
        int pid,
        UpdateApplyKind kind,
        string packagePath,
        string installDir,
        string relaunchPath,
        string version,
        string channel)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$pidToWait = {pid}");
        sb.AppendLine($"$package = '{EscapePs(packagePath)}'");
        sb.AppendLine($"$installDir = '{EscapePs(installDir)}'");
        sb.AppendLine($"$relaunch = '{EscapePs(relaunchPath)}'");
        sb.AppendLine("while (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 400 }");
        sb.AppendLine("Start-Sleep -Seconds 1");
        if (kind == UpdateApplyKind.Msi)
        {
            sb.AppendLine("Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/i', $package, '/qn', '/norestart') -Wait -Verb RunAs");
        }
        else
        {
            sb.AppendLine("$tmp = Join-Path $env:TEMP ('ti-unz-' + [guid]::NewGuid().ToString('n'))");
            sb.AppendLine("New-Item -ItemType Directory -Force -Path $tmp | Out-Null");
            sb.AppendLine("Expand-Archive -LiteralPath $package -DestinationPath $tmp -Force");
            sb.AppendLine("Copy-Item -Path (Join-Path $tmp '*') -Destination $installDir -Recurse -Force");
            sb.AppendLine("Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue");
        }

        sb.AppendLine("if (Test-Path -LiteralPath $relaunch) {");
        sb.AppendLine("  if ($relaunch -like '*.app') { Start-Process 'open' -ArgumentList $relaunch }");
        sb.AppendLine("  else { Start-Process -FilePath $relaunch -WorkingDirectory $installDir }");
        sb.AppendLine("}");
        sb.AppendLine($"Write-Output 'Updated Inspector to {EscapePs(version)} ({EscapePs(channel)}).'");
        return sb.ToString();
    }

    public static string BuildUnixScript(
        int pid,
        string packagePath,
        string installDir,
        string relaunchPath,
        string version,
        string channel)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine($"pid={pid}");
        sb.AppendLine($"package={BashQuote(packagePath)}");
        sb.AppendLine($"install_dir={BashQuote(installDir)}");
        sb.AppendLine($"relaunch={BashQuote(relaunchPath)}");
        sb.AppendLine("while kill -0 \"$pid\" 2>/dev/null; do sleep 0.4; done");
        sb.AppendLine("sleep 1");
        sb.AppendLine("tmp=$(mktemp -d)");
        sb.AppendLine("unzip -qo \"$package\" -d \"$tmp\"");
        sb.AppendLine("cp -a \"$tmp\"/. \"$install_dir\"/");
        sb.AppendLine("rm -rf \"$tmp\"");
        sb.AppendLine("chmod +x \"$install_dir/TitaniumInspector\" 2>/dev/null || true");
        sb.AppendLine("if [[ \"$relaunch\" == *.app ]]; then");
        sb.AppendLine("  open \"$relaunch\" || true");
        sb.AppendLine("elif [[ -x \"$relaunch\" ]]; then");
        sb.AppendLine("  (cd \"$install_dir\" && nohup \"$relaunch\" >/dev/null 2>&1 &)");
        sb.AppendLine("fi");
        sb.AppendLine($"echo 'Updated Inspector to {version} ({channel}).'");
        return sb.ToString();
    }

    private static string EscapePs(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string BashQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
