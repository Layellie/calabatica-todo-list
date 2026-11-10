using CyberTodo.Models;
using CyberTodo.Services;

namespace CalabaticaTests;

/// <summary>
/// StorageService.Normalize karakterizasyon testi: elle düzenlenmiş / bozuk veriye karşı savunmanın TÜM kuralları.
/// Bu test, metodu parçalara bölmeden önce yazıldı; davranış korunmalı (sıra dahil: şema geçişi ayarlardan sonra, görevlerden önce).
/// </summary>
internal static class NormalizeTests
{
    public static Task Run(TestContext ctx)
    {
        void Check(bool ok, string name) => ctx.Check(ok, name);

        // ── Null listeler/nesneler ──
        var nulls = new CyberData
        {
            Settings = null!, Tasks = null!, Days = null!, Templates = null!, FocusSessions = null!, SavedFilters = null!, Archive = null!,
        };
        StorageService.Normalize(nulls);
        Check(nulls.Settings is not null && nulls.Tasks is not null && nulls.Days is not null && nulls.Templates is not null
            && nulls.FocusSessions is not null && nulls.SavedFilters is not null && nulls.Archive is not null,
            "normalize: null lists and objects are replaced with empty ones");
        Check(nulls.NextTaskNumber == 1 && nulls.SchemaVersion == CyberData.CurrentSchemaVersion, "normalize: empty data keeps next number 1 and current schema");

        // ── Kayıtlı filtreler ──
        var filters = new CyberData();
        filters.SavedFilters.AddRange([null!, new SavedFilter { Name = "  " }, new SavedFilter { Name = new string('x', 100), SmartView = (SmartView)99 }, new SavedFilter { Name = "ok", SmartView = SmartView.Today }]);
        StorageService.Normalize(filters);
        Check(filters.SavedFilters.Count == 2 && filters.SavedFilters[0].Name.Length == 80 && filters.SavedFilters[0].SmartView == SmartView.All
            && filters.SavedFilters[1].Name == "ok" && filters.SavedFilters[1].SmartView == SmartView.Today,
            "normalize: saved filters — null/blank dropped, name capped at 80, unknown smart view -> All");

        // ── Arşiv ve odak oturumları ──
        var misc = new CyberData();
        misc.Archive.TaskCount = -5;
        misc.Archive.TrackedMinutes = -1;
        misc.FocusSessions.AddRange([null!, new FocusSession { Minutes = 0 }, new FocusSession { Minutes = -3 }, new FocusSession { Minutes = 99999, Title = null! }, new FocusSession { Minutes = 25, Title = "t" }]);
        StorageService.Normalize(misc);
        Check(misc.Archive.TaskCount == 0 && misc.Archive.TrackedMinutes == 0, "normalize: negative archive totals are clamped to 0");
        Check(misc.FocusSessions.Count == 2 && misc.FocusSessions[0].Minutes == 24 * 60 && misc.FocusSessions[0].Title == string.Empty
            && misc.FocusSessions[1].Minutes == 25 && misc.FocusSessions[1].Title == "t",
            "normalize: focus sessions — non-positive dropped, capped at 24 h, null title -> empty");

        // ── Şablonlar ──
        var templates = new CyberData();
        templates.Templates.Add(new TaskTemplate { Name = null!, Items = null! });
        templates.Templates.Add(null!);
        templates.Templates.Add(new TaskTemplate
        {
            Name = null!,
            Items =
            [
                null!, new TemplateItem { Title = " " },
                new TemplateItem { Title = "big", EstimatedHours = 99, Subtasks = null! },
                new TemplateItem { Title = "tiny", EstimatedHours = 0 },
                new TemplateItem { Title = "nan", EstimatedHours = double.NaN },
                new TemplateItem { Title = "ok", EstimatedHours = 2, Subtasks = ["a"] },
            ],
        });
        StorageService.Normalize(templates);
        var items = templates.Templates.Count == 1 ? templates.Templates[0].Items : [];
        Check(templates.Templates.Count == 1 && templates.Templates[0].Name == string.Empty && items.Count == 4, "normalize: templates — null/broken dropped, blank items dropped, null name -> empty");
        Check(items.Count == 4 && items[0].EstimatedHours == 24 && items[0].Subtasks.Count == 0 && items[1].EstimatedHours == 0.01
            && items[2].EstimatedHours == 1 && items[3].EstimatedHours == 2 && items[3].Subtasks.SequenceEqual(["a"]),
            "normalize: template items — hours clamped to 0.01..24, NaN -> 1, null subtasks -> empty");

        // ── Ayarlar ──
        foreach (var (scale, focus, theme, expectScale, expectFocus) in new[]
        {
            (0.1, 3, (ThemeId)42, AppSettings.DefaultUiScale, 25), (2.0, 500, ThemeId.Neon, AppSettings.DefaultUiScale, 25),
            (double.NaN, 5, ThemeId.Neon, AppSettings.DefaultUiScale, 5), (0.5, 180, ThemeId.Neon, 0.5, 180), (1.5, 25, ThemeId.Neon, 1.5, 25),
        })
        {
            var s = new CyberData { Settings = { UiScale = scale, FocusMinutes = focus, Theme = theme } };
            StorageService.Normalize(s);
            Check(s.Settings.UiScale == expectScale && s.Settings.FocusMinutes == expectFocus && Enum.IsDefined(s.Settings.Theme),
                $"normalize: settings scale={scale} focus={focus} theme={theme} -> scale {expectScale}, focus {expectFocus}");
        }

        var badTheme = new CyberData { Settings = { Theme = (ThemeId)42 } };
        StorageService.Normalize(badTheme);
        Check(badTheme.Settings.Theme == ThemeId.Neon, "normalize: unknown theme -> Neon");
        var mini = new CyberData { Settings = { MiniLeft = double.PositiveInfinity, MiniTop = 10 } };
        StorageService.Normalize(mini);
        Check(mini.Settings.MiniLeft is null && mini.Settings.MiniTop is null, "normalize: non-finite mini window position clears both coordinates");
        var miniOk = new CyberData { Settings = { MiniLeft = 5, MiniTop = 6 } };
        StorageService.Normalize(miniOk);
        Check(miniOk.Settings.MiniLeft == 5 && miniOk.Settings.MiniTop == 6, "normalize: finite mini window position is kept");

        // ── Görevler ──
        var data = new CyberData { NextTaskNumber = 1 };
        data.Tasks.AddRange(
        [
            null!,
            new CyberTask
            {
                Number = 7, Title = null!, Subtasks = [null!, new SubTask { Title = "s" }], TrackedMinutes = -4, CompletionCreditHours = double.NaN,
                Progress = 250, EstimatedHours = 99, IsCompleted = false, CompletedAt = DateTime.Today, DueAt = null, HasDueTime = true,
                RepeatDays = (WeekDays)0xFFFF, Recurrence = Recurrence.Weekly,
            },
            new CyberTask { Number = 3, Title = "done", IsCompleted = true, CompletedAt = DateTime.Today, Progress = double.NaN, EstimatedHours = -2, CompletionCreditHours = -1, DueAt = DateTime.Today, HasDueTime = true },
            new CyberTask { Number = 4, Title = "selected-days-empty", Recurrence = Recurrence.SelectedDays, RepeatDays = WeekDays.None },
            new CyberTask { Number = 5, Title = "selected-days-ok", Recurrence = Recurrence.SelectedDays, RepeatDays = WeekDays.Monday | WeekDays.Sunday },
        ]);
        data.Days.AddRange([null!, new DayActivity { Entries = null!, HoursWorked = -3, CompletedCount = -2 }, new DayActivity { HoursWorked = double.NaN }]);
        StorageService.Normalize(data);
        var t = data.Tasks;
        Check(t.Count == 4 && data.Days.Count == 2, "normalize: null tasks and days are dropped");
        var first = t.Count > 0 ? t[0] : null;
        Check(first is not null && first.Title == string.Empty && first.Subtasks.Count == 1 && first.TrackedMinutes == 0 && first.CompletionCreditHours == 0
            && first.Progress == 100 && first.EstimatedHours == 24 && first.CompletedAt is null && !first.HasDueTime
            && first.RepeatDays == WeekDays.All && first.Recurrence == Recurrence.Weekly,
            "normalize: task fields — clamps, null title/subtasks, open task has no completion time, no due -> no due time, repeat mask trimmed");
        var done = t.Count > 1 ? t[1] : null;
        Check(done is not null && done.Progress == 0 && done.EstimatedHours == 0 && done.CompletionCreditHours == 0 && done.CompletedAt == DateTime.Today && done.HasDueTime,
            "normalize: completed task keeps its completion time and due time; NaN progress -> 0, negative hours -> 0");
        Check(t.Count > 3 && t[2].Recurrence == Recurrence.None && t[3].Recurrence == Recurrence.SelectedDays && t[3].RepeatDays == (WeekDays.Monday | WeekDays.Sunday),
            "normalize: 'selected days' recurrence with no days becomes None; with days it is kept");
        Check(data.Days[0].Entries is not null && data.Days[0].HoursWorked == 0 && data.Days[0].CompletedCount == 0 && data.Days[1].HoursWorked == 0,
            "normalize: day activity — null entries -> empty, negatives/NaN -> 0");
        Check(data.NextTaskNumber == 8, "normalize: next task number moves past the highest existing number");

        var fine = new CyberData { NextTaskNumber = 50 };
        fine.Tasks.Add(new CyberTask { Number = 3, Title = "x" });
        StorageService.Normalize(fine);
        Check(fine.NextTaskNumber == 50, "normalize: a larger next number is left alone");
        StorageService.Normalize(fine);
        Check(fine.NextTaskNumber == 50 && fine.Tasks.Count == 1, "normalize: idempotent on already clean data");
        return Task.CompletedTask;
    }
}
