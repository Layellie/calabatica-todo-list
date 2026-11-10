using System.IO;
using System.Windows.Threading;
using CyberTodo;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.ViewModels;
using CyberTodo.Views;
using static CalabaticaTests.TestSupport;

namespace CalabaticaTests;
internal static class UndoTests
{
    public static async Task Run(TestContext ctx)
    {
        void Check(bool ok, string name) => ctx.Check(ok, name);
        string dir = ctx.Dir;
        string path = ctx.DataPath;
        var clock = ctx.Clock;
        Task<MainViewModel> Fresh() => ctx.Fresh();

        // ══════════════ Geri alma (Ctrl+Z / ↶) ══════════════
        var uClock = new ManualTime(DateTime.Today.AddHours(12));
        var u = new MainViewModel(new StorageService(Path.Combine(dir, "undo.json")), time: uClock);
        await u.InitializeAsync();
        Check(!u.CanUndo && !u.UndoCommand.CanExecute(null), "undo: nothing to undo on start");

        u.NewTaskTitle = "Undo A";
        await u.AddTaskCommand.ExecuteAsync(null);
        Check(u.CanUndo && u.UndoHint.Contains("#001"), "undo: add is undoable, hint names the task: " + u.UndoHint);
        await u.UndoCommand.ExecuteAsync(null);
        Check(u.ActiveTasks.Count == 0 && !u.CanUndo, "undo: add -> task removed");

        u.NewTaskTitle = "Undo B";
        await u.AddTaskCommand.ExecuteAsync(null);
        u.NewTaskTitle = "Undo C";
        await u.AddTaskCommand.ExecuteAsync(null);
        var ub = u.ActiveTasks.First(t => t.Title == "Undo B");
        double ubOrder = ub.SortOrder;
        await u.CompleteTaskCommand.ExecuteAsync(ub);
        int doneToday = u.ChartBars[^1].Day.CompletedCount;
        Check(ub.IsCompleted && doneToday == 1, "undo: complete done");
        await u.UndoCommand.ExecuteAsync(null);
        Check(!ub.IsCompleted && u.ActiveTasks.Contains(ub) && ub.SortOrder == ubOrder && u.ChartBars[^1].Day.CompletedCount == 0,
            "undo: complete -> active again, same place, day credit removed");

        await Task.Delay(400); // çift tıklama kilidi (350 ms)
        u.NewTaskTitle = "Undo R *daily @today";
        await u.AddTaskCommand.ExecuteAsync(null);
        var ur = u.ActiveTasks.First(t => t.Title == "Undo R");
        await u.CompleteTaskCommand.ExecuteAsync(ur);
        Check(u.ActiveTasks.Count(t => t.Title == "Undo R") == 1 && ur.IsCompleted, "undo: recurring complete spawns next copy");
        await u.UndoCommand.ExecuteAsync(null);
        Check(u.ActiveTasks.Count(t => t.Title == "Undo R") == 1 && u.ActiveTasks.Contains(ur), "undo: recurring complete -> spawned copy removed, original back");

        await Task.Delay(400);
        await u.CompleteTaskCommand.ExecuteAsync(ub);
        await Task.Delay(400);
        await u.RestoreTaskCommand.ExecuteAsync(ub);
        Check(!ub.IsCompleted, "undo: restore done");
        await u.UndoCommand.ExecuteAsync(null);
        Check(ub.IsCompleted && u.CompletedTasks.Contains(ub), "undo: restore -> completed again");
        await u.UndoCommand.ExecuteAsync(null); // ilk tamamlamayı da geri al
        Check(!ub.IsCompleted, "undo: stack is LIFO (complete undone after restore undo)");

        u.EditTaskCommand.Execute(ub);
        u.Editor!.Title = "Undo B edited";
        u.Editor.Notes = "note";
        await u.SaveEditCommand.ExecuteAsync(null);
        Check(ub.Title == "Undo B edited" && ub.Notes == "note", "undo: edit applied");
        await u.UndoCommand.ExecuteAsync(null);
        Check(ub.Title == "Undo B" && ub.Notes is null, "undo: edit -> previous title and notes");

        var orderBefore = u.ActiveTasks.ToDictionary(t => t.Id, t => t.SortOrder);
        var uc = u.ActiveTasks.First(t => t.Title == "Undo C");
        await u.MoveTaskAsync(uc, u.ActiveTasks.First(t => t.Title == "Undo B"));
        Check(u.SelectedSortOption.Value == SortMode.Manual, "undo: move switches to manual sort");
        await u.UndoCommand.ExecuteAsync(null);
        Check(u.SelectedSortOption.Value == SortMode.Created && u.ActiveTasks.All(t => orderBefore[t.Id] == t.SortOrder),
            "undo: move -> previous order and sort mode");

        await u.DeleteTaskCommand.ExecuteAsync(uc);
        Check(!u.ActiveTasks.Contains(uc) && u.Toasts.Any(t => t.HasAction), "undo: delete shows UNDO toast");
        await u.UndoCommand.ExecuteAsync(null); // Ctrl+Z
        Check(u.ActiveTasks.Contains(uc) && !u.Toasts.Any(t => t.HasAction), "undo: Ctrl+Z restores deleted task and removes its toast");
        Check(u.Toasts.Any(t => t.Message.StartsWith("↶")), "undo: 'undone' feedback toast");
    }
}
