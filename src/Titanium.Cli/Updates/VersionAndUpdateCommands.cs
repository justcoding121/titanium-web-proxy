using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Titanium.Cli;
using Titanium.Cli.Config;
using Titanium.Cli.Http3;

namespace Titanium.Cli.Updates;

internal static class VersionCommand
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        var check = args.Contains("--check", StringComparer.OrdinalIgnoreCase);
        var plus = args.Contains("--plus", StringComparer.OrdinalIgnoreCase);
        var channel = ParseChannel(args);
        var channelDisplay = FormatChannel(channel);

        PrintLocalVersions(plus);

        if (!check)
        {
            return 0;
        }

        var client = new UpdateFeedClient(channel);
        var manifest = await client.TryGetManifestAsync();
        if (manifest is null)
        {
            AsyncConsole.WriteError("Unable to query update feed.");
            return 1;
        }

        var local = typeof(Program).Assembly.GetName().Version ?? new Version(0, 0);
        var remote = Version.TryParse(StripPrerelease(manifest.Version), out var v) ? v : new Version(0, 0);
        AsyncConsole.WriteLine($"Remote Cli ({channelDisplay}): {manifest.Version}");

        var exit = 0;
        if (remote > local)
        {
            AsyncConsole.WriteLine($"A newer Cli build is available ({channelDisplay}). Run: titanium update --channel {channel}");
            exit = 2;
        }
        else
        {
            AsyncConsole.WriteLine($"Cli is up to date ({channelDisplay}).");
        }

        if (plus)
        {
            var plusLocal = TryGetLocalPlusVersion();
            var plusRemote = manifest.Products?.Plus?.Version;
            if (!string.IsNullOrEmpty(plusRemote) &&
                Version.TryParse(StripPrerelease(plusRemote), out var pr) &&
                (plusLocal is null || pr > plusLocal))
            {
                AsyncConsole.WriteLine(
                    $"A newer Plus build is available ({plusRemote}, {channelDisplay}). Run: titanium update --plus --channel {channel}");
                exit = 2;
            }
            else if (plusLocal is not null)
            {
                AsyncConsole.WriteLine($"Plus is up to date ({plusLocal}, {channelDisplay}).");
            }
        }

        return exit;
    }

    private static Version? TryGetLocalPlusVersion()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Titanium.Plus.dll");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return AssemblyName.GetAssemblyName(path).Version;
        }
        catch
        {
            return null;
        }
    }

    private static void PrintLocalVersions(bool includePlus)
    {
        void Print(string name, Assembly? asm)
        {
            var ver = asm?.GetName().Version?.ToString() ?? "(not loaded)";
            AsyncConsole.WriteLine($"{name}: {ver}");
        }

        Print("Cli", typeof(Program).Assembly);
        Print("Core", typeof(Titanium.Web.Proxy.ProxyServer).Assembly);
        Print("Abstractions", typeof(Titanium.Web.Proxy.Abstractions.ReverseProxyOptions).Assembly);
        Print("Configuration", typeof(Titanium.Web.Proxy.Configuration.TwpConfigLoader).Assembly);

        if (includePlus || File.Exists(Path.Combine(AppContext.BaseDirectory, "Titanium.Plus.dll")))
        {
            var module = PlusLoader.TryLoad(out var warning);
            if (warning is not null)
            {
                AsyncConsole.WriteLine($"Plus: {warning}");
            }
            else if (module is not null)
            {
                AsyncConsole.WriteLine($"Plus: {module.GetType().Assembly.GetName().Version} (RequiredAbstractions={module.RequiredAbstractionsVersion})");
            }
            else
            {
                AsyncConsole.WriteLine("Plus: not present");
            }
        }
    }

    internal static string ParseChannel(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--channel" && i + 1 < args.Length)
            {
                return args[i + 1].Trim().ToLowerInvariant();
            }
        }

        return (Environment.GetEnvironmentVariable("TITANIUM_UPDATE_CHANNEL") ?? "stable").Trim().ToLowerInvariant();
    }

    internal static string FormatChannel(string channel) =>
        channel.Equals("beta", StringComparison.OrdinalIgnoreCase) ? "beta" : "stable";

    internal static string StripPrerelease(string? version)
    {
        if (string.IsNullOrEmpty(version))
        {
            return "0.0.0";
        }

        var trimmed = version.TrimStart('v');
        var dash = trimmed.IndexOf('-');
        return dash > 0 ? trimmed[..dash] : trimmed;
    }
}

