using CommunityToolkit.Mvvm.ComponentModel;
using CyberTodo.Models;

namespace CyberTodo.ViewModels;

public enum FocusEventKind
{
    /// <summary>Odak süresi doldu (Minutes = tam süre).</summary>
    FocusCompleted,

    /// <summary>Mola süresi doldu (Task = moladan önceki görev).</summary>
    BreakEnded,
}

/// <summary>Saat ilerledikçe olan şey. Kayıt, bildirim ve dil işleri çağırana (ViewModel) aittir.</summary>
public sealed record FocusEvent(FocusEventKind Kind, CyberTask Task, int Minutes);

/// <summary>Durdurulan oturumun sonucu: en az 1 tam dakika geçtiyse kredilendirilecek süre.</summary>
public sealed record FocusStop(CyberTask Task, int Minutes);

/// <summary>
/// Odak (Pomodoro) + mola durum makinesi. Saat dışarıdan verilir (deterministik test), kalıcılık/UI metni yok.
/// Durumlar: boşta → odak → (süre dolar) → mola (ayar açıksa) → boşta. Elle başlatılan odak molayı iptal eder.
/// </summary>
public sealed partial class FocusTimer : ObservableObject
{
    public const int ShortBreakMinutes = 5;
    public const int LongBreakMinutes = 15;
    public const int FocusesPerLongBreak = 4;

    private DateTime _startedAt;
    private TimeSpan _duration;
    private DateTime _breakStartedAt;
    private DateTime _breakEndsAt;
    private int _completedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    private CyberTask? _task;

    [ObservableProperty]
    private string _remainingText = string.Empty;

    /// <summary>0..1 geçen süre oranı.</summary>
    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isOnBreak;

    [ObservableProperty]
    private bool _isLongBreak;

    [ObservableProperty]
    private string _breakRemainingText = string.Empty;

    [ObservableProperty]
    private double _breakProgress;

    public bool IsRunning => Task is not null;

    /// <summary>Moladan önce odaklanılan görev (otomatik devam için).</summary>
    public CyberTask? BreakTask { get; private set; }

    public void Start(CyberTask task, DateTime now, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Focus duration must be positive.");
        }

        CancelBreak();
        if (Task is { } previous)
        {
            previous.IsFocused = false;
        }

        _startedAt = now;
        _duration = duration;
        task.IsFocused = true;
        Task = task;
        UpdateFocusDisplay(now);
    }

    /// <summary>Oturumu kapatır. Geçen süre (en fazla oturum süresi) tam dakikaya aşağı yuvarlanır; çalışmıyorsa null.</summary>
    public FocusStop? Stop(DateTime now)
    {
        if (Task is not { } task)
        {
            return null;
        }

        var elapsed = now - _startedAt;
        if (elapsed > _duration)
        {
            elapsed = _duration;
        }

        task.IsFocused = false;
        Task = null;
        RemainingText = string.Empty;
        Progress = 0;
        return new FocusStop(task, Math.Max(0, (int)Math.Floor(elapsed.TotalMinutes)));
    }

    /// <summary>Saati ilerletir; odak ya da mola bittiyse olayı döndürür, aksi halde göstergeleri günceller.</summary>
    public FocusEvent? Tick(DateTime now, bool breaksEnabled)
    {
        if (Task is { } task)
        {
            if (now - _startedAt < _duration)
            {
                UpdateFocusDisplay(now);
                return null;
            }

            int minutes = (int)Math.Round(_duration.TotalMinutes);
            Stop(now);
            _completedCount++;
            if (breaksEnabled)
            {
                StartBreak(task, now);
            }

            return new FocusEvent(FocusEventKind.FocusCompleted, task, minutes);
        }

        if (!IsOnBreak)
        {
            return null;
        }

        if (now < _breakEndsAt)
        {
            UpdateBreakDisplay(now);
            return null;
        }

        var breakTask = BreakTask!;
        CancelBreak();
        return new FocusEvent(FocusEventKind.BreakEnded, breakTask, 0);
    }

    /// <summary>Molayı hemen bitirir; moladan önceki görevi döndürür (mola yoksa null).</summary>
    public CyberTask? SkipBreak()
    {
        var task = BreakTask;
        CancelBreak();
        return task;
    }

    public void CancelBreak()
    {
        if (!IsOnBreak)
        {
            return;
        }

        IsOnBreak = false;
        IsLongBreak = false;
        BreakTask = null;
        BreakRemainingText = string.Empty;
        BreakProgress = 0;
    }

    /// <summary>Her şeyi bırakır, süre kredilendirilmez (veri değiştirildiğinde eski veriye ait oturum).</summary>
    public void Reset()
    {
        if (Task is { } task)
        {
            task.IsFocused = false;
        }

        Task = null;
        RemainingText = string.Empty;
        Progress = 0;
        CancelBreak();
        _completedCount = 0;
    }

    private void StartBreak(CyberTask task, DateTime now)
    {
        IsLongBreak = _completedCount % FocusesPerLongBreak == 0;
        BreakTask = task;
        _breakStartedAt = now;
        _breakEndsAt = now.AddMinutes(IsLongBreak ? LongBreakMinutes : ShortBreakMinutes);
        IsOnBreak = true;
        UpdateBreakDisplay(now);
    }

    private void UpdateFocusDisplay(DateTime now) =>
        (RemainingText, Progress) = Countdown(_startedAt, _startedAt + _duration, now);

    private void UpdateBreakDisplay(DateTime now) =>
        (BreakRemainingText, BreakProgress) = Countdown(_breakStartedAt, _breakEndsAt, now);

    private static (string Text, double Progress) Countdown(DateTime start, DateTime end, DateTime now)
    {
        var total = end - start;
        var remaining = end - now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        string text = $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
        double progress = total.TotalSeconds <= 0 ? 1 : Math.Clamp(1 - remaining.TotalSeconds / total.TotalSeconds, 0, 1);
        return (text, progress);
    }
}
