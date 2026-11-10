using CyberTodo.Models;
using CyberTodo.ViewModels;

namespace CalabaticaTests;

/// <summary>
/// QuickAddParser karakterizasyon tablosu (TryApply bölünmeden önce yazıldı). Bugün = Çarşamba 2026-03-04.
/// Her satır: girdi → başlık, kategori, öncelik, tarih, saat, tekrar, süre, tekrar günleri.
/// </summary>
internal static class QuickAddParserTests
{
    private static readonly DateOnly s_today = new(2026, 3, 4);

    public static Task Run(TestContext ctx)
    {
        void Case(string? input, string title, string? category = null, TaskPriority? priority = null, DateOnly? date = null,
            TimeOnly? time = null, Recurrence? recurrence = null, double? hours = null, WeekDays days = WeekDays.None)
        {
            var r = QuickAddParser.Parse(input, s_today);
            bool ok = r.Title == title && r.Category == category && r.Priority == priority && r.DueDate == date && r.DueTime == time
                && r.Recurrence == recurrence && r.Hours == hours && r.RepeatDays == days;
            ctx.Check(ok, $"quick-add: '{input}' -> {(ok ? "as expected" : $"got title='{r.Title}' cat={r.Category} prio={r.Priority} date={r.DueDate} time={r.DueTime} rec={r.Recurrence} hours={r.Hours} days={r.RepeatDays}")}");
        }

        static DateOnly D(int month, int day, int year = 2026) => new(year, month, day);

        Case(null, "");
        Case("Buy milk", "Buy milk");
        Case("Cuma namazı", "Cuma namazı");
        Case("Rapor #iş ! @yarın @17:00 ~1.5", "Rapor", "iş", TaskPriority.HighPriority, D(3, 5), new TimeOnly(17, 0), null, 1.5);
        Case("a !! b", "a b", priority: TaskPriority.HighPriority);
        Case("#a #b x", "x", category: "b");

        // Tarihler
        Case("t @bugün", "t", date: D(3, 4));
        Case("t @TODAY", "t", date: D(3, 4));
        Case("t @YARIN", "t", date: D(3, 5));
        Case("t @tomorrow", "t", date: D(3, 5));
        Case("t @cuma", "t", date: D(3, 6));
        Case("t @FRIDAY", "t", date: D(3, 6));
        Case("t @wednesday", "t", date: D(3, 4));
        Case("t @salı", "t", date: D(3, 10));
        Case("t @25.11", "t", date: D(11, 25));
        Case("t @1.3", "t", date: D(3, 1, 2027));
        Case("t @4.3", "t", date: D(3, 4));
        Case("t @25.11.2026", "t", date: D(11, 25));
        Case("t @1.12", "t", date: D(12, 1));

        // Saat
        Case("t @14:30", "t", date: D(3, 4), time: new TimeOnly(14, 30));
        Case("t @cuma @09:05", "t", date: D(3, 6), time: new TimeOnly(9, 5));

        // Tekrar
        Case("x *günlük", "x", date: D(3, 4), recurrence: Recurrence.Daily);
        Case("x *WEEKLY", "x", date: D(3, 4), recurrence: Recurrence.Weekly);
        Case("x *aylık", "x", date: D(3, 4), recurrence: Recurrence.Monthly);
        Case("Ders @cuma *haftalık", "Ders", date: D(3, 6), recurrence: Recurrence.Weekly);
        Case("Gym *pzt,çar,cum", "Gym", date: D(3, 4), recurrence: Recurrence.SelectedDays, days: WeekDays.Monday | WeekDays.Wednesday | WeekDays.Friday);
        Case("Run *haftaiçi", "Run", date: D(3, 4), recurrence: Recurrence.SelectedDays, days: WeekDays.Weekdays);
        Case("Run *hafta-sonu", "Run", date: D(3, 7), recurrence: Recurrence.SelectedDays, days: WeekDays.Weekend);
        Case("Run *weekend", "Run", date: D(3, 7), recurrence: Recurrence.SelectedDays, days: WeekDays.Weekend);
        Case("Run *MON,Thu", "Run", date: D(3, 5), recurrence: Recurrence.SelectedDays, days: WeekDays.Monday | WeekDays.Thursday);

        // Süre
        Case("t ~2", "t", hours: 2);
        Case("t ~abc", "t ~abc");

        // Tanınmayanlar başlıkta kalır, sessizce yutulmaz
        Case("Bak *pzt,xyz", "Bak *pzt,xyz");
        Case("Bak @bilinmeyen", "Bak @bilinmeyen");
        Case("# @ * ~ !x", "# @ * ~ !x");
        Case("t *", "t *");
        return Task.CompletedTask;
    }
}
