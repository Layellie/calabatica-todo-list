using System.IO;
using System.Windows.Threading;
using CyberTodo;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.ViewModels;
using CyberTodo.Views;
using static CalabaticaTests.TestSupport;

namespace CalabaticaTests;
internal static class CoreScenarioTests
{
    public static async Task Run(TestContext ctx)
    {
        void Check(bool ok, string name) => ctx.Check(ok, name);
        string dir = ctx.Dir;
        string path = ctx.DataPath;
        var clock = ctx.Clock;
        Task<MainViewModel> Fresh() => ctx.Fresh();

        Check(Localizer.MissingKeys().Count == 0, "TR/EN key sets identical: " + string.Join(",", Localizer.MissingKeys()));

        var vm = await Fresh();
        Check(vm.ActiveTasks.Count == 0 && vm.CompletedTasks.Count == 0, "fresh start has no seed tasks");
        Check(vm.ChartBars.Count == 30 && vm.ChartBars.All(b => !b.IsHovered), "30 bars, none hovered on load");
        Check(vm.SelectedDay?.Date == DateOnly.FromDateTime(DateTime.Today), "log shows today by default");
        Check(Math.Abs(vm.UiScale - 0.85) < 1e-9, "default UI scale 0.85");

        // ── Quick add parser ──
        var today = DateOnly.FromDateTime(DateTime.Today);
        var q = QuickAddParser.Parse("Rapor yaz #iş @yarın @14:30 ! *haftalık ~2,5", today);
        Check(q.Title == "Rapor yaz" && q.Category == "iş" && q.Priority == TaskPriority.HighPriority
              && q.DueDate == today.AddDays(1) && q.DueTime == new TimeOnly(14, 30) && q.Recurrence == Recurrence.Weekly && q.Hours == 2.5,
              "parser: all tokens");
        var q2 = QuickAddParser.Parse("Cuma namazı @1.12", today);
        Check(q2.Title == "Cuma namazı" && q2.DueDate is { Month: 12, Day: 1 } && q2.DueTime is null, "parser: bare words kept, @1.12 is a date not 01:12");
        var q3 = QuickAddParser.Parse("Toplantı @FRIDAY @SALI", today);
        Check(q3.DueDate?.DayOfWeek == DayOfWeek.Tuesday && q3.Title == "Toplantı", "parser: EN/TR weekdays (uppercase, Turkish I)");
        var q4 = QuickAddParser.Parse("Sadece saat @09:00", today);
        Check(q4.DueDate == today && q4.DueTime == new TimeOnly(9, 0), "parser: time only -> today");
        // ── Seçili günlerde tekrar ──
        var qd = QuickAddParser.Parse("Spor *pzt,ÇAR,cum @18:00", today);
        var expectedFirst = RecurrenceRules.FirstMatchOnOrAfter(today, WeekDays.Monday | WeekDays.Wednesday | WeekDays.Friday);
        Check(qd.Title == "Spor" && qd.Recurrence == Recurrence.SelectedDays && qd.RepeatDays == (WeekDays.Monday | WeekDays.Wednesday | WeekDays.Friday)
              && qd.DueDate == expectedFirst && qd.DueTime == new TimeOnly(18, 0), "parser: *pzt,ÇAR,cum -> first matching day " + qd.DueDate);
        Check(QuickAddParser.Parse("İş *HAFTAİÇİ", today).RepeatDays == WeekDays.Weekdays, "parser: *HAFTAİÇİ");
        Check(QuickAddParser.Parse("Run *mon,fri", today).RepeatDays == (WeekDays.Monday | WeekDays.Friday), "parser: *mon,fri");
        Check(QuickAddParser.Parse("x *pzt,foo", today).Title == "x *pzt,foo", "parser: invalid day list stays in title");
        // Advance: Cuma 18:00 → Pzt/Çar/Cum maskesinde sonraki Pazartesi 18:00
        var fri = new DateTime(2026, 11, 13, 18, 0, 0); // Cuma
        var nextAfterFri = RecurrenceRules.Advance(fri, Recurrence.SelectedDays, WeekDays.Monday | WeekDays.Wednesday | WeekDays.Friday);
        Check(nextAfterFri == new DateTime(2026, 11, 16, 18, 0, 0), "advance Fri -> Mon keeps time: " + nextAfterFri);

        var q5 = QuickAddParser.Parse("e-posta @bilinmeyen #", today);
        Check(q5.Title == "e-posta @bilinmeyen #" && !q5.HasTokens, "parser: unknown tokens stay in title");

        vm.NewTaskTitle = "Fatura öde #ev @bugün @23:59 ~0,5";
        Check(vm.QuickAddPreview.Contains("#ev") && vm.QuickAddPreview.Contains("BUGÜN 23:59"), "preview: " + vm.QuickAddPreview);
        await vm.AddTaskCommand.ExecuteAsync(null);
        var fatura = vm.ActiveTasks.Single();
        Check(fatura.Title == "Fatura öde" && fatura.Category == "ev" && fatura.EstimatedHours == 0.5 && fatura.DueState == DueState.Today, "quick add creates parsed task");
        Check(vm.SmartViews.First(s => s.Value == SmartView.Today).Count == 1, "smart view TODAY count = 1");
        vm.SelectedSmartView = vm.SmartViews.First(s => s.Value == SmartView.Overdue);
        Check(vm.ActiveView.IsEmpty, "smart view OVERDUE filters out today task");
        vm.SelectedSmartView = vm.SmartViews[0];

        // ── Dakikalık saat işi: hiçbir şey değişmediyse liste yeniden kurulmaz (boştaki CPU sıçraması) ──
        var due = fatura.DueAt!.Value; // bugün 23:59; saatler sabit, test günün saatine bağlı değil
        int resets = 0;
        System.Collections.Specialized.NotifyCollectionChangedEventHandler onReset = (_, e) =>
        { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };
        ((System.Collections.Specialized.INotifyCollectionChanged)vm.ActiveView).CollectionChanged += onReset;
        bool refreshed = vm.RefreshTimeDependentState(due.AddMinutes(-30));
        Check(!refreshed && resets == 0 && fatura.DueState == DueState.Today, "minute tick: no state change -> no view rebuild");
        refreshed = vm.RefreshTimeDependentState(due.AddMinutes(1));
        Check(refreshed && resets == 1 && fatura.DueState == DueState.Overdue
            && vm.SmartViews.First(s => s.Value == SmartView.Overdue).Count == 1, "minute tick: TODAY -> OVERDUE refreshes views and counts");
        refreshed = vm.RefreshTimeDependentState(due.Date.AddDays(2).AddMinutes(30));
        Check(refreshed && resets == 2, "minute tick: day change alone (state stays OVERDUE) refreshes views");
        refreshed = vm.RefreshTimeDependentState(due.Date.AddDays(2).AddMinutes(31));
        Check(!refreshed && resets == 2, "minute tick: same day, same state -> no rebuild");
        vm.RefreshTimeDependentState(due.AddMinutes(-30)); // bugüne geri dön
        ((System.Collections.Specialized.INotifyCollectionChanged)vm.ActiveView).CollectionChanged -= onReset;
        Check(fatura.DueState == DueState.Today, "minute tick: state restored for following tests");
        await vm.DeleteTaskCommand.ExecuteAsync(fatura);
        await vm.UndoCommand.ExecuteAsync(null);
        Check(vm.ActiveTasks.Contains(fatura), "Ctrl+Z undo delete");
        vm.SearchText = "xyz"; vm.EscapeCommand.Execute(null);
        Check(vm.SearchText == "", "Esc clears search");
        await vm.DeleteTaskCommand.ExecuteAsync(fatura);
        vm.Toasts.Clear();

        // Editör: seçili günler (gün yoksa hata, varsa tarih otomatik + satır etiketi)
        vm.NewTaskTitle = "Gym"; await vm.AddTaskCommand.ExecuteAsync(null);
        var gym = vm.ActiveTasks.Single(t => t.Title == "Gym");
        vm.EditTaskCommand.Execute(gym);
        var ged = vm.Editor!;
        ged.SelectedRecurrence = ged.RecurrenceOptions.First(o => o.Value == Recurrence.SelectedDays);
        Check(ged.IsDaysRecurrence && ged.DayToggles.Count == 7 && ged.DayToggles[0].Label == "PZT", "editor shows 7 day chips (PZT first)");
        await vm.SaveEditCommand.ExecuteAsync(null);
        Check(vm.Editor is not null && ged.ValidationError is not null, "no day selected -> error: " + ged.ValidationError);
        ged.SelectWeekdaysCommand.Execute(null);
        ged.DueTimeText = "07:00";
        await vm.SaveEditCommand.ExecuteAsync(null);
        Check(vm.Editor is null && gym.RepeatDays == WeekDays.Weekdays && gym.RecurrenceText == "HAFTA İÇİ" && gym.DueAt is not null
              && RecurrenceRules.Contains(WeekDays.Weekdays, gym.DueAt.Value.DayOfWeek), "weekdays saved, due auto on a weekday: " + gym.DueAt);
        vm.EditTaskCommand.Execute(gym);
        vm.Editor!.ToggleDayCommand.Execute(vm.Editor.DayToggles[1]); // Salı kapat
        await vm.SaveEditCommand.ExecuteAsync(null);
        Check(gym.RecurrenceText == "PZT · ÇAR · PER · CUM", "row label lists days: " + gym.RecurrenceText);
        await vm.DeleteTaskCommand.ExecuteAsync(gym);
        vm.Toasts.Clear();

        vm.HoverBar(vm.ChartBars[3]);
        Check(vm.ChartBars[3].IsHovered && vm.SelectedDay == vm.ChartBars[3].Day, "hover selects bar");
        vm.EndHover();
        Check(vm.ChartBars.All(b => !b.IsHovered) && vm.SelectedDay == vm.ChartBars[^1].Day, "mouse leave clears reticle, log back to today");

        vm.NewTaskTitle = "Rapor yaz"; vm.NewTaskCategory = "iş"; vm.NewTaskHours = "1,5";
        await vm.AddTaskCommand.ExecuteAsync(null);
        vm.NewTaskTitle = "Spor"; vm.NewTaskCategory = "sağlık"; vm.NewTaskHours = "1";
        vm.SelectedPriorityOption = vm.PriorityOptions[1];
        await vm.AddTaskCommand.ExecuteAsync(null);
        Check(vm.ActiveTasks.Count == 2 && vm.ActiveTasks[0].Code == "#003" && vm.ActiveTasks[0].EstimatedHours == 1.5, "add tasks (#003, 1,5h)");
        Check(vm.HasCategories && vm.CategoryFilters.Count == 3, "category filters built");

        vm.SelectedCategoryFilter = vm.CategoryFilters.First(c => c.Value == "iş");
        Check(vm.ActiveView.Cast<CyberTask>().Count() == 1, "category filter");
        vm.SelectedCategoryFilter = vm.CategoryFilters[0];
        vm.SearchText = "RAPOR";
        Check(vm.ActiveView.Cast<CyberTask>().Single().Title == "Rapor yaz", "case-insensitive search");
        vm.SearchText = "";

        vm.SelectedSortOption = vm.SortOptions.First(o => o.Value == SortMode.Priority);
        Check(vm.ActiveView.Cast<CyberTask>().First().Title == "Spor", "sort by priority");

        var rapor = vm.ActiveTasks.First(t => t.Title == "Rapor yaz");
        vm.EditTaskCommand.Execute(rapor);
        var ed = vm.Editor!;
        ed.DueTimeText = "10:00";
        await vm.SaveEditCommand.ExecuteAsync(null);
        Check(vm.Editor is not null && ed.ValidationError is not null, "time without date rejected: " + ed.ValidationError);
        ed.SetDueTodayCommand.Execute(null);
        ed.DueTimeText = "23:59";
        ed.SelectedRecurrence = ed.RecurrenceOptions.First(o => o.Value == Recurrence.Weekly);
        ed.SelectedReminder = ed.ReminderOptions.First(o => o.Value == 15);
        ed.Notes = "Müşteri linki: example.com";
        ed.NewSubtaskTitle = "Taslak"; ed.AddSubtaskCommand.Execute(null);
        ed.NewSubtaskTitle = "Gözden geçir"; ed.AddSubtaskCommand.Execute(null);
        await vm.SaveEditCommand.ExecuteAsync(null);
        Check(vm.Editor is null && rapor.Recurrence == Recurrence.Weekly && rapor.Subtasks.Count == 2 && rapor.DueState == DueState.Today, "edit saved (weekly, due today, 2 subtasks)");

        Check(rapor.HasNotes && rapor.HasDetails, "notes saved -> HasNotes/HasDetails");
        vm.SearchText = "müşteri";
        Check(vm.ActiveView.Cast<CyberTask>().SingleOrDefault() == rapor, "search matches notes");
        vm.SearchText = "";

        // ── Focus (Pomodoro): bitmiş oturumu simüle et ──
        await vm.StartFocusCommand.ExecuteAsync(rapor);
        Check(vm.Focus.IsRunning && rapor.IsFocused && vm.Focus.RemainingText == "25:00", "focus started 25:00");
        int attention = 0; vm.AttentionRequested += (_, _) => attention++;
        clock.Advance(TimeSpan.FromMinutes(25));
        vm.Tick();
        Check(!vm.Focus.IsRunning && rapor.TrackedMinutes == 25 && attention >= 1, "focus completes, 25 min credited");
        Check(Math.Abs(vm.SelectedDay!.HoursWorked - 25 / 60.0) < 0.01 && vm.SelectedDay.Entries.Any(e => e.Status == LogStatus.Focus), "focus logged to today");
        await vm.StartFocusCommand.ExecuteAsync(rapor);
        await vm.StopFocusCommand.ExecuteAsync(null);
        Check(!vm.Focus.IsRunning && rapor.TrackedMinutes == 25, "stop <1 min credits nothing");
        vm.Toasts.Clear();
        attention = 0;

        await vm.ToggleSubtaskCommand.ExecuteAsync(rapor.Subtasks[0]);
        Check(rapor.Progress == 50 && rapor.SubtaskSummary == "1/2", "subtask toggle -> progress 50%");

        vm.EditTaskCommand.Execute(rapor); vm.Editor!.Title = "DEĞİŞTİ"; vm.CancelEditCommand.Execute(null);
        Check(rapor.Title == "Rapor yaz", "cancel edit leaves task unchanged");

        await vm.CompleteTaskCommand.ExecuteAsync(rapor);
        var next = vm.ActiveTasks.FirstOrDefault(t => t.Title == "Rapor yaz");
        Check(next is not null && next.DueAt!.Value.Date == DateTime.Today.AddDays(7) && next.Subtasks.All(s => !s.IsDone), "recurring spawn next week, subtasks reset");
        Check(vm.SelectedDay!.CompletedCount == 1 && Math.Abs(vm.SelectedDay.HoursWorked - 25 / 60.0) < 0.01 && rapor.CompletionCreditHours == 0,
              "completion after focus: no double count (estimate not added)");
        Check(next!.Notes == rapor.Notes && next.TrackedMinutes == 0, "recurring copy keeps notes, resets tracked time");
        Check(vm.Toasts.Any(t => t.Message.StartsWith("↻")), "recurring toast shown");

        await Task.Delay(400);
        await vm.RestoreTaskCommand.ExecuteAsync(rapor);
        Check(vm.ActiveTasks.Count(t => t.Title == "Rapor yaz") == 1 && vm.SelectedDay.CompletedCount == 0
              && Math.Abs(vm.SelectedDay.HoursWorked - 25 / 60.0) < 0.01, "restore removes spawned copy + log, keeps focus time");

        var spor = vm.ActiveTasks.First(t => t.Title == "Spor");
        await vm.DeleteTaskCommand.ExecuteAsync(spor);
        Check(!vm.ActiveTasks.Contains(spor), "delete");
        var undo = vm.Toasts.Last(t => t.HasAction);
        await vm.RunToastActionCommand.ExecuteAsync(undo);
        Check(vm.ActiveTasks.Contains(spor), "undo delete");
        await vm.RunToastActionCommand.ExecuteAsync(undo);
        Check(vm.ActiveTasks.Count(t => t == spor) == 1, "double undo is ignored");

        vm.EditTaskCommand.Execute(spor);
        vm.Editor!.SetDueTodayCommand.Execute(null); vm.Editor.DueTimeText = "00:00";
        vm.Editor.SelectedReminder = vm.Editor.ReminderOptions.First(o => o.Value == 0);
        int fired = 0; vm.AttentionRequested += (_, _) => fired++;
        await vm.SaveEditCommand.ExecuteAsync(null);
        await Task.Delay(1300);
        Check(fired == 1 && spor.ReminderFired && vm.Toasts.Any(t => t.Kind == ToastKind.Reminder), "reminder fires once");
        Check(spor.DueState == DueState.Overdue, "overdue state");

        vm.SelectedLanguageOption = vm.LanguageOptions[1];
        Check(Localizer.Instance["Tasks_Header"] == "DAILY_OBJECTIVES" && vm.SelectedDayTitle.EndsWith("ACTIVITY LOG"), "switch to English: " + vm.SelectedDayTitle);
        string expectedLabel = DateTime.Today.ToString("MMM d", System.Globalization.CultureInfo.GetCultureInfo("en-US")).ToUpperInvariant();
        Check(vm.ChartBars[^1].Label == expectedLabel, "chart labels English: " + vm.ChartBars[^1].Label);
        vm.SelectedScaleOption = vm.ScaleOptions[1];
        await vm.ShutdownAsync();

        var vm2 = await Fresh();
        var spor2 = vm2.ActiveTasks.First(t => t.Title == "Spor");
        Check(Localizer.Instance.Language == AppLanguage.English && Math.Abs(vm2.UiScale - 0.85) < 1e-9, "settings persisted (EN, 85%)");
        Check(spor2.ReminderFired && !vm2.Toasts.Any(), "fired reminder not repeated after reload");
        var rapor2 = vm2.ActiveTasks.First(t => t.Title == "Rapor yaz");
        Check(rapor2.Subtasks.Count == 2 && rapor2.Recurrence == Recurrence.Weekly && rapor2.HasDueTime, "task fields persisted");
        vm2.SelectedLanguageOption = vm2.LanguageOptions[0];
        await vm2.ShutdownAsync();

        File.WriteAllText(path, """
        {"schemaVersion":1,"nextTaskNumber":5,"tasks":[{"id":"583a7c92-0542-4a09-b8d7-0a6290efdd0e","number":4,"title":"OLD","priority":"Decrypting","estimatedHours":2,"createdAt":"2026-11-09T20:04:19+03:00","progress":68,"isCompleted":false,"completedAt":null}],"days":[]}
        """);
        var vm3 = await Fresh();
        Check(vm3.ActiveTasks.Single().Title == "OLD" && vm3.ActiveTasks[0].Subtasks.Count == 0, "v1 JSON loads");
        await vm3.ShutdownAsync();

        // v2 → v3 geçişi: eski tamamlanmış görevin geri alınması tahmini süreyi düşmeli
        string todayIso = DateTime.Today.ToString("yyyy-MM-dd");
        File.WriteAllText(path, $$"""
        {"schemaVersion":2,"nextTaskNumber":2,"settings":{"language":"Turkish","uiScale":0.7},
         "tasks":[{"id":"11111111-1111-1111-1111-111111111111","number":1,"title":"V2","estimatedHours":2,"isCompleted":true,"completedAt":"{{todayIso}}T10:00:00","progress":100}],
         "days":[{"date":"{{todayIso}}","hoursWorked":2,"completedCount":1,"entries":[{"time":"10:00","description":"#001 V2","status":"Done","taskId":"11111111-1111-1111-1111-111111111111"}]}]}
        """);
        var vm5 = await Fresh();
        Check(Math.Abs(vm5.UiScale - 0.7) < 1e-9, "explicit saved 70% scale respected");
        await vm5.RestoreTaskCommand.ExecuteAsync(vm5.CompletedTasks.Single());
        Check(vm5.SelectedDay!.HoursWorked == 0 && vm5.SelectedDay.CompletedCount == 0, "v2->v3 migration: restore subtracts estimate");
        await vm5.ShutdownAsync();

        File.WriteAllText(path, "{ not json");
        var vm4 = await Fresh();
        Check(vm4.ErrorMessage is not null && Directory.GetFiles(dir, "cyber_data.corrupt-*").Length == 1, "corrupt file backed up: " + vm4.ErrorMessage);
        await vm4.ShutdownAsync();

        // ── UpdateService (ağsız) ──
        const string releaseJson = """
        {"tag_name":"v2.3.1","html_url":"https://github.com/acme/calabatica/releases/tag/v2.3.1","draft":false,"prerelease":false,
         "published_at":"2026-11-01T10:00:00Z",
         "assets":[{"name":"CALABATICA-win-x64.zip","browser_download_url":"https://github.com/acme/calabatica/releases/download/v2.3.1/CALABATICA-win-x64.zip"},
                   {"name":"CALABATICA-win-x64.zip.sha256","browser_download_url":"https://github.com/acme/calabatica/releases/download/v2.3.1/CALABATICA-win-x64.zip.sha256"},
                   {"name":"evil.zip","browser_download_url":"https://evil.example/x.zip"}]}
        """;
        var rel = UpdateService.ParseRelease(releaseJson);
        Check(rel is not null && rel.Version == new Version(2, 3, 1) && rel.AssetUrl is not null && rel.ChecksumUrl is not null, "parse release + assets");
        Check(UpdateService.ParseRelease(releaseJson.Replace("https://github.com/acme/calabatica/releases/tag", "https://evil.example/x")) is null, "non-github page url rejected");
        Check(UpdateService.ParseRelease(releaseJson.Replace("\"prerelease\":false", "\"prerelease\":true")) is null, "prerelease ignored");
        Check(!UpdateService.IsValidRepository("acme/..") && !UpdateService.IsValidRepository("acme/x?y=1") && !UpdateService.IsValidRepository("../x")
              && UpdateService.IsValidRepository("acme/calabatica"), "repository validation (no path/query injection)");
        Check(UpdateService.FormatVersion(new Version(2, 0, 0)) == "v2.0" && UpdateService.FormatVersion(new Version(2, 1, 3)) == "v2.1.3", "version format");
        var upd = new UpdateService("acme/calabatica", new Version(2, 0, 0), _ => Task.FromResult(releaseJson));
        Check((await upd.CheckAsync()).Status == UpdateCheckStatus.UpdateAvailable, "update available 2.0 -> 2.3.1");
        var same = new UpdateService("acme/calabatica", new Version(2, 3, 1), _ => Task.FromResult(releaseJson));
        Check((await same.CheckAsync()).Status == UpdateCheckStatus.UpToDate, "up to date");
        var fail = new UpdateService("acme/calabatica", new Version(2, 0, 0), _ => throw new System.Net.Http.HttpRequestException("404"));
        Check((await fail.CheckAsync()).Status == UpdateCheckStatus.Failed, "network failure -> Failed (no throw)");
        Check((await new UpdateService(null, new Version(2, 0, 0)).CheckAsync()).Status == UpdateCheckStatus.NotConfigured, "unconfigured -> no network");
        var noRelease = new UpdateService("acme/calabatica", new Version(2, 0, 0),
            _ => throw new System.Net.Http.HttpRequestException("Not Found", null, System.Net.HttpStatusCode.NotFound));
        var noReleaseResult = await noRelease.CheckAsync();
        Check(noReleaseResult.Status == UpdateCheckStatus.NoRelease && noReleaseResult.Error is null, "HTTP 404 -> NoRelease (not a failure)");
        var forbidden = new UpdateService("acme/calabatica", new Version(2, 0, 0),
            _ => throw new System.Net.Http.HttpRequestException("rate limited", null, System.Net.HttpStatusCode.Forbidden));
        Check((await forbidden.CheckAsync()).Status == UpdateCheckStatus.Failed, "HTTP 403 (rate limit) stays a failure");
        var vmNoRelease = new MainViewModel(new StorageService(Path.Combine(ctx.Dir, "norelease", "cyber_data.json")), noRelease);
        await vmNoRelease.CheckUpdatesCommand.ExecuteAsync(null);
        Check(vmNoRelease.UpdateStatusText == Localizer.Instance["Update_NoRelease"] && !vmNoRelease.HasUpdate, "settings text for 404: 'no published release yet'");

        // ── Yeni VM: tema, başlangıç, güncelleme rozeti, şablon, sıralama, istatistik, takvim ──
        File.Delete(path);
        var startup = new InMemoryStartupRegistration();
        var vm6 = new MainViewModel(new StorageService(path), upd, startup);
        await vm6.InitializeAsync();
        await Task.Delay(200); // açılıştaki otomatik güncelleme denetimi
        Check(vm6.VersionText == "v2.0" && vm6.HasUpdate && vm6.UpdateBadgeText == "▲ v2.3.1", "header version + update badge: " + vm6.UpdateBadgeText);
        Uri? opened = null; vm6.OpenUrlRequested += (_, u) => opened = u;
        vm6.OpenReleasePageCommand.Execute(null);
        Check(opened?.Host == "github.com", "open release page -> github url");

        vm6.SelectedThemeOption = vm6.ThemeOptions.First(o => o.Value == ThemeId.Matrix);
        Check(vm6.CurrentTheme.Id == ThemeId.Matrix, "theme switch");
        vm6.SelectedStartupOption = vm6.SoundOptions[0];
        Check(startup.IsEnabled(), "start with Windows -> registration enabled");
        vm6.SelectedTrayOption = vm6.SoundOptions[1];
        Check(!vm6.MinimizeToTray, "close-to-tray off");

        foreach (var title in new[] { "A", "B", "C" }) { vm6.NewTaskTitle = title; await vm6.AddTaskCommand.ExecuteAsync(null); }
        var tA = vm6.ActiveTasks.First(t => t.Title == "A");
        var tC = vm6.ActiveTasks.First(t => t.Title == "C");
        await vm6.MoveTaskAsync(tA, tC, after: true); // A'yı C'nin altına
        Check(string.Join("", vm6.ActiveView.Cast<CyberTask>().Select(t => t.Title)) == "BCA" && vm6.SelectedSortOption.Value == SortMode.Manual, "drag A below C -> BCA, manual sort");
        await vm6.MoveTaskAsync(tC, null);
        Check(string.Join("", vm6.ActiveView.Cast<CyberTask>().Select(t => t.Title)) == "BAC", "drop on list end -> BAC");

        vm6.NewTemplateName = "Sabah";
        await vm6.CreateTemplateCommand.ExecuteAsync(null);
        Check(vm6.Templates.Count == 1 && vm6.Templates[0].Items.Count == 3, "template from 3 visible tasks");
        await vm6.ApplyTemplateCommand.ExecuteAsync(vm6.Templates[0]);
        Check(vm6.ActiveTasks.Count == 6 && vm6.ActiveTasks.Select(t => t.Number).Distinct().Count() == 6, "apply template adds 3 new numbered tasks");

        await vm6.CompleteTaskCommand.ExecuteAsync(vm6.ActiveTasks[0]);
        vm6.ShowStatsCommand.Execute(null);
        Check(vm6.StatTiles.Count == 6 && vm6.StatTiles[0].Value.StartsWith("1 ") && vm6.WeekdayStats.Count == 7, "stats: streak 1 day, 7 weekday bars: " + vm6.StatTiles[0].Value);

        // seri: bugün boş, dün ve önceki gün dolu -> 2 (bugün henüz bozmaz)
        var t0 = DateOnly.FromDateTime(DateTime.Today);
        var fake = new Dictionary<DateOnly, DayActivity>
        {
            [t0.AddDays(-1)] = new() { Date = t0.AddDays(-1), CompletedCount = 1 },
            [t0.AddDays(-2)] = new() { Date = t0.AddDays(-2), CompletedCount = 2 },
            [t0.AddDays(-5)] = new() { Date = t0.AddDays(-5), CompletedCount = 1 },
            [t0.AddDays(-6)] = new() { Date = t0.AddDays(-6), CompletedCount = 1 },
            [t0.AddDays(-7)] = new() { Date = t0.AddDays(-7), CompletedCount = 1 },
        };
        Check(InsightsBuilder.CurrentStreak(fake, t0) == 2 && InsightsBuilder.LongestStreak(fake) == 3, "streak math (current 2, longest 3)");

        vm6.NewTaskTitle = "Yoga *pzt,çar @07:00"; await vm6.AddTaskCommand.ExecuteAsync(null);
        vm6.ShowCalendarCommand.Execute(null);
        var mondaysInGrid = vm6.CalendarCells.Where(c => c.Date >= t0 && c.Date.DayOfWeek == DayOfWeek.Monday).ToList();
        Check(vm6.CalendarCells.Count == 42 && mondaysInGrid.All(c => c.AllItems.Any(i => i.Title == "Yoga")), "calendar 42 cells, recurring Yoga on every upcoming Monday");
        Check(vm6.CalendarCells.Single(c => c.IsToday).IsSelected && vm6.CalendarTitle.Length > 0, "calendar selects today: " + vm6.CalendarTitle);
        var nextMon = mondaysInGrid.First(c => c.Date > t0);
        vm6.SelectCalendarDayCommand.Execute(nextMon);
        Check(vm6.SelectedCalendarItems.Any(i => i.Title == "Yoga") && vm6.SelectedDay?.Date == nextMon.Date, "select calendar day -> items + log day");
        vm6.NextMonthCommand.Execute(null);
        Check(vm6.CalendarCells.Count == 42 && vm6.CalendarCells.Count(c => c.IsCurrentMonth) >= 28, "next month grid");
        vm6.ShowLast30DaysCommand.Execute(null);
        Check(vm6.ChartBars.Count == 30 && vm6.SelectedDay?.Date == t0, "back to chart -> log today");
        await vm6.ShutdownAsync();

        var vm7 = new MainViewModel(new StorageService(path), null, startup);
        await vm7.InitializeAsync();
        Check(vm7.Templates.Count == 1 && vm7.CurrentTheme.Id == ThemeId.Matrix && !vm7.MinimizeToTray && vm7.SelectedSortOption.Value == SortMode.Manual,
              "templates/theme/tray/sort persisted");
        Check(vm7.SelectedStartupOption.Value, "startup state read from registration");
        vm7.SelectedLanguageOption = vm7.LanguageOptions[1];
        await vm7.ShutdownAsync();

        // Hata: İngilizce kayıtla açılışta takvim gün başlıkları Türkçe kalıyordu
        Localizer.Instance.SetLanguage(AppLanguage.Turkish);
        var vm8 = new MainViewModel(new StorageService(path));
        await vm8.InitializeAsync();
        Check(vm8.WeekdayHeaders[0] == "MON" && vm8.WeekdayHeaders[6] == "SUN", "English on startup -> calendar headers English: " + string.Join(",", vm8.WeekdayHeaders));
        vm8.SelectedLanguageOption = vm8.LanguageOptions[0];
        await vm8.ShutdownAsync();

        // ── Sızıntı: düzenleyici kapandıktan sonra gün çipleri bellekten silinebilmeli ──
        var vm9 = new MainViewModel(new StorageService(Path.Combine(dir, "leak.json")));
        await vm9.InitializeAsync();
        vm9.NewTaskTitle = "Leak probe"; await vm9.AddTaskCommand.ExecuteAsync(null);
        WeakReference OpenAndCloseEditor()
        {
            vm9.EditTaskCommand.Execute(vm9.ActiveTasks[0]);
            var weak = new WeakReference(vm9.Editor!.DayToggles[0]);
            vm9.CancelEditCommand.Execute(null);
            return weak;
        }
        var probe = OpenAndCloseEditor();
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Check(!probe.IsAlive, "closed editor's day chips are garbage-collected (no event leak)");

        // ── Kayıt toplama: art arda 20 değişiklik tek yazım; kapanışta bekleyen kayıt diske iner ──
        string coalescePath = Path.Combine(dir, "leak.json");
        var before = File.Exists(coalescePath) ? File.GetLastWriteTimeUtc(coalescePath) : DateTime.MinValue;
        for (int i = 0; i < 20; i++) { await vm9.SetTaskProgressAsync(vm9.ActiveTasks[0], i * 5); }
        Check((File.Exists(coalescePath) ? File.GetLastWriteTimeUtc(coalescePath) : DateTime.MinValue) == before, "rapid changes are not written immediately (coalesced)");
        await Task.Delay(700);
        var afterDelay = File.GetLastWriteTimeUtc(coalescePath);
        Check(afterDelay > before && File.ReadAllText(coalescePath).Contains("\"progress\": 95"), "coalesced save lands after delay with final value");
        await vm9.SetTaskProgressAsync(vm9.ActiveTasks[0], 40);
        await vm9.ShutdownAsync(); // gecikmeyi beklemeden yazmalı
        Check(File.ReadAllText(coalescePath).Contains("\"progress\": 40"), "shutdown flushes pending save immediately");
        await vm9.SetTaskProgressAsync(vm9.ActiveTasks[0], 60);
        vm9.FlushNow(); // oturum kapanışı yolu (senkron)
        Check(File.ReadAllText(coalescePath).Contains("\"progress\": 60"), "FlushNow writes synchronously (session ending)");
    }
}
