using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CyberTodo.Models;
using CyberTodo.Services;

namespace CyberTodo.ViewModels;

public sealed partial class MainViewModel
{
    private static readonly TimeSpan s_undoLifetime = TimeSpan.FromSeconds(6);

    private const int MaxUndo = 30;

    private readonly List<UndoEntry> _undoStack = [];

    // ───────── Geri alma ─────────

    public bool CanUndo => _undoStack.Count > 0;

    /// <summary>Son geri alınabilir işlemin açıklaması (düğme ipucu); yoksa boş.</summary>
    public string UndoHint => _undoStack.Count > 0 ? _loc.Format("Tip_UndoLast", _undoStack[^1].Message) : _loc["Tip_Undo"];

    /// <summary>Ctrl+Z / ↶: en son işlemi (ekle, tamamla, geri aç, düzenle, taşı, sil, şablon) geri alır.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo), AllowConcurrentExecutions = true)]
    private async Task UndoAsync()
    {
        if (_undoStack.Count > 0)
        {
            await RevertAsync(_undoStack[^1]);
        }
    }

    private UndoEntry PushUndo(string message, Func<Task> revert)
    {
        var entry = new UndoEntry(message, revert);
        _undoStack.Add(entry);
        if (_undoStack.Count > MaxUndo)
        {
            _undoStack.RemoveAt(0);
        }

        OnUndoStackChanged();
        return entry;
    }

    /// <summary>Kaydı yığından çıkarıp geri alır. Kayıt zaten kullanılmışsa (bildirimden + Ctrl+Z) hiçbir şey yapmaz.</summary>
    private async Task RevertAsync(UndoEntry entry)
    {
        if (!_undoStack.Remove(entry))
        {
            return;
        }

        OnUndoStackChanged();
        if (entry.Toast is { } toast)
        {
            Toasts.Remove(toast);
        }

        await entry.Revert();
        ShowToast(_loc.Format("Toast_Undone", entry.Message), ToastKind.Info, lifetime: s_infoLifetime);
    }

    private void ClearUndo()
    {
        foreach (var toast in _undoStack.Select(static e => e.Toast).OfType<ToastItem>())
        {
            Toasts.Remove(toast);
        }

        _undoStack.Clear();
        OnUndoStackChanged();
    }

    private void OnUndoStackChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoHint));
        UndoCommand.NotifyCanExecuteChanged();
    }
}
