using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.ViewModels;

namespace CalabaticaTests;

/// <summary>
/// TaskEditorViewModel.TryApply karakterizasyon testi (bölünmeden önce yazıldı): doğrulama sırası ve hata mesajları,
/// kırpma sınırları, tekrar → otomatik tarih, hatırlatma durumunun sıfırlanması, başarısızlıkta görevin değişmemesi.
/// </summary>
internal static class TaskEditorApplyTests
{
    public static Task Run(TestContext ctx)
    {
        var loc = Localizer.Instance;
        var clock = ctx.Clock;
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

        CyberTask Original() => new()
        {
            Number = 1, Title = "orig", Notes = "old", Category = "cat", EstimatedHours = 2, Priority = TaskPriority.Decrypting,
            DueAt = today.AddDays(3).ToDateTime(new TimeOnly(9, 0)), HasDueTime = true, ReminderMinutesBefore = 60,
            ReminderFired = true, SnoozedUntil = DateTime.Now, Subtasks = [new SubTask { Title = "s1", IsDone = true }],
        };

        TaskEditorViewModel Editor(CyberTask task) => new(task, clock);

        // Hata durumu: görev DEĞİŞMEMELİ, ValidationError beklenen anahtardaki metin olmalı.
        void Fails(string name, string errorKey, Action<TaskEditorViewModel> edit)
        {
            var task = Original();
            var editor = Editor(task);
            edit(editor);
            bool applied = editor.TryApply();
            bool untouched = task.Title == "orig" && task.Notes == "old" && task.Category == "cat" && task.EstimatedHours == 2
                && task.Priority == TaskPriority.Decrypting && task.HasDueTime && task.ReminderMinutesBefore == 60 && task.ReminderFired
                && task.Subtasks.Count == 1 && task.DueAt == today.AddDays(3).ToDateTime(new TimeOnly(9, 0));
            ctx.Check(!applied && editor.ValidationError == loc[errorKey] && untouched, $"editor: {name} -> {errorKey}, task untouched");
        }

        static void SelectDays(TaskEditorViewModel e, params DayOfWeek[] days)
        {
            foreach (var toggle in e.DayToggles)
            {
                toggle.IsSelected = days.Contains(toggle.Day);
            }
        }

        static LocalizedOption<Recurrence> Rec(Recurrence r) => OptionSets.Find(OptionSets.Recurrences, r);

        Fails("blank title", "Err_Title", e => e.Title = "   ");
        Fails("hours not a number", "Err_Hours", e => e.HoursText = "abc");
        Fails("hours zero", "Err_Hours", e => e.HoursText = "0");
        Fails("hours above 24", "Err_Hours", e => e.HoursText = "25");
        Fails("bad date", "Err_Date", e => e.DueDateText = "99.99.2026");
        Fails("time without a date (no recurrence)", "Err_TimeNeedsDate", e => { e.DueDateText = ""; e.DueTimeText = "10:00"; });
        Fails("bad time", "Err_Time", e => e.DueTimeText = "25:61");
        Fails("selected days with none ticked", "Err_DaysRequired", e => { e.SelectedRecurrence = Rec(Recurrence.SelectedDays); SelectDays(e); });
        Fails("reminder without a date", "Err_ReminderNeedsDate", e => { e.DueDateText = ""; e.DueTimeText = ""; });
        // Doğrulama sırası: başlık, saat, tarih, zaman... ilk hata kazanır.
        {
            var task = Original();
            var editor = Editor(task);
            editor.Title = "";
            editor.HoursText = "abc";
            editor.DueDateText = "zzz";
            editor.TryApply();
            ctx.Check(editor.ValidationError == loc["Err_Title"], "editor: validation order — title error wins over hours/date errors");
        }

        // Başarılı kayıt: kırpma, ayrıştırma, alanların yazılması, alt görev kopyası
        {
            var task = Original();
            var editor = Editor(task);
            editor.Title = "  New title  ";
            editor.HoursText = "1,5";
            editor.DueDateText = today.AddDays(5).ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);
            editor.DueTimeText = "14:30";
            editor.Category = "  iş  ";
            editor.Notes = "  note  ";
            editor.SelectedPriority = OptionSets.Find(OptionSets.Priorities, TaskPriority.HighPriority);
            editor.SelectedReminder = OptionSets.Find(OptionSets.Reminders, 15);
            editor.Subtasks.Add(new SubTask { Title = "s2" });
            bool applied = editor.TryApply();
            ctx.Check(applied && editor.ValidationError is null && task.Title == "New title" && task.EstimatedHours == 1.5 && task.Category == "iş" && task.Notes == "note"
                && task.Priority == TaskPriority.HighPriority && task.DueAt == today.AddDays(5).ToDateTime(new TimeOnly(14, 30)) && task.HasDueTime
                && task.Recurrence == Recurrence.None && task.RepeatDays == WeekDays.None && task.ReminderMinutesBefore == 15,
                "editor: valid input is trimmed, parsed and written to the task");
            ctx.Check(task.Subtasks.Count == 2 && task.Subtasks[0].IsDone && task.Subtasks[1].Title == "s2" && !ReferenceEquals(task.Subtasks[0], editor.Subtasks[0]),
                "editor: subtasks are copied into the task, not shared");
            ctx.Check(!task.ReminderFired && task.SnoozedUntil is null, "editor: changed due time resets the fired/snoozed reminder state");
        }

