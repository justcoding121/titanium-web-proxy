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

        manager.Show(new Notification(title, message, type, TimeSpan.FromSeconds(3.5)));
    }
}
