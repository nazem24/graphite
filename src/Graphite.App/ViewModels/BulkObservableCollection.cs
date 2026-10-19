using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Graphite.App.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can swap its whole content with ONE change
/// notification. Clearing and re-adding N items raises N+1 notifications, and every one of
/// them re-runs the bindings (markup panel, counters) — which made undo of a page full of
/// markup noticeably slow.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IReadOnlyCollection<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
