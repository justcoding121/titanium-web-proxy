using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.ViewModels;

/// <summary>Filters and follow state for Recent logs. The ring is read on a timer, not per log line.</summary>
public sealed class RunLogViewModel : INotifyPropertyChanged
{
    public const string AllLevels = "All";

    private readonly RunLogRing _ring;
    private readonly Func<string> _minimumLevel;
    private long _seenVersion = -1;
    private string _filter = "";
    private string _level = AllLevels;
    private bool _following = true;
    private IReadOnlyList<RunLogEntry> _visible = [];
    private string _emptyText = "";

    public RunLogViewModel(RunLogRing ring, Func<string> minimumLevel)
    {
        _ring = ring;
        _minimumLevel = minimumLevel;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value))
            {
                Publish(_ring.Snapshot());
            }
        }
    }

    public string Level
    {
        get => _level;
        set
        {
            if (Set(ref _level, value))
            {
                Publish(_ring.Snapshot());
            }
        }
    }

    public bool Following
    {
        get => _following;
        set => Set(ref _following, value);
    }

    public IReadOnlyList<RunLogEntry> VisibleEntries
    {
        get => _visible;
        private set => Set(ref _visible, value);
    }

    /// <summary>Legacy alias used by older tests; same as <see cref="VisibleEntries"/>.</summary>
    public IReadOnlyList<RunLogEntry> VisibleLines => VisibleEntries;

    public string EmptyText
    {
        get => _emptyText;
        private set => Set(ref _emptyText, value);
    }

    public bool ShowEmpty => _emptyText.Length > 0;

    public void NoteScroll(bool atBottom)
    {
        Following = atBottom;
    }

    public void Refresh()
    {
        var version = _ring.Version;
        if (version == _seenVersion)
        {
            return;
        }

        _seenVersion = version;
        Publish(_ring.Snapshot());
    }

    public void Clear()
    {
        _ring.Clear();
        Refresh();
    }

    public string CopyText() =>
        string.Join(Environment.NewLine, _visible.Select(RunLogFormat.FormatLine));

    public static IReadOnlyList<RunLogEntry> FilterEntries(
        IReadOnlyList<RunLogEntry> entries,
        string? level,
        string? filter)
    {
        var selected = string.IsNullOrWhiteSpace(level) || level == AllLevels
            ? (LogLevel?)null
            : Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsed) ? parsed : null;
        var needle = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        if (selected is null && needle is null)
        {
            return entries;
        }

        var kept = new List<RunLogEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (selected is not null && entry.Level != selected.Value)
            {
                continue;
            }

            if (needle is not null)
            {
                var token = RunLogFormat.LevelToToken(entry.Level);
                if (token.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0
                    && entry.Category.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0
                    && entry.Message.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
            }

            kept.Add(entry);
        }

        return kept;
    }

    private void Publish(IReadOnlyList<RunLogEntry> entries)
    {
        VisibleEntries = FilterEntries(entries, _level, _filter);
        var minimum = _minimumLevel();
        EmptyText = entries.Count == 0 && string.Equals(minimum, "Error", StringComparison.OrdinalIgnoreCase)
            ? LanguageService.Get("runLog.empty")
            : "";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowEmpty)));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
