using System.Globalization;
using System.Text;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.ViewModels;

/// <summary>
/// Timeline state. Subscribes only while open and rebuilds on a caller-driven tick,
/// so a closed window adds nothing to capture.
/// </summary>
public sealed class TimelineViewModel : IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly SessionStore _store;
    private readonly Action<long>? _select;
    private bool _subscribed;
    private bool _visible;
    private bool _hasRefresh;
    private DateTimeOffset _lastRefresh;
    private double _width = TimelineLayout.LabelWidth + TimelineLayout.MinTrack;

    public TimelineViewModel(SessionStore store, Action<long>? select = null)
    {
        _store = store;
        _select = select;
        Chart = TimelineLayout.Build([], 0, DateTimeOffset.UtcNow, _width);
    }

    public TimelineLayout.Chart Chart { get; private set; }

    public bool IsSubscribed => _subscribed;

    public bool IsVisible => _visible;

    public int RefreshCount { get; private set; }

    public int Notifications { get; private set; }

    public string Detail { get; private set; } = "";

    public string Summary =>
        Chart.Rows.Count == 0
            ? LanguageService.Get("timeline.empty")
            : Chart.Capped
                ? LanguageService.Format("timeline.capped", TimelineLayout.MaxRows)
                : "";

    public void Open(DateTimeOffset now, double width = 800)
    {
        _width = width;
        Subscribe();
        _visible = true;
        Refresh(now);
    }

    public void SetVisible(bool visible, DateTimeOffset now, double width = 800)
    {
        _visible = visible;
        _width = width;
        if (visible && _subscribed)
        {
            Refresh(now);
        }
    }

    /// <summary>Rebuilds at most once per <see cref="RefreshInterval"/> while the window is visible.</summary>
    public bool Tick(DateTimeOffset now, double width = 800)
    {
        if (!_subscribed || !_visible)
        {
            return false;
        }

        _width = width;
        if (_hasRefresh && now - _lastRefresh < RefreshInterval)
        {
            return false;
        }

        Refresh(now);
        return true;
    }

    public long? HitTest(double x, double y) => TimelineLayout.HitTest(Chart, x, y);

    public void SelectAt(double x, double y)
    {
        var id = HitTest(x, y);
        if (id is not long sessionId)
        {
            Detail = "";
            return;
        }

        Detail = Describe(Chart.Rows.First(row => row.Id == sessionId));
        _select?.Invoke(sessionId);
    }

    public void Close()
    {
        if (_subscribed)
        {
            _store.SessionAdded -= OnStoreChanged;
            _store.SessionsRemoved -= OnStoreRemoved;
            _subscribed = false;
        }

        _visible = false;
    }

    public void Dispose() => Close();

    public static string Describe(TimelineLayout.Row row)
    {
        switch (row.Kind)
        {
            case TimelineLayout.RowKind.Tunnel:
                return LanguageService.Get("timeline.tunnel");
            case TimelineLayout.RowKind.Estimated:
                return LanguageService.Get("timeline.estimated");
            case TimelineLayout.RowKind.InFlight:
                return LanguageService.Get("timeline.inFlight");
            case TimelineLayout.RowKind.Plain:
                var duration = row.Segments.Count > 0 ? row.Segments[0].Milliseconds : 0;
                return row.Label + " " + duration.ToString("0", CultureInfo.CurrentCulture);
        }

        var text = new StringBuilder();
        foreach (var segment in row.Segments)
        {
            if (text.Length > 0)
            {
                text.Append(", ");
            }

            var name = segment.Phase is SessionStatistics.Phase phase
                ? LanguageService.Get(SessionStatistics.PhaseKey(phase))
                : LanguageService.Get("timeline.tunnel");
            text.Append(name);
            text.Append(' ');
            text.Append(segment.Milliseconds.ToString("0", CultureInfo.CurrentCulture));
        }

        return text.ToString();
    }

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _store.SessionAdded += OnStoreChanged;
        _store.SessionsRemoved += OnStoreRemoved;
        _subscribed = true;
    }

    private void OnStoreChanged(SessionSnapshot _) => Notifications++;

    private void OnStoreRemoved(IReadOnlyList<SessionSnapshot> _) => Notifications++;

    private void Refresh(DateTimeOffset now)
    {
        var total = _store.Count;
        var copy = _store.CopyNewest(TimelineLayout.MaxRows);
        Chart = TimelineLayout.Build(copy, total, now, _width);
        _lastRefresh = now;
        _hasRefresh = true;
        RefreshCount++;
    }
}