internal static class UpdateCommand
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        var plus = args.Contains("--plus", StringComparer.OrdinalIgnoreCase);
        var channel = VersionCommand.ParseChannel(args);
        var channelDisplay = VersionCommand.FormatChannel(channel);

        AsyncConsole.WriteLine(plus
            ? $"Checking Plus updates ({channelDisplay})…"
            : $"Checking for updates ({channelDisplay})…");

        var client = new UpdateFeedClient(channel);
        var manifest = await client.TryGetManifestAsync();
        if (manifest is null)
        {
            AsyncConsole.WriteError("Unable to query update feed.");
            return 1;
        }

        if (plus)
        {
            return await UpdatePlusAsync(manifest, channelDisplay);
        }

        return await UpdateCliAsync(manifest, channelDisplay);
    }

    private static async Task<int> UpdateCliAsync(ReleaseManifest manifest, string channelDisplay)
    {
        var local = typeof(Program).Assembly.GetName().Version ?? new Version(0, 0);
        var remoteText = manifest.Version?.TrimStart('v') ?? "0.0.0";
        var remote = Version.TryParse(VersionCommand.StripPrerelease(remoteText), out var v) ? v : new Version(0, 0);
        if (remote <= local)
        {
            AsyncConsole.WriteLine($"Titanium CLI is up to date ({channelDisplay}).");
            return 0;
        }

        var rid = ResolveRid();
        var asset = manifest.Products?.Cli?.Assets?.GetValueOrDefault(rid);
        if (asset?.Url is null)
        {
            AsyncConsole.WriteError(
                $"No CLI package for RID {rid} on channel {channelDisplay} (remote {remoteText}).");
            return 1;
        }

        AsyncConsole.WriteLine($"Update {remoteText} ({channelDisplay}) is available. Installing…");
        AsyncConsole.WriteLine("Downloading…");

        var workDir = Path.Combine(Path.GetTempPath(), "TitaniumCli-update");
        Directory.CreateDirectory(workDir);
        var destZip = Path.Combine(workDir, $"Titanium.Cli-{rid}-{remoteText}.zip");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Titanium.Cli/7.0");
            var bytes = await http.GetByteArrayAsync(asset.Url);
            AsyncConsole.WriteLine("Verifying SHA256…");
            if (!string.IsNullOrEmpty(asset.Sha256))
            {
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    AsyncConsole.WriteError("SHA256 mismatch — aborting update.");
                    return 1;
                }
            }

            await File.WriteAllBytesAsync(destZip, bytes);
        }
        catch (Exception ex)
        {
            AsyncConsole.WriteError($"Download failed: {ex.Message}");
            return 1;
        }

        var installDir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var exeName = OperatingSystem.IsWindows() ? "titanium.exe" : "titanium";
        var relaunch = Path.Combine(installDir, exeName);
        if (!File.Exists(relaunch))
        {
            // Published layout may use AssemblyName titanium without extension on Unix already handled;
            // twp sibling is optional.
            relaunch = Path.Combine(installDir, OperatingSystem.IsWindows() ? "twp.exe" : "twp");
        }

        AsyncConsole.WriteLine("Restarting updater…");
        CliUpdateApplyHelper.StartDetached(
            Environment.ProcessId,
            destZip,
            installDir,
            relaunch,
            remoteText,
            channelDisplay);

        AsyncConsole.WriteLine(
            $"Installing {remoteText} ({channelDisplay}) in the background. When finished, run: titanium version");
        // Exit so the helper can replace locked binaries.
        return 0;
    }

    private static async Task<int> UpdatePlusAsync(ReleaseManifest manifest, string channelDisplay)
    {
        var asset = manifest.Products?.Plus?.Asset;
        if (asset?.Url is null)
        {
            AsyncConsole.WriteError($"No Plus package on channel {channelDisplay}.");
            return 1;
        }

        var remoteLabel = manifest.Products?.Plus?.Version ?? manifest.Version ?? "unknown";
        var dest = Path.Combine(AppContext.BaseDirectory, "Titanium.Plus.dll");
        var backup = dest + ".bak";
        AsyncConsole.WriteLine("Downloading…");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Titanium.Cli/7.0");
            var bytes = await http.GetByteArrayAsync(asset.Url);
            AsyncConsole.WriteLine("Verifying SHA256…");
            if (!string.IsNullOrEmpty(asset.Sha256))
            {
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    AsyncConsole.WriteError("SHA256 mismatch — aborting Plus update.");
                    return 1;
                }
            }

            if (File.Exists(dest))
            {
                File.Copy(dest, backup, overwrite: true);
            }

            var staging = dest + ".new";
            await File.WriteAllBytesAsync(staging, bytes);
            File.Move(staging, dest, overwrite: true);
            AsyncConsole.WriteLine($"Updated Titanium.Plus.dll to {remoteLabel} ({channelDisplay}).");
            return 0;
        }
        catch (Exception ex)
        {
            AsyncConsole.WriteError($"Plus update failed: {ex.Message}");
            return 1;
        }
    }

    private static string ResolveRid() => Http3DepsCommand.SuggestRid();
}

