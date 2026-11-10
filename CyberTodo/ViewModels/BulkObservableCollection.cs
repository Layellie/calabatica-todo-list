using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace CyberTodo.ViewModels;

/// <summary>
/// Toplu eklemede öğe başına bildirim yerine tek <see cref="NotifyCollectionChangedAction.Reset"/> yayınlayan koleksiyon.
/// Bağlı CollectionView bir kez sıralar/filtreler, ItemsControl listeyi bir kez kurar.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();

        bool added = false;
        foreach (var item in items)
        {
            Items.Add(item);
            added = true;
        }

        if (!added)
        {
            return;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
