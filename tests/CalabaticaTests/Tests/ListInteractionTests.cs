using System.IO;
using System.Windows.Threading;
using CyberTodo;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.ViewModels;
using CyberTodo.Views;
using static CalabaticaTests.TestSupport;

namespace CalabaticaTests;
internal static class ListInteractionTests
{
    public static async Task Run(TestContext ctx)
    {
        void Check(bool ok, string name) => ctx.Check(ok, name);
        string dir = ctx.Dir;
        string path = ctx.DataPath;
        var clock = ctx.Clock;
        Task<MainViewModel> Fresh() => ctx.Fresh();

        // ══════════════ Klavyeyle liste ══════════════
        var kb = new MainViewModel(new StorageService(Path.Combine(dir, "keys.json")));
        await kb.InitializeAsync();
        foreach (string title in new[] { "K1", "K2", "K3" })
        {
            kb.NewTaskTitle = title;
            await kb.AddTaskCommand.ExecuteAsync(null);
        }

        CyberTask K(string title) => kb.ActiveTasks.Single(t => t.Title == title);
        kb.MoveSelection(1);
        Check(kb.SelectedTask == K("K1") && K("K1").IsSelected, "keys: ↓ with no selection selects first");
        kb.MoveSelection(1);
        kb.MoveSelection(1);
        kb.MoveSelection(1);
        Check(kb.SelectedTask == K("K3") && !K("K1").IsSelected, "keys: ↓ stops at the last row");
        kb.MoveSelection(-1);
        Check(kb.SelectedTask == K("K2"), "keys: ↑ moves up");
        kb.SelectEdge(last: true);
        var k3 = kb.SelectedTask!;
        await kb.CompleteSelectedAsync();
        Check(k3.IsCompleted && kb.SelectedTask == K("K2"), "keys: Space completes, selection stays in place");
        kb.SelectEdge(last: false);
        var k1 = kb.SelectedTask!;
        await kb.DeleteSelectedAsync();
        Check(!kb.ActiveTasks.Contains(k1) && kb.SelectedTask == K("K2"), "keys: Del deletes, next row selected");
        kb.EditSelected();
        Check(kb.Editor?.Target == K("K2"), "keys: Enter opens editor");
        kb.CancelEditCommand.Execute(null);
        await kb.ToggleFocusSelectedAsync();
        Check(kb.Focus.Task == K("K2"), "keys: F starts focus");
        await kb.ToggleFocusSelectedAsync();
        Check(kb.Focus.Task is null, "keys: F again stops focus");
        var k2 = K("K2");
        kb.EscapeCommand.Execute(null);
        Check(kb.SelectedTask is null && !k2.IsSelected, "keys: Esc clears selection");

        // ══════════════ Mini mod ══════════════
        var mn = new MainViewModel(new StorageService(Path.Combine(dir, "mini.json")));
        await mn.InitializeAsync();
        mn.NewTaskTitle = "Due today @today @23:59";
        await mn.AddTaskCommand.ExecuteAsync(null);
        mn.NewTaskTitle = $"Later @{DateTime.Today.AddDays(3):dd.MM}";
        await mn.AddTaskCommand.ExecuteAsync(null);
        mn.NewTaskTitle = "Late";
        await mn.AddTaskCommand.ExecuteAsync(null);
        mn.NewTaskTitle = "No date";
        await mn.AddTaskCommand.ExecuteAsync(null);
        mn.ActiveTasks.Single(t => t.Title == "Late").DueAt = DateTime.Today.AddDays(-1);
        mn.RefreshTimeDependentState(DateTime.Now);
        mn.ToggleMiniModeCommand.Execute(null);
        var miniTitles = mn.MiniTasks.Cast<CyberTask>().Select(t => t.Title).ToList();
        Check(mn.IsMiniMode && mn.HasMiniTasks && miniTitles.SequenceEqual(["Late", "Due today"]),
            "mini: shows overdue + today, by due date: " + string.Join(",", miniTitles));
        await mn.CompleteTaskCommand.ExecuteAsync(mn.ActiveTasks.Single(t => t.Title == "Late"));
        Check(mn.MiniTasks.Cast<CyberTask>().Select(t => t.Title).SequenceEqual(["Due today"]), "mini: completed task leaves the mini list");
        mn.SaveMiniPosition(120.4, 340.6);
        mn.ToggleMiniModeCommand.Execute(null);
        mn.FlushNow();
        var miniSaved = await StorageService.ReadFileAsync(Path.Combine(dir, "mini.json"));
        Check(!mn.IsMiniMode && miniSaved.Settings.MiniLeft == 120 && miniSaved.Settings.MiniTop == 341, "mini: toggles back, position persisted");

        // ══════════════ Artımlı görünüm güncellemesi (tam Refresh olmadan doğru filtre/sıra) ══════════════
        var iv = new MainViewModel(new StorageService(Path.Combine(dir, "incremental.json")), time: new ManualTime(DateTime.Today.AddHours(8)));
        await iv.InitializeAsync();
        foreach (string title in new[] { "Alpha @today @18:00", "Beta @today @09:00", "Gamma report @today @12:00" })
        {
            iv.NewTaskTitle = title;
            await iv.AddTaskCommand.ExecuteAsync(null);
        }

        iv.SelectedSortOption = iv.SortOptions.First(o => o.Value == SortMode.DueDate);
        string Visible() => string.Join(",", iv.ActiveView.Cast<CyberTask>().Select(t => t.Title));
        iv.NewTaskTitle = "Delta @today @10:00";
        await iv.AddTaskCommand.ExecuteAsync(null);
        Check(Visible() == "Beta,Delta,Gamma report,Alpha", "incremental: added task lands in due-date order: " + Visible());
        iv.SearchText = "report";
        iv.NewTaskTitle = "Epsilon @today @07:00";
        await iv.AddTaskCommand.ExecuteAsync(null);
        iv.NewTaskTitle = "Second report @today @06:00";
        await iv.AddTaskCommand.ExecuteAsync(null);
        Check(Visible() == "Second report,Gamma report", "incremental: search filter applies to newly added tasks: " + Visible());
        var gamma = iv.ActiveTasks.Single(t => t.Title == "Gamma report");
        await iv.CompleteTaskCommand.ExecuteAsync(gamma);
        Check(Visible() == "Second report" && iv.CompletedView.Cast<CyberTask>().First() == gamma, "incremental: completed task leaves active view, tops completed view");
        await Task.Delay(400);
        await iv.RestoreTaskCommand.ExecuteAsync(gamma);
        Check(Visible() == "Second report,Gamma report", "incremental: restored task returns to sorted position: " + Visible());
        iv.SearchText = string.Empty;
        Check(iv.SmartViews.First(s => s.Value == SmartView.Today).Count == 4 && iv.SmartViews.First(s => s.Value == SmartView.Overdue).Count == 2, "incremental: smart-view counts still updated (08:00: 4 today, 2 overdue)");
    }
}
