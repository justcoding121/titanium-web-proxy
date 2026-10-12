using System.Globalization;
using System.Text;
using System.Windows.Input;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.ViewModels;

/// <summary>One Statistics legend row. Built only while the Statistics tab is selected.</summary>
/// <summary>One Statistics event line. Built only while the Statistics tab is selected.</summary>
public sealed class StatisticsEventRow
{
    public string TimeText { get; init; } = "";

    public string Text { get; init; } = "";

    public bool IsFailure { get; init; }

    public bool ShowPlain => !IsFailure;
}

public sealed class StatisticsPhaseRow
{
    public string Label { get; init; } = "";

    public string MillisecondsText { get; init; } = "";

    public double BarWidth { get; init; }

    public double Opacity { get; init; } = 1;

    public string? ToolTip { get; init; }
}

public sealed partial class MainWindowViewModel
{
    private const double StatisticsBarTrack = SessionStatistics.BarTrackPixels;

    private static readonly double[] PhaseOpacity = [1, 0.86, 0.74, 0.62, 0.52, 0.44, 0.36];

    private RelayCommand? _copyStatisticsCommand;
    private string _statisticsSummary = "";
    private string _statisticsCopyText = "";
    private string _statisticsBarName = "";
    private bool _showStatisticsBar;
    private bool _showStatisticsEvents;
    private IReadOnlyList<StatisticsPhaseRow> _statisticsPhases = [];
    private IReadOnlyList<StatisticsEventRow> _statisticsEvents = [];

    public ICommand CopyStatisticsCommand => _copyStatisticsCommand ??= Cmd(CopyStatisticsAsync);

    public string StatisticsSummary
    {
        get => _statisticsSummary;
        private set => SetField(ref _statisticsSummary, value);
    }

    public string StatisticsCopyText => _statisticsCopyText;

    public string StatisticsBarName
    {
        get => _statisticsBarName;
        private set => SetField(ref _statisticsBarName, value);
    }

    public bool ShowStatisticsBar
    {
        get => _showStatisticsBar;
        private set => SetField(ref _showStatisticsBar, value);
    }

    public IReadOnlyList<StatisticsPhaseRow> StatisticsPhases
    {
        get => _statisticsPhases;
        private set => SetField(ref _statisticsPhases, value);
    }

    public IReadOnlyList<StatisticsEventRow> StatisticsEvents
    {
        get => _statisticsEvents;
        private set => SetField(ref _statisticsEvents, value);
    }

    public bool ShowStatisticsEvents
    {
        get => _showStatisticsEvents;
        private set => SetField(ref _showStatisticsEvents, value);
    }

    private void RefreshStatisticsIfVisible()
    {
        if (_selectedInspectTabIndex != (int)InspectTab.Statistics)
        {
            return;
        }

        var selection = StatisticsSelection();
        var model = SessionStatistics.Build(selection);
        IReadOnlyList<SessionEventNote>? events = null;
        if (selection.Count == 1)
        {
            events = selection[0].Events;
        }

        ApplyStatistics(model, events);
    }

    private IReadOnlyList<SessionSnapshot> StatisticsSelection()
    {
        if (_selectedSessions.Count > 1)
        {
            return _selectedSessions;
        }

        if (_selected is not null)
        {
            return [_selected];
        }

        return _selectedSessions;
    }

    private void ApplyStatistics(SessionStatistics.Model model, IReadOnlyList<SessionEventNote>? events)
    {
        var summary = new StringBuilder();
        var copy = new StringBuilder();
        IReadOnlyList<StatisticsPhaseRow> rows = [];
        var barName = "";
        var eventRows = BuildEventRows(events);

        switch (model.Kind)
        {
            case SessionStatistics.Kind.Estimated:
                AppendLine(summary, LanguageService.Get("stats.estimated"));
                break;
            case SessionStatistics.Kind.NotRecorded:
                AppendLine(summary, LanguageService.Get("stats.notRecorded"));
                break;
            case SessionStatistics.Kind.Phases:
                AppendPhaseSummary(model, summary, out rows, out barName);
                break;
            case SessionStatistics.Kind.Multi:
                AppendMulti(model, summary);
                break;
        }

        copy.Append(summary);
        foreach (var row in rows)
        {
            AppendLine(copy, row.Label + "  " + row.MillisecondsText);
        }

        foreach (var row in eventRows)
        {
            AppendLine(copy, row.TimeText + "  " + row.Text);
        }

        StatisticsSummary = summary.ToString().TrimEnd();
        _statisticsCopyText = copy.ToString().TrimEnd();
        StatisticsPhases = rows;
        ShowStatisticsBar = rows.Count > 0 && rows.Any(row => row.BarWidth > 0);
        StatisticsBarName = barName;
        StatisticsEvents = eventRows;
        ShowStatisticsEvents = eventRows.Count > 0;
    }

