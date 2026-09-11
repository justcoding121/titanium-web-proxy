using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Titanium.Web.Proxy.Abstractions.Updates;

namespace Titanium.Inspector.Services;

public enum UpdateApplyKind
{
    Msi,
    Zip,
}

/// <summary>How an offered channel install should be described to the user.</summary>
public enum UpdateOfferKind
{
    None,
    Upgrade,
    ChannelSwitch,
    Downgrade,
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
    /// <summary>True when remote semver is lower than the running build (channel switch / downgrade).</summary>
    public bool IsDowngrade { get; init; }
    public UpdateOfferKind OfferKind { get; init; }
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

            var remoteText = NormalizeReleaseTag(manifest.Version);
            var remote = ReleaseVersion.ParseComparable(remoteText);
            var localComparable = ReleaseVersion.ToComparable(local);
            var localInfo = AssemblyInformationalVersion();

            var installedTag = _settings.Current.InstalledReleaseTag;
            var installedChannel = _settings.Current.InstalledReleaseChannel;
            if (!ShouldOfferChannelInstall(
                    local, remoteText, channelDisplay, installedTag, installedChannel, localInfo))
            {
                var localLabel = ReleaseVersion.ResolveLocalReleaseLabel(local, localInfo, installedTag);
                var message = FormatNoOfferMessage(localLabel, remoteText, channelDisplay);
                if (ShouldSeedInstalledIdentity(localInfo, installedTag, remoteText))
                {
                    SeedInstalledIdentity(remoteText, channelDisplay);
                }

                return new UpdateCheckResult
                {
                    RemoteVersion = remoteText,
                    ChannelDisplay = channelDisplay,
                    Message = message,
                };
            }

            var offerKind = ClassifyOfferKind(
                local, remoteText, channelDisplay, installedTag, installedChannel, localInfo);
            var (kind, asset) = ResolveAsset(manifest);
            if (asset?.Url is null)
            {
                return new UpdateCheckResult
                {
                    UpdateAvailable = true,
                    RemoteVersion = remoteText,
                    ChannelDisplay = channelDisplay,
                    IsDowngrade = offerKind == UpdateOfferKind.Downgrade,
                    OfferKind = offerKind,
                    Message =
                        $"Install {remoteText} ({channelDisplay}) is available, but no package was found for this install.",
                };
            }

            // Same ProductVersion is a MajorUpgrade (AllowSameVersionUpgrades). Older is still blocked.
            if (kind == UpdateApplyKind.Msi && MsiOfferIsDowngrade(localComparable, remote))
            {
                return new UpdateCheckResult
                {
                    RemoteVersion = remoteText,
                    ChannelDisplay = channelDisplay,
                    OfferKind = UpdateOfferKind.None,
                    Message =
                        $"Windows Installer cannot replace this install with {remoteText} ({channelDisplay}) " +
                        "(older version). Uninstall Titanium Inspector first, or download from the website.",
                };
            }

            var offerMessage = offerKind switch
            {
                UpdateOfferKind.Upgrade => $"Update available: {remoteText} ({channelDisplay})",
                UpdateOfferKind.Downgrade =>
                    $"Install older {channelDisplay} {remoteText} (replaces your current build)",
                _ => $"Switch to {channelDisplay} {remoteText} (replaces your current build)",
            };

