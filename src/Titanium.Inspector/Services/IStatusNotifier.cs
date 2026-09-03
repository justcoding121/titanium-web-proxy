namespace Titanium.Inspector.Services;

/// <summary>Optional toast/notification surface for important status outcomes.</summary>
public interface IStatusNotifier
{
    void Show(string message, StatusSeverity severity);
}

/// <summary>No-op notifier for unit / headless tests.</summary>
public sealed class NullStatusNotifier : IStatusNotifier
{
    public static NullStatusNotifier Instance { get; } = new();

    public void Show(string message, StatusSeverity severity)
    {
    }
}

/// <summary>Records toast calls for tests.</summary>
public sealed class RecordingStatusNotifier : IStatusNotifier
{
    public List<(string Message, StatusSeverity Severity)> Calls { get; } = new();

    public void Show(string message, StatusSeverity severity) =>
        Calls.Add((message, severity));
}
