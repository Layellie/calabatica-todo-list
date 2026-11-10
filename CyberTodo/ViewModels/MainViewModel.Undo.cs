using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using CyberTodo.Models;

namespace CyberTodo.ViewModels;

public sealed partial class MainViewModel
{
    private static readonly TimeSpan s_undoLifetime = TimeSpan.FromSeconds(6);

    private const int MaxUndo = 30;

    private readonly UndoStack _undo = new(MaxUndo);

    // ───────── Geri alma ─────────

    public bool CanUndo => _undo.CanUndo;

    /// <summary>Son geri alınabilir işlemin açıklaması (düğme ipucu); yoksa genel ipucu.</summary>
    public string UndoHint => _undo.Last is { } last ? _loc.Format("Tip_UndoLast", last.Message) : _loc["Tip_Undo"];

    /// <summary>Ctrl+Z / ↶: en son işlemi (ekle, tamamla, geri aç, düzenle, taşı, sil, şablon) geri alır.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo), AllowConcurrentExecutions = true)]
    private async Task UndoAsync()
    {
        if (_undo.Last is { } last)
        {
            await RevertAsync(last);
        }
    }

    private UndoEntry PushUndo(string message, Func<Task> revert) => _undo.Push(message, revert);

    /// <summary>Kaydı yığından çıkarıp geri alır. Kayıt zaten kullanılmışsa (bildirimden + Ctrl+Z) hiçbir şey yapmaz.</summary>
    private async Task RevertAsync(UndoEntry entry)
    {
        if (!_undo.Remove(entry))
        {
            return;
        }

        if (entry.Toast is { } toast)
        {
            _toasts.Remove(toast);
        }

        await entry.Revert();
        ShowToast(_loc.Format("Toast_Undone", entry.Message), ToastKind.Info, lifetime: s_infoLifetime);
    }

    private void ClearUndo()
    {
        foreach (var toast in _undo.Clear().Select(static e => e.Toast).OfType<ToastItem>())
        {
            _toasts.Remove(toast);
        }
    }

    private void OnUndoStackChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoHint));
        UndoCommand.NotifyCanExecuteChanged();
    }
}