            return new UpdateCheckResult
            {
                UpdateAvailable = true,
                RemoteVersion = remoteText,
                ChannelDisplay = channelDisplay,
                AssetUrl = asset.Url,
                AssetSha256 = asset.Sha256,
                ApplyKind = kind,
                IsDowngrade = offerKind == UpdateOfferKind.Downgrade,
                OfferKind = offerKind,
                Message = offerMessage,
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

    /// <summary>
    /// Whether the selected channel's latest release should be offered — upgrades and intentional
    /// channel/build switches (not phantom same-version reinstalls from 3-part vs 4-part Version).
    /// Same-core prerelease is never newer than a release (Stable 7.0.5 is not offered 7.0.5-beta).
    /// </summary>
    /// <param name="localInformationalVersion">
    /// Optional assembly informational version (e.g. <c>7.0.5-beta</c>). When it matches
    /// <paramref name="remoteText"/>, the install is treated as already up to date.
    /// </param>
    public static bool ShouldOfferChannelInstall(
        Version local,
        string remoteText,
        string channelDisplay,
        string? installedReleaseTag,
        string? installedReleaseChannel,
        string? localInformationalVersion = null)
    {
        remoteText = NormalizeReleaseTag(remoteText);
        var remoteSemver = ReleaseVersion.ParseComparable(remoteText);
        var localSemver = ReleaseVersion.ToComparable(local);
        var localLabel = ReleaseVersion.ResolveLocalReleaseLabel(
            local, localInformationalVersion, installedReleaseTag);

        if (!string.IsNullOrEmpty(localInformationalVersion)
            && localLabel.Equals(remoteText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var tagMatches = !string.IsNullOrEmpty(installedReleaseTag)
            && installedReleaseTag.Equals(remoteText, StringComparison.OrdinalIgnoreCase);
        var channelMatches = !string.IsNullOrEmpty(installedReleaseChannel)
            && installedReleaseChannel.Equals(channelDisplay, StringComparison.OrdinalIgnoreCase);

        // Exact channel build already installed and assembly matches remote semver.
        if (tagMatches && channelMatches && remoteSemver == localSemver)
        {
            return false;
        }

        // Persisted tag matches remote but assembly does not (e.g. failed MSI/UAC) — re-offer.
        if (tagMatches && channelMatches && remoteSemver != localSemver)
        {
            return true;
        }

        // SemVer-ish: remote must be newer than the known local label (release > same-core beta).
        if (ReleaseVersion.IsRemoteNewer(localLabel, remoteText))
        {
            return true;
        }

        // Same core / older remote: only intentional channel switch from a known other channel
        // when remote is not a same-or-older prerelease relative to a release local.
        if (remoteSemver == localSemver)
        {
            return ShouldOfferSameSemverSwitch(
                channelDisplay,
                installedReleaseChannel,
                localLabel,
                remoteText,
                tagMatches);
        }

        // remote core < local: only intentional channel / known-origin switches.
        if (!string.IsNullOrEmpty(installedReleaseChannel)
            && !installedReleaseChannel.Equals(channelDisplay, StringComparison.OrdinalIgnoreCase)
            && !ReleaseVersion.IsPrereleaseTag(remoteText))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(installedReleaseChannel)
            && !installedReleaseChannel.Equals(channelDisplay, StringComparison.OrdinalIgnoreCase)
            && ReleaseVersion.IsPrereleaseTag(localLabel))
        {
            // Known beta install switching channels to an older remote (intentional downgrade).
            return true;
        }

        return false;
    }

    /// <summary>
    /// MSI MajorUpgrade replaces the same ProductVersion (last install wins). A lower
    /// ProductVersion is still a WiX downgrade and cannot apply in-place.
    /// </summary>
    public static bool MsiOfferIsDowngrade(Version localComparable, Version remoteComparable) =>
        remoteComparable < localComparable;

    private static bool ShouldOfferSameSemverSwitch(
        string channelDisplay,
        string? installedReleaseChannel,
        string localLabel,
        string remoteText,
        bool tagMatches)
    {
        if (tagMatches)
        {
            return false;
        }

        // Never offer same-core prerelease over a release-looking local (Stable 7.0.5 ↛ 7.0.5-beta).
        if (ReleaseVersion.IsPrereleaseTag(remoteText) && !ReleaseVersion.IsPrereleaseTag(localLabel))
        {
            return false;
        }

        // Beta → Stable at same core: Stable supersedes prerelease.
        if (!ReleaseVersion.IsPrereleaseTag(remoteText) && ReleaseVersion.IsPrereleaseTag(localLabel))
        {
            return true;
        }

        // Known channel identity differs (e.g. intentional Stable↔Beta when both are releases — rare).
        if (!string.IsNullOrEmpty(installedReleaseChannel)
            && !installedReleaseChannel.Equals(channelDisplay, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>Classify an offered install for dialog copy.</summary>
    public static UpdateOfferKind ClassifyOfferKind(
        Version local,
        string remoteText,
        string channelDisplay,
        string? installedReleaseTag,
        string? installedReleaseChannel,
        string? localInformationalVersion = null)
    {
        if (!ShouldOfferChannelInstall(
                local, remoteText, channelDisplay, installedReleaseTag, installedReleaseChannel,
                localInformationalVersion))
        {
            return UpdateOfferKind.None;
        }

        var remoteSemver = ReleaseVersion.ParseComparable(remoteText);
        var localSemver = ReleaseVersion.ToComparable(local);
        if (remoteSemver > localSemver)
        {
            return UpdateOfferKind.Upgrade;
        }

        if (remoteSemver < localSemver)
        {
            return UpdateOfferKind.Downgrade;
        }

        // Same core: Stable over beta is a channel switch (or upgrade-ish promotion).
        return UpdateOfferKind.ChannelSwitch;
    }

    /// <summary>
    /// Status text when nothing is offered. Distinguishes true up-to-date from
    /// "latest Beta is not newer than your Stable".
    /// </summary>
    public static string FormatNoOfferMessage(string localLabel, string remoteText, string channelDisplay)
    {
        remoteText = NormalizeReleaseTag(remoteText);
        localLabel = NormalizeReleaseTag(localLabel);
        var isBetaChannel = channelDisplay.Equals("Beta", StringComparison.OrdinalIgnoreCase);
        if (isBetaChannel
            && ReleaseVersion.IsPrereleaseTag(remoteText)
            && !ReleaseVersion.IsPrereleaseTag(localLabel)
            && ReleaseVersion.ParseComparable(localLabel) == ReleaseVersion.ParseComparable(remoteText))
        {
            return
                $"No newer Beta than your current build ({localLabel}). Latest Beta is {remoteText}.";
        }

        return $"Titanium Inspector is up to date ({channelDisplay}).";
    }

    /// <summary>
    /// Only seed identity when the remote tag is the build we actually have — never mark a
    /// Stable install as <c>7.0.5-beta</c> after a "no newer beta" check.
    /// </summary>
    public static bool ShouldSeedInstalledIdentity(
        string? localInformationalVersion,
        string? installedReleaseTag,
        string remoteText)
    {
        remoteText = NormalizeReleaseTag(remoteText);
        if (!string.IsNullOrEmpty(localInformationalVersion)
            && NormalizeReleaseTag(localInformationalVersion)
                .Equals(remoteText, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrEmpty(installedReleaseTag)
            && installedReleaseTag.Equals(remoteText, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeReleaseTag(string? tag) => ReleaseVersion.NormalizeTag(tag);

    public static string StripPrerelease(string tag) => ReleaseVersion.StripPrerelease(tag);

    private void SeedInstalledIdentity(string remoteText, string channelDisplay)
    {
        var changed = false;
        if (!string.Equals(_settings.Current.InstalledReleaseTag, remoteText, StringComparison.OrdinalIgnoreCase))
        {
            _settings.Current.InstalledReleaseTag = remoteText;
            changed = true;
        }

        if (!string.Equals(_settings.Current.InstalledReleaseChannel, channelDisplay, StringComparison.OrdinalIgnoreCase))
        {
            _settings.Current.InstalledReleaseChannel = channelDisplay;
            changed = true;
        }

        if (changed)
        {
            _settings.Save();
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
                Environment.ProcessId,
                check.ApplyKind,
                packagePath,
                installDir,
                relaunchPath,
                check.RemoteVersion ?? "",
                check.ChannelDisplay);

            // Persist after the helper starts so a failed spawn does not claim the build is installed.
            // If MSI UAC is cancelled later, tag may ahead of assembly — ShouldOffer re-offers when they differ.
            _settings.Current.InstalledReleaseTag = check.RemoteVersion;
            _settings.Current.InstalledReleaseChannel = check.ChannelDisplay;
            _settings.Save();

            return (true, $"Installing {check.RemoteVersion} ({check.ChannelDisplay})…");
        }
        catch (Exception ex)
        {
            return (false, $"Update failed: {ex.Message}");
        }
    }

    public static Version AssemblyVersion() =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    /// <summary>
    /// Assembly informational version without Source Link <c>+commit</c> metadata
    /// (e.g. <c>7.0.5-beta</c>). Null when the attribute is missing.
    /// </summary>
    public static string? AssemblyInformationalVersion() =>
        FormatInformationalVersion(
            Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion);

    /// <summary>User-facing version for About: informational when present, else Major.Minor.Build.</summary>
    public static string FormatAssemblyDisplayVersion()
    {
        var info = AssemblyInformationalVersion();
        if (!string.IsNullOrEmpty(info))
        {
            return info;
        }

        return ReleaseVersion.FormatDisplay(AssemblyVersion());
    }

    /// <summary>Strip Source Link metadata from an informational version string.</summary>
    public static string? FormatInformationalVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return null;
        }

        var trimmed = informationalVersion.Trim();
        var plus = trimmed.IndexOf('+');
        if (plus >= 0)
        {
            trimmed = trimmed[..plus];
        }

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

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

            if (HasInspectorInstallMarker(Registry.CurrentUser)
                || HasInspectorInstallMarker(Registry.LocalMachine))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasInspectorInstallMarker(RegistryKey hive)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = hive.OpenSubKey(@"Software\justcoding121\TitaniumInspector");
            return key?.GetValue("installed") is not null;
        }
        catch
        {
            return false;
        }
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

    public static (UpdateApplyKind Kind, ManifestAsset? Asset) ResolveAsset(InspectorReleaseManifest manifest)
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

    private static async Task<InspectorReleaseManifest?> TryGetManifestAsync(
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
