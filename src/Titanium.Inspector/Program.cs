using System;
using System.Runtime.InteropServices;
using Avalonia;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Inspector;

// Bundle id: com.justcoding121.titaniuminspector
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Framework-dependent macOS Debug: System.Net.Quic does not search BaseDirectory for
        // libmsquic; relaunch with DYLD_FALLBACK when natives were copied beside the apphost.
        Http3NativeBootstrap.EnsureAppLocalMsQuicVisible(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();

        // macOS: host menus/context menus as overlay popups instead of child NSWindows.
        // Without this, Menu and ContextMenu often fail to open when Inspector is launched from
        // a fullscreen host (Cursor/Rider/iTerm) — AvaloniaUI/Avalonia#15178 / #17264.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            builder = builder.With(new AvaloniaNativePlatformOptions
            {
                OverlayPopups = true,
            });
        }

#if DEBUG
        // Avalonia Trace is typically sync; keep it Debug-only so published builds never
        // push framework noise through a blocking Trace listener.
        builder = builder.LogToTrace();
#endif
        return builder;
    }
}
