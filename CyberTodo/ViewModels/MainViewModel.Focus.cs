using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using CyberTodo.Models;

namespace CyberTodo.ViewModels;

/// <summary>Odak (Pomodoro) + mola: durum makinesi <see cref="FocusTimer"/>'da; burada kredi, bildirim ve kayıt.</summary>
public sealed partial class MainViewModel
{
    private const int MaxFocusSessions = 20_000; // ~yıllarca günde birkaç oturum; dosya şişmesin

    private readonly List<FocusSession> _focusSessions = [];

    /// <summary>Odak ve mola durumu (XAML: <c>Focus.IsRunning</c>, <c>Focus.RemainingText</c>…).</summary>
    public FocusTimer Focus { get; } = new();

    /// <summary>Görevde odak başlatır; aynı görevde çalışıyorsa durdurur. Başka görevde çalışan oturum önce kaydedilir.</summary>
    [RelayCommand]
    private async Task StartFocusAsync(CyberTask? task)
    {
        if (task is null || task.IsCompleted)
        {
            return;
        }

        var now = Now;
        bool sameTask = Focus.Task == task;
        bool credited = StopFocusCore(now);
        if (!sameTask)
        {
            Focus.Start(task, now, TimeSpan.FromMinutes(_settings.FocusMinutes)); // molayı da iptal eder
        }

        if (credited)
        {
            await SaveAsync();
        }
    }

    [RelayCommand]
    private async Task StopFocusAsync()
    {
        if (StopFocusCore(Now))
        {
            await SaveAsync();
        }
    }

    /// <summary>Molayı atla: hemen biter (otomatik devam ayarı açıksa odak başlar).</summary>
    [RelayCommand]
    private void SkipBreak()
    {
        if (Focus.IsOnBreak)
        {
            ResumeAfterBreak(Focus.SkipBreak(), Now, notify: false);
        }
    }

    /// <summary>Oturumu kapatır; en az 1 dakika geçtiyse süreyi göreve ve güne işler. İşlendiyse true.</summary>
    private bool StopFocusCore(DateTime now)
    {
        if (Focus.Stop(now) is not { Minutes: >= 1 } stop)
        {
            return false;
        }

        CreditFocus(stop.Task, stop.Minutes, now);
        return true;
    }

    /// <summary>Saatin odak/mola olaylarını işler (saniyelik tick'ten).</summary>
    private void HandleFocusEvent(FocusEvent focusEvent, DateTime now)
    {
        var task = focusEvent.Task;
        if (focusEvent.Kind == FocusEventKind.BreakEnded)
        {
            ResumeAfterBreak(task, now, notify: true);
            return;
        }

        CreditFocus(task, focusEvent.Minutes, now);
        string message = _loc.Format("Toast_FocusDone", $"{task.Code} {task.Title}", FormatMinutes(focusEvent.Minutes));
        ShowToast(message, ToastKind.Reminder);
        AttentionRequested?.Invoke(this, message);
        _ = SaveAsync();
    }

    /// <summary>Mola bitti: ayar açıksa ve görev hâlâ aktifse aynı görevde odak başlar.</summary>
    private void ResumeAfterBreak(CyberTask? task, DateTime now, bool notify)
    {
        bool resume = _settings.AutoStartNextFocus && task is { IsCompleted: false } && ActiveTasks.Contains(task) && !Focus.IsRunning;
        if (notify)
        {
            string message = resume ? _loc.Format("Toast_BreakOverResume", $"{task!.Code} {task.Title}") : _loc["Toast_BreakOver"];
            ShowToast(message, ToastKind.Reminder, lifetime: resume ? s_infoLifetime : null);
            AttentionRequested?.Invoke(this, message);
        }

        if (resume)
        {
            Focus.Start(task!, now, TimeSpan.FromMinutes(_settings.FocusMinutes));
        }
    }

    private void CreditFocus(CyberTask task, int minutes, DateTime now)
    {
        task.TrackedMinutes += minutes;
        RefreshTrackedText(task);

        _focusSessions.Add(new FocusSession
        {
            Start = now.AddMinutes(-minutes),
            Minutes = minutes,
            TaskId = task.Id,
            TaskNumber = task.Number,
            Title = task.Title,
            Category = string.IsNullOrWhiteSpace(task.Category) ? null : task.Category.Trim(),
        });
        if (_focusSessions.Count > MaxFocusSessions)
        {
            _focusSessions.RemoveRange(0, _focusSessions.Count - MaxFocusSessions); // en eskiler
        }

        var day = GetOrCreateDay(DateOnly.FromDateTime(now));
        day.HoursWorked = Math.Round(day.HoursWorked + minutes / 60.0, 2);

        // TaskId bilinçli boş: geri alma yalnızca "tamamlandı" satırını aramalı, odak kayıtları tarihtir.
        InsertEntrySorted(day, new ActivityLogEntry
        {
            Time = now.ToString("HH:mm", CultureInfo.InvariantCulture),
            Description = $"{task.Code} {task.Title} · {FormatMinutes(minutes)}",
            Status = LogStatus.Focus,
        });

        RebuildChart();
    }
}
