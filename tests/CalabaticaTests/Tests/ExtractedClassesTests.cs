using CyberTodo.Models;
using CyberTodo.ViewModels;

namespace CalabaticaTests;

/// <summary>ViewModel'den ayrılan UndoStack, ToastCenter ve ReminderScheduler'ın arayüzsüz birim testleri.</summary>
internal static class ExtractedClassesTests
{
    public static async Task Run(TestContext ctx)
    {
        // ── UndoStack ──
        var undo = new UndoStack(capacity: 3);
        int changes = 0;
        undo.Changed += () => changes++;
        var log = new List<string>();
        var a = undo.Push("A", () => { log.Add("A"); return Task.CompletedTask; });
        var b = undo.Push("B", () => { log.Add("B"); return Task.CompletedTask; });
        ctx.Check(undo.CanUndo && undo.Count == 2 && undo.Last == b && changes == 2, "undo stack: push order, Last, change notifications");
        ctx.Check(undo.Remove(b) && undo.Last == a && !undo.Remove(b), "undo stack: removing twice is refused (no double undo)");
        undo.Push("C", () => Task.CompletedTask);
        undo.Push("D", () => Task.CompletedTask);
        var e = undo.Push("E", () => Task.CompletedTask);
        ctx.Check(undo.Count == 3 && !undo.Remove(a) && undo.Last == e, "undo stack: capacity drops the oldest entry");
        changes = 0;
        var cleared = undo.Clear();
        ctx.Check(cleared.Count == 3 && !undo.CanUndo && undo.Last is null && changes == 1 && undo.Clear().Count == 0 && changes == 1,
            "undo stack: clear returns removed entries, notifies once, empty clear is silent");
        await a.Revert();
        ctx.Check(log.SequenceEqual(["A"]), "undo stack: entry revert callback is kept");

        // ── ToastCenter ──
        var center = new ToastCenter(maxToasts: 3);
        var t0 = new DateTime(2026, 11, 10, 12, 0, 0);
        var keep = center.Add(new ToastItem { Message = "keep" });
        var soon = center.Add(new ToastItem { Message = "soon", ExpiresAt = t0.AddSeconds(4) });
        var later = center.Add(new ToastItem { Message = "later", ExpiresAt = t0.AddSeconds(10) });
        ctx.Check(center.RemoveExpired(t0.AddSeconds(3)) == 0 && center.Items.Count == 3, "toast center: nothing expires early");
        ctx.Check(center.RemoveExpired(t0.AddSeconds(4)) == 1 && !center.Items.Contains(soon) && center.Items.Contains(keep), "toast center: expires exactly at the deadline, no-expiry toasts stay");
        center.Add(new ToastItem { Message = "x" });
        center.Add(new ToastItem { Message = "y" });
        ctx.Check(center.Items.Count == 3 && !center.Items.Contains(keep) && center.Items.Contains(later), "toast center: capacity drops the oldest");
        ctx.Check(center.Remove(later) && !center.Remove(later), "toast center: second remove returns false");
        bool threw = false;
        try { _ = new ToastCenter(0); } catch (ArgumentOutOfRangeException) { threw = true; }
        ctx.Check(threw, "toast center: capacity must be positive");

        // ── ReminderScheduler ──
        var now = new DateTime(2026, 11, 10, 10, 0, 0);
        CyberTask MakeTask(string title, DateTime? due, bool hasTime, int? reminder) => new() { Title = title, DueAt = due, HasDueTime = hasTime, ReminderMinutesBefore = reminder };
        var onTime = MakeTask("on time", now.AddMinutes(-1), true, 0);
        var early = MakeTask("15 min before", now.AddMinutes(10), true, 15);
        var notYet = MakeTask("not yet", now.AddHours(2), true, 15);
        var dateOnly = MakeTask("date only", now.Date, false, 0);          // saatsiz: o gün 09:00
        var noReminder = MakeTask("no reminder", now.AddMinutes(-5), true, null);
        var noDue = MakeTask("no due", null, false, 0);
        var already = MakeTask("already fired", now.AddMinutes(-30), true, 0); already.ReminderFired = true;
        var due = ReminderScheduler.CollectDue([onTime, early, notYet, dateOnly, noReminder, noDue, already], now);
        ctx.Check(due.SequenceEqual([onTime, early, dateOnly]), "reminders: due = on time, within lead time, date-only after 09:00: " + string.Join(",", due.Select(x => x.Title)));
        ctx.Check(due.All(x => x.ReminderFired) && !notYet.ReminderFired && !noReminder.ReminderFired, "reminders: flags set only on the ones shown");
        ctx.Check(ReminderScheduler.CollectDue([onTime, early, dateOnly], now.AddMinutes(1)).Count == 0, "reminders: a shown reminder never repeats");
        ctx.Check(ReminderScheduler.GetReminderMoment(dateOnly, 60) == now.Date.AddHours(8), "reminders: date-only tasks use 09:00 as the base");

        var snoozed = MakeTask("snoozed", now.AddMinutes(-20), true, 0); snoozed.ReminderFired = true; snoozed.SnoozedUntil = now.AddMinutes(10);
        ctx.Check(ReminderScheduler.CollectDue([snoozed], now.AddMinutes(9)).Count == 0 && snoozed.SnoozedUntil is not null, "reminders: snoozed task waits");
        var after = ReminderScheduler.CollectDue([snoozed], now.AddMinutes(10));
        ctx.Check(after.Single() == snoozed && snoozed.SnoozedUntil is null && snoozed.ReminderFired, "reminders: snooze ends exactly on time and clears itself");
        bool noDueThrows = false;
        try { ReminderScheduler.GetReminderMoment(noDue, 0); } catch (InvalidOperationException) { noDueThrows = true; }
        ctx.Check(noDueThrows, "reminders: moment of a task without due date is an error, not a crash");
    }
}