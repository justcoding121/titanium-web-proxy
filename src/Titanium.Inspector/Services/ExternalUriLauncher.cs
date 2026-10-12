using Avalonia.Controls;

namespace Titanium.Inspector.Services;

/// <summary>Opens an http(s) address in the user's browser. Tests supply their own implementation.</summary>
public interface IExternalUriLauncher
{
    Task LaunchAsync(Uri uri);
}

/// <summary>
/// Uses the window's Avalonia launcher, the same path as the About links.
/// Does nothing when the window is not available.
/// </summary>
public sealed class WindowUriLauncher(Func<TopLevel?> topLevel) : IExternalUriLauncher
{
    public async Task LaunchAsync(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var launcher = topLevel()?.Launcher;
        if (launcher is null)
            return;

        await launcher.LaunchUriAsync(uri);
    }
}
