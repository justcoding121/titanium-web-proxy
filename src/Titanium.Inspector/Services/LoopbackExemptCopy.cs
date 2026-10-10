using Titanium.Inspector.Localization;

namespace Titanium.Inspector.Services;

/// <summary>Whether Inspector can receive Store-app traffic right now.</summary>
public enum LoopbackCaptureReadiness
{
    Ready,
    ProxyStopped,
    SystemProxyOff,
}

/// <summary>Result returned when the Allow Store apps dialog closes.</summary>
public sealed class LoopbackExemptResult
{
    public bool Changed { get; init; }

    public string StatusText { get; init; } = LoopbackExemptCopy.ClosedUnchanged;
}

/// <summary>
/// Proxy state and the one-shot reconnect used by Allow Store apps.
/// Checkbox edits do not call <see cref="RefreshRunningAppsAsync"/>.
/// </summary>
public sealed class LoopbackExemptSession
{
    public Func<LoopbackCaptureReadiness>? Readiness { get; init; }

    /// <summary>
    /// Turns the system proxy off and back on so running apps open a new connection.
    /// Returns true only when the proxy is enabled again afterward.
    /// </summary>
    public Func<Task<bool>>? RefreshRunningAppsAsync { get; init; }

    public LoopbackCaptureReadiness CurrentReadiness() =>
        Readiness?.Invoke() ?? LoopbackCaptureReadiness.ProxyStopped;
}

/// <summary>User-facing copy for Allow Store apps. Shared by the dialog and tests.</summary>
public static class LoopbackExemptCopy
{
    public static string Intro => LanguageService.Get("loopback.intro");

    public static string ClosedUnchanged => LanguageService.Get("loopback.closedUnchanged");

    public static string ProxyStoppedWarning => LanguageService.Get("loopback.proxyStopped");

    public static string SystemProxyOffWarning => LanguageService.Get("loopback.systemProxyOff");

    public static string RefreshingStatus => LanguageService.Get("loopback.refreshing");

    public static string? ProxyWarning(LoopbackCaptureReadiness readiness) => readiness switch
    {
        LoopbackCaptureReadiness.ProxyStopped => ProxyStoppedWarning,
        LoopbackCaptureReadiness.SystemProxyOff => SystemProxyOffWarning,
        _ => null,
    };

    /// <summary>
    /// Refresh only after a real allow-list change, and only while System proxy is already on.
    /// Checkbox edits are not a change until Apply or Clear.
    /// </summary>
    public static bool ShouldRefreshRunningApps(bool setChanged, LoopbackCaptureReadiness readiness) =>
        setChanged && readiness == LoopbackCaptureReadiness.Ready;

    public static bool SameSet(IEnumerable<string> left, IEnumerable<string> right)
    {
        var a = ToSet(left);
        var b = ToSet(right);
        return a.SetEquals(b);
    }

    public static string UnchangedStatus(int count) =>
        LanguageService.Format("loopback.unchangedCount", count);

    public static string AppliedStatus(int count, LoopbackCaptureReadiness readiness, bool refreshed)
    {
        var allowed = LanguageService.Format("loopback.allowed", count);
        if (refreshed)
        {
            return allowed + LanguageService.Get("loopback.allowedRefreshed");
        }

        return readiness switch
        {
            LoopbackCaptureReadiness.ProxyStopped =>
                allowed + LanguageService.Get("loopback.allowedProxyStopped"),
            LoopbackCaptureReadiness.SystemProxyOff =>
                allowed + LanguageService.Get("loopback.allowedSystemProxyOff"),
            _ => allowed + LanguageService.Get("loopback.allowedReady"),
        };
    }

    public static string ClearedStatus(LoopbackCaptureReadiness readiness, bool refreshed)
    {
        if (refreshed)
        {
            return LanguageService.Get("loopback.clearedRefreshed");
        }

        return readiness == LoopbackCaptureReadiness.Ready
            ? LanguageService.Get("loopback.clearedReady")
            : LanguageService.Get("loopback.cleared");
    }

    private static HashSet<string> ToSet(IEnumerable<string> values) =>
        new(values.Where(static s => !string.IsNullOrWhiteSpace(s)), StringComparer.OrdinalIgnoreCase);
}
