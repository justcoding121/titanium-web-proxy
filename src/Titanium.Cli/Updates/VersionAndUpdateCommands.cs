using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Titanium.Cli;
using Titanium.Cli.Config;
using Titanium.Cli.Http3;
using Titanium.Cli.Service;
using Titanium.Web.Proxy.Abstractions.Updates;

namespace Titanium.Cli.Updates;

internal static class VersionCommand
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        if (CliHelp.RequestsHelp(args.AsSpan(1)))
        {
            return PrintHelp();
        }

        var check = args.Contains("--check", StringComparer.OrdinalIgnoreCase);
        var plus = args.Contains("--plus", StringComparer.OrdinalIgnoreCase);

        if (!TryResolveChannel(args, out var channel, out var channelError))
        {
            AsyncConsole.WriteError(channelError!);
            return 1;
        }

        var channelDisplay = FormatChannel(channel);
        PrintLocalVersions(plus);

        if (!check)
        {
            return 0;
        }

        var client = new UpdateFeedClient(channel);
        var (manifest, feedError) = await client.TryGetManifestWithErrorAsync();
        if (manifest is null)
        {
            AsyncConsole.WriteError(feedError ?? "Unable to query update feed.");
            return 1;
        }

        var local = typeof(Program).Assembly.GetName().Version ?? new Version(0, 0);
        var remoteText = ReleaseVersion.NormalizeTag(manifest.Version);
        var remote = ReleaseVersion.ParseComparable(remoteText);
        var localComparable = ReleaseVersion.ToComparable(local);
        var localDisplay = ReleaseVersion.FormatDisplay(local);

        AsyncConsole.WriteLine($"Remote Cli ({channelDisplay}): {remoteText}");
        AsyncConsole.WriteLine($"Local Cli: {localDisplay} → remote {remoteText} ({channelDisplay})");

        var exit = 0;
        var cmp = remote.CompareTo(localComparable);
        if (cmp > 0)
        {
            AsyncConsole.WriteLine(
                $"A newer Cli build is available ({localDisplay} → {remoteText}, {channelDisplay}). Run: titanium update --channel {channel}");
            exit = 2;
        }
        else if (cmp < 0)
        {
            AsyncConsole.WriteLine(
                $"Local Cli {localDisplay} is newer than {channelDisplay} {remoteText}.");
        }
        else
        {
            AsyncConsole.WriteLine($"Cli is up to date ({remoteText}, {channelDisplay}).");
        }

        if (plus)
        {
            exit = Math.Max(exit, PrintPlusCheck(manifest, channel, channelDisplay));
        }

        return exit;
    }

    internal static int PrintHelp()
    {
        AsyncConsole.WriteLine("""
            titanium version [--check] [--plus] [--channel stable|beta]

              (default)   Print local Cli / Core / Abstractions / Configuration versions.
              --check     Compare local Cli (and optionally Plus) to the update feed.
              --plus      Include Plus DLL version (with or without --check).
              --channel   stable (default) or beta. Also: TITANIUM_UPDATE_CHANNEL.

            Exit codes with --check: 0 up to date, 2 update available, 1 feed error.
            """);
        CliHelp.WriteDocsFooter();
        return 0;
    }

    private static int PrintPlusCheck(ReleaseManifest manifest, string channel, string channelDisplay)
    {
        var plusLocal = TryGetLocalPlusVersion();
        var plusRemoteText = ReleaseVersion.NormalizeTag(
            manifest.Products?.Plus?.Version ?? manifest.Version);
        var plusRemote = ReleaseVersion.ParseComparable(plusRemoteText);

        if (plusLocal is null)
        {
            AsyncConsole.WriteLine(
                $"Plus is not installed. Run: titanium update --plus --channel {channel}");
            return 2;
        }

        var plusLocalDisplay = ReleaseVersion.FormatDisplay(plusLocal);
        AsyncConsole.WriteLine($"Local Plus: {plusLocalDisplay} → remote {plusRemoteText} ({channelDisplay})");
        var cmp = plusRemote.CompareTo(ReleaseVersion.ToComparable(plusLocal));
        if (cmp > 0)
        {
            AsyncConsole.WriteLine(
                $"A newer Plus build is available ({plusLocalDisplay} → {plusRemoteText}, {channelDisplay}). Run: titanium update --plus --channel {channel}");
            return 2;
        }

        if (cmp < 0)
        {
            AsyncConsole.WriteLine(
                $"Local Plus {plusLocalDisplay} is newer than {channelDisplay} {plusRemoteText}.");
            return 0;
        }

        AsyncConsole.WriteLine($"Plus is up to date ({plusRemoteText}, {channelDisplay}).");
        return 0;
    }

    internal static Version? TryGetLocalPlusVersion()
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
            var ver = asm?.GetName().Version;
            AsyncConsole.WriteLine($"{name}: {(ver is null ? "(not loaded)" : ReleaseVersion.FormatDisplay(ver))}");
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
                AsyncConsole.WriteLine(
                    $"Plus: {ReleaseVersion.FormatDisplay(module.GetType().Assembly.GetName().Version)} (RequiredAbstractions={module.RequiredAbstractionsVersion})");
            }
            else
            {
                AsyncConsole.WriteLine("Plus: not present");
            }
        }
    }

    /// <summary>Parse --channel; returns false when the value is not stable/beta.</summary>
    internal static bool TryResolveChannel(string[] args, out string channel, out string? error)
    {
        channel = "stable";
        error = null;
        string? raw = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--channel" && i + 1 < args.Length)
            {
                raw = args[i + 1].Trim().ToLowerInvariant();
                break;
            }
        }

        raw ??= (Environment.GetEnvironmentVariable("TITANIUM_UPDATE_CHANNEL") ?? "stable").Trim().ToLowerInvariant();
        if (raw is not ("stable" or "beta"))
        {
            error = $"Unknown channel '{raw}'. Use --channel stable or --channel beta.";
            return false;
        }

        channel = raw;
        return true;
    }

    internal static string ParseChannel(string[] args)
    {
        if (TryResolveChannel(args, out var channel, out _))
        {
            return channel;
        }

        // Legacy tests / callers: invalid values previously fell through as stable via FormatChannel.
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

    internal static string StripPrerelease(string? version) => ReleaseVersion.StripPrerelease(version);

    /// <summary>Whether CLI should install the remote release (upgrade or same-semver channel/tag switch).</summary>
    internal static bool ShouldInstallCliRelease(
        Version local,
        string remoteText,
        string channelDisplay,
        string? installedReleaseTag,
        string? installedReleaseChannel)
    {
        remoteText = ReleaseVersion.NormalizeTag(remoteText);
        var remoteSemver = ReleaseVersion.ParseComparable(remoteText);
        var localSemver = ReleaseVersion.ToComparable(local);

        if (remoteSemver > localSemver)
        {
            return true;
        }

        if (remoteSemver < localSemver)
        {
            return false;
        }

        // Same semver: install when switching to beta tag or channel identity differs.
        var isBeta = channelDisplay.Equals("beta", StringComparison.OrdinalIgnoreCase);
        if (isBeta && remoteText.Contains('-', StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(installedReleaseTag)
                || !installedReleaseTag.Equals(remoteText, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(installedReleaseChannel)
                || !installedReleaseChannel.Equals(channelDisplay, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

internal static class UpdateCommand
{
    private static readonly string CliIdentityPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TitaniumCli",
        "installed-release.json");

    public static async Task<int> ExecuteAsync(string[] args)
    {
        if (CliHelp.RequestsHelp(args.AsSpan(1)))
        {
            return PrintHelp();
        }

        var plus = args.Contains("--plus", StringComparer.OrdinalIgnoreCase);
        if (!VersionCommand.TryResolveChannel(args, out var channel, out var channelError))
        {
            AsyncConsole.WriteError(channelError!);
            return 1;
        }

        var channelDisplay = VersionCommand.FormatChannel(channel);

        if (await ServiceCommand.IsDefaultServiceRunningAsync().ConfigureAwait(false))
        {
            AsyncConsole.WriteError(
                "Warning: the Titanium OS service appears to be running. Stop it before updating " +
                "(`titanium service stop`), then run update again, then `titanium service start`.");
        }

        AsyncConsole.WriteLine(plus
            ? $"Checking Plus updates ({channelDisplay})…"
            : $"Checking for updates ({channelDisplay})…");

        var client = new UpdateFeedClient(channel);
        var (manifest, feedError) = await client.TryGetManifestWithErrorAsync();
        if (manifest is null)
        {
            AsyncConsole.WriteError(feedError ?? "Unable to query update feed.");
            return 1;
        }

        if (plus)
        {
            return await UpdatePlusAsync(manifest, channelDisplay);
        }

        return await UpdateCliAsync(manifest, channelDisplay);
    }

    internal static int PrintHelp()
    {
        AsyncConsole.WriteLine("""
            titanium update [--plus] [--channel stable|beta]

              (default)   Download and install a newer CLI zip when the feed is ahead.
              --plus      Update Titanium.Plus.dll beside the CLI instead of the CLI zip.
              --channel   stable (default) or beta. Also: TITANIUM_UPDATE_CHANNEL.

            Does not use winget. If an OS service is running, stop it first so the exe can be replaced.
            """);
        CliHelp.WriteDocsFooter();
        return 0;
    }

    private static async Task<int> UpdateCliAsync(ReleaseManifest manifest, string channelDisplay)
    {
        var local = typeof(Program).Assembly.GetName().Version ?? new Version(0, 0);
        var remoteText = ReleaseVersion.NormalizeTag(manifest.Version);
        var (installedTag, installedChannel) = ReadCliIdentity();
        var localDisplay = ReleaseVersion.FormatDisplay(local);

        if (!VersionCommand.ShouldInstallCliRelease(
                local, remoteText, channelDisplay, installedTag, installedChannel))
        {
            var remote = ReleaseVersion.ParseComparable(remoteText);
            var localComparable = ReleaseVersion.ToComparable(local);
            if (remote < localComparable)
            {
                AsyncConsole.WriteLine(
                    $"Local Cli {localDisplay} is newer than {channelDisplay} {remoteText}. No changes.");
            }
            else
            {
                AsyncConsole.WriteLine($"Titanium CLI is up to date ({remoteText}, {channelDisplay}).");
                WriteCliIdentity(remoteText, channelDisplay);
            }

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

        var remoteCmp = ReleaseVersion.ParseComparable(remoteText);
        var localCmp = ReleaseVersion.ToComparable(local);
        var action = remoteCmp > localCmp
            ? $"Update {localDisplay} → {remoteText} ({channelDisplay})"
            : $"Switching to {remoteText} ({channelDisplay})";
        AsyncConsole.WriteLine($"{action}. Installing…");
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

        WriteCliIdentity(remoteText, channelDisplay);
        AsyncConsole.WriteLine(
            $"Installing {remoteText} ({channelDisplay}) in the background. When finished, run: titanium version");
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

        var remoteLabel = ReleaseVersion.NormalizeTag(
            manifest.Products?.Plus?.Version ?? manifest.Version ?? "unknown");
        var remoteSemver = ReleaseVersion.ParseComparable(remoteLabel);
        var dest = Path.Combine(AppContext.BaseDirectory, "Titanium.Plus.dll");
        var backup = dest + ".bak";
        var plusLocal = VersionCommand.TryGetLocalPlusVersion();
        var installing = plusLocal is null;

        if (plusLocal is not null)
        {
            var localComparable = ReleaseVersion.ToComparable(plusLocal);
            if (remoteSemver == localComparable
                || (!string.IsNullOrEmpty(asset.Sha256) && File.Exists(dest) && FileSha256Matches(dest, asset.Sha256)))
            {
                AsyncConsole.WriteLine(
                    $"Plus is already at {remoteLabel} ({channelDisplay}).");
                return 0;
            }
        }

        AsyncConsole.WriteLine(installing
            ? $"Installing Titanium.Plus {remoteLabel} ({channelDisplay})…"
            : $"Updating Plus {ReleaseVersion.FormatDisplay(plusLocal)} → {remoteLabel} ({channelDisplay})…");
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
            try
            {
                File.Move(staging, dest, overwrite: true);
            }
            catch
            {
                TryRestorePlusBackup(dest, backup);
                throw;
            }

            AsyncConsole.WriteLine(installing
                ? $"Installed Titanium.Plus.dll {remoteLabel} ({channelDisplay})."
                : $"Updated Titanium.Plus.dll to {remoteLabel} ({channelDisplay}).");
            return 0;
        }
        catch (Exception ex)
        {
            TryRestorePlusBackup(dest, backup);
            AsyncConsole.WriteError($"Plus update failed: {ex.Message}");
            return 1;
        }
    }

    private static bool FileSha256Matches(string path, string expectedHex)
    {
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            return hash.Equals(expectedHex, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryRestorePlusBackup(string dest, string backup)
    {
        try
        {
            if (File.Exists(backup))
            {
                File.Copy(backup, dest, overwrite: true);
            }
        }
        catch
        {
            // Best-effort restore.
        }
    }

    private static (string? Tag, string? Channel) ReadCliIdentity()
    {
        try
        {
            if (!File.Exists(CliIdentityPath))
            {
                return (null, null);
            }

            var json = File.ReadAllText(CliIdentityPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag", out var t) ? t.GetString() : null;
            var channel = root.TryGetProperty("channel", out var c) ? c.GetString() : null;
            return (tag, channel);
        }
        catch
        {
            return (null, null);
        }
    }

    private static void WriteCliIdentity(string tag, string channel)
    {
        try
        {
            var dir = Path.GetDirectoryName(CliIdentityPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(
                CliIdentityPath,
                JsonSerializer.Serialize(new { tag, channel }));
        }
        catch
        {
            // Non-fatal.
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
                // Absolute path: Sonar S4036 (PATH lookup for powershell.exe is a vulnerability).
                FileName = ResolveWindowsPowerShellPath(),
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

    /// <summary>Absolute Windows PowerShell path — avoids PATH-based Process.Start (Sonar S4036).</summary>
    private static string ResolveWindowsPowerShellPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

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
        var (manifest, _) = await TryGetManifestWithErrorAsync();
        return manifest;
    }

    public async Task<(ReleaseManifest? Manifest, string? Error)> TryGetManifestWithErrorAsync()
    {
        var feed = Environment.GetEnvironmentVariable("TITANIUM_UPDATE_FEED");
        if (feed == string.Empty)
        {
            return (null, "Update feed disabled (TITANIUM_UPDATE_FEED is empty).");
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Titanium.Cli/7.0");

            if (!string.IsNullOrEmpty(feed))
            {
                var json = await http.GetStringAsync(feed);
                var fromFeed = JsonSerializer.Deserialize<ReleaseManifest>(json, ManifestJson);
                return fromFeed is null
                    ? (null, "Update feed returned invalid JSON.")
                    : (fromFeed, null);
            }

            var api = _channel.Equals("beta", StringComparison.OrdinalIgnoreCase)
                ? "https://api.github.com/repos/justcoding121/titanium-web-proxy/releases"
                : "https://api.github.com/repos/justcoding121/titanium-web-proxy/releases/latest";

            using var response = await http.GetAsync(api);
            if (!response.IsSuccessStatusCode)
            {
                return (null, $"Update feed HTTP {(int)response.StatusCode} from GitHub Releases.");
            }

            var payload = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(payload);
            if (!TrySelectRelease(doc.RootElement, out var release))
            {
                return (null, _channel.Equals("beta", StringComparison.OrdinalIgnoreCase)
                    ? "No beta release found."
                    : "No stable release found.");
            }

            var version = release.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "0.0.0";
            var fromAsset = await TryLoadManifestAssetAsync(http, release, version);
            return (fromAsset ?? new ReleaseManifest { Version = version, Channel = _channel }, null);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Update feed network error: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return (null, "Update feed timed out.");
        }
        catch (JsonException ex)
        {
            return (null, $"Update feed JSON error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (null, $"Unable to query update feed: {ex.Message}");
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