/// <summary>Detached helper that replaces the CLI install after this process exits.</summary>
internal static class CliUpdateApplyHelper
{
    public static void StartDetached(
        int pid,
        string zipPath,
        string installDir,
        string relaunchPath,
        string version,
        string channel)
    {
        var workDir = Path.GetDirectoryName(zipPath) ?? Path.GetTempPath();
        if (OperatingSystem.IsWindows())
        {
            var ps1 = Path.Combine(workDir, "apply-cli-update.ps1");
            File.WriteAllText(ps1, BuildWindowsScript(pid, zipPath, installDir, relaunchPath, version, channel), Encoding.UTF8);
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WorkingDirectory = workDir,
            });
            return;
        }

        var sh = Path.Combine(workDir, "apply-cli-update.sh");
        File.WriteAllText(sh, BuildUnixScript(pid, zipPath, installDir, relaunchPath, version, channel), new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"\"{sh}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
        });
    }

    internal static string BuildWindowsScript(
        int pid,
        string zipPath,
        string installDir,
        string relaunchPath,
        string version,
        string channel)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$pidToWait = {pid}");
        sb.AppendLine($"$package = '{EscapePs(zipPath)}'");
        sb.AppendLine($"$installDir = '{EscapePs(installDir)}'");
        sb.AppendLine($"$relaunch = '{EscapePs(relaunchPath)}'");
        sb.AppendLine("while (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 400 }");
        sb.AppendLine("Start-Sleep -Seconds 1");
        sb.AppendLine("$tmp = Join-Path $env:TEMP ('ti-cli-unz-' + [guid]::NewGuid().ToString('n'))");
        sb.AppendLine("New-Item -ItemType Directory -Force -Path $tmp | Out-Null");
        sb.AppendLine("Expand-Archive -LiteralPath $package -DestinationPath $tmp -Force");
        sb.AppendLine("Copy-Item -Path (Join-Path $tmp '*') -Destination $installDir -Recurse -Force");
        sb.AppendLine("Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue");
        sb.AppendLine($"Write-Host 'Updated to {EscapePs(version)} ({EscapePs(channel)}).'");
        sb.AppendLine("if (Test-Path -LiteralPath $relaunch) {");
        sb.AppendLine("  & $relaunch version");
        sb.AppendLine("}");
        return sb.ToString();
    }

    internal static string BuildUnixScript(
        int pid,
        string zipPath,
        string installDir,
        string relaunchPath,
        string version,
        string channel)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine($"pid={pid}");
        sb.AppendLine($"package={BashQuote(zipPath)}");
        sb.AppendLine($"install_dir={BashQuote(installDir)}");
        sb.AppendLine($"relaunch={BashQuote(relaunchPath)}");
        sb.AppendLine("while kill -0 \"$pid\" 2>/dev/null; do sleep 0.4; done");
        sb.AppendLine("sleep 1");
        sb.AppendLine("tmp=$(mktemp -d)");
        sb.AppendLine("unzip -qo \"$package\" -d \"$tmp\"");
        sb.AppendLine("cp -a \"$tmp\"/. \"$install_dir\"/");
        sb.AppendLine("rm -rf \"$tmp\"");
        sb.AppendLine("chmod +x \"$install_dir/titanium\" \"$install_dir/twp\" 2>/dev/null || true");
        sb.AppendLine($"echo 'Updated to {version} ({channel}).'");
        sb.AppendLine("if [[ -x \"$relaunch\" ]]; then \"$relaunch\" version || true; fi");
        return sb.ToString();
    }

    private static string EscapePs(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string BashQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

internal sealed class UpdateFeedClient
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _channel;

    public UpdateFeedClient(string channel) => _channel = channel;

    public async Task<ReleaseManifest?> TryGetManifestAsync()
    {
        var feed = Environment.GetEnvironmentVariable("TITANIUM_UPDATE_FEED");
        if (feed == string.Empty)
        {
            return null;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Titanium.Cli/7.0");

            if (!string.IsNullOrEmpty(feed))
            {
                var json = await http.GetStringAsync(feed);
                return JsonSerializer.Deserialize<ReleaseManifest>(json, ManifestJson);
            }

            var api = _channel.Equals("beta", StringComparison.OrdinalIgnoreCase)
                ? "https://api.github.com/repos/justcoding121/titanium-web-proxy/releases"
                : "https://api.github.com/repos/justcoding121/titanium-web-proxy/releases/latest";

            var payload = await http.GetStringAsync(api);
            using var doc = JsonDocument.Parse(payload);
            if (!TrySelectRelease(doc.RootElement, out var release))
            {
                return null;
            }

            var version = release.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "0.0.0";
            var fromAsset = await TryLoadManifestAssetAsync(http, release, version);
            return fromAsset ?? new ReleaseManifest { Version = version, Channel = _channel };
        }
        catch
        {
            return null;
        }
    }

    private bool TrySelectRelease(JsonElement root, out JsonElement release)
    {
        release = default;
        if (_channel.Equals("beta", StringComparison.OrdinalIgnoreCase))
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

    private async Task<ReleaseManifest?> TryLoadManifestAssetAsync(
        HttpClient http,
        JsonElement release,
        string version)
    {
        if (!release.TryGetProperty("assets", out var assets))
        {
            return null;
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

            var manifestJson = await http.GetStringAsync(url);
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestJson, ManifestJson);
            if (manifest is null)
            {
                continue;
            }

            manifest.Version ??= version;
            manifest.Channel ??= _channel;
            return manifest;
        }

        return null;
    }
}

internal sealed class ReleaseManifest
{
    public string? Version { get; set; }
    public string? Channel { get; set; }
    public ProductsBlock? Products { get; set; }
}

internal sealed class ProductsBlock
{
    public CliBlock? Cli { get; set; }
    public PlusProductBlock? Plus { get; set; }
    public InspectorBlock? Inspector { get; set; }
}

internal sealed class CliBlock
{
    public Dictionary<string, AssetBlock>? Assets { get; set; }
}

internal sealed class InspectorBlock
{
    public Dictionary<string, AssetBlock>? Assets { get; set; }
}

internal sealed class PlusProductBlock
{
    public string? Version { get; set; }
    public AssetBlock? Asset { get; set; }
}

internal sealed class AssetBlock
{
    public string? Url { get; set; }
    public string? Sha256 { get; set; }
}
