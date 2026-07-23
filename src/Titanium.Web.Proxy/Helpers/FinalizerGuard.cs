using System.Diagnostics;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Several disposable types in the proxy (connections, session event args, streams) have a finalizer
///     whose only purpose is to catch a missing <c>Dispose()</c> call during development - if the finalizer
///     ever runs, something failed to dispose the object deterministically. That used to unconditionally call
///     <see cref="Debugger.Break" />, which is fine for a quiet unit test but makes the proxy unusable for
///     interactive testing while attached to a debugger (e.g. browsing real sites through the example app):
///     a single dropped/aborted connection - entirely expected under real-world load, browsers routinely race
///     and abandon connections - freezes every other in-flight connection too, since breaking pauses every
///     thread in the process.
/// </summary>
internal static class FinalizerGuard
{
    /// <summary>
    ///     Reports (via <see cref="Trace" />, so it shows up in the debugger's Output window without pausing
    ///     execution) that <paramref name="typeName" /> was finalized without its <c>Dispose()</c> ever having
    ///     run. DEBUG-only, same as the callers.
    /// </summary>
    public static void ReportUndisposedFinalizer(string typeName)
    {
        Trace.WriteLine(
            $"[Titanium.Web.Proxy] {typeName} was garbage-collected without Dispose() having been called first - this usually indicates a missing Dispose()/using somewhere.");
    }
}
