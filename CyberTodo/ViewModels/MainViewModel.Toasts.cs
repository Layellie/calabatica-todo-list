using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using CyberTodo.Models;

namespace CyberTodo.ViewModels;

public sealed partial class MainViewModel
{
    private const int MaxToasts = 4;

    private static readonly TimeSpan s_infoLifetime = TimeSpan.FromSeconds(4);

    private readonly ToastCenter _toasts = new(MaxToasts);

    /// <summary>Sağ alttaki bildirimler (arayüz buna bağlanır).</summary>
    public ObservableCollection<ToastItem> Toasts => _toasts.Items;

    [RelayCommand]
    private void DismissToast(ToastItem? toast)
    {
        if (toast is not null)
        {
            _toasts.Remove(toast);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunToastActionAsync(ToastItem? toast)
    {
        // Remove başarısızsa bildirim zaten süresi dolup kalkmış ya da kullanılmıştır → çift "geri al" olmaz.
        if (toast is null || !_toasts.Remove(toast) || toast.Action is null)
        {
            return;
        }

        await toast.Action();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunToastSecondaryActionAsync(ToastItem? toast)
    {
        if (toast is null || !_toasts.Remove(toast) || toast.SecondaryAction is null)
        {
            return;
        }

        await toast.SecondaryAction();
    }

    private ToastItem ShowToast(string message, ToastKind kind, string? actionLabel = null, Func<Task>? action = null, TimeSpan? lifetime = null) =>
        _toasts.Add(new ToastItem
        {
            Message = message,
            Kind = kind,
            ActionLabel = actionLabel,
            Action = action,
            ExpiresAt = lifetime is { } span ? Now + span : null,
        });
}