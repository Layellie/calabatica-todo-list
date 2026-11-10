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
    private readonly DispatcherTimer _clock;

    private int _lastClockMinute = -1;

    private DateOnly _viewsDay;

    private void OnClockTick(object? sender, EventArgs e) => Tick(DateTime.Now);

    /// <summary>Saniyelik saat işi. Testler zamanı ileri sarmak için doğrudan çağırır.</summary>
    internal void Tick(DateTime now)
    {
        for (int i = Toasts.Count - 1; i >= 0; i--)
        {
            if (Toasts[i].ExpiresAt is { } expires && now >= expires)
            {
                Toasts.RemoveAt(i);
            }
        }

        if (FocusTask is { } focused)
        {
            if (now - _focusStartedAt >= _focusDuration)
            {
                int minutes = (int)Math.Round(_focusDuration.TotalMinutes);
                StopFocusCore(now);
                _completedFocusCount++;
                string message = _loc.Format("Toast_FocusDone", $"{focused.Code} {focused.Title}", FormatMinutes(minutes));
                ShowToast(message, ToastKind.Reminder);
                AttentionRequested?.Invoke(this, message);
                _ = SaveAsync();

                if (_settings.BreaksEnabled)
                {
                    StartBreak(focused, now);
                }
            }
            else
            {
                UpdateFocusDisplay(now);
            }
        }
        else if (IsOnBreak)
        {
            if (now >= _breakEndsAt)
            {
                EndBreak(now, notify: true);
            }
            else
            {
                UpdateBreakDisplay(now);
            }
        }

        CheckReminders(now);

        if (now.Minute != _lastClockMinute)
        {
            _lastClockMinute = now.Minute;
            RefreshTimeDependentState(now);
        }

        if (Today != _chartAnchorDate)
        {
            RebuildChart(); // gece yarısı geçildi
        }
    }

    /// <summary>
    /// Dakikalık saat işi: son tarih metinlerini/durumlarını günceller; görünümleri yalnızca bir görevin
    /// <see cref="DueState"/>'i veya takvim günü değiştiyse yeniler. Koşulsuz <c>Refresh()</c> her dakika tüm
    /// satırları yeniden kuruyordu; boşta, JIT'in bu yolları dakikalarca parça parça yeniden derlemesine
    /// (tier-up/PGO) ve küçük listede bile ~1–2 sn tek çekirdek CPU sıçramalarına yol açıyordu.
    /// </summary>
    /// <returns>Görünümler yenilendiyse <see langword="true"/>.</returns>
    internal bool RefreshTimeDependentState(DateTime now)
    {
        bool stateChanged = false;
        foreach (var task in ActiveTasks)
        {
            var before = task.DueState;
            RefreshDueInfo(task, now); // metin bağlama üzerinden güncellenir, liste yenilemesi gerektirmez
            stateChanged |= task.DueState != before; // "BUGÜN" → gecikmiş: akıllı liste üyeliği değişir
        }

        // "Yaklaşan" listesi bugüne göre hesaplanır: gün dönünce üyelik değişebilir.
        var today = DateOnly.FromDateTime(now);
        if (today != _viewsDay)
        {
            _viewsDay = today;
            stateChanged = true;
            _ = CreateDailyBackupAsync(now); // tepside günlerce açık kalsa da her gün bir yedek
        }

        if (stateChanged)
        {
            RefreshViews();
            UpdateQuickAddPreview(); // "@yarın" önizlemesi güne bağlı
        }

        return stateChanged;
    }

    /// <summary>Saat girilmemişse hatırlatma tabanı o gün 09:00.</summary>
    private static DateTime GetReminderMoment(CyberTask task, int minutesBefore)
    {
        var due = task.DueAt!.Value;
        var basis = task.HasDueTime ? due : due.Date.AddHours(9);
        return basis.AddMinutes(-minutesBefore);
    }

    private void CheckReminders(DateTime now)
    {
        bool changed = false;
        foreach (var task in ActiveTasks)
        {
            if (task.ReminderMinutesBefore is not { } minutes || task.DueAt is null)
            {
                continue;
            }

            if (task.SnoozedUntil is { } until)
            {
                if (now < until)
                {
                    continue;
                }

                task.SnoozedUntil = null; // ertelenen hatırlatma zamanı geldi
            }
            else if (task.ReminderFired || now < GetReminderMoment(task, minutes))
            {
                continue;
            }

            task.ReminderFired = true;
            changed = true;

            string message = _loc.Format("Toast_Reminder", task.Code, task.Title);
            if (task.DueText.Length > 0)
            {
                message += $" ({task.DueText})";
            }

            // Kullanıcı kapatana kadar kalır; 10 dk / 1 sa erteleme düğmeleri.
            Toasts.Add(new ToastItem
            {
                Message = message,
                Kind = ToastKind.Reminder,
                ActionLabel = _loc["Snooze_10"],
                Action = () => SnoozeAsync(task, 10),
                SecondaryActionLabel = _loc["Snooze_60"],
                SecondaryAction = () => SnoozeAsync(task, 60),
            });
            while (Toasts.Count > MaxToasts)
            {
                Toasts.RemoveAt(0);
            }

            AttentionRequested?.Invoke(this, message);
        }

        if (changed)
        {
            _ = SaveAsync();
        }
    }

    /// <summary>Hatırlatmayı <paramref name="minutes"/> dakika sonra yeniden gösterir (görev hâlâ aktifse).</summary>
    private async Task SnoozeAsync(CyberTask task, int minutes)
    {
        if (!ActiveTasks.Contains(task) || task.ReminderMinutesBefore is null)
        {
            return;
        }

        var until = DateTime.Now.AddMinutes(minutes);
        task.SnoozedUntil = until;
        ShowToast(_loc.Format("Toast_Snoozed", task.Code, until.ToString("HH:mm", CultureInfo.InvariantCulture)), ToastKind.Info, lifetime: s_infoLifetime);
        await SaveAsync();
    }
}
