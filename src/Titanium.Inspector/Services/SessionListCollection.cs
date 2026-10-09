using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Titanium.Inspector.Services;

/// <summary>
/// Session list that can swap its contents with one <see cref="NotifyCollectionChangedAction.Reset"/>
/// so the DataGrid does not process one add or remove per row.
/// </summary>
public sealed class SessionListCollection : ObservableCollection<SessionSnapshot>
{
    /// <summary>
    /// Raised before the contents are swapped, ahead of any collection observer. The DataGrid resets its scroll
    /// offset while handling the Reset; the window uses this to tell that apart from the user scrolling.
    /// </summary>
    public event Action? Replacing;

    public void ReplaceAll(IReadOnlyList<SessionSnapshot> items)
    {
        CheckReentrancy();
        Replacing?.Invoke();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
