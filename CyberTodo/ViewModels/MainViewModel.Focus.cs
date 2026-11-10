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
    private const int MaxFocusSessions = 20_000; // ~yıllarca günde birkaç oturum; dosya şişmesin

    private readonly List<FocusSession> _focusSessions = [];

    private DateTime _focusStartedAt;

    private TimeSpan _focusDuration;

    // ───────── Odak (Pomodoro) ─────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFocusRunning))]
    private CyberTask? _focusTask;

    [ObservableProperty]
    private string _focusRemainingText = string.Empty;

    /// <summary>0..1 geçen süre oranı (başlıktaki ince çubuk).</summary>
    [ObservableProperty]
    private double _focusProgress;

    public bool IsFocusRunning => FocusTask is not null;

    /// <summary>Görevde odak başlatır; aynı görevde çalışıyorsa durdurur. Başka görevde çalışan oturum önce kaydedilir.</summary>
    [RelayCommand]
    private async Task StartFocusAsync(CyberTask? task)
    {
        if (task is null || task.IsCompleted)
        {
            return;
        }

        var now = DateTime.Now;
        bool credited = false;
        if (FocusTask is not null)
        {
            bool sameTask = FocusTask == task;
            credited = StopFocusCore(now);
            if (sameTask)
            {
                if (credited)
                {
                    await SaveAsync();
                }

                return;
            }
        }

        CancelBreak(); // molada elle odak başlatıldı
        BeginFocus(task, now);

        if (credited)
        {
            await SaveAsync();
        }
    }

    [RelayCommand]
    private async Task StopFocusAsync()
    {
        if (StopFocusCore(DateTime.Now))
        {
            await SaveAsync();
        }
    }

    /// <summary>Test için: odak oturumunu verilen anda bitirir (gerçek dakikalarca beklemeden).</summary>
    internal bool StopFocusAt(DateTime now) => StopFocusCore(now);

    /// <summary>Oturumu kapatır; en az 1 dakika geçtiyse süreyi göreve ve güne işler. İşlendiyse true.</summary>
    private bool StopFocusCore(DateTime now)
    {
        if (FocusTask is not { } task)
        {
            return false;
        }

        var elapsed = now - _focusStartedAt;
        if (elapsed > _focusDuration)
        {
            elapsed = _focusDuration;
        }

        task.IsFocused = false;
        FocusTask = null;
        FocusRemainingText = string.Empty;
        FocusProgress = 0;

        int minutes = (int)Math.Floor(elapsed.TotalMinutes);
        if (minutes < 1)
        {
            return false;
        }

        CreditFocus(task, minutes, now);
        return true;
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

    private void BeginFocus(CyberTask task, DateTime now)
    {
        _focusStartedAt = now;
        _focusDuration = TimeSpan.FromMinutes(_settings.FocusMinutes);
        task.IsFocused = true;
        FocusTask = task;
        UpdateFocusDisplay(now);
    }

    // ───────── Mola ─────────

    public const int ShortBreakMinutes = 5;

    public const int LongBreakMinutes = 15;

    public const int FocusesPerLongBreak = 4;

    private DateTime _breakStartedAt;

    private DateTime _breakEndsAt;

    private CyberTask? _breakTask;

    private int _completedFocusCount;

    [ObservableProperty]
    private bool _isOnBreak;

    [ObservableProperty]
    private bool _isLongBreak;

    [ObservableProperty]
    private string _breakRemainingText = string.Empty;

    [ObservableProperty]
    private double _breakProgress;

    /// <summary>Odak bitti: kısa mola; her <see cref="FocusesPerLongBreak"/>. odaktan sonra uzun mola.</summary>
    private void StartBreak(CyberTask task, DateTime now)
    {
        IsLongBreak = _completedFocusCount > 0 && _completedFocusCount % FocusesPerLongBreak == 0;
        _breakTask = task;
        _breakStartedAt = now;
        _breakEndsAt = now.AddMinutes(IsLongBreak ? LongBreakMinutes : ShortBreakMinutes);
        IsOnBreak = true;
        UpdateBreakDisplay(now);
    }

    /// <summary>Mola bitti (süre doldu ya da atlandı). Ayar açıksa ve görev hâlâ aktifse aynı görevde odak başlar.</summary>
    private void EndBreak(DateTime now, bool notify)
    {
        var task = _breakTask;
        CancelBreak();

        bool resume = _settings.AutoStartNextFocus && task is not null && !task.IsCompleted && ActiveTasks.Contains(task) && FocusTask is null;
        if (notify)
        {
            string message = resume ? _loc.Format("Toast_BreakOverResume", $"{task!.Code} {task.Title}") : _loc["Toast_BreakOver"];
            ShowToast(message, ToastKind.Reminder, lifetime: resume ? s_infoLifetime : null);
            AttentionRequested?.Invoke(this, message);
        }

        if (resume)
        {
            BeginFocus(task!, now);
        }
    }

    private void CancelBreak()
    {
        if (!IsOnBreak)
        {
            return;
        }

        IsOnBreak = false;
        IsLongBreak = false;
        _breakTask = null;
        BreakRemainingText = string.Empty;
        BreakProgress = 0;
    }

    /// <summary>Molayı atla: hemen biter (otomatik devam ayarı açıksa odak başlar).</summary>
    [RelayCommand]
    private void SkipBreak()
    {
        if (IsOnBreak)
        {
            EndBreak(DateTime.Now, notify: false);
        }
    }

    private void UpdateBreakDisplay(DateTime now)
    {
        var total = _breakEndsAt - _breakStartedAt;
        var remaining = _breakEndsAt - now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        BreakRemainingText = $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
        BreakProgress = total.TotalSeconds <= 0 ? 1 : Math.Clamp(1 - remaining.TotalSeconds / total.TotalSeconds, 0, 1);
    }

    private void UpdateFocusDisplay(DateTime now)
    {
        var remaining = _focusDuration - (now - _focusStartedAt);
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        FocusRemainingText = $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
        FocusProgress = _focusDuration.TotalSeconds <= 0 ? 1 : Math.Clamp(1 - remaining.TotalSeconds / _focusDuration.TotalSeconds, 0, 1);
    }
}
