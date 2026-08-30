using Avalonia.Headless;

namespace Titanium.E2E.Tests.Harness;

/// <summary>
/// Shared Headless session for the process — recreating sessions between MSTest
/// methods races Dispatcher.ResetForUnitTests and drops IFontManagerImpl.
/// </summary>
internal static class InspectorHeadlessSessionHost
{
    private static readonly object Gate = new();
    private static HeadlessUnitTestSession? _session;

    public static HeadlessUnitTestSession Get()
    {
        lock (Gate)
        {
            Environment.SetEnvironmentVariable("TITANIUM_INSPECTOR_SKIP_AUTO_MAINWINDOW", "1");
            return _session ??= HeadlessUnitTestSession.StartNew(typeof(InspectorHeadlessBootstrap));
        }
    }
}
