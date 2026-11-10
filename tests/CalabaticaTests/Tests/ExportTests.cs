using System.IO;
using System.Windows.Threading;
using CyberTodo;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.ViewModels;
using CyberTodo.Views;
using static CalabaticaTests.TestSupport;

namespace CalabaticaTests;
internal static class ExportTests
{
    public static async Task Run(TestContext ctx)
    {
        void Check(bool ok, string name) => ctx.Check(ok, name);
        string dir = ctx.Dir;
        string path = ctx.DataPath;
        var clock = ctx.Clock;
        Task<MainViewModel> Fresh() => ctx.Fresh();

        // ══════════════ CSV / ICS ══════════════
        var tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
        var csvTasks = new[]
        {
            new CyberTask { Number = 7, Title = "=HYPERLINK(\"x\")", Category = "a;b", EstimatedHours = 0.5, Notes = "line1\nline2" },
        };
        string csv = DataExporter.ToCsv(csvTasks, tr);
        string row = csv.Split("\r\n")[1];
        Check(csv.StartsWith("Code;Title;"), "csv: list separator from culture (tr-TR -> ;)");
        Check(row.Contains("\"'=HYPERLINK(\"\"x\"\")\"") && row.Contains("\"a;b\"") && row.Contains(";0,5;"), "csv: formula neutralized, quoting, decimal comma: " + row);
        Check(csv.Contains("\"line1\nline2\""), "csv: multi-line notes quoted");

        var icsDue = new CyberTask
        {
            Number = 9, Title = "Toplantı, plan; özet", DueAt = new DateTime(2026, 11, 23, 14, 0, 0), HasDueTime = true, EstimatedHours = 1.5,
            ReminderMinutesBefore = 15, Recurrence = Recurrence.SelectedDays, RepeatDays = WeekDays.Monday | WeekDays.Wednesday, Category = "iş",
            Notes = new string('ğ', 80), Subtasks = [new SubTask { Title = "hazırlık", IsDone = true }],
        };
        var icsAllDay = new CyberTask { Number = 10, Title = "Fatura", DueAt = new DateTime(2026, 11, 24), ReminderMinutesBefore = 60 };
        var icsDone = new CyberTask { Number = 11, Title = "Done", DueAt = new DateTime(2026, 11, 24), IsCompleted = true };
        var icsNoDate = new CyberTask { Number = 12, Title = "No date" };
        string ics = DataExporter.ToIcs([icsDue, icsAllDay, icsDone, icsNoDate], new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc));
        string unfolded = ics.Replace("\r\n ", string.Empty);
        Check(ics.StartsWith("BEGIN:VCALENDAR\r\n") && ics.TrimEnd().EndsWith("END:VCALENDAR") && !ics.Replace("\r\n", "").Contains('\n'), "ics: CRLF lines, calendar wrapper");
        Check(unfolded.Contains("DTSTART:20261123T140000") && unfolded.Contains("DTEND:20261123T153000"), "ics: timed event with estimated duration");
        Check(unfolded.Contains("RRULE:FREQ=WEEKLY;BYDAY=MO,WE") && unfolded.Contains("TRIGGER:-PT15M"), "ics: selected-days RRULE and reminder");
        Check(unfolded.Contains("SUMMARY:#009 Toplantı\\, plan\\; özet") && unfolded.Contains("CATEGORIES:iş"), "ics: text escaping");
        Check(unfolded.Contains("DTSTART;VALUE=DATE:20261124") && unfolded.Contains("DTEND;VALUE=DATE:20261125") && unfolded.Contains("TRIGGER:PT480M"),
            "ics: all-day event, reminder relative to 09:00");
        Check(!unfolded.Contains("Done") && !unfolded.Contains("No date") && unfolded.Split("BEGIN:VEVENT").Length == 3, "ics: only active dated tasks");
        Check(ics.Split("\r\n").All(l => System.Text.Encoding.UTF8.GetByteCount(l) <= 75) && unfolded.Contains(new string('ğ', 80)),
            "ics: lines folded at 75 octets without splitting UTF-8 characters");
    }
}
