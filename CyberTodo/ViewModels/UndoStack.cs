namespace CyberTodo.ViewModels;

/// <summary>
/// Sınırlı, LIFO geri alma yığını. Arayüz/bildirim bilmez: yalnızca kayıtları tutar; geri almayı çağıran (ViewModel)
/// kaydı <see cref="Remove"/> ile yığından çıkarıp <see cref="UndoEntry.Revert"/>'i çalıştırır. Kayıt zaten çıkarılmışsa
/// (ör. hem bildirimin düğmesi hem Ctrl+Z) <see cref="Remove"/> false döner → çift geri alma olmaz.
/// </summary>
internal sealed class UndoStack(int capacity)
{
    private readonly List<UndoEntry> _entries = [];

    /// <summary>Yığın değişti (ekleme, çıkarma, temizleme, taşma).</summary>
    public event Action? Changed;

    public int Count => _entries.Count;

    public bool CanUndo => _entries.Count > 0;

    /// <summary>En son kayıt (yoksa null).</summary>
    public UndoEntry? Last => _entries.Count > 0 ? _entries[^1] : null;

    /// <summary>Kayıt ekler; kapasite aşılırsa en eski kayıt sessizce düşer.</summary>
    public UndoEntry Push(string message, Func<Task> revert)
    {
        var entry = new UndoEntry(message, revert);
        _entries.Add(entry);
        if (_entries.Count > capacity)
        {
            _entries.RemoveAt(0);
        }

        Changed?.Invoke();
        return entry;
    }

    /// <summary>Kaydı çıkarır. Yığında yoksa (zaten kullanılmış / taşmış) false.</summary>
    public bool Remove(UndoEntry entry)
    {
        bool removed = _entries.Remove(entry);
        if (removed)
        {
            Changed?.Invoke();
        }

        return removed;
    }

    /// <summary>Hepsini siler ve silinenleri döndürür (çağıran bağlı bildirimleri kaldırabilsin).</summary>
    public IReadOnlyList<UndoEntry> Clear()
    {
        var removed = _entries.ToArray();
        _entries.Clear();
        if (removed.Length > 0)
        {
            Changed?.Invoke();
        }

        return removed;
    }
}
