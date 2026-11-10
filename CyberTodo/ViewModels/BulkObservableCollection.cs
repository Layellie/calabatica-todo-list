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

        RaiseReset();
    }

    /// <summary>Eşleşen öğeleri tek Reset ile çıkarır (çok sayıda Remove bildirimi listeyi defalarca yeniden düzenlerdi).</summary>
    public int RemoveWhere(Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        CheckReentrancy();
        int removed = 0;
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (predicate(Items[i]))
            {
                Items.RemoveAt(i);
                removed++;
            }
        }

        if (removed > 0)
        {
            RaiseReset();
        }

        return removed;
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
