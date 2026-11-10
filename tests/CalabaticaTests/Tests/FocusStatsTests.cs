using System.IO;
using System.Windows.Threading;
using CyberTodo;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.ViewModels;
using CyberTodo.Views;
using static CalabaticaTests.TestSupport;

namespace CalabaticaTests;
internal static class FocusStatsTests
{
    public static async Task Run(TestContext ctx)
    {
        void Check(bool ok, string name) => ctx.Check(ok, name);
        string dir = ctx.Dir;
        string path = ctx.DataPath;
        var clock = ctx.Clock;
        Task<MainViewModel> Fresh() => ctx.Fresh();

        // ══════════════ Odak oturumları ══════════════
        var uClock = new ManualTime(DateTime.Today.AddHours(12));
        string focusPath = Path.Combine(dir, "focus-stats.json");
        var u = new MainViewModel(new StorageService(focusPath), time: uClock);
        await u.InitializeAsync();
        u.NewTaskTitle = "Focus me #study";
        await u.AddTaskCommand.ExecuteAsync(null);
        var uf = u.ActiveTasks.First(t => t.Title == "Focus me");
        await u.StartFocusCommand.ExecuteAsync(uf);
        uClock.Advance(TimeSpan.FromMinutes(10));
        await u.StopFocusCommand.ExecuteAsync(null);
        Check(uf.TrackedMinutes == 10, "focus: 10 min session credited");
        u.ShowFocusCommand.Execute(null);
        Check(u.IsFocusView && u.FocusByCategory.Count == 1 && u.FocusByCategory[0].Label == "#study"
            && u.FocusByCategory[0].ValueText == Localizer.Instance.Format("Fmt_Minutes", 10) && u.FocusByTask[0].Label == uf.Code + " Focus me",
            "focus: tab shows category and task: " + string.Join(",", u.FocusByCategory.Select(s => s.Label + "=" + s.ValueText)));
        Check(u.FocusTiles[0].Value == Localizer.Instance.Format("Fmt_Minutes", 10) && u.FocusTiles[1].Value == "1", "focus: total and session tiles");
        u.FlushNow();
        var reloaded = await StorageService.ReadFileAsync(focusPath);
        Check(reloaded.SchemaVersion == CyberData.CurrentSchemaVersion && reloaded.FocusSessions.Count == 1 && reloaded.FocusSessions[0].Category == "study"
            && reloaded.FocusSessions[0].Minutes == 10, "focus: session persisted");

        var fToday = DateOnly.FromDateTime(DateTime.Today);
        var sessions = new List<FocusSession>
        {
            new() { Start = DateTime.Today.AddHours(9), Minutes = 30, TaskId = Guid.NewGuid(), TaskNumber = 1, Title = "A", Category = "Work" },
            new() { Start = DateTime.Today.AddDays(-2).AddHours(9), Minutes = 50, TaskId = Guid.NewGuid(), TaskNumber = 2, Title = "B", Category = "work" },
            new() { Start = DateTime.Today.AddDays(-3).AddHours(9), Minutes = 20, TaskId = Guid.NewGuid(), TaskNumber = 3, Title = "C" },
            new() { Start = DateTime.Today.AddDays(-10).AddHours(9), Minutes = 99, TaskId = Guid.NewGuid(), TaskNumber = 4, Title = "Old", Category = "x" },
        };
        var (fTiles, fCats, fTasks) = InsightsBuilder.BuildFocus(sessions, fToday, 7, Localizer.Instance, m => m + "m");
        Check(fTiles[0].Value == "100m" && fTiles[0].Sub.Contains("99m") && fTiles[1].Value == "3", "focus builder: totals, sessions, previous period");
        Check(fCats.Count == 2 && fCats[0].Label.Equals("#Work", StringComparison.OrdinalIgnoreCase) && fCats[0].ValueText == "80m" && fCats[0].IsTop
            && fCats[1].ValueText == "20m", "focus builder: categories merged case-insensitively, sorted");
        Check(fTasks.Count == 3 && fTasks[0].Label == "#002 B" && fTasks.All(s => s.Label != "#004 Old"), "focus builder: by task within range");
    }
}
