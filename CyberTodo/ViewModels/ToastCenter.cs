using System.Collections.ObjectModel;
using CyberTodo.Models;

namespace CyberTodo.ViewModels;

/// <summary>
/// Sağ alttaki bildirimler: ekleme (en fazla <see cref="MaxToasts"/>, taşan en eski düşer), kaldırma ve süresi
/// dolanları temizleme. Metin/dil ve geri alma mantığı çağırana (ViewModel) aittir. UI'ya <see cref="Items"/> bağlanır.
/// </summary>
internal sealed class ToastCenter(int maxToasts)
{
    public int MaxToasts { get; } = maxToasts > 0 ? maxToasts : throw new ArgumentOutOfRangeException(nameof(maxToasts));

    public ObservableCollection<ToastItem> Items { get; } = [];

    public ToastItem Add(ToastItem toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        Items.Add(toast);
        while (Items.Count > MaxToasts)
        {
            Items.RemoveAt(0);
        }

        return toast;
    }

    /// <summary>Bildirimi kaldırır; zaten yoksa (süresi dolmuş / kullanılmış) false.</summary>
    public bool Remove(ToastItem toast) => Items.Remove(toast);

    /// <summary><paramref name="now"/> anında süresi dolmuş bildirimleri kaldırır; kaldırılan sayısını döndürür.</summary>
    public int RemoveExpired(DateTime now)
    {
        int removed = 0;
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].ExpiresAt is { } expires && now >= expires)
            {
                Items.RemoveAt(i);
                removed++;
            }
        }

        return removed;
    }
}
