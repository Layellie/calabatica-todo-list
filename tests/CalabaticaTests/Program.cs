using System.IO;
using System.Windows.Threading;
using CyberTodo;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.Views;
using CyberTodo.ViewModels;

int failures = 0;
void Check(bool ok, string name) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}"); if (!ok) failures++; }

string dir = Path.Combine(Path.GetTempPath(), "cybertodo-harness-" + Guid.NewGuid().ToString("N"));
string path = Path.Combine(dir, "cyber_data.json");
AppLog.Configure(Path.Combine(dir, "logs")); // testler gerçek log klasörüne yazmasın
// Paylaşılan sahte saat (öğlen): "bugün 23:59" gibi testler gün içindeki saatten bağımsız.
var clock = new ManualTime(DateTime.Today.AddHours(12));
// Rehber ayrı test edilir; buradaki senaryolar "rehberi kapatmış kullanıcı" ile çalışır.
async Task<MainViewModel> Fresh() { var v = new MainViewModel(new StorageService(path), time: clock); await v.InitializeAsync(); v.CloseOnboardingCommand.Execute(null); return v; }

var thread = new Thread(() =>
{
    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
    var frame = new DispatcherFrame();
    Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
    {
        try
        {
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

            // ══════════════ Odak oturumları ══════════════
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
            var reloaded = await StorageService.ReadFileAsync(Path.Combine(dir, "undo.json"));
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

            // ══════════════ Yedekler ══════════════
            string bkData = Path.Combine(dir, "bk", "cyber_data.json");
            var backups = new BackupService(bkData);
            byte[] payload = StorageService.Serialize(new CyberData());
            for (int i = 0; i < 9; i++)
            {
                await backups.CreateDailyAsync(payload, fToday.AddDays(-i));
            }

            Check(backups.List().Count(b => b.IsDaily) == BackupService.KeepDaily
                && backups.List().All(b => b.FileName != BackupService.DailyFileName(fToday.AddDays(-8))), "backup: keeps newest 7 daily backups");
            Check(await backups.CreateDailyAsync(payload, fToday) is null, "backup: once per day");
            for (int i = 0; i < 7; i++)
            {
                await backups.CreateSafetyAsync(payload, DateTime.Now.AddSeconds(i), "manual");
            }

            Check(backups.List().Count(b => !b.IsDaily) == BackupService.KeepSafety, "backup: keeps 5 safety backups");
            Check(!backups.Contains(Path.Combine(dir, "bk", "cyber_data.json"))
                && !backups.Contains(Path.Combine(backups.BackupDirectory, "..", "cyber_data-x.json"))
                && backups.Contains(backups.List()[0].FullPath), "backup: only files inside backup folder are accepted");
            bool rejected = false;
            try { await backups.CreateSafetyAsync(payload, DateTime.Now, "..\\evil"); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "backup: reason cannot inject a path");

            // VM: açılışta günlük yedek, geri yükleme, içe/dışa aktarma
            string vmData = Path.Combine(dir, "vmdata", "cyber_data.json");
            var seed = new CyberData();
            seed.Tasks.Add(new CyberTask { Number = 1, Title = "Original" });
            seed.NextTaskNumber = 2;
            await StorageService.WriteAtomicAsync(vmData, StorageService.Serialize(seed));
            var dialogs = new FakeDialogs();
            var dv = new MainViewModel(new StorageService(vmData), dialogs: dialogs);
            await dv.InitializeAsync();
            await Task.Delay(300);
            var dvBackups = new BackupService(vmData);
            Check(dvBackups.List().Any(b => b.FileName == BackupService.DailyFileName(fToday)), "backup: daily backup taken on launch");

            var other = new CyberData();
            other.Tasks.Add(new CyberTask { Number = 1, Title = "From backup 1" });
            other.Tasks.Add(new CyberTask { Number = 2, Title = "From backup 2", IsCompleted = true, CompletedAt = DateTime.Now });
            string otherPath = await dvBackups.CreateSafetyAsync(StorageService.Serialize(other), DateTime.Now.AddMinutes(-5), "test");
            dv.OpenDataCommand.Execute(null);
            var otherInfo = dv.Backups.First(b => b.FullPath == otherPath);

            dialogs.ConfirmResult = false;
            await dv.RestoreBackupCommand.ExecuteAsync(otherInfo);
            Check(dv.ActiveTasks.Single().Title == "Original" && dialogs.LastConfirm.Contains("2"), "restore: declined -> nothing changes");

            dialogs.ConfirmResult = true;
            u.NewTaskTitle = string.Empty;
            dv.NewTaskTitle = "Undo me later";
            await dv.AddTaskCommand.ExecuteAsync(null);
            await dv.RestoreBackupCommand.ExecuteAsync(otherInfo);
            Check(dv.ActiveTasks.Single().Title == "From backup 1" && dv.CompletedTasks.Single().Title == "From backup 2", "restore: data replaced");
            Check(!dv.CanUndo, "restore: undo history cleared (would point to old data)");
            Check(File.ReadAllText(vmData).Contains("From backup 1"), "restore: written to disk immediately");
            Check(dvBackups.List().Any(b => b.FileName.Contains("before-restore")) && !dv.IsDataOpen, "restore: safety backup of previous state, panel closed");
            var safetyFile = dvBackups.List().First(b => b.FileName.Contains("before-restore"));
            Check((await StorageService.ReadFileAsync(safetyFile.FullPath)).Tasks.Any(t => t.Title == "Undo me later"), "restore: safety backup contains the replaced data");

            string exportPath = Path.Combine(dir, "export", "out.json");
            dialogs.SavePath = exportPath;
            await dv.ExportJsonCommand.ExecuteAsync(null);
            Check((await StorageService.ReadFileAsync(exportPath)).Tasks.Count == 2, "export: JSON round-trips");
            dialogs.SavePath = Path.Combine(dir, "export", "out.csv");
            await dv.ExportCsvCommand.ExecuteAsync(null);
            byte[] csvBytes = File.ReadAllBytes(dialogs.SavePath);
            Check(csvBytes.Length > 3 && csvBytes[0] == 0xEF && csvBytes[1] == 0xBB && csvBytes[2] == 0xBF, "export: CSV has UTF-8 BOM for Excel");

            string badImport = Path.Combine(dir, "export", "bad.json");
            File.WriteAllText(badImport, "{ not json");
            dialogs.OpenPath = badImport;
            await dv.ImportJsonCommand.ExecuteAsync(null);
            Check(dv.ErrorMessage is not null && dv.ActiveTasks.Single().Title == "From backup 1", "import: invalid file -> error, data untouched");
            dv.DismissErrorCommand.Execute(null);

            seed.Tasks.Add(new CyberTask { Number = 2, Title = "Imported 2" });
            string goodImport = Path.Combine(dir, "export", "good.json");
            await StorageService.WriteAtomicAsync(goodImport, StorageService.Serialize(seed));
            dialogs.OpenPath = goodImport;
            await dv.ImportJsonCommand.ExecuteAsync(null);
            Check(dv.ActiveTasks.Count == 2 && dv.ActiveTasks.Any(t => t.Title == "Imported 2")
                && dvBackups.List().Any(b => b.FileName.Contains("before-import")), "import: replaces data after safety backup");

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

            // ══════════════ Otomatik güncelleme ══════════════
            Check(UpdateInstaller.ParseChecksum("ABCDEF0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789  CALABATICA-win-x64.zip\n")
                == "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", "update: checksum file parsed");
            Check(UpdateInstaller.IsAllowedDownloadUri(new Uri("https://objects.githubusercontent.com/x"))
                && !UpdateInstaller.IsAllowedDownloadUri(new Uri("http://github.com/x"))
                && !UpdateInstaller.IsAllowedDownloadUri(new Uri("https://github.com.evil.com/x"))
                && !UpdateInstaller.IsAllowedDownloadUri(new Uri("https://evilgithubusercontent.com/x")), "update: only https GitHub hosts");

            string realExe = Path.Combine(AppContext.BaseDirectory, "CALABATICA.exe"); // ürün adı/sürümü gömülü gerçek exe (1.0.0)
            string installDir = Path.Combine(dir, "install");
            Directory.CreateDirectory(installDir);
            string installedExe = Path.Combine(installDir, "CALABATICA.exe");
            File.WriteAllText(installedExe, "old build");
            Check(!new UpdateInstaller(installedExe).CanSelfUpdate, "update: not an installed copy without Uninstall.exe");
            File.WriteAllText(Path.Combine(installDir, "Uninstall.exe"), "marker");

            byte[] goodZip = MakeZip(("CALABATICA.exe", File.ReadAllBytes(realExe)));
            string goodSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(goodZip));
            var assetUri = new Uri("https://github.com/o/r/releases/download/v1.0.0/CALABATICA-win-x64.zip");
            var shaUri = new Uri("https://github.com/o/r/releases/download/v1.0.0/CALABATICA-win-x64.zip.sha256");
            var release = new ReleaseInfo(new Version(1, 0, 0), "v1.0.0", new Uri("https://github.com/o/r/releases/tag/v1.0.0"), assetUri, shaUri, null);

            async Task<string?> TryStage(FakeHttp http, ReleaseInfo rel)
            {
                try { return await new UpdateInstaller(installedExe, http).DownloadAndStageAsync(rel); }
                catch (InvalidDataException ex) { return "REJECTED:" + ex.Message; }
            }

            var badSha = new FakeHttp { [assetUri] = goodZip, [shaUri] = System.Text.Encoding.ASCII.GetBytes(new string('0', 64)) };
            string? stagedBad = await TryStage(badSha, release);
            Check(stagedBad?.StartsWith("REJECTED:SHA256") == true && !File.Exists(installedExe + ".new") && !File.Exists(Path.Combine(installDir, "CALABATICA.update.zip")),
                "update: SHA256 mismatch rejected, no files left: " + stagedBad);

            byte[] extraZip = MakeZip(("CALABATICA.exe", File.ReadAllBytes(realExe)), ("..\\evil.dll", [1, 2, 3]));
            var extra = new FakeHttp { [assetUri] = extraZip, [shaUri] = System.Text.Encoding.ASCII.GetBytes(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(extraZip))) };
            Check((await TryStage(extra, release))?.StartsWith("REJECTED:Unexpected update package content") == true, "update: package with extra/traversal entries rejected");

            var redirect = new FakeHttp { [assetUri] = goodZip, [shaUri] = System.Text.Encoding.ASCII.GetBytes(goodSha), RedirectTo = new Uri("https://evil.example.com/x.zip") };
            Check((await TryStage(redirect, release))?.StartsWith("REJECTED:Redirected") == true, "update: redirect to foreign host rejected");

            var good = new FakeHttp { [assetUri] = goodZip, [shaUri] = System.Text.Encoding.ASCII.GetBytes(goodSha + "  CALABATICA-win-x64.zip") };
            Check((await TryStage(good, release with { Version = new Version(9, 9, 9) }))?.StartsWith("REJECTED:Version mismatch") == true, "update: version mismatch rejected");

            string json = $$"""
                {"tag_name":"v1.0.0","html_url":"https://github.com/o/r/releases/tag/v1.0.0","draft":false,"prerelease":false,
                 "assets":[{"name":"CALABATICA-win-x64.zip","browser_download_url":"{{assetUri}}"},
                           {"name":"CALABATICA-win-x64.zip.sha256","browser_download_url":"{{shaUri}}"}]}
                """;
            var updVm = new MainViewModel(new StorageService(Path.Combine(dir, "upd.json")),
                new UpdateService("o/r", new Version(0, 9, 0), _ => Task.FromResult(json)),
                installer: new UpdateInstaller(installedExe, good));
            await updVm.InitializeAsync();
            string? restartedWith = null;
            updVm.RestartRequested += (_, path) => restartedWith = path;
            await updVm.CheckUpdatesCommand.ExecuteAsync(null);
            Check(updVm.CanInstallUpdate && updVm.Toasts.Any(t => t.HasAction && t.ActionLabel == Localizer.Instance["Update_Install"]), "update: install offered (button + toast)");
            await updVm.InstallUpdateCommand.ExecuteAsync(null);
            Check(restartedWith == installedExe && File.ReadAllBytes(installedExe).SequenceEqual(File.ReadAllBytes(realExe))
                && File.ReadAllText(installedExe + ".old") == "old build", "update: verified exe swapped in, old kept, restart requested");
            UpdateInstaller.CleanupLeftovers(installedExe);
            Check(!File.Exists(installedExe + ".old"), "update: leftovers cleaned on next launch");

            var notInstalled = new MainViewModel(new StorageService(Path.Combine(dir, "upd2.json")),
                new UpdateService("o/r", new Version(0, 9, 0), _ => Task.FromResult(json)),
                installer: new UpdateInstaller(Path.Combine(dir, "CALABATICA.exe"), good));
            await notInstalled.InitializeAsync();
            await notInstalled.CheckUpdatesCommand.ExecuteAsync(null);
            Check(notInstalled.HasUpdate && !notInstalled.CanInstallUpdate, "update: dev build only offers the release page");

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

            // ══════════════ İlk açılış rehberi ══════════════
            var ob = new MainViewModel(new StorageService(Path.Combine(dir, "onboarding", "cyber_data.json")), time: new ManualTime(DateTime.Today.AddHours(12)));
            await ob.InitializeAsync();
            Check(ob.IsOnboardingOpen && ob.OnboardingStepText == "1 / 3" && !ob.PreviousOnboardingStepCommand.CanExecute(null) && ob.OnboardingBody.Length > 50,
                "onboarding: opens on first run at step 1");
            ob.NextOnboardingStepCommand.Execute(null);
            ob.NextOnboardingStepCommand.Execute(null);
            Check(ob.IsLastOnboardingStep && ob.PreviousOnboardingStepCommand.CanExecute(null) && ob.OnboardingTitle != "!Onb_3_Title!", "onboarding: step 3 is last");
            ob.NextOnboardingStepCommand.Execute(null);
            ob.FlushNow();
            Check(!ob.IsOnboardingOpen && (await StorageService.ReadFileAsync(Path.Combine(dir, "onboarding", "cyber_data.json"))).Settings.OnboardingDone,
                "onboarding: finishing closes it and remembers");
            var ob2 = new MainViewModel(new StorageService(Path.Combine(dir, "onboarding", "cyber_data.json")));
            await ob2.InitializeAsync();
            Check(!ob2.IsOnboardingOpen, "onboarding: not shown again on next launch");
            ob2.OpenOnboardingCommand.Execute(null);
            Check(ob2.IsOnboardingOpen && ob2.OnboardingStep == 0, "onboarding: ? reopens from step 1");
            ob2.EscapeCommand.Execute(null);
            Check(!ob2.IsOnboardingOpen, "onboarding: Esc closes");

            // ══════════════ Kayıtlı görünümler ══════════════
            var sfPath = Path.Combine(dir, "filters", "cyber_data.json");
            var sf = new MainViewModel(new StorageService(sfPath), time: new ManualTime(DateTime.Today.AddHours(9)));
            await sf.InitializeAsync();
            sf.CloseOnboardingCommand.Execute(null);
            foreach (string title in new[] { "Write report #work @today @17:00", "Read reports #work", "Dishes #home @today @20:00" })
            {
                sf.NewTaskTitle = title;
                await sf.AddTaskCommand.ExecuteAsync(null);
            }

            Check(!sf.SaveCurrentFilterCommand.CanExecute(null), "filters: default view cannot be saved");
            sf.SelectedCategoryFilter = sf.CategoryFilters.First(o => o.Value == "work");
            sf.SelectedSmartView = sf.SmartViews.First(o => o.Value == SmartView.Today);
            sf.SearchText = "report";
            await sf.SaveCurrentFilterCommand.ExecuteAsync(null);
            var saved = sf.SavedFilters.Single();
            Check(saved.Name.Contains("#work") && saved.Name.Contains("“report”") && saved.IsActive && !sf.SaveCurrentFilterCommand.CanExecute(null),
                "filters: auto-named, active, duplicate cannot be saved: " + saved.Name);
            sf.SearchText = string.Empty;
            sf.SelectedCategoryFilter = sf.CategoryFilters[0];
            sf.SelectedSmartView = sf.SmartViews[0];
            Check(!saved.IsActive && sf.ActiveView.Cast<CyberTask>().Count() == 3, "filters: changing filters deactivates the chip");
            sf.ApplySavedFilterCommand.Execute(saved);
            Check(saved.IsActive && sf.ActiveView.Cast<CyberTask>().Select(t => t.Title).SequenceEqual(["Write report"]), "filters: applying restores search + category + list");
            sf.FlushNow();
            var sfReloaded = new MainViewModel(new StorageService(sfPath));
            await sfReloaded.InitializeAsync();
            Check(sfReloaded.SavedFilters.Count == 1 && sfReloaded.SavedFilters[0].Category == "work" && sfReloaded.SavedFilters[0].SmartView == SmartView.Today,
                "filters: persisted");
            await sf.DeleteSavedFilterCommand.ExecuteAsync(saved);
            Check(!sf.HasSavedFilters && sf.SaveCurrentFilterCommand.CanExecute(null), "filters: delete");

            // ══════════════ Yüksek kontrast ══════════════
            sf.SelectedThemeOption = sf.ThemeOptions.First(o => o.Value == ThemeId.Matrix);
            sf.SetSystemHighContrast(true);
            Check(sf.CurrentTheme.IsHighContrast && sf.SelectedThemeOption.Value == ThemeId.Matrix, "contrast: Windows high contrast overrides the palette, choice kept");
            sf.SelectedThemeOption = sf.ThemeOptions.First(o => o.Value == ThemeId.Amber);
            Check(sf.CurrentTheme.IsHighContrast, "contrast: picking a theme while high contrast is on keeps it readable");
            sf.SetSystemHighContrast(false);
            Check(sf.CurrentTheme.Id == ThemeId.Amber, "contrast: turning it off restores the chosen theme");
            Check(sf.ThemeOptions.Any(o => o.Value == ThemeId.HighContrast), "contrast: also selectable manually");

            // ══════════════ Şema geçişleri (örnek eski dosyalar) ══════════════
            Check(SchemaMigrations.LatestVersion == CyberData.CurrentSchemaVersion, "migrations: last step matches CurrentSchemaVersion");
            string v2Json = """
                {"schemaVersion":2,"nextTaskNumber":3,"tasks":[
                  {"id":"6f1f8e8e-0000-4000-8000-000000000001","number":1,"title":"Old done","estimatedHours":2.5,"isCompleted":true,"completedAt":"2025-01-02T10:00:00"},
                  {"id":"6f1f8e8e-0000-4000-8000-000000000002","number":2,"title":"Daily","recurrence":"Daily","createdAt":"2025-01-01T09:00:00"}]}
                """;
            string v2Path = Path.Combine(dir, "v2.json");
            File.WriteAllText(v2Path, v2Json);
            var v2 = await StorageService.ReadFileAsync(v2Path);
            Check(v2.SchemaVersion == CyberData.CurrentSchemaVersion && v2.Tasks[0].CompletionCreditHours == 2.5
                && v2.Tasks[1].SeriesId == v2.Tasks[1].Id && v2.FocusSessions.Count == 0 && v2.Archive.TaskCount == 0,
                "migrations: v2 file -> v3 credit, v5 series, new lists initialized");
            var again = await StorageService.ReadFileAsync(v2Path);
            SchemaMigrations.Apply(again);
            Check(again.Tasks[0].CompletionCreditHours == 2.5, "migrations: re-applying on current data is a no-op");

            string newerPath = Path.Combine(dir, "newer", "cyber_data.json");
            Directory.CreateDirectory(Path.GetDirectoryName(newerPath)!);
            string newerJson = """{"schemaVersion":99,"nextTaskNumber":2,"futureField":{"x":1},"tasks":[{"number":1,"title":"From the future"}]}""";
            File.WriteAllText(newerPath, newerJson);
            var nv = new MainViewModel(new StorageService(newerPath));
            await nv.InitializeAsync();
            nv.NewTaskTitle = "change";
            await nv.AddTaskCommand.ExecuteAsync(null);
            nv.FlushNow();
            Check(nv.ErrorMessage is not null && File.ReadAllText(newerPath) == newerJson && nv.ActiveTasks.Any(t => t.Title == "From the future"),
                "migrations: file from a newer version opens read-only (unknown fields never overwritten)");
            bool importRejected = false;
            try { await StorageService.ReadFileAsync(newerPath); } catch (InvalidDataException) { importRejected = true; }
            Check(importRejected, "migrations: importing/restoring a newer-version file is refused");

            // ══════════════ Arşiv (400 günden eski tamamlananlar) ══════════════
            string arcPath = Path.Combine(dir, "arc", "cyber_data.json");
            var arcNow = new DateTime(2026, 11, 10, 12, 0, 0);
            var arcData = new CyberData();
            arcData.Tasks.Add(new CyberTask { Number = 1, Title = "Ancient", IsCompleted = true, CompletedAt = arcNow.AddDays(-700), TrackedMinutes = 30 });
            arcData.Tasks.Add(new CyberTask { Number = 2, Title = "Old", IsCompleted = true, CompletedAt = arcNow.AddDays(-401), TrackedMinutes = 15 });
            arcData.Tasks.Add(new CyberTask { Number = 3, Title = "Recent", IsCompleted = true, CompletedAt = arcNow.AddDays(-399) });
            arcData.Tasks.Add(new CyberTask { Number = 4, Title = "Active" });
            arcData.NextTaskNumber = 5;
            await StorageService.WriteAtomicAsync(arcPath, StorageService.Serialize(arcData));
            var av = new MainViewModel(new StorageService(arcPath), time: new ManualTime(arcNow));
            await av.InitializeAsync();
            for (int i = 0; i < 50 && av.CompletedTasks.Count != 1; i++) { await Task.Delay(20); } // arşiv açılışta arka planda
            var arcFiles = new ArchiveService(arcPath).List();
            Check(av.CompletedTasks.Select(t => t.Title).SequenceEqual(["Recent"]) && av.ActiveTasks.Single().Title == "Active",
                "archive: tasks completed > 400 days ago leave the main data");
            Check(arcFiles.Count == 2 && arcFiles.All(f => Path.GetFileName(f).StartsWith("completed-20")), "archive: one file per completion year: " + string.Join(",", arcFiles.Select(Path.GetFileName)));
            var archivedTitles = new List<string>();
            foreach (var f in arcFiles) { archivedTitles.AddRange((await StorageService.ReadFileAsync(f)).Tasks.Select(t => t.Title)); }
            Check(archivedTitles.Order().SequenceEqual(["Ancient", "Old"]), "archive: archived tasks readable with the normal reader");
            av.FlushNow();
            var arcSaved = await StorageService.ReadFileAsync(arcPath);
            Check(arcSaved.Archive.TaskCount == 2 && arcSaved.Archive.TrackedMinutes == 45 && arcSaved.Tasks.Count == 2 && av.ArchiveText.Contains('2'),
                "archive: summary keeps totals (2 tasks, 45 focus min)");
            av.ShowStatsCommand.Execute(null);
            Check(av.StatTiles.Any(t => t.Value.Contains("45")), "archive: focus total in stats includes archived minutes");

            // Çökme benzetimi: görev hem arşivde hem ana veride → tekrar arşivlenince kopya olmaz, sayaç şişmez.
            var dupData = await StorageService.ReadFileAsync(arcPath);
            dupData.Tasks.Add(new CyberTask { Id = (await StorageService.ReadFileAsync(arcFiles[^1])).Tasks[0].Id, Title = "Ancient", IsCompleted = true, CompletedAt = arcNow.AddDays(-700) });
            await StorageService.WriteAtomicAsync(arcPath, StorageService.Serialize(dupData));
            var av2 = new MainViewModel(new StorageService(arcPath), time: new ManualTime(arcNow));
            await av2.InitializeAsync();
            for (int i = 0; i < 50 && av2.CompletedTasks.Count != 1; i++) { await Task.Delay(20); }
            av2.FlushNow();
            int ancientCopies = 0;
            foreach (var f in new ArchiveService(arcPath).List()) { ancientCopies += (await StorageService.ReadFileAsync(f)).Tasks.Count(t => t.Title == "Ancient"); }
            Check(ancientCopies == 1 && (await StorageService.ReadFileAsync(arcPath)).Archive.TaskCount == 2, "archive: re-archiving after a crash creates no duplicates");

            // Arşiv yazılamazsa hiçbir şey silinmez.
            string blockedPath = Path.Combine(dir, "arc-blocked", "cyber_data.json");
            await StorageService.WriteAtomicAsync(blockedPath, StorageService.Serialize(arcData));
            File.WriteAllText(Path.Combine(dir, "arc-blocked", "archive"), "not a folder");
            var bv = new MainViewModel(new StorageService(blockedPath), time: new ManualTime(arcNow));
            await bv.InitializeAsync();
            int archivedNow = await bv.ArchiveExpiredTasksAsync(arcNow);
            Check(archivedNow == 0 && bv.CompletedTasks.Count == 3 && bv.ErrorMessage is not null, "archive: write failure keeps every task in place");

            // ══════════════ Render modu ve bellek kırpma ══════════════
            Check(RenderPolicy.Parse(null) == RenderPreference.Software && RenderPolicy.Parse("") == RenderPreference.Software
                && RenderPolicy.Parse("software") == RenderPreference.Software && RenderPolicy.Parse("bogus") == RenderPreference.Software
                && RenderPolicy.Parse("gpu") == RenderPreference.Gpu && RenderPolicy.Parse("  GPU ") == RenderPreference.Gpu,
                "render: CALABATICA_RENDER=gpu is the only way to opt out of software rendering");

            static long WorkingSetMb() { using var p = System.Diagnostics.Process.GetCurrentProcess(); return p.WorkingSet64 / 1048576; }
            static void AllocateAndRelease() { var big = new byte[160 * 1024 * 1024]; Array.Fill(big, (byte)7); GC.KeepAlive(big); }
            AllocateAndRelease();
            long beforeTrim = WorkingSetMb();
            bool trimmed = MemoryTrimmer.Trim();
            long afterTrim = WorkingSetMb();
            Check(trimmed && afterTrim < beforeTrim - 80, $"memory trim: working set {beforeTrim} MB -> {afterTrim} MB after releasing 160 MB");

            // ══════════════ Veriyi önceden yükleme (BeginPreload) ══════════════
            string prePath = Path.Combine(dir, "preload", "cyber_data.json");
            var preSeed = new CyberData();
            preSeed.Tasks.Add(new CyberTask { Number = 1, Title = "Preloaded 1" });
            preSeed.Tasks.Add(new CyberTask { Number = 2, Title = "Preloaded 2", IsCompleted = true, CompletedAt = DateTime.Now });
            preSeed.NextTaskNumber = 3;
            await StorageService.WriteAtomicAsync(prePath, StorageService.Serialize(preSeed));
            var pv = new MainViewModel(new StorageService(prePath));
            pv.BeginPreload();
            pv.BeginPreload(); // ikinci çağrı ikinci okuma başlatmaz
            await pv.InitializeAsync();
            Check(pv.ActiveTasks.Single().Title == "Preloaded 1" && pv.CompletedTasks.Single().Title == "Preloaded 2" && pv.ErrorMessage is null,
                "preload: initialize uses the pre-started load (same data as a normal load)");

            string preBadPath = Path.Combine(dir, "preload-bad", "cyber_data.json");
            Directory.CreateDirectory(Path.GetDirectoryName(preBadPath)!);
            File.WriteAllText(preBadPath, "{ not json");
            var pbv = new MainViewModel(new StorageService(preBadPath));
            pbv.BeginPreload();
            await pbv.InitializeAsync();
            Check(pbv.ErrorMessage is not null && pbv.ActiveTasks.Count == 0 && Directory.GetFiles(Path.GetDirectoryName(preBadPath)!, "cyber_data.corrupt-*.json").Length == 1,
                "preload: corrupt file is still backed up and reported");

            string preLockPath = Path.Combine(dir, "preload-lock", "cyber_data.json");
            await StorageService.WriteAtomicAsync(preLockPath, StorageService.Serialize(preSeed));
            string lockedContent = File.ReadAllText(preLockPath);
            using (var exclusive = new FileStream(preLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var plv = new MainViewModel(new StorageService(preLockPath));
                plv.BeginPreload();
                await plv.InitializeAsync();
                plv.NewTaskTitle = "must not overwrite";
                await plv.AddTaskCommand.ExecuteAsync(null);
                plv.FlushNow();
                Check(plv.ErrorMessage is not null, "preload: a locked file reports an error and disables saving");
            }

            Check(File.ReadAllText(preLockPath) == lockedContent, "preload: locked file content was never overwritten");

            // ══════════════ Sürüm etiketi sağlamlığı ══════════════
            bool versionThrew = false; bool versionParsed = true;
            try { versionParsed = UpdateService.TryParseVersion("v\u0661.\u0662", out _); } // Arapça-Hint rakamlar: regex \d eşler, int.Parse reddeder
            catch (FormatException) { versionThrew = true; }
            Check(!versionThrew && !versionParsed, "update: non-ASCII digits in a release tag are rejected without throwing");
            Check(UpdateService.TryParseVersion("v1.2.3", out var v123) && v123 == new Version(1, 2, 3)
                && UpdateService.TryParseVersion("2.1", out var v21) && v21 == new Version(2, 1, 0)
                && !UpdateService.TryParseVersion("v1.2.3-beta", out _) && !UpdateService.TryParseVersion("1.2.3.4", out _) && !UpdateService.TryParseVersion("", out _)
                && !UpdateService.TryParseVersion("v123456", out _), "update: normal tags still parse, malformed ones are rejected");

            // ══════════════ Log ══════════════
            Check(File.Exists(Path.Combine(dir, "logs", "app.log")) && File.ReadAllText(Path.Combine(dir, "logs", "app.log")).Contains("ERROR [MainViewModel]"),
                "log: caught errors (e.g. invalid import) are written to app.log");
            string? crash1 = null;
            for (int i = 0; i < AppLog.MaxCrashFiles + 3; i++)
            {
                crash1 ??= AppLog.WriteCrash(new InvalidOperationException("boom " + i), "Test");
                Thread.Sleep(2); // dosya adı milisaniyeli
                AppLog.WriteCrash(new InvalidOperationException("boom " + i), "Test");
            }

            var crashFiles = Directory.GetFiles(Path.Combine(dir, "logs"), "crash-*.log");
            Check(crashFiles.Length == AppLog.MaxCrashFiles && File.ReadAllText(crashFiles.Max()!).Contains("boom")
                && File.ReadAllText(crashFiles.Max()!).Contains("Version"), "log: crash report written, newest 10 kept");
            string bigLog = Path.Combine(dir, "logs", "app.log");
            File.WriteAllText(bigLog, new string('x', (int)AppLog.MaxAppLogBytes + 10));
            AppLog.Warn("Test", "after rotate");
            Check(File.Exists(Path.Combine(dir, "logs", "app.1.log")) && new FileInfo(bigLog).Length < 1000, "log: app.log rotates at 1 MB");

            Check(Localizer.MissingKeys().Count == 0, "TR/EN key sets identical after new features: " + string.Join(",", Localizer.MissingKeys()));

            // ══════════════ UI duman testi: gerçek pencere, ekran dışında, odak çalmadan ══════════════
            // Tüm sekmeler/paneller açılır; WPF binding hataları toplanır (XAML değişikliklerinin güvenlik ağı).
            // CALA_UI_SHOTS ortam değişkeni bir klasörse her ekranın PNG'si oraya yazılır (pencere göstermeden görsel kontrol).
            // Uygulamanın gerçek varsayılanı (yazılım render): gerçek HWND bu yolla oluşturulup tüm paneller gezilir.
            Check(RenderPolicy.Apply(string.Empty) == RenderPreference.Software && System.Windows.Media.RenderOptions.ProcessRenderMode == System.Windows.Interop.RenderMode.SoftwareOnly,
                "render: software is the default process render mode");
            var uiApp = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            CyberTodo.Themes.ThemeResources.EnsureLoaded(uiApp);
            var bindingErrors = new List<string>();
            var bindingListener = new CollectingTraceListener(bindingErrors);
            System.Diagnostics.PresentationTraceSources.Refresh();
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(bindingListener);
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Error;
            string? shots = Environment.GetEnvironmentVariable("CALA_UI_SHOTS");

            async Task Idle()
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Task.Delay(60);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }

            void Shot(System.Windows.FrameworkElement element, string name)
            {
                if (string.IsNullOrEmpty(shots) || element.ActualWidth < 1)
                {
                    return;
                }

                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(element);
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                Directory.CreateDirectory(shots);
                using var stream = File.Create(Path.Combine(shots, name + ".png"));
                png.Save(stream);
            }

            var uiClock = new ManualTime(DateTime.Today.AddHours(11));
            var uiVm = new MainViewModel(new StorageService(Path.Combine(dir, "ui", "cyber_data.json")), time: uiClock);
            MainWindow? win = null;
            System.Windows.FrameworkElement winRoot0() => (System.Windows.FrameworkElement)win!.Content;
            win = new MainWindow(uiVm, tray: null)
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            win.Show();
            await Idle();
            Check(uiVm.IsOnboardingOpen, "ui: first run shows the onboarding guide");
            var deferredViews = FindAll<DeferredView>(win);
            Check(deferredViews.Count == 8 && deferredViews.Count(d => d.IsCreated) == 1 && deferredViews.Single(d => d.IsCreated).ViewType == typeof(OnboardingOverlay),
                $"ui: hidden panels are not created at startup (8 deferred, created: {string.Join(",", deferredViews.Where(d => d.IsCreated).Select(d => d.ViewType!.Name))})");
            Shot(winRoot0(), "onboarding");
            uiVm.CloseOnboardingCommand.Execute(null);
            foreach (string title in new[] { "Write report #work @today @17:00 !", "Gym *mon,wed,fri @today", "Read book #study ~2" })
            {
                uiVm.NewTaskTitle = title;
                await uiVm.AddTaskCommand.ExecuteAsync(null);
            }

            await uiVm.StartFocusCommand.ExecuteAsync(uiVm.ActiveTasks[0]);
            uiVm.MoveSelection(1);
            await Idle();
            var winRoot = winRoot0();
            Shot(winRoot, "main");
            foreach (var mode in Enum.GetValues<ChartMode>())
            {
                uiVm.ChartMode = mode;
                await Idle();
                Shot(winRoot, "tab-" + mode);
            }

            uiVm.OpenSettingsCommand.Execute(null); await Idle(); Shot(winRoot, "settings");
            uiVm.OpenDataCommand.Execute(null); await Idle(); Shot(winRoot, "data");
            uiVm.OpenTemplatesCommand.Execute(null); await Idle(); Shot(winRoot, "templates");
            uiVm.CloseTemplatesCommand.Execute(null);
            uiVm.EditTaskCommand.Execute(uiVm.ActiveTasks[1]); await Idle(); Shot(winRoot, "editor");
            uiVm.CancelEditCommand.Execute(null);
            uiClock.Advance(TimeSpan.FromMinutes(25)); uiVm.Tick(); await Idle(); Shot(winRoot, "break");
            uiVm.SelectedCategoryFilter = uiVm.CategoryFilters.Last(); await uiVm.SaveCurrentFilterCommand.ExecuteAsync(null);
            uiVm.SetSystemHighContrast(true); await Idle(); Shot(winRoot, "highcontrast");
            uiVm.SetSystemHighContrast(false);

            // Mini pencere doğrudan (MainWindow'un ShowMini'si ekrana yerleştirip etkinleştirirdi)
            // (IsMiniMode'u değiştirmek MainWindow'un gerçek mini penceresini ekrana çıkarıp etkinleştirirdi; burada yapılmaz.)
            var mini = new MiniWindow(uiVm) { ShowActivated = false, Left = -32000, Top = -32000 };
            uiVm.MiniTasks.Refresh();
            mini.Show();
            await Idle();
            Shot((System.Windows.FrameworkElement)mini.Content, "mini");

            Check(bindingErrors.Count == 0, $"ui: no binding errors across all tabs/panels ({bindingErrors.Count})" +
                (bindingErrors.Count > 0 ? "\n      " + string.Join("\n      ", bindingErrors.Distinct().Take(8)) : string.Empty));
            Check(winRoot.ActualWidth > 0 && win.IsLoaded, "ui: main window loaded and laid out off-screen");
            Check(deferredViews.All(d => d.IsCreated), "ui: every deferred panel is created on first use");
            Check(deferredViews.Where(d => d.ViewType != typeof(OnboardingOverlay)).All(d => d.Visibility == System.Windows.Visibility.Collapsed || d.IsOpen),
                "ui: deferred panels follow their open flag");
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingListener);
            mini.CloseForShutdown();
            win.Hide();

            // ── Bilgi (test değil): 2500 görevlik veride bir kaydın maliyeti ──
            var big = new CyberData();
            for (int i = 1; i <= 2500; i++)
            {
                big.Tasks.Add(new CyberTask
                {
                    Number = i, Title = $"Task number {i} with a reasonably long title", Category = $"cat{i % 8}",
                    Notes = $"note {i}", EstimatedHours = 1, DueAt = DateTime.Today.AddDays(i % 20), HasDueTime = true,
                    Subtasks = [new SubTask { Title = "a", IsDone = true }, new SubTask { Title = "b" }],
                    IsCompleted = i > 500, CompletedAt = i > 500 ? DateTime.Today.AddDays(-(i % 60)) : null,
                });
            }
            var bigStorage = new StorageService(Path.Combine(dir, "big.json"));
            await bigStorage.SaveAsync(big); // ısınma
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) { await bigStorage.SaveAsync(big); }
            Console.WriteLine($"INFO  save 2500 tasks: {sw.Elapsed.TotalMilliseconds / 5:F1} ms avg, file {new FileInfo(Path.Combine(dir, "big.json")).Length / 1024} KB");
        }
        catch (Exception ex) { Console.WriteLine(ex); failures++; }
        finally { frame.Continue = false; }
    });
    Dispatcher.PushFrame(frame);
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start(); thread.Join();
Directory.Delete(dir, true);
Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures;

