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
    private const int MaxToasts = 4;

    private static readonly TimeSpan s_infoLifetime = TimeSpan.FromSeconds(4);

    public ObservableCollection<ToastItem> Toasts { get; } = [];

    [RelayCommand]
    private void DismissToast(ToastItem? toast)
    {
        if (toast is not null)
        {
            Toasts.Remove(toast);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunToastActionAsync(ToastItem? toast)
    {
        // Remove başarısızsa bildirim zaten süresi dolup kalkmış ya da kullanılmıştır → çift "geri al" olmaz.
        if (toast is null || !Toasts.Remove(toast) || toast.Action is null)
        {
            return;
        }

        await toast.Action();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunToastSecondaryActionAsync(ToastItem? toast)
    {
        if (toast is null || !Toasts.Remove(toast) || toast.SecondaryAction is null)
        {
            return;
        }

        await toast.SecondaryAction();
    }

    private ToastItem ShowToast(string message, ToastKind kind, string? actionLabel = null, Func<Task>? action = null, TimeSpan? lifetime = null)
    {
        var toast = new ToastItem
        {
            Message = message,
            Kind = kind,
            ActionLabel = actionLabel,
            Action = action,
            ExpiresAt = lifetime is { } span ? DateTime.Now + span : null,
        };
        Toasts.Add(toast);

        while (Toasts.Count > MaxToasts)
        {
            Toasts.RemoveAt(0);
        }

        return toast;
    }
}
