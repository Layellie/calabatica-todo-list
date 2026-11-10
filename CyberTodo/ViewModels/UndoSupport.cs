using CyberTodo.Models;

namespace CyberTodo.ViewModels;

/// <summary>Geri alma yığınındaki tek işlem. Revert, işlemi tersine çevirir ve yeni geri alma kaydı üretmez.</summary>
internal sealed class UndoEntry(string message, Func<Task> revert)
{
    public string Message { get; } = message;

    public Func<Task> Revert { get; } = revert;

    /// <summary>İşleme ait "GERİ AL" bildirimi (varsa); Ctrl+Z ile geri alınınca bildirim de kaldırılır.</summary>
    public ToastItem? Toast { get; set; }
}

/// <summary>Düzenleyicinin değiştirebildiği alanların anlık görüntüsü (düzenlemeyi geri almak için).</summary>
internal sealed record TaskMemento(
    string Title,
    string? Notes,
    string? Category,
    double EstimatedHours,
    TaskPriority Priority,
    DateTime? DueAt,
    bool HasDueTime,
    Recurrence Recurrence,
    WeekDays RepeatDays,
    int? ReminderMinutesBefore,
    bool ReminderFired,
    double Progress,
    IReadOnlyList<(string Title, bool IsDone)> Subtasks)
{
    public static TaskMemento Capture(CyberTask task) => new(
        task.Title, task.Notes, task.Category, task.EstimatedHours, task.Priority, task.DueAt, task.HasDueTime,
        task.Recurrence, task.RepeatDays, task.ReminderMinutesBefore, task.ReminderFired, task.Progress,
        [.. task.Subtasks.Select(static s => (s.Title, s.IsDone))]);

    public void ApplyTo(CyberTask task)
    {
        task.Title = Title;
        task.Notes = Notes;
        task.Category = Category;
        task.EstimatedHours = EstimatedHours;
        task.Priority = Priority;
        task.DueAt = DueAt;
        task.HasDueTime = HasDueTime;
        task.Recurrence = Recurrence;
        task.RepeatDays = RepeatDays;
        task.ReminderMinutesBefore = ReminderMinutesBefore;
        task.ReminderFired = ReminderFired;
        task.Progress = Progress;
        task.Subtasks.Clear();
        foreach (var (title, isDone) in Subtasks)
        {
            task.Subtasks.Add(new SubTask { Title = title, IsDone = isDone });
        }
    }
}
