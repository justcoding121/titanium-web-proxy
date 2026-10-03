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
    public void ReplaceAll(IReadOnlyList<SessionSnapshot> items)
    {
        CheckReentrancy();
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
