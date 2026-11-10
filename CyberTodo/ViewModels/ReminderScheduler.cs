using CyberTodo.Models;

namespace CyberTodo.ViewModels;

/// <summary>
/// Hatırlatma zamanlaması (saf mantık, UI yok). Bir görev şu durumlarda "vakti geldi" sayılır:
///  - ertelenmişse (<see cref="CyberTask.SnoozedUntil"/>) erteleme bitince (erteleme bitince temizlenir),
///  - ertelenmemişse, henüz gösterilmediyse (<see cref="CyberTask.ReminderFired"/> = false) ve hatırlatma anı geldiyse.
/// Vakti gelen görevin <c>ReminderFired</c> bayrağı true yapılır, yani aynı hatırlatma ikinci kez çıkmaz.
/// </summary>
internal static class ReminderScheduler
{
    /// <summary>Saat girilmemişse hatırlatma tabanı o gün 09:00.</summary>
    public static DateTime GetReminderMoment(CyberTask task, int minutesBefore)
    {
        ArgumentNullException.ThrowIfNull(task);
        var due = task.DueAt ?? throw new InvalidOperationException("Task has no due date.");
        var basis = task.HasDueTime ? due : due.Date.AddHours(9);
        return basis.AddMinutes(-minutesBefore);
    }

    /// <summary>Vakti gelen görevleri döndürür ve bayraklarını günceller (durum değişikliği çağıranın kaydedeceği şeydir).</summary>
    public static List<CyberTask> CollectDue(IEnumerable<CyberTask> activeTasks, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(activeTasks);
        var due = new List<CyberTask>();
        foreach (var task in activeTasks)
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
            due.Add(task);
        }

        return due;
    }
}
