using System.ComponentModel;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>Number of <see cref="InspectTab"/> values. Tools indices in <see cref="SelectedDetailTabIndex"/> start here.</summary>
    public const int InspectTabCount = (int)InspectTab.Protobuf + 1;

    /// <summary>Composer, Breakpoints, AutoResponder, Scripts, Map Remote.</summary>
    public const int ToolsTabCount = 5;

    /// <summary>Headers start collapsed when the inspect pane is shorter than this.</summary>
    public const double ShortInspectPaneHeight = 420;

    /// <summary>Default headers share of the headers-plus-body split (1:2).</summary>
    public const double DefaultInspectHeadersRatio = 1.0 / 3.0;

    private const double MinInspectHeadersRatio = 0.12;
    private const double MaxInspectHeadersRatio = 0.70;

    private bool _inspectHeadersCollapsed;
    private bool _inspectHeadersUserChosen;
    private double _lastInspectPaneHeight;
    private double _inspectHeadersStar = DefaultInspectHeadersRatio;
    private double _inspectBodyStar = 1 - DefaultInspectHeadersRatio;
    private int _selectedRequestHeaderCount;
    private int _selectedResponseHeaderCount;

    public bool InspectHeadersCollapsed => _inspectHeadersCollapsed;

    /// <summary>Headers text and the splitter. The header bar stays when this is false.</summary>
    public bool ShowInspectHeadersPane => !_inspectHeadersCollapsed;

    public string InspectHeadersToggleGlyph => _inspectHeadersCollapsed ? "▸" : "▾";

    public string InspectHeadersToggleTip =>
        _inspectHeadersCollapsed ? "Show headers" : "Hide headers";

    /// <summary>Star weight for the headers row while it is expanded.</summary>
    public double InspectHeadersStar => _inspectHeadersStar;

    /// <summary>Star weight for the body row.</summary>
    public double InspectBodyStar => _inspectBodyStar;

    /// <summary>Headers' share of the split. Matches <see cref="InspectorSettings.InspectHeadersRatio"/> once saved.</summary>
    public double InspectHeadersRatio => _inspectHeadersStar;

    public int SelectedRequestHeaderCount
    {
        get => _selectedRequestHeaderCount;
        private set
        {
            if (SetField(ref _selectedRequestHeaderCount, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RequestHeadersCaption)));
            }
        }
    }

    public int SelectedResponseHeaderCount
    {
        get => _selectedResponseHeaderCount;
        private set
        {
            if (SetField(ref _selectedResponseHeaderCount, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResponseHeadersCaption)));
            }
        }
    }

    public string RequestHeadersCaption => $"Headers ({_selectedRequestHeaderCount})";

    public string ResponseHeadersCaption => $"Headers ({_selectedResponseHeaderCount})";

    public void ToggleInspectHeadersCollapsed() =>
        SetInspectHeadersCollapsed(!_inspectHeadersCollapsed, userChosen: true);

    /// <summary>
    /// Applies the short-pane rule from the inspect host height. A collapse or expand the user
    /// chose is left alone. A height of 0 (pane not measured yet) is ignored.
    /// </summary>
    public void ApplyInspectPaneHeight(double height)
    {
        if (height > 0)
        {
            _lastInspectPaneHeight = height;
        }

        if (height <= 0 || _inspectHeadersUserChosen)
        {
            return;
        }

        SetInspectHeadersCollapsed(height < ShortInspectPaneHeight, userChosen: false);
    }

    /// <summary>Stores the splitter position as a ratio and persists it.</summary>
    public void CommitInspectHeadersRatio(double headersPx, double bodyPx)
    {
        var total = headersPx + bodyPx;
        if (total < 1)
        {
            return;
        }

        SetInspectHeadersRatio(headersPx / total, persist: true);
    }

    /// <summary>Restores the 1:2 headers-to-body split.</summary>
    public void ResetInspectHeadersRatio() =>
        SetInspectHeadersRatio(DefaultInspectHeadersRatio, persist: true);

    private void ApplyInspectLayoutFromSettings()
    {
        var saved = _settings.Current.InspectHeadersRatio;
        var ratio = saved is > 0.05 and < 0.95 ? saved : DefaultInspectHeadersRatio;
        _inspectHeadersStar = Math.Clamp(ratio, MinInspectHeadersRatio, MaxInspectHeadersRatio);
        _inspectBodyStar = 1 - _inspectHeadersStar;
        _inspectHeadersUserChosen = _settings.Current.InspectHeadersCollapsed is bool;
        _inspectHeadersCollapsed = _settings.Current.InspectHeadersCollapsed
            ?? (_lastInspectPaneHeight > 0 && _lastInspectPaneHeight < ShortInspectPaneHeight);
        NotifyInspectHeadersLayout();
    }

    private void SetInspectHeadersCollapsed(bool collapsed, bool userChosen)
    {
        if (userChosen)
        {
            _inspectHeadersUserChosen = true;
            if (_settings.Current.InspectHeadersCollapsed != collapsed)
            {
                _settings.Current.InspectHeadersCollapsed = collapsed;
                _settings.Save();
            }
        }

        if (_inspectHeadersCollapsed == collapsed)
        {
            return;
        }

        _inspectHeadersCollapsed = collapsed;
        NotifyInspectHeadersLayout();
    }

    private void SetInspectHeadersRatio(double ratio, bool persist)
    {
        ratio = Math.Clamp(ratio, MinInspectHeadersRatio, MaxInspectHeadersRatio);
        var changed = Math.Abs(_inspectHeadersStar - ratio) > 0.0001;
        _inspectHeadersStar = ratio;
        _inspectBodyStar = 1 - ratio;
        if (persist && Math.Abs(_settings.Current.InspectHeadersRatio - ratio) > 0.0001)
        {
            _settings.Current.InspectHeadersRatio = ratio;
            _settings.Save();
        }

        if (changed)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InspectHeadersStar)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InspectBodyStar)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InspectHeadersRatio)));
        }
    }

    private void NotifyInspectHeadersLayout()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InspectHeadersCollapsed)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowInspectHeadersPane)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InspectHeadersToggleGlyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InspectHeadersToggleTip)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InspectHeadersStar)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InspectBodyStar)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InspectHeadersRatio)));
    }

    private void PublishHeaderCounts(SessionSnapshot? selected)
    {
        if (selected is null)
        {
            SelectedRequestHeaderCount = 0;
            SelectedResponseHeaderCount = 0;
            return;
        }

        SelectedRequestHeaderCount = CountHeaderLines(selected.RequestHeadersText);
        SelectedResponseHeaderCount = ResponseNotStarted(selected)
            ? 0
            : CountHeaderLines(selected.ResponseHeadersText);
    }

    private static int CountHeaderLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.IndexOf(':') > 0)
            {
                count++;
            }
        }

        return count;
    }
}
