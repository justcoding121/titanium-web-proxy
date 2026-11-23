using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Inspector;

// Bundle id: com.justcoding121.titaniuminspector
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Any(static a => string.Equals(a, "--clear-system-proxy", StringComparison.Ordinal)))
        {
            InstalledProxyCleanup.ForceClear();
            return;
        }

        // Framework-dependent macOS Debug: System.Net.Quic does not search BaseDirectory for
        // libmsquic; relaunch with DYLD_FALLBACK when natives were copied beside the apphost.
        // On Linux, leftover root-owned OpenSSL beside MsQuic must stop before MsQuic loads.
        var blockedOpenSsl = Http3NativeBootstrap.EnsureAppLocalMsQuicVisible(args);
        if (blockedOpenSsl.Count > 0)
        {
            ShowBlockedOpenSslAndExit(args, blockedOpenSsl);
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) when (IsXOpenDisplayFailure(ex))
        {
            // Avalonia 11.2 always initializes X11 on Linux. XOpenDisplay throws when DISPLAY
            // is missing or this process has no X authority (package scripts, SSH). Leaving the
            // exception unhandled aborts the process, which Ubuntu records as a crash report.
            Console.Error.WriteLine(
                "Titanium Inspector could not open the X display ({0}).",
                ex.Message);
            Console.Error.WriteLine(
                "Start it from the desktop session so it can use the existing display.");
            Environment.ExitCode = 1;
        }
    }

    private static void ShowBlockedOpenSslAndExit(string[] args, IReadOnlyList<string> blockedPaths)
    {
        var message = Http3NativeBootstrap.FormatBlockedOpenSslMessage(blockedPaths);
        Environment.SetEnvironmentVariable("TITANIUM_INSPECTOR_SKIP_AUTO_MAINWINDOW", "1");
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, () =>
            {
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                {
                    return;
                }

                var text = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(16),
                };
                var ok = new Button
                {
                    Content = "OK",
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(16),
                    MinWidth = 88,
                };
                var root = new DockPanel();
                DockPanel.SetDock(ok, Dock.Bottom);
                root.Children.Add(ok);
                root.Children.Add(text);
                var window = new Window
                {
                    Title = "Titanium Inspector",
                    Width = 560,
                    Height = 320,
                    CanResize = false,
                    Content = root,
                };
                ok.Click += (_, _) => window.Close();
                window.Closed += (_, _) => desktop.Shutdown(1);
                desktop.MainWindow = window;
            });
            Environment.ExitCode = 1;
        }
        catch (Exception ex) when (IsXOpenDisplayFailure(ex))
        {
            Console.Error.WriteLine(message);
            Environment.ExitCode = 1;
        }
    }

    internal static bool IsXOpenDisplayFailure(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current.Message.Contains("XOpenDisplay failed", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
