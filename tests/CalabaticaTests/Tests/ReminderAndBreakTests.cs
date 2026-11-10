using System.IO;
using System.Windows.Threading;
using CyberTodo;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.ViewModels;
using CyberTodo.Views;
using static CalabaticaTests.TestSupport;

namespace CalabaticaTests;
internal static class ReminderAndBreakTests
{
    public static async Task Run(TestContext ctx)
    {
        void Check(bool ok, string name) => ctx.Check(ok, name);
        string dir = ctx.Dir;
        string path = ctx.DataPath;
        var clock = ctx.Clock;
        Task<MainViewModel> Fresh() => ctx.Fresh();

        // ══════════════ Hatırlatma erteleme (sahte saat) ══════════════
        var snClock = new ManualTime(DateTime.Today.AddHours(10));
        var sn = new MainViewModel(new StorageService(Path.Combine(dir, "snooze.json")), time: snClock);
        await sn.InitializeAsync();
        sn.NewTaskTitle = "Call mom";
        await sn.AddTaskCommand.ExecuteAsync(null);
        var st = sn.ActiveTasks.Single();
        st.DueAt = snClock.LocalNow.AddMinutes(30);
        st.HasDueTime = true;
        st.ReminderMinutesBefore = 0;
        snClock.Advance(TimeSpan.FromMinutes(29));
        sn.Tick();
        Check(!sn.Toasts.Any(t => t.Kind == ToastKind.Reminder), "snooze: no reminder before due time");
        snClock.Advance(TimeSpan.FromMinutes(1));
        sn.Tick();
        var rt = sn.Toasts.Single(t => t.Kind == ToastKind.Reminder);
        Check(st.ReminderFired && rt.HasAction && rt.HasSecondaryAction, "snooze: reminder toast offers 10 min and 1 h");
        await sn.RunToastSecondaryActionCommand.ExecuteAsync(rt);
        Check(st.SnoozedUntil == snClock.LocalNow.AddMinutes(60) && !sn.Toasts.Contains(rt), "snooze: 1 h snooze sets exact time and closes the toast");
        snClock.Advance(TimeSpan.FromMinutes(59));
        sn.Tick();
        Check(!sn.Toasts.Any(t => t.Kind == ToastKind.Reminder), "snooze: no reminder before snooze time");
        snClock.Advance(TimeSpan.FromMinutes(1));
        sn.Tick();
        var rt2 = sn.Toasts.SingleOrDefault(t => t.Kind == ToastKind.Reminder);
        Check(rt2 is not null && st.SnoozedUntil is null, "snooze: reminder shown again exactly when snooze ends");
        await sn.RunToastActionCommand.ExecuteAsync(rt2);
        Check(st.SnoozedUntil == snClock.LocalNow.AddMinutes(10), "snooze: 10 min snooze");
        await sn.CompleteTaskCommand.ExecuteAsync(st);
        snClock.Advance(TimeSpan.FromMinutes(11));
        sn.Tick();
        Check(!sn.Toasts.Any(t => t.Kind == ToastKind.Reminder), "snooze: completed task does not remind again");
        sn.FlushNow();
        Check(File.ReadAllText(Path.Combine(dir, "snooze.json")).Contains("\"snoozedUntil\""), "snooze: snooze time persisted");

        // ══════════════ Mola zamanlayıcısı (sahte saat) ══════════════
        var brClock = new ManualTime(DateTime.Today.AddHours(9));
        var br = new MainViewModel(new StorageService(Path.Combine(dir, "breaks.json")), time: brClock);
        await br.InitializeAsync();
        br.NewTaskTitle = "Deep work";
        await br.AddTaskCommand.ExecuteAsync(null);
        var bt = br.ActiveTasks.Single();
        void Run(TimeSpan span) { brClock.Advance(span); br.Tick(); }
        await br.StartFocusCommand.ExecuteAsync(bt);
        Run(TimeSpan.FromMinutes(12));
        Check(br.Focus.IsRunning && br.Focus.RemainingText == "13:00" && Math.Abs(br.Focus.Progress - 12 / 25.0) < 1e-9, "breaks: countdown follows the clock");
        Run(TimeSpan.FromMinutes(13));
        Check(!br.Focus.IsRunning && br.Focus.IsOnBreak && !br.Focus.IsLongBreak && br.Focus.BreakRemainingText == "05:00" && bt.TrackedMinutes == 25,
            "breaks: 5 min break exactly when focus ends: " + br.Focus.BreakRemainingText);
        Run(TimeSpan.FromMinutes(5));
        Check(!br.Focus.IsOnBreak && !br.Focus.IsRunning && br.Toasts.Any(t => t.Message.StartsWith("☕")), "breaks: break ends with notice, no auto-start by default");

        br.SelectedAutoNextOption = br.SoundOptions.First(o => o.Value);
        await br.StartFocusCommand.ExecuteAsync(bt);
        Run(TimeSpan.FromMinutes(25));                     // 2. odak → kısa mola
        Run(TimeSpan.FromMinutes(5));                      // mola bitti → odak otomatik
        Check(br.Focus.IsRunning && br.Focus.Task == bt && !br.Focus.IsOnBreak, "breaks: auto-start next focus on the same task");
        Run(TimeSpan.FromMinutes(25));                     // 3. odak
        Check(br.Focus.IsOnBreak && !br.Focus.IsLongBreak, "breaks: 3rd focus -> short break");
        br.SkipBreakCommand.Execute(null);
        Check(!br.Focus.IsOnBreak && br.Focus.IsRunning, "breaks: skip ends break (and auto-starts focus)");
        Run(TimeSpan.FromMinutes(25));                     // 4. odak
        Check(br.Focus.IsOnBreak && br.Focus.IsLongBreak && br.Focus.BreakRemainingText == "15:00", "breaks: every 4th focus -> 15 min long break: " + br.Focus.BreakRemainingText);
        Check(bt.TrackedMinutes == 100, "breaks: four full sessions credited (100 min)");
        await br.StartFocusCommand.ExecuteAsync(bt);
        Check(!br.Focus.IsOnBreak && br.Focus.IsRunning, "breaks: starting focus manually cancels the break");
        br.SelectedBreaksOption = br.SoundOptions.First(o => !o.Value);
        Run(TimeSpan.FromMinutes(25));
        Check(!br.Focus.IsOnBreak && !br.Focus.IsRunning, "breaks: disabled -> no break after focus");

        // ── FocusTimer birim testleri (UI'sız) ──
        var ft = new FocusTimer();
        var ftTask = new CyberTask { Title = "x" };
        var ftStart = new DateTime(2026, 1, 1, 9, 0, 0);
        ft.Start(ftTask, ftStart, TimeSpan.FromMinutes(25));
        Check(ft.Stop(ftStart.AddSeconds(59))?.Minutes == 0 && !ftTask.IsFocused, "focus timer: <1 min stop credits 0, clears flag");
        ft.Start(ftTask, ftStart, TimeSpan.FromMinutes(25));
        Check(ft.Stop(ftStart.AddHours(3))?.Minutes == 25, "focus timer: stop after overrun is capped at session length");
        Check(ft.Tick(ftStart, breaksEnabled: true) is null && ft.Stop(ftStart) is null, "focus timer: idle tick/stop do nothing");
        ft.Start(ftTask, ftStart, TimeSpan.FromMinutes(25));
        var ev = ft.Tick(ftStart.AddMinutes(25), breaksEnabled: false);
        Check(ev is { Kind: FocusEventKind.FocusCompleted, Minutes: 25 } && !ft.IsOnBreak, "focus timer: completes without break when disabled");
    }
}
