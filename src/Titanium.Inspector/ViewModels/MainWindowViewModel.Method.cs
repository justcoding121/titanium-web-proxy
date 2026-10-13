using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly HashSet<string> _seenMethods = new(StringComparer.OrdinalIgnoreCase);
    private bool _methodWrite;

    /// <summary>Method menu rows: common verbs, then methods seen or named in the query, then CONNECT.</summary>
    public ObservableCollection<MethodFilterRow> MethodFilterRows { get; } = new();

    /// <summary>The CONNECT row (also the last item of <see cref="MethodFilterRows"/>).</summary>
    public MethodFilterRow? ConnectMethodRow { get; private set; }

    public ICommand SelectAllMethodsCommand { get; private set; } = null!;

    /// <summary>Count pill. Empty when nothing is hidden, otherwise <c>1</c>–<c>9</c> or <c>9+</c>.</summary>
    public string MethodFilterBadge => BadgeText(HiddenMethodCount());

    /// <summary>Help text when idle; the hidden verbs when a hide is active.</summary>
    public string MethodFilterTip
    {
        get
        {
            var names = HiddenMethodNames();
            return names.Count == 0
                ? LanguageService.Get("toolbar.methodTip")
                : LanguageService.Format("toolbar.methodActiveTip", string.Join(", ", names));
        }
    }

    public bool MethodFilterActive
    {
        get
        {
            foreach (var row in MethodFilterRows)
            {
                if (!row.IsChecked)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>True when the next edit will replace a typed <c>method:</c> or <c>hide:tunnel</c> token.</summary>
    public bool MethodFilterShowsTypedNote =>
        MethodFilter.HasPositiveMethodToken(SearchQuery) || MethodFilter.HasHideTunnel(SearchQuery);

    private void WireMethodFilter()
    {
        SelectAllMethodsCommand = Cmd(() =>
        {
            SelectAllMethods();
            return Task.CompletedTask;
        });
        SyncMethodRows();
    }

    private void NoteSessionMethod(string? method)
    {
        if (string.IsNullOrEmpty(method) ||
            MethodFilter.IsCommon(method) ||
            method.Equals(MethodFilter.Connect, StringComparison.OrdinalIgnoreCase) ||
            _seenMethods.Contains(method))
        {
            return;
        }

        if (!MethodFilter.TryCanonical(method, out var canonical) || !_seenMethods.Add(canonical))
        {
            return;
        }

        SyncMethodRows();
    }

    private void ResetSeenMethods()
    {
        _seenMethods.Clear();
        SyncMethodRows();
    }

    private void SyncMethodRows()
    {
        var desired = DesiredNames();
        if (!SameNames(desired))
        {
            var writing = _methodWrite;
            if (!writing)
            {
                _methodWrite = true;
            }

            try
            {
                MethodFilterRows.Clear();
                MethodFilterRow? connect = null;
                foreach (var name in desired)
                {
                    var row = new MethodFilterRow(
                        name,
                        name.Equals(MethodFilter.Connect, StringComparison.OrdinalIgnoreCase),
                        OnlyMethod);
                    row.AttachChanged(OnMethodRowEdited);
                    MethodFilterRows.Add(row);
                    if (row.IsTunnel)
                    {
                        connect = row;
                    }
                }

                ConnectMethodRow = connect;
            }
            finally
            {
                if (!writing)
                {
                    _methodWrite = false;
                }
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectMethodRow)));
        }

        ApplyMethodChecks();
        NotifyMethodLabel();
    }

    private List<string> DesiredNames()
    {
        var extras = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var seen in _seenMethods)
        {
            if (MethodFilter.TryCanonical(seen, out var canonical) &&
                !MethodFilter.IsCommon(canonical) &&
                !canonical.Equals(MethodFilter.Connect, StringComparison.OrdinalIgnoreCase))
            {
                extras.Add(canonical);
            }
        }

        var mentioned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MethodFilter.CollectMentioned(SearchQuery, mentioned);
        foreach (var name in mentioned.Where(static name =>
                     !MethodFilter.IsCommon(name) &&
                     !name.Equals(MethodFilter.Connect, StringComparison.OrdinalIgnoreCase)))
        {
            extras.Add(name);
        }

        var names = new List<string>(MethodFilter.Common.Length + extras.Count + 1);
        names.AddRange(MethodFilter.Common);
        names.AddRange(extras);
        names.Add(MethodFilter.Connect);
        return names;
    }

    private bool SameNames(List<string> desired)
    {
        if (MethodFilterRows.Count != desired.Count)
        {
            return false;
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (!MethodFilterRows[i].Name.Equals(desired[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void ApplyMethodChecks()
    {
        foreach (var row in MethodFilterRows)
        {
            row.SetCheckedFromQuery(MethodFilter.IsShown(row.Name, SearchQuery));
        }
    }

    private void OnMethodRowEdited()
    {
        if (_methodWrite)
        {
            return;
        }

        WriteFromRows();
    }

    private void SelectAllMethods()
    {
        _methodWrite = true;
        try
        {
            foreach (var row in MethodFilterRows)
            {
                row.SetCheckedFromQuery(true);
            }
        }
        finally
        {
            _methodWrite = false;
        }

        WriteFromRows();
    }

    private void OnlyMethod(string name)
    {
        _methodWrite = true;
        try
        {
            foreach (var row in MethodFilterRows)
            {
                row.SetCheckedFromQuery(row.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            _methodWrite = false;
        }

        WriteFromRows();
    }

    /// <summary>
    /// Writes one <c>-method:</c> token for the unchecked rows.
    /// Drops a typed <c>method:</c> and <c>hide:tunnel</c> so the boxes are the whole method filter.
    /// </summary>
    private void WriteFromRows()
    {
        if (_methodWrite)
        {
            return;
        }

        var hidden = new List<string>();
        foreach (var row in MethodFilterRows)
        {
            if (!row.IsChecked)
            {
                hidden.Add(row.Name);
            }
        }

        var query = SessionSearch.RemoveKeyedTokens(SearchQuery, "method");
        query = SessionSearch.RemoveKeyedTokens(query, "-method");
        query = SessionSearch.RemoveToken(query, "hide", "tunnel");
        query = SessionSearch.RemoveToken(query, "hide", "connect");
        if (hidden.Count > 0)
        {
            query = SessionSearch.SetKeyedToken(query, "-method", string.Join(',', hidden));
        }

        _methodWrite = true;
        try
        {
            SearchQuery = query;
        }
        finally
        {
            _methodWrite = false;
        }
    }

    private int HiddenMethodCount()
    {
        var hidden = 0;
        foreach (var row in MethodFilterRows)
        {
            if (!row.IsChecked)
            {
                hidden++;
            }
        }

        return hidden;
    }

    private List<string> HiddenMethodNames()
    {
        var names = new List<string>();
        foreach (var row in MethodFilterRows)
        {
            if (!row.IsChecked)
            {
                names.Add(row.Name);
            }
        }

        return names;
    }

    private static string BadgeText(int count)
    {
        if (count <= 0)
        {
            return "";
        }

        return count > 9 ? "9+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void NotifyMethodLabel()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MethodFilterBadge)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MethodFilterTip)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MethodFilterActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MethodFilterShowsTypedNote)));
    }
}

/// <summary>One verb in the Method menu. Checked means the verb is shown.</summary>
public sealed class MethodFilterRow : INotifyPropertyChanged
{
    private readonly Action<string> _only;
    private Action? _changed;
    private bool _checked = true;

    public MethodFilterRow(string name, bool isTunnel, Action<string> only)
    {
        Name = name;
        IsTunnel = isTunnel;
        _only = only;
        OnlyCommand = new MethodRowCommand(() => _only(Name));
    }

    public string Name { get; }

    public bool IsTunnel { get; }

    public string RowAutomationId => "MethodFilterRow_" + Name;

    public string OnlyAutomationId => "MethodFilterOnly_" + Name;

    public ICommand OnlyCommand { get; }

    public bool IsChecked
    {
        get => _checked;
        set
        {
            if (_checked == value)
            {
                return;
            }

            _checked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            _changed?.Invoke();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void AttachChanged(Action changed) => _changed = changed;

    /// <summary>Updates the box from the search query without writing a new token.</summary>
    public void SetCheckedFromQuery(bool value)
    {
        if (_checked == value)
        {
            return;
        }

        _checked = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
    }
}

internal sealed class MethodRowCommand(Action execute) : ICommand
{
    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();

    public event EventHandler? CanExecuteChanged
    {
        add => _ = value;
        remove => _ = value;
    }
}
