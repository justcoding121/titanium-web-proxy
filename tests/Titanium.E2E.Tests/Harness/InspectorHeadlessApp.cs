using Avalonia;
using Avalonia.Headless;

namespace Titanium.E2E.Tests.Harness;

/// <summary>
/// Headless AppBuilders — never UsePlatformDetect / classic desktop (hangs CI).
/// One Skia-backed builder for interaction and CaptureRenderedFrame so
/// IFontManagerImpl is not dropped across session dispose/recreate.
/// </summary>
public static class InspectorHeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Titanium.Inspector.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
            })
            .WithInterFont()
            .LogToTrace();
}

/// <summary>Bootstrap type for <see cref="HeadlessUnitTestSession.StartNew"/>.</summary>
public static class InspectorHeadlessBootstrap
{
    public static AppBuilder BuildAvaloniaApp() => InspectorHeadlessApp.BuildAvaloniaApp();
}
