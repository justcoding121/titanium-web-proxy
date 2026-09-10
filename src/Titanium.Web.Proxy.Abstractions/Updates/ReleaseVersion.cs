namespace Titanium.Web.Proxy.Abstractions.Updates;

/// <summary>
/// Normalizes release tags and assembly <see cref="Version"/> values so 3-part feed tags
/// (e.g. <c>7.0.5</c>) compare equal to 4-part assembly versions (e.g. <c>7.0.5.0</c>).
/// Same-core ordering treats a release as newer than a prerelease (<c>7.0.5</c> &gt; <c>7.0.5-beta</c>).
/// </summary>
public static class ReleaseVersion
{
    /// <summary>Strip a leading <c>v</c> from a release tag.</summary>
    public static string NormalizeTag(string? tag) =>
        (tag ?? "0.0.0").TrimStart('v');

    /// <summary>Numeric core of a tag without prerelease suffix (<c>7.0.5-beta</c> → <c>7.0.5</c>).</summary>
    public static string StripPrerelease(string? tag)
    {
        var normalized = NormalizeTag(tag);
        var dash = normalized.IndexOf('-');
        return dash > 0 ? normalized[..dash] : normalized;
    }

    /// <summary>True when the tag has a prerelease suffix (e.g. <c>7.0.5-beta</c>).</summary>
    public static bool IsPrereleaseTag(string? tag)
    {
        var normalized = NormalizeTag(tag);
        var dash = normalized.IndexOf('-');
        return dash > 0 && dash < normalized.Length - 1;
    }

    /// <summary>
    /// Pad missing Build/Revision (-1) to 0 so <see cref="Version"/> comparisons are stable.
    /// </summary>
    public static Version ToComparable(Version? version)
    {
        if (version is null)
        {
            return new Version(0, 0, 0, 0);
        }

        var major = version.Major < 0 ? 0 : version.Major;
        var minor = version.Minor < 0 ? 0 : version.Minor;
        var build = version.Build < 0 ? 0 : version.Build;
        var revision = version.Revision < 0 ? 0 : version.Revision;
        return new Version(major, minor, build, revision);
    }

    /// <summary>Parse a release tag into a comparable 4-part version (prerelease stripped).</summary>
    public static Version ParseComparable(string? tag)
    {
        var core = StripPrerelease(tag);
        return Version.TryParse(core, out var parsed)
            ? ToComparable(parsed)
            : new Version(0, 0, 0, 0);
    }

    /// <summary>Display as Major.Minor.Build (hides assembly revision).</summary>
    public static string FormatDisplay(Version? version)
    {
        var comparable = ToComparable(version);
        return $"{comparable.Major}.{comparable.Minor}.{comparable.Build}";
    }

    /// <summary>Compare two versions after normalization. Negative if left is older.</summary>
    public static int Compare(Version? left, Version? right) =>
        ToComparable(left).CompareTo(ToComparable(right));

    /// <summary>
    /// Compare release tags with SemVer-ish channel awareness: numeric core first, then
    /// non-prerelease &gt; prerelease at the same core (<c>7.0.5</c> &gt; <c>7.0.5-beta</c>).
    /// Negative if <paramref name="left"/> is older than <paramref name="right"/>.
    /// </summary>
    public static int CompareReleaseTags(string? left, string? right)
    {
        var leftNorm = NormalizeTag(left);
        var rightNorm = NormalizeTag(right);
        if (leftNorm.Equals(rightNorm, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var coreCmp = ParseComparable(leftNorm).CompareTo(ParseComparable(rightNorm));
        if (coreCmp != 0)
        {
            return coreCmp;
        }

        var leftPre = IsPrereleaseTag(leftNorm);
        var rightPre = IsPrereleaseTag(rightNorm);
        if (leftPre == rightPre)
        {
            return string.Compare(leftNorm, rightNorm, StringComparison.OrdinalIgnoreCase);
        }

        // Release (no suffix) is newer than prerelease at the same core.
        return leftPre ? -1 : 1;
    }

    /// <summary>
    /// Best-known local release label for compares: informational version when present,
    /// else installed tag, else numeric assembly display (treated as a release).
    /// </summary>
    public static string ResolveLocalReleaseLabel(
        Version? assemblyVersion,
        string? informationalVersion,
        string? installedReleaseTag)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var info = informationalVersion.Trim();
            var plus = info.IndexOf('+');
            if (plus >= 0)
            {
                info = info[..plus];
            }

            if (!string.IsNullOrWhiteSpace(info))
            {
                return NormalizeTag(info);
            }
        }

        if (!string.IsNullOrWhiteSpace(installedReleaseTag))
        {
            return NormalizeTag(installedReleaseTag);
        }

        return FormatDisplay(assemblyVersion);
    }

    /// <summary>
    /// Whether a remote feed tag is newer than the local install label (SemVer-ish).
    /// </summary>
    public static bool IsRemoteNewer(string? localReleaseLabel, string? remoteTag) =>
        CompareReleaseTags(localReleaseLabel, remoteTag) < 0;
}
