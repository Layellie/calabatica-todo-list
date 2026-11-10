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
    private const int MaxTitleLength = 120;

    private const int MaxCategoryLength = 40;

    /// <summary>Tamamla/geri al sonrası satırlar kayar; bu süre içindeki ikinci tıklama yok sayılır.</summary>
    private const long ListMoveGuardMs = 350;

    private int _nextTaskNumber = 1;

    private long _lastListMoveTick = long.MinValue / 2;

    // ───────── Hızlı ekleme ─────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTaskCommand))]
    private string _newTaskTitle = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTaskCommand))]
    private string _newTaskHours = "1";

    [ObservableProperty]
    private string _newTaskCategory = string.Empty;

    [ObservableProperty]
    private LocalizedOption<TaskPriority> _selectedPriorityOption;

    /// <summary>Hızlı ekleme satırındaki komutların canlı önizlemesi ("→ #iş · YARIN 14:00").</summary>
    [ObservableProperty]
    private string _quickAddPreview = string.Empty;

    [ObservableProperty]
    private TaskEditorViewModel? _editor;

    // ═════════════════════════════ Görev komutları ═════════════════════════════

    private bool CanAddTask()
    {
        var parsed = QuickAddParser.Parse(NewTaskTitle, Today);
        return !string.IsNullOrWhiteSpace(parsed.Title)
            && (parsed.Hours is not null || InputParsing.TryParseHours(NewTaskHours, out _));
    }

    /// <summary>Hızlı ekleme: satırdaki #kategori, !, @tarih/@saat, *tekrar, ~süre komutları uygulanır.</summary>
    [RelayCommand(CanExecute = nameof(CanAddTask))]
    private async Task AddTaskAsync()
    {
        var parsed = QuickAddParser.Parse(NewTaskTitle, Today);
        if (string.IsNullOrWhiteSpace(parsed.Title))
        {
            return;
        }

        double hours;
        if (parsed.Hours is { } parsedHours)
        {
            hours = parsedHours;
        }
        else if (!InputParsing.TryParseHours(NewTaskHours, out hours))
        {
            return;
        }

        string title = parsed.Title.Trim();
        string category = (parsed.Category ?? NewTaskCategory).Trim();

        var task = new CyberTask
        {
            Number = _nextTaskNumber++,
            Title = title.Length > MaxTitleLength ? title[..MaxTitleLength] : title,
            Category = category.Length == 0 ? null : (category.Length > MaxCategoryLength ? category[..MaxCategoryLength] : category),
            Priority = parsed.Priority ?? SelectedPriorityOption?.Value ?? TaskPriority.Active,
            EstimatedHours = hours,
            DueAt = parsed.DueDate?.ToDateTime(parsed.DueTime ?? TimeOnly.MinValue),
            HasDueTime = parsed.DueTime is not null,
            Recurrence = parsed.Recurrence ?? Recurrence.None,
            RepeatDays = parsed.RepeatDays,
            CreatedAt = Now,
            SortOrder = NextSortOrder(),
        };

        UpdateDerivedState(task, Now);
        ActiveTasks.Add(task);
        NewTaskTitle = string.Empty; // kategori kutusu bilinçli korunur: aynı kategoriye art arda ekleme kolay olsun
        OnTasksChanged(TaskChange.Membership);

        PushUndo(_loc.Format("Undo_Add", task.Code), async () =>
        {
            if (!ActiveTasks.Contains(task))
            {
                return;
            }

            if (Focus.Task == task)
            {
                StopFocusCore(Now); // harcanan süre günlüğe işlenir, kaybolmaz
            }

            ActiveTasks.Remove(task);
            OnTasksChanged(TaskChange.Membership);
            await SaveAsync();
        });

        await SaveAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CompleteTaskAsync(CyberTask? task)
    {
        if (task is null || task.IsCompleted || !TryBeginListMove())
        {
            return;
        }

        double sortOrder = task.SortOrder;
        CompleteCore(task, Now);
        PushUndo(_loc.Format("Undo_Complete", task.Code), async () =>
        {
            if (!task.IsCompleted || !CompletedTasks.Contains(task))
            {
                return;
            }

            RestoreCore(task, sortOrder);
            await SaveAsync();
        });

        await SaveAsync();
    }

    /// <summary>Tamamlama: log satırı, güne saat, listeler arası taşıma, tekrar kopyası. Geri alma kaydı üretmez.</summary>
    private void CompleteCore(CyberTask task, DateTime now)
    {
        // Bu görevde odak çalışıyorsa önce geçen süre işlenir.
        if (Focus.Task == task)
        {
            StopFocusCore(now);
        }

        var day = GetOrCreateDay(DateOnly.FromDateTime(now));

        // Odak süresi kaydedilmişse o süre zaten güne işlendi; tahmini süre ayrıca eklenmez (çift sayım olmaz).
        double credit = task.TrackedMinutes > 0 ? 0 : task.EstimatedHours;
        task.CompletionCreditHours = credit;
        task.IsCompleted = true;
        task.CompletedAt = now;
        task.IsExpanded = false;

        InsertEntrySorted(day, new ActivityLogEntry
        {
            Time = now.ToString("HH:mm", CultureInfo.InvariantCulture),
            Description = $"{task.Code} {task.Title}",
            Status = LogStatus.Done,
            TaskId = task.Id,
        });
        day.HoursWorked = Math.Round(day.HoursWorked + credit, 2);
        day.CompletedCount++;

        ActiveTasks.Remove(task);
        CompletedTasks.Insert(0, task);

        // Tekrarlayan görev: bir sonraki kopya hemen aktif listeye eklenir.
        if (task.Recurrence != Recurrence.None)
        {
            var next = CreateNextOccurrence(task, now);
            task.SpawnedNextId = next.Id;
            UpdateDerivedState(next, now);
            ActiveTasks.Add(next);
            ShowToast(_loc.Format("Toast_Recurring", $"{next.Code} {next.DueText}"), ToastKind.Info, lifetime: s_infoLifetime);
        }

        OnTasksChanged(TaskChange.Membership);
        RebuildChart(); // ölçek (max saat) değişmiş olabilir
    }

    /// <summary>Tamamlanan görevi geri alır; log satırını, eklenen saati ve (varsa) üretilen tekrar kopyasını geri çeker.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RestoreTaskAsync(CyberTask? task)
    {
        if (task is null || !task.IsCompleted || !TryBeginListMove())
        {
            return;
        }

        RestoreCore(task, sortOrder: null);
        PushUndo(_loc.Format("Undo_Restore", task.Code), async () =>
        {
            if (task.IsCompleted || !ActiveTasks.Contains(task))
            {
                return;
            }

            CompleteCore(task, Now);
            await SaveAsync();
        });

        await SaveAsync();
    }

    /// <param name="sortOrder">null → listenin sonuna; değer → eski yerine (tamamlamayı geri alırken).</param>
    private void RestoreCore(CyberTask task, double? sortOrder)
    {
        if (task.CompletedAt is { } completedAt
            && _daysByDate.TryGetValue(DateOnly.FromDateTime(completedAt), out var day))
        {
            var entry = day.Entries.FirstOrDefault(e => e.TaskId == task.Id && e.Status == LogStatus.Done);
            if (entry is not null)
            {
                day.Entries.Remove(entry);
                day.HoursWorked = Math.Max(0, Math.Round(day.HoursWorked - task.CompletionCreditHours, 2));
                day.CompletedCount = Math.Max(0, day.CompletedCount - 1);
            }
        }

        // Tekrardan doğan kopya hâlâ aktifse kaldır (aksi halde çift görev oluşurdu).
        if (task.SpawnedNextId is { } spawnedId)
        {
            var spawned = ActiveTasks.FirstOrDefault(t => t.Id == spawnedId);
            if (spawned is not null)
            {
                if (Focus.Task == spawned)
                {
                    StopFocusCore(Now);
                }

                ActiveTasks.Remove(spawned);
            }

            task.SpawnedNextId = null;
        }

        task.CompletionCreditHours = 0;
        task.IsCompleted = false;
        task.CompletedAt = null;
        task.SortOrder = sortOrder ?? NextSortOrder();

        CompletedTasks.Remove(task);
        ActiveTasks.Add(task);

        OnTasksChanged(TaskChange.Membership);
        RebuildChart();
    }

    /// <summary>Siler; 6 sn boyunca "GERİ AL" (veya Ctrl+Z) ile aynı yerine geri konabilir. Log geçmişi korunur.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DeleteTaskAsync(CyberTask? task)
    {
        if (task is null)
        {
            return;
        }

        var list = task.IsCompleted ? CompletedTasks : ActiveTasks;
        int index = list.IndexOf(task);
        if (index < 0)
        {
            return;
        }

        if (Editor?.Target == task)
        {
            Editor = null;
        }

        if (Focus.Task == task)
        {
            StopFocusCore(Now); // harcanan süre kaybolmasın
        }

        list.RemoveAt(index);
        OnTasksChanged(TaskChange.Membership);
        await SaveAsync();

        var entry = PushUndo(_loc.Format("Undo_Delete", task.Code), async () =>
        {
            if (list.Contains(task))
            {
                return;
            }

            list.Insert(Math.Min(index, list.Count), task);
            OnTasksChanged(TaskChange.Membership);
            await SaveAsync();
        });

        entry.Toast = ShowToast(
            _loc.Format("Toast_Deleted", task.Code),
            ToastKind.Info,
            _loc["Toast_Undo"],
            () => RevertAsync(entry),
            s_undoLifetime);
    }

    [RelayCommand]
    private void EditTask(CyberTask? task)
    {
        if (task is null || task.IsCompleted)
        {
            return;
        }

        CloseOverlays();
        Editor = new TaskEditorViewModel(task, _time);
    }

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        var before = TaskMemento.Capture(editor.Target);
        if (!editor.TryApply())
        {
            return; // geçersizse diyalog açık kalır, hata metni gösterilir
        }

        var task = editor.Target;
        UpdateDerivedState(task, Now);
        Editor = null;

        OnTasksChanged(); // sıralama anahtarı / akıllı liste üyeliği değişmiş olabilir
        PushUndo(_loc.Format("Undo_Edit", task.Code), async () =>
        {
            if (!ActiveTasks.Contains(task))
            {
                return;
            }

            if (Editor?.Target == task)
            {
                Editor = null; // açık taslak eski değerlerin üzerine yazmasın
            }

            before.ApplyTo(task);
            UpdateDerivedState(task, Now);
            OnTasksChanged();
            await SaveAsync();
        });

        await SaveAsync();
    }

    [RelayCommand]
    private void CancelEdit() => Editor = null;

    [RelayCommand]
    private void ToggleExpand(CyberTask? task)
    {
        if (task is not null)
        {
            task.IsExpanded = !task.IsExpanded;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleSubtaskAsync(SubTask? subtask)
    {
        if (subtask is null)
        {
            return;
        }

        var owner = ActiveTasks.FirstOrDefault(t => t.Subtasks.Contains(subtask));
        if (owner is null)
        {
            return;
        }

        subtask.IsDone = !subtask.IsDone;
        UpdateSubtaskInfo(owner);
        await SaveAsync();
    }

    // ───────── Sürükle-bırak ─────────

    /// <summary>Sürükleme sırasında hedef satır işareti (tek satır; üst ya da alt çizgi).</summary>
    public void SetDropTarget(CyberTask? target, bool after = false)
    {
        foreach (var task in ActiveTasks)
        {
            bool isTarget = ReferenceEquals(task, target);
            if (task.IsDropTarget != isTarget)
            {
                task.IsDropTarget = isTarget;
            }

            bool isAfter = isTarget && after;
            if (task.IsDropAfter != isAfter)
            {
                task.IsDropAfter = isAfter;
            }
        }
    }

    /// <summary>
    /// Sürüklenen görevi hedefin önüne/arkasına (hedef null ise listenin sonuna) taşır. Mevcut görünür sıra esas alınır,
    /// tüm aktif görevler 0..n yeniden numaralanır ve sıralama "ELLE" moduna geçer.
    /// </summary>
    public async Task MoveTaskAsync(CyberTask dragged, CyberTask? target, bool after = false)
    {
        ArgumentNullException.ThrowIfNull(dragged);
        SetDropTarget(null);
        if (ReferenceEquals(dragged, target) || !ActiveTasks.Contains(dragged))
        {
            return;
        }

        var previousOrders = ActiveTasks.Select(static t => (Task: t, t.SortOrder)).ToList();
        var previousSort = SelectedSortOption;

        var comparer = new TaskComparer(SelectedSortOption?.Value ?? SortMode.Created);
        var ordered = ActiveTasks.OrderBy(t => t, Comparer<CyberTask>.Create((a, b) => comparer.Compare(a, b))).ToList();
        ordered.Remove(dragged);
        int index = target is null ? ordered.Count : Math.Max(0, ordered.IndexOf(target)) + (after ? 1 : 0);
        ordered.Insert(Math.Min(index, ordered.Count), dragged);

        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].SortOrder = i;
        }

        SelectedSortOption = OptionSets.Find(OptionSets.SortModes, SortMode.Manual); // CustomSort'u da ayarlar + kaydeder
        ActiveView.Refresh();

        PushUndo(_loc.Format("Undo_Move", dragged.Code), async () =>
        {
            foreach (var (task, sortOrder) in previousOrders)
            {
                task.SortOrder = sortOrder;
            }

            if (SelectedSortOption == previousSort)
            {
                ActiveView.Refresh(); // mod değişmediyse CustomSort yenilenmez → elle yenile
            }
            else
            {
                SelectedSortOption = previousSort;
            }

            await SaveAsync();
        });

        await SaveAsync();
    }

    private double NextSortOrder() => ActiveTasks.Count == 0 ? 0 : ActiveTasks.Max(static t => t.SortOrder) + 1;

    /// <summary>İlerleme çubuğuna tıklanınca çağrılır. %5 adımlara yuvarlar. Alt görev varsa yok sayılır.</summary>
    public async Task SetTaskProgressAsync(CyberTask task, double percent)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsCompleted || task.HasSubtasks || !double.IsFinite(percent))
        {
            return;
        }

        double snapped = Math.Clamp(Math.Round(percent / 5) * 5, 0, 100);
        if (snapped.Equals(task.Progress))
        {
            return;
        }

        task.Progress = snapped;
        await SaveAsync();
    }

    partial void OnNewTaskTitleChanged(string value) => UpdateQuickAddPreview();

    private void UpdateQuickAddPreview()
    {
        var parsed = QuickAddParser.Parse(NewTaskTitle, Today);
        if (!parsed.HasTokens)
        {
            QuickAddPreview = string.Empty;
            return;
        }

        var parts = new List<string>();
        if (parsed.Category is { } category)
        {
            parts.Add("#" + category);
        }

        if (parsed.Priority is not null)
        {
            parts.Add(_loc["Prio_High"]);
        }

        if (parsed.DueDate is { } date)
        {
            parts.Add("⏱ " + FormatDue(date, parsed.DueTime, Now));
        }

        if (parsed.Recurrence is { } recurrence)
        {
            parts.Add("↻ " + FormatRecurrence(recurrence, parsed.RepeatDays));
        }

        if (parsed.Hours is { } hours)
        {
            parts.Add($"~{InputParsing.FormatHours(hours)} {_loc["Unit_Hours"]}");
        }

        QuickAddPreview = _loc["Preview_Prefix"] + string.Join("  ·  ", parts);
    }

    private CyberTask CreateNextOccurrence(CyberTask source, DateTime now)
    {
        var basis = source.DueAt ?? now.Date;
        var next = RecurrenceRules.Advance(basis, source.Recurrence, source.RepeatDays);

        // Gecikmiş bir tekrarı tamamlamak, geçmiş tarihli yeni bir gecikmiş görev üretmesin.
        var probe = new CyberTask { DueAt = next, HasDueTime = source.HasDueTime };
        for (int guard = 0; guard < 1000 && GetEffectiveDue(probe) < now; guard++)
        {
            next = RecurrenceRules.Advance(next, source.Recurrence, source.RepeatDays);
            probe.DueAt = next;
        }

        source.SeriesId ??= source.Id; // zincirin ilk halkası: kimlik kendi Id'si
        return new CyberTask
        {
            SeriesId = source.SeriesId,
            Number = _nextTaskNumber++,
            Title = source.Title,
            Category = source.Category,
            Notes = source.Notes,
            Priority = source.Priority,
            EstimatedHours = source.EstimatedHours,
            DueAt = next,
            HasDueTime = source.HasDueTime,
            Recurrence = source.Recurrence,
            RepeatDays = source.RepeatDays,
            ReminderMinutesBefore = source.ReminderMinutesBefore,
            Subtasks = [.. source.Subtasks.Select(static s => new SubTask { Title = s.Title })],
            CreatedAt = now,
            SortOrder = source.SortOrder, // elle sıralamada aynı yerde kalsın
        };
    }

    /// <summary>
    /// Bir satır listeden çıkınca alttaki satırın kutusu imlecin altına kayar. Çift/hızlı tıklama
    /// istemeden ikinci görevi de tamamlamasın (veya geri almasın) diye kısa süreli kilit.
    /// </summary>
    private bool TryBeginListMove()
    {
        long now = Environment.TickCount64;
        if (now - _lastListMoveTick < ListMoveGuardMs)
        {
            return false;
        }

        _lastListMoveTick = now;
        return true;
    }

    private DayActivity GetOrCreateDay(DateOnly date)
    {
        if (!_daysByDate.TryGetValue(date, out var day))
        {
            day = new DayActivity { Date = date };
            _daysByDate[date] = day;
        }

        return day;
    }

    private static void InsertEntrySorted(DayActivity day, ActivityLogEntry entry)
    {
        int index = 0;
        while (index < day.Entries.Count && string.CompareOrdinal(day.Entries[index].Time, entry.Time) <= 0)
        {
            index++;
        }

        day.Entries.Insert(index, entry);
    }
}
