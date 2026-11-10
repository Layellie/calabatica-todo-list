using System.IO;
using System.Windows.Threading;
using CyberTodo;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.ViewModels;
using CyberTodo.Views;
using static CalabaticaTests.TestSupport;

namespace CalabaticaTests;
internal static class ViewsAndOnboardingTests
{
    public static async Task Run(TestContext ctx)
    {
        void Check(bool ok, string name) => ctx.Check(ok, name);
        string dir = ctx.Dir;
        string path = ctx.DataPath;
        var clock = ctx.Clock;
        Task<MainViewModel> Fresh() => ctx.Fresh();

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
    }
}
