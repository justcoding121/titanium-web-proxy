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
    Deb,
    Rpm,
    AppImage,
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

            // A lower ProductVersion cannot replace an MSI, same as a Mac disk image
            // that is not newer than the Applications copy.
            if (kind == UpdateApplyKind.Msi &&
                MsiOfferIsDowngrade(ReleaseVersion.ToComparable(local), ReleaseVersion.ParseComparable(remoteText)))
            {
                return new UpdateCheckResult
                {
                    RemoteVersion = remoteText,
                    ChannelDisplay = channelDisplay,
                    OfferKind = UpdateOfferKind.None,
                    Message =
                        $"Windows Installer cannot replace this install with {remoteText} ({channelDisplay}) " +
                        "(older version). The installed copy was left in place.",
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
    /// True when the remote ProductVersion is lower. The installer still replaces
    /// the current install after the user confirms; this only classifies the offer.
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

        // Same semver: changing Stable↔Beta is a channel switch, not an upgrade/downgrade.
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
    /// <summary>
    ///     When non-null, in-app zip/MSI apply must not run from this location
    ///     (DMG volume or AppImage mount). Caller should offer relocate first.
    /// </summary>
    public static string? RefuseApplyReason()
    {
        if (OperatingSystem.IsMacOS() && MacAppRelocation.IsRunningFromDmgVolume())
        {
            return "Move Titanium Inspector to Applications before installing updates. " +
                   "Updates cannot be applied while running from a disk image.";
        }

        if (OperatingSystem.IsLinux() && LinuxAppRelocation.IsRunningFromAppImage())
        {
            return "Install Titanium Inspector locally (~/.local) before installing updates. " +
                   "Updates cannot be applied while running from an AppImage.";
        }

        return null;
    }

    public async Task<(bool Ok, string Message)> DownloadAndStartApplyAsync(
        UpdateCheckResult check,
        CancellationToken cancellationToken = default)
    {
        if (!check.UpdateAvailable || string.IsNullOrEmpty(check.AssetUrl))
        {
            return (false, "No update package to install.");
        }

        var refuse = RefuseApplyReason();
        if (refuse is not null)
            return (false, refuse);

        var workDir = Path.Combine(Path.GetTempPath(), "TitaniumInspector-update");
        Directory.CreateDirectory(workDir);
        var fileName = PackageFileName(check.ApplyKind, check.RemoteVersion);
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

            var (installDir, relaunchPath) = ResolveApplyInstallPaths(check.ApplyKind);

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
            _settings.Current.PendingPostUpdateTrustCheck = true;
            _settings.Save();

            return (true, $"Installing {check.RemoteVersion} ({check.ChannelDisplay})…");
        }
        catch (Exception ex)
        {
            return (false, $"Update failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Install directory and relaunch path for the apply helper. Mac bundles relaunch
    ///     the .app; a local Linux share install relaunches that copy; an AppImage relaunches
    ///     the image path.
    /// </summary>
    private static (string InstallDir, string RelaunchPath) ResolveApplyInstallPaths(UpdateApplyKind applyKind)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var exeName = OperatingSystem.IsWindows() ? "TitaniumInspector.exe" : "TitaniumInspector";
        var relaunchPath = Path.Combine(installDir, exeName);
        if (OperatingSystem.IsMacOS() && TryFindMacAppBundle(installDir, out var appBundle))
        {
            // Zip payload is publish output → Contents/MacOS; relaunch the .app.
            relaunchPath = appBundle;
            installDir = Path.Combine(appBundle, "Contents", "MacOS");
        }
        else if (TryUseLinuxLocalShare(installDir, out var localShare, out var localExe))
        {
            installDir = localShare;
            relaunchPath = localExe;
        }

        if (applyKind == UpdateApplyKind.AppImage)
            (installDir, relaunchPath) = PreferAppImageRelaunch(installDir, relaunchPath);

        return (installDir, relaunchPath);
    }

    private static bool TryUseLinuxLocalShare(string installDir, out string localShare, out string localExe)
    {
        localShare = "";
        localExe = "";
        if (!OperatingSystem.IsLinux())
            return false;

        localShare = LinuxAppRelocation.DefaultInstallDir();
        if (!Directory.Exists(localShare) || !installDir.StartsWith(localShare, StringComparison.Ordinal))
            return false;

        localExe = Path.Combine(localShare, LinuxAppRelocation.ExeName);
        return true;
    }

    private static (string InstallDir, string RelaunchPath) PreferAppImageRelaunch(
        string installDir, string relaunchPath)
    {
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (string.IsNullOrWhiteSpace(appImage))
            return (installDir, relaunchPath);

        return (Path.GetDirectoryName(appImage) ?? installDir, appImage);
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

    /// <summary>
    /// Deb and rpm installs live under <c>/opt/titanium-inspector</c>. Copying a zip there
    /// fails without root, and App Center will not replace a package of the same version.
    /// </summary>
    public static UpdateApplyKind DetectLinuxPackageKind(string baseDirectory)
    {
        if (!OperatingSystem.IsLinux())
        {
            return UpdateApplyKind.Zip;
        }

        if (IsRunningAppImage())
        {
            return UpdateApplyKind.AppImage;
        }

        var full = Path.GetFullPath(baseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.Equals("/opt/titanium-inspector", StringComparison.Ordinal)
            && !full.StartsWith("/opt/titanium-inspector" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return UpdateApplyKind.Zip;
        }

        if (PackageQuerySucceeds("/usr/bin/dpkg-query", "-W", "titanium-inspector"))
        {
            return UpdateApplyKind.Deb;
        }

        if (PackageQuerySucceeds("/usr/bin/rpm", "-q", "titanium-inspector"))
        {
            return UpdateApplyKind.Rpm;
        }

        return File.Exists("/usr/bin/dpkg") ? UpdateApplyKind.Deb : UpdateApplyKind.Zip;
    }

    /// <summary>True when this process was started from an AppImage (<c>APPIMAGE</c> is set by the runtime).</summary>
    public static bool IsRunningAppImage()
    {
        var path = Environment.GetEnvironmentVariable("APPIMAGE");
        return !string.IsNullOrWhiteSpace(path);
    }

    internal static string PackageFileName(UpdateApplyKind kind, string? remoteVersion)
    {
        var version = string.IsNullOrWhiteSpace(remoteVersion) ? "update" : remoteVersion;
        var ext = kind switch
        {
            UpdateApplyKind.Msi => ".msi",
            UpdateApplyKind.Deb => ".deb",
            UpdateApplyKind.Rpm => ".rpm",
            UpdateApplyKind.AppImage => ".AppImage",
            _ => ".zip",
        };
        return "TitaniumInspector-" + version + ext;
    }

    private static bool PackageQuerySucceeds(string fileName, params string[] args)
    {
        if (!File.Exists(fileName))
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null || !process.WaitForExit(5000))
            {
                return false;
            }

            return process.ExitCode == 0;
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
        var linuxKind = DetectLinuxPackageKind(AppContext.BaseDirectory);
        if (linuxKind == UpdateApplyKind.Deb
            && assets.TryGetValue(rid + ".deb", out var deb)
            && !string.IsNullOrEmpty(deb.Url))
        {
            return (UpdateApplyKind.Deb, deb);
        }

        if (linuxKind == UpdateApplyKind.Rpm
            && assets.TryGetValue(rid + ".rpm", out var rpm)
            && !string.IsNullOrEmpty(rpm.Url))
        {
            return (UpdateApplyKind.Rpm, rpm);
        }

        if (linuxKind == UpdateApplyKind.AppImage
            && assets.TryGetValue(rid + ".AppImage", out var appImage)
            && !string.IsNullOrEmpty(appImage.Url))
        {
            return (UpdateApplyKind.AppImage, appImage);
        }

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
        File.WriteAllText(sh, BuildUnixScript(pid, packagePath, installDir, relaunchPath, version, channel, kind), new UTF8Encoding(false));
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
        string channel,
        UpdateApplyKind kind = UpdateApplyKind.Zip)
    {
        var plistVersion = StripPrereleaseSuffix(version);
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine($"pid={pid}");
        sb.AppendLine($"package={BashQuote(packagePath)}");
        sb.AppendLine($"install_dir={BashQuote(installDir)}");
        sb.AppendLine($"relaunch={BashQuote(relaunchPath)}");
        sb.AppendLine($"version={BashQuote(version)}");
        sb.AppendLine($"plist_version={BashQuote(plistVersion)}");
        sb.AppendLine("while kill -0 \"$pid\" 2>/dev/null; do sleep 0.4; done");
        sb.AppendLine("sleep 1");
        if (kind == UpdateApplyKind.AppImage)
        {
            sb.AppendLine("cp -f \"$package\" \"$relaunch\"");
            sb.AppendLine("chmod +x \"$relaunch\"");
            sb.AppendLine("nohup \"$relaunch\" >/dev/null 2>&1 &");
            sb.AppendLine($"echo 'Updated Inspector to {version} ({channel}).'");
            return sb.ToString();
        }

        if (kind is UpdateApplyKind.Deb or UpdateApplyKind.Rpm)
        {
            var tool = kind == UpdateApplyKind.Deb ? "dpkg" : "rpm";
            var toolArgs = kind == UpdateApplyKind.Deb ? "-i" : "-Uvh";
            sb.AppendLine("if sudo -n true >/dev/null 2>&1; then");
            sb.AppendLine($"  sudo -n {tool} {toolArgs} \"$package\"");
            sb.AppendLine("elif [[ -x /usr/bin/pkexec ]]; then");
            sb.AppendLine($"  pkexec {tool} {toolArgs} \"$package\"");
            sb.AppendLine("elif [[ -x /usr/bin/sudo ]]; then");
            sb.AppendLine($"  sudo {tool} {toolArgs} \"$package\"");
            sb.AppendLine("else");
            sb.AppendLine("  echo 'Need pkexec or sudo to install the update.' >&2");
            sb.AppendLine("  exit 1");
            sb.AppendLine("fi");
            sb.AppendLine("if [[ \"$relaunch\" == *.app ]]; then");
            sb.AppendLine("  open \"$relaunch\" || true");
            sb.AppendLine("elif [[ -x \"$relaunch\" ]]; then");
            sb.AppendLine("  (cd \"$install_dir\" && nohup \"$relaunch\" >/dev/null 2>&1 &)");
            sb.AppendLine("fi");
            sb.AppendLine($"echo 'Updated Inspector to '\"$version\"' ({channel}).'");
            return sb.ToString();
        }

        sb.AppendLine("if [[ ! -w \"$install_dir\" && \"$relaunch\" != *.app ]]; then");
        sb.AppendLine("  echo \"Cannot update $install_dir (not writable). Use the .deb or .rpm package.\" >&2");
        sb.AppendLine("  exit 1");
        sb.AppendLine("fi");
        sb.AppendLine("tmp=$(mktemp -d)");
        sb.AppendLine("unzip -qo \"$package\" -d \"$tmp\"");
        sb.AppendLine("mkdir -p \"$install_dir\"");
        // macOS .app: zip is publish output → replace Contents/MacOS, then open the bundle.
        sb.AppendLine("if [[ \"$relaunch\" == *.app ]]; then");
        sb.AppendLine("  macos_dir=\"$relaunch/Contents/MacOS\"");
        sb.AppendLine("  mkdir -p \"$macos_dir\"");
        sb.AppendLine("  if command -v ditto >/dev/null 2>&1; then");
        sb.AppendLine("    ditto \"$tmp\" \"$macos_dir\"");
        sb.AppendLine("  else");
        sb.AppendLine("    cp -a \"$tmp\"/. \"$macos_dir\"/");
        sb.AppendLine("  fi");
        sb.AppendLine("  chmod +x \"$macos_dir/TitaniumInspector\" 2>/dev/null || true");
        sb.AppendLine("  rm -rf \"$tmp\"");
        // Keep Finder / MacAppRelocation version in sync with the applied release
        // (zip only replaces Contents/MacOS; Info.plist would otherwise lag).
        sb.AppendLine("  plist=\"$relaunch/Contents/Info.plist\"");
        sb.AppendLine("  if [[ -f \"$plist\" ]] && [[ -x /usr/libexec/PlistBuddy ]]; then");
        sb.AppendLine("    /usr/libexec/PlistBuddy -c \"Set :CFBundleShortVersionString $plist_version\" \"$plist\" 2>/dev/null \\");
        sb.AppendLine("      || /usr/libexec/PlistBuddy -c \"Add :CFBundleShortVersionString string $plist_version\" \"$plist\"");
        sb.AppendLine("    /usr/libexec/PlistBuddy -c \"Set :CFBundleVersion $plist_version\" \"$plist\" 2>/dev/null \\");
        sb.AppendLine("      || /usr/libexec/PlistBuddy -c \"Add :CFBundleVersion string $plist_version\" \"$plist\"");
        sb.AppendLine("  fi");
        sb.AppendLine("  open \"$relaunch\" || true");
        sb.AppendLine("else");
        // Linux zip installs through 8.0.5 bundled libssl/libcrypto. Drop them before the
        // overlay so an in-place update picks up the host OpenSSL, but only when this
        // directory is a Titanium layout (libmsquic is present). The .app branch above
        // keeps macOS libssl.3.dylib / libcrypto.3.dylib.
        sb.AppendLine("  if compgen -G \"$install_dir\"/libmsquic.so* >/dev/null; then");
        sb.AppendLine("    rm -f \"$install_dir\"/libssl.so* \"$install_dir\"/libcrypto.so*");
        sb.AppendLine("  fi");
        sb.AppendLine("  cp -a \"$tmp\"/. \"$install_dir\"/");
        sb.AppendLine("  rm -rf \"$tmp\"");
        sb.AppendLine("  chmod +x \"$install_dir/TitaniumInspector\" 2>/dev/null || true");
        sb.AppendLine("  if [[ -x \"$relaunch\" ]]; then");
        sb.AppendLine("    (cd \"$install_dir\" && nohup \"$relaunch\" >/dev/null 2>&1 &)");
        sb.AppendLine("  fi");
        sb.AppendLine("fi");
        sb.AppendLine($"echo 'Updated Inspector to '\"$version\"' ({channel}).'");
        return sb.ToString();
    }

    /// <summary>
    ///     Same as release packaging (<c>RELEASE_VERSION%%-*</c>): drop prerelease so
    ///     <c>8.0.5-beta</c> becomes <c>8.0.5</c> for Info.plist.
    /// </summary>
    internal static string StripPrereleaseSuffix(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return version;
        var dash = version.IndexOf('-');
        return dash > 0 ? version[..dash] : version;
    }

    private static string EscapePs(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string BashQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
