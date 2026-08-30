using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Titanium.Inspector.ViewModels;

/// <summary>Breakpoint rules: Continue/Abort/Edit; max 1 active; 120s timeout.</summary>
public sealed class BreakpointViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private static readonly object Gate = new();
    private BreakpointHit? _active;
    private bool _enabled;
    private string _urlFilter = "*";

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

    public TimeSpan Timeout { get; } = TimeSpan.FromSeconds(120);
    public BreakpointHit? Active => _active;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

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
                return false;
            }

            hit = new BreakpointHit(session, Timeout);
            _active = hit;
            return true;
        }
    }

    public void Continue()
    {
        lock (Gate)
        {
            _active?.Complete(BreakpointAction.Continue);
            _active = null;
        }
    }

    public void Abort()
    {
        lock (Gate)
        {
            _active?.Complete(BreakpointAction.Abort);
            _active = null;
        }
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

    private bool Matches(string url)
    {
        if (string.IsNullOrEmpty(UrlFilter) || UrlFilter == "*")
        {
            return true;
        }

        var pattern = "^" + Regex.Escape(UrlFilter).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(url, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
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

    public BreakpointHit(Services.SessionSnapshot session, TimeSpan timeout)
    {
        Session = session;
        _ = Task.Delay(timeout).ContinueWith(_ =>
        {
            Complete(BreakpointAction.Continue);
        });
    }

    public Services.SessionSnapshot Session { get; }
    public string? EditedBody { get; set; }
    public int? ContentLength { get; set; }

    public Task<BreakpointAction> WaitAsync() => _tcs.Task;

    public void Complete(BreakpointAction action) => _tcs.TrySetResult(action);
}
