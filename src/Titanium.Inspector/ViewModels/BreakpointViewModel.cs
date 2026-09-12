using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Titanium.Inspector.ViewModels;

/// <summary>Breakpoint rules: Continue/Abort/Edit; max 1 active; 120s timeout.</summary>
public sealed class BreakpointViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private static readonly object Gate = new();
    private BreakpointHit? _active;
    private bool _enabled;
    private string _urlFilter = "*";
    private string _graphQlOperationName = "";
    private string _activeSummary = "";
    private string _lastOverflowMessage = "";

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            PropertyChanged?.Invoke(this, new(nameof(Enabled)));
        }
    }

    public string UrlFilter
    {
        get => _urlFilter;
        set
        {
            _urlFilter = value;
            PropertyChanged?.Invoke(this, new(nameof(UrlFilter)));
        }
    }

    /// <summary>Optional GraphQL operationName; when set, breakpoints only match that operation.</summary>
    public string GraphQlOperationName
    {
        get => _graphQlOperationName;
        set
        {
            _graphQlOperationName = value ?? "";
            PropertyChanged?.Invoke(this, new(nameof(GraphQlOperationName)));
        }
    }

    public TimeSpan Timeout { get; } = TimeSpan.FromSeconds(120);

    public BreakpointHit? Active
    {
        get => _active;
        private set
        {
            if (ReferenceEquals(_active, value))
                return;
            _active = value;
            PropertyChanged?.Invoke(this, new(nameof(Active)));
            PropertyChanged?.Invoke(this, new(nameof(HasActiveHit)));
            ActiveSummary = value is null
                ? ""
                : $"Paused {value.Session.Method} {TruncateUrl(value.Session.Url)} — Continue or Abort ({(int)Timeout.TotalSeconds}s)";
        }
    }

    public bool HasActiveHit => _active is not null;

    public string ActiveSummary
    {
        get => _activeSummary;
        private set
        {
            if (_activeSummary == value)
                return;
            _activeSummary = value;
            PropertyChanged?.Invoke(this, new(nameof(ActiveSummary)));
        }
    }

    /// <summary>Last overflow / auto-continue notice for status bar (cleared on next enter).</summary>
    public string LastOverflowMessage
    {
        get => _lastOverflowMessage;
        private set
        {
            if (_lastOverflowMessage == value)
                return;
            _lastOverflowMessage = value;
            PropertyChanged?.Invoke(this, new(nameof(LastOverflowMessage)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised on the thread that entered / cleared the hit (marshal to UI in the host).</summary>
    public event EventHandler? ActiveHitChanged;

    public bool TryEnter(Services.SessionSnapshot session, out BreakpointHit hit)
    {
        hit = null!;
        if (!Enabled || !Matches(session.Url))
        {
            return false;
        }

        lock (Gate)
        {
            if (_active is not null)
            {
                // Max 1 active — overflow auto-continue.
                LastOverflowMessage = "Already paused — extra breakpoint hit continued";
                return false;
            }

            LastOverflowMessage = "";
            hit = new BreakpointHit(session, Timeout, OnHitTimedOut);
            Active = hit;
        }

        ActiveHitChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Continue()
    {
        ClearActive(BreakpointAction.Continue, raiseHitChanged: true);
    }

    public void Abort()
    {
        ClearActive(BreakpointAction.Abort, raiseHitChanged: true);
    }

    public void EditBody(string newBody)
    {
        lock (Gate)
        {
            if (_active is null)
            {
                return;
            }

            _active.EditedBody = newBody;
            _active.ContentLength = Encoding.UTF8.GetByteCount(newBody);
        }
    }

    private void OnHitTimedOut(BreakpointHit hit)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(_active, hit))
                return;
            hit.Complete(BreakpointAction.Continue);
            Active = null;
            LastOverflowMessage = "Breakpoint auto-continued (timeout)";
        }

        ActiveHitChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearActive(BreakpointAction action, bool raiseHitChanged)
    {
        lock (Gate)
        {
            _active?.Complete(action);
            Active = null;
        }

        if (raiseHitChanged)
            ActiveHitChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool Matches(string url)
    {
        if (string.IsNullOrEmpty(UrlFilter) || UrlFilter == "*")
        {
            return true;
        }

        var pattern = "^" + Regex.Escape(UrlFilter).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(url, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static string TruncateUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || url.Length <= 64)
            return url;
        return url[..61] + "...";
    }
}

public enum BreakpointAction
{
    Continue,
    Abort,
}

public sealed class BreakpointHit
{
    private readonly TaskCompletionSource<BreakpointAction> _tcs = new();
    private readonly Action<BreakpointHit>? _onTimeout;

    public BreakpointHit(Services.SessionSnapshot session, TimeSpan timeout, Action<BreakpointHit>? onTimeout = null)
    {
        Session = session;
        _onTimeout = onTimeout;
        _ = Task.Delay(timeout).ContinueWith(_ =>
        {
            if (_onTimeout is not null)
            {
                _onTimeout(this);
                return;
            }

            Complete(BreakpointAction.Continue);
        });
    }

    public Services.SessionSnapshot Session { get; }
    public string? EditedBody { get; set; }
    public int? ContentLength { get; set; }

    public Task<BreakpointAction> WaitAsync(CancellationToken cancellationToken = default) =>
        _tcs.Task.WaitAsync(cancellationToken);

    public void Complete(BreakpointAction action) => _tcs.TrySetResult(action);
}
