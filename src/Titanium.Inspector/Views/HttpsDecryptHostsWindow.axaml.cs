using Avalonia.Controls;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

/// <summary>Backward-compatible entry — use <see cref="ExcludedHostsWindow"/>.</summary>
public static class HttpsDecryptHostsWindow
{
    public static Task<bool> ShowAsync(Window owner, SettingsService settings, Action? onSaved) =>
        ExcludedHostsWindow.ShowAsync(owner, settings, readOnly: false, onSaved);
}
