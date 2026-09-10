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
        if (manager is null)
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

        // Errors/warnings stay long enough to read (SmartScreen, msiexec, update failures);
        // Avalonia's notification chrome still lets the user dismiss early.
        var duration = severity is StatusSeverity.Error or StatusSeverity.Warning
            ? TimeSpan.FromSeconds(15)
            : TimeSpan.FromSeconds(4);

        manager.Show(new Notification(title, message, type, duration));
    }
}