static List<T> FindAll<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
{
    var found = new List<T>();
    void Walk(System.Windows.DependencyObject d)
    {
        if (d is T match) { found.Add(match); }
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++) { Walk(System.Windows.Media.VisualTreeHelper.GetChild(d, i)); }
    }

    Walk(root);
    return found;
}

static byte[] MakeZip(params (string Name, byte[] Content)[] entries)
{
    using var memory = new MemoryStream();
    using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
    {
        foreach (var (name, content) in entries)
        {
            using var stream = archive.CreateEntry(name).Open();
            stream.Write(content);
        }
    }

    return memory.ToArray();
}

/// <summary>Dosya diyalogları yerine: önceden belirlenen yol / onay yanıtı.</summary>
sealed class FakeDialogs : IFileDialogs
{
    public string? SavePath { get; set; }
    public string? OpenPath { get; set; }
    public bool ConfirmResult { get; set; } = true;
    public string LastConfirm { get; private set; } = string.Empty;

    public string? PickSavePath(string defaultFileName, string filter) => SavePath;
    public string? PickOpenPath(string filter) => OpenPath;
    public bool Confirm(string message) { LastConfirm = message; return ConfirmResult; }
}

/// <summary>Ağ yerine: adres → içerik. RedirectTo verilirse yanıt o adrese yönlendirilmiş gibi görünür.</summary>
sealed class FakeHttp : System.Net.Http.HttpMessageHandler
{
    private readonly Dictionary<Uri, byte[]> _content = [];
    public Uri? RedirectTo { get; set; }
    public byte[] this[Uri uri] { set => _content[uri] = value; }

    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = _content.TryGetValue(request.RequestUri!, out var bytes)
            ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.ByteArrayContent(bytes) }
            : new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        response.RequestMessage = RedirectTo is null ? request : new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, RedirectTo);
        return Task.FromResult(response);
    }
}

/// <summary>WPF binding hata izlerini toplar.</summary>
sealed class CollectingTraceListener(List<string> sink) : System.Diagnostics.TraceListener
{
    private readonly System.Text.StringBuilder _line = new();

    public override void Write(string? message) => _line.Append(message);

    public override void WriteLine(string? message)
    {
        _line.Append(message);
        lock (sink)
        {
            sink.Add(_line.ToString());
        }

        _line.Clear();
    }
}

/// <summary>Elle ilerletilen saat (Microsoft.Extensions.TimeProvider.Testing paketi yerine; bağımlılıksız).</summary>
sealed class ManualTime(DateTime localStart) : TimeProvider
{
    private DateTimeOffset _utc = new DateTimeOffset(localStart, TimeZoneInfo.Local.GetUtcOffset(localStart)).ToUniversalTime();

    public DateTime LocalNow => GetLocalNow().DateTime;

    public override DateTimeOffset GetUtcNow() => _utc;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;

    public void Advance(TimeSpan span) => _utc += span;
}
