using Avalonia;
using System;

namespace Titanium.Inspector;

// Bundle id: com.justcoding121.titaniuminspector
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();
#if DEBUG
        // Avalonia Trace is typically sync; keep it Debug-only so published builds never
        // push framework noise through a blocking Trace listener.
        builder = builder.LogToTrace();
#endif
        return builder;
    }
}