    private static IReadOnlyList<StatisticsEventRow> BuildEventRows(IReadOnlyList<SessionEventNote>? events)
    {
        if (events is null || events.Count == 0)
        {
            return [];
        }

        var rows = new List<StatisticsEventRow>(Math.Min(events.Count, SessionEventBuffer.Cap));
        foreach (var note in events.Take(SessionEventBuffer.Cap))
        {
            var sentence = LanguageService.Get(note.Key);
            rows.Add(new StatisticsEventRow
            {
                TimeText = LanguageService.Format("stats.eventAt", FormatMs(note.OffsetMs)),
                Text = note.Failure ? LanguageService.Format("stats.eventFailure", sentence) : sentence,
                IsFailure = note.Failure,
            });
        }

        return rows;
    }

    private static void AppendPhaseSummary(
        SessionStatistics.Model model,
        StringBuilder summary,
        out IReadOnlyList<StatisticsPhaseRow> rows,
        out string barName)
    {
        if (model.TotalMs is double total)
        {
            AppendLine(summary, LanguageService.Format("stats.total", FormatMs(total)));
        }

        if (model.Slowest is SessionStatistics.Phase slowest)
        {
            AppendLine(summary, LanguageService.Format("stats.slowest", LanguageService.Get(SessionStatistics.PhaseKey(slowest))));
        }

        if (model.Note == SessionStatistics.ConnectionNote.Reused)
        {
            AppendLine(summary, LanguageService.Get("stats.reused"));
        }
        else if (model.Note == SessionStatistics.ConnectionNote.Prefetched)
        {
            AppendLine(summary, LanguageService.Get("stats.prefetched"));
        }

        if (model.Attempts > 1)
        {
            AppendLine(summary, LanguageService.Format("stats.retried", model.Attempts));
        }

        var sum = 0d;
        foreach (var phase in model.Phases)
        {
            if (phase.Milliseconds > 0)
            {
                sum += phase.Milliseconds;
            }
        }

        var built = new List<StatisticsPhaseRow>(model.Phases.Count);
        var names = new StringBuilder();
        for (var i = 0; i < model.Phases.Count; i++)
        {
            var phase = model.Phases[i];
            var label = LanguageService.Get(SessionStatistics.PhaseKey(phase.Phase));
            var ms = LanguageService.Format("stats.total", FormatMs(phase.Milliseconds));
            var width = 0d;
            if (phase.Milliseconds > 0 && sum > 0)
            {
                width = Math.Max(2, phase.Milliseconds / sum * StatisticsBarTrack);
            }

            built.Add(new StatisticsPhaseRow
            {
                Label = label,
                MillisecondsText = ms,
                BarWidth = width,
                Opacity = PhaseOpacity[Math.Min(i, PhaseOpacity.Length - 1)],
                ToolTip = phase.Phase == SessionStatistics.Phase.Wait ? LanguageService.Get("stats.ttfbTip") : null,
            });
            if (names.Length > 0)
            {
                names.Append(", ");
            }

            names.Append(label).Append(' ').Append(ms);
        }

        rows = built;
        barName = names.ToString();
    }

    private static void AppendMulti(SessionStatistics.Model model, StringBuilder summary)
    {
        AppendLine(summary, LanguageService.Format("stats.sessions", model.SessionCount));
        AppendLine(summary, LanguageService.Format("stats.size", SessionDisplayFormat.FormatByteSize(model.TotalBytes)));
        if (model.SlowestDurationMs is double slowest)
        {
            AppendLine(summary, LanguageService.Format("stats.slowestSession", FormatMs(slowest)));
        }

        foreach (var status in model.StatusCounts)
        {
            AppendLine(summary, status.Class == 0
                ? LanguageService.Format("stats.statusOther", status.Count)
                : LanguageService.Format("stats.status", status.Class, status.Count));
        }

        if (model.Truncated)
        {
            AppendLine(summary, LanguageService.Format("stats.truncated", model.ShownCount));
        }
    }

    private Task CopyStatisticsAsync()
    {
        return CopyTextToClipboardAsync(_statisticsCopyText);
    }

    private static void AppendLine(StringBuilder builder, string line)
    {
        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append(line);
    }

    private static string FormatMs(double milliseconds) =>
        milliseconds.ToString("0.#", CultureInfo.InvariantCulture);
}
