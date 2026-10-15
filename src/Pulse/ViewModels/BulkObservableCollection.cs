using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Pulse.ViewModels;

/// <summary>
/// An ObservableCollection that can swap its whole contents with a single Reset notification,
/// instead of one Add notification (and one layout pass) per item.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
