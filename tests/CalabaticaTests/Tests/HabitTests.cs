using System.IO;
using System.Windows.Threading;
using CyberTodo;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.ViewModels;
using CyberTodo.Views;
using static CalabaticaTests.TestSupport;

namespace CalabaticaTests;
internal static class HabitTests
{
    public static async Task Run(TestContext ctx)
    {
        void Check(bool ok, string name) => ctx.Check(ok, name);
        string dir = ctx.Dir;
        string path = ctx.DataPath;
        var clock = ctx.Clock;
        Task<MainViewModel> Fresh() => ctx.Fresh();

        // ══════════════ Alışkanlıklar ══════════════
        var hToday = DateOnly.FromDateTime(DateTime.Today);
        var hSeries = Guid.NewGuid();
        CyberTask Done(int daysAgo) => new()
        {
            Title = "Run", Recurrence = Recurrence.Daily, SeriesId = hSeries, IsCompleted = true,
            DueAt = DateTime.Today.AddDays(-daysAgo), CompletedAt = DateTime.Today.AddDays(-daysAgo).AddHours(20),
            CreatedAt = DateTime.Today.AddDays(-12),
        };
        var hDone = new[] { 1, 2, 3, 5, 10, 11, 12 }.Select(Done).ToList();
        var hActive = new CyberTask { Number = 5, Title = "Run", Recurrence = Recurrence.Daily, SeriesId = hSeries, DueAt = DateTime.Today, CreatedAt = DateTime.Now };
        var hRow = InsightsBuilder.BuildHabits([hActive], hDone, hToday, Localizer.Instance, _ => "DAILY").Single();
        Check(hRow.Streak == 3 && hRow.BestText.Contains('3'), "habits: streak 3 (today pending does not break it), best 3: " + hRow.StreakText + " " + hRow.BestText);
        Check(hRow.Cells.Count == 28 && hRow.Cells[^1].State == HabitCellState.Pending && hRow.Cells[^5].State == HabitCellState.Missed
            && hRow.Cells[^2].State == HabitCellState.Done && hRow.Cells[^14].State == HabitCellState.Off, "habits: 28-day strip states");
        Check(hRow.RateText.Contains("58"), "habits: 30-day rate 7/12: " + hRow.RateText);

        var wed = new DateOnly(2026, 11, 11); // çarşamba
        var mwfSeries = Guid.NewGuid();
        CyberTask Mwf(DateOnly d) => new()
        {
            Title = "Gym", Recurrence = Recurrence.SelectedDays, RepeatDays = WeekDays.Monday | WeekDays.Wednesday | WeekDays.Friday,
            SeriesId = mwfSeries, IsCompleted = true, DueAt = d.ToDateTime(TimeOnly.MinValue), CompletedAt = d.ToDateTime(new TimeOnly(19, 0)),
            CreatedAt = new DateTime(2026, 11, 4),
        };
        var mwfActive = new CyberTask
        {
            Title = "Gym", Recurrence = Recurrence.SelectedDays, RepeatDays = WeekDays.Monday | WeekDays.Wednesday | WeekDays.Friday,
            SeriesId = mwfSeries, DueAt = wed.ToDateTime(TimeOnly.MinValue), CreatedAt = new DateTime(2026, 11, 9),
        };
        var mwfRow = InsightsBuilder.BuildHabits([mwfActive], [Mwf(new(2026, 11, 4)), Mwf(new(2026, 11, 6)), Mwf(new(2026, 11, 9))], wed, Localizer.Instance, _ => "MWF").Single();
        Check(mwfRow.Streak == 3 && mwfRow.Cells[^2].State == HabitCellState.Off && mwfRow.Cells[^3].State == HabitCellState.Done,
            "habits: selected days skip unscheduled days");

        var legacy = new CyberData { SchemaVersion = 4 };
        for (int i = 0; i < 3; i++)
        {
            legacy.Tasks.Add(new CyberTask { Title = "Spor", Category = "sağlık", Recurrence = Recurrence.Daily, IsCompleted = true, CompletedAt = DateTime.Now, CreatedAt = DateTime.Today.AddDays(-5 + i) });
        }

        legacy.Tasks.Add(new CyberTask { Title = "spor ", Category = "Sağlık", Recurrence = Recurrence.Daily, CreatedAt = DateTime.Today });
        legacy.Tasks.Add(new CyberTask { Title = "Rapor", Recurrence = Recurrence.Weekly, CreatedAt = DateTime.Today });
        legacy.Tasks.Add(new CyberTask { Title = "Once" });
        string legacyPath = Path.Combine(dir, "legacy-v4.json");
        await StorageService.WriteAtomicAsync(legacyPath, StorageService.Serialize(legacy));
        var migrated = await StorageService.ReadFileAsync(legacyPath);
        var sporCopies = migrated.Tasks.Where(t => t.Title.Trim().Equals("spor", StringComparison.OrdinalIgnoreCase)).ToList();
        Check(migrated.SchemaVersion == 5 && sporCopies.Count == 4 && sporCopies.All(t => t.SeriesId == legacy.Tasks[0].Id)
            && migrated.Tasks.Single(t => t.Title == "Rapor").SeriesId is { } rs && rs != legacy.Tasks[0].Id
            && migrated.Tasks.Single(t => t.Title == "Once").SeriesId is null, "habits: v4 -> v5 links old recurring copies into series");

        var hv = new MainViewModel(new StorageService(Path.Combine(dir, "habits.json")));
        await hv.InitializeAsync();
        hv.NewTaskTitle = "Stretch *daily @today";
        await hv.AddTaskCommand.ExecuteAsync(null);
        var hFirst = hv.ActiveTasks.Single();
        await hv.CompleteTaskCommand.ExecuteAsync(hFirst);
        var hNext = hv.ActiveTasks.Single();
        Check(hFirst.SeriesId == hFirst.Id && hNext.SeriesId == hFirst.Id, "habits: completing links the next copy to the series");
        hv.ShowHabitsCommand.Execute(null);
        Check(hv.IsHabitsView && hv.Habits.Count == 1 && hv.Habits[0].Streak == 1 && hv.Habits[0].Cells[^1].State == HabitCellState.Done,
            "habits: tab shows the habit with today's completion");
    }
}