        // Boş kategori/not → null; uzun metinler kırpılır
        {
            var task = Original();
            var editor = Editor(task);
            editor.Title = new string('t', 200);
            editor.Category = "   ";
            editor.Notes = "";
            editor.TryApply();
            ctx.Check(task.Title.Length == 120 && task.Category is null && task.Notes is null, "editor: title capped at 120; blank category/notes become null");
            var task2 = Original();
            var editor2 = Editor(task2);
            editor2.Category = new string('c', 100);
            editor2.Notes = new string('n', 5000);
            editor2.TryApply();
            ctx.Check(task2.Category!.Length == 40 && task2.Notes!.Length == 4000, "editor: category capped at 40, notes at 4000");
        }

        // Hatırlatma durumu: zaman/hatırlatıcı aynıysa korunur
        {
            var task = Original();
            var editor = Editor(task);
            editor.Title = "renamed only";
            bool applied = editor.TryApply();
            ctx.Check(applied && task.ReminderFired && task.SnoozedUntil is not null, "editor: unchanged due/reminder keeps the fired and snoozed state");
            var task2 = Original();
            var editor2 = Editor(task2);
            editor2.SelectedReminder = OptionSets.Find(OptionSets.Reminders, 15);
            editor2.TryApply();
            ctx.Check(!task2.ReminderFired && task2.SnoozedUntil is null, "editor: changed reminder offset resets the state");
        }

        // Tekrar: tarih boşsa otomatik tarih
        {
            var daily = Original();
            var de = Editor(daily);
            de.DueDateText = "";
            de.DueTimeText = "";
            de.SelectedReminder = OptionSets.Find(OptionSets.Reminders, null);
            de.SelectedRecurrence = Rec(Recurrence.Daily);
            bool ok = de.TryApply();
            ctx.Check(ok && daily.DueAt == today.ToDateTime(TimeOnly.MinValue) && !daily.HasDueTime && daily.Recurrence == Recurrence.Daily, "editor: daily recurrence without a date starts today");

            var timeOnly = Original();
            var te = Editor(timeOnly);
            te.DueDateText = "";
            te.DueTimeText = "08:15";
            te.SelectedRecurrence = Rec(Recurrence.Weekly);
            ok = te.TryApply();
            ctx.Check(ok && timeOnly.DueAt == today.ToDateTime(new TimeOnly(8, 15)) && timeOnly.HasDueTime, "editor: recurring task accepts a time without a date (date = today)");

            var days = Original();
            var se = Editor(days);
            se.DueDateText = "";
            se.DueTimeText = "";
            se.SelectedReminder = OptionSets.Find(OptionSets.Reminders, null);
            se.SelectedRecurrence = Rec(Recurrence.SelectedDays);
            var target = today.DayOfWeek == DayOfWeek.Monday ? DayOfWeek.Tuesday : DayOfWeek.Monday;
            SelectDays(se, target);
            ok = se.TryApply();
            var expected = today;
            while (expected.DayOfWeek != target)
            {
                expected = expected.AddDays(1);
            }

            ctx.Check(ok && days.DueAt == expected.ToDateTime(TimeOnly.MinValue) && days.RepeatDays == RecurrenceRules.ToFlag(target) && days.Recurrence == Recurrence.SelectedDays,
                "editor: selected-days recurrence without a date starts on the next matching day");

            var keepsDate = Original();
            var ke = Editor(keepsDate);
            ke.SelectedRecurrence = Rec(Recurrence.Daily);
            ke.TryApply();
            ctx.Check(keepsDate.DueAt == today.AddDays(3).ToDateTime(new TimeOnly(9, 0)), "editor: recurring task with an explicit date keeps it");
        }

        // Hata sonrası düzeltip yeniden kaydetme hatayı temizler
        {
            var task = Original();
            var editor = Editor(task);
            editor.Title = "";
            editor.TryApply();
            bool hadError = editor.ValidationError is not null;
            editor.Title = "fixed";
            bool ok = editor.TryApply();
            ctx.Check(hadError && ok && editor.ValidationError is null && task.Title == "fixed", "editor: fixing the input clears the validation error");
        }

        return Task.CompletedTask;
    }
}
