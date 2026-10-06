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
    public const string Intro =
        "Windows Store / UWP apps are blocked from localhost by default. Check apps that should use Inspector, then Apply. " +
        "Restart each app (fully quit it) after Apply. Inspector must be running with System proxy on. " +
        "Clear all removes every allow entry.";

    public const string ClosedUnchanged = "Store apps unchanged.";

    public const string ProxyStoppedWarning =
        "Start the proxy and turn on System proxy. Store apps cannot be captured until both are on.";

    public const string SystemProxyOffWarning =
        "Turn on System proxy (Capture menu). The allow list only lets Store apps reach localhost; they still follow the system proxy.";

    public const string RefreshingStatus = "Refreshing system proxy so running apps reconnect…";

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
        count == 1
            ? "Allow list already matches this app."
            : $"Allow list already matches these {count} apps.";

    public static string AppliedStatus(int count, LoopbackCaptureReadiness readiness, bool refreshed)
    {
        var allowed = count == 1 ? "Allowed 1 app." : $"Allowed {count} apps.";
        if (refreshed)
        {
            return allowed +
                   " System proxy was refreshed so running apps reconnect. If traffic still does not appear, fully quit and reopen the app.";
        }

        return readiness switch
        {
            LoopbackCaptureReadiness.ProxyStopped =>
                allowed + " Start the proxy and turn on System proxy, then fully quit and reopen the app.",
            LoopbackCaptureReadiness.SystemProxyOff =>
                allowed + " Turn on System proxy, then fully quit and reopen the app.",
            _ => allowed + " Fully quit and reopen the app to capture traffic.",
        };
    }

    public static string ClearedStatus(LoopbackCaptureReadiness readiness, bool refreshed)
    {
        if (refreshed)
        {
            return "All loopback exemptions cleared. System proxy was refreshed so running apps reconnect.";
        }

        return readiness == LoopbackCaptureReadiness.Ready
            ? "All loopback exemptions cleared. Fully quit and reopen any app that was using Inspector."
            : "All loopback exemptions cleared.";
    }

    private static HashSet<string> ToSet(IEnumerable<string> values) =>
        new(values.Where(static s => !string.IsNullOrWhiteSpace(s)), StringComparer.OrdinalIgnoreCase);
}
