using Avalonia.Controls;
using Avalonia.Controls.Notifications;

namespace Titanium.Inspector.Services;

/// <summary>Shows short in-window toasts via Avalonia <see cref="WindowNotificationManager"/>.</summary>
public sealed class AvaloniaStatusNotifier : IStatusNotifier
{
    private readonly Func<WindowNotificationManager?> _manager;

    public AvaloniaStatusNotifier(Func<WindowNotificationManager?> manager) =>
        _manager = manager;

    public void Show(string message, StatusSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var manager = _manager();
        if (manager is null || !CanShowWindowToast(manager))
        {
            return;
        }

        var type = severity switch
        {
            StatusSeverity.Success => NotificationType.Success,
            StatusSeverity.Warning => NotificationType.Warning,
            StatusSeverity.Error => NotificationType.Error,
            StatusSeverity.Busy => NotificationType.Information,
            _ => NotificationType.Information,
        };

        var title = severity switch
        {
            StatusSeverity.Success => "Done",
            StatusSeverity.Warning => "Notice",
            StatusSeverity.Error => "Error",
            _ => "Inspector",
        };

        // Errors/warnings stay long enough to read (SmartScreen, msiexec, update failures).
        // Avalonia's notification chrome still lets the user dismiss early.
        var duration = severity is StatusSeverity.Error or StatusSeverity.Warning
            ? TimeSpan.FromSeconds(15)
            : TimeSpan.FromSeconds(4);

        manager.Show(new Notification(title, message, type, duration));
    }

    /// <summary>
    /// <see cref="WindowNotificationManager.Show"/> is async void: it posts the card, then
    /// closes it after <c>Task.Delay</c>. Headless drains that post in
    /// <c>ResetForUnitTests</c> before the animation clock exists, and the delay
    /// continuation runs after the dispatch clears the sync context, so <c>Close</c>
    /// hits the thread pool and crashes the test host. The status bar still updates.
    /// </summary>
    private static bool CanShowWindowToast(WindowNotificationManager manager)
    {
        var top = TopLevel.GetTopLevel(manager);
        var platform = (top as Window)?.PlatformImpl ?? top?.PlatformImpl;
        var name = platform?.GetType().FullName;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        return !name.Contains("Avalonia.Headless", StringComparison.Ordinal);
    }
}
