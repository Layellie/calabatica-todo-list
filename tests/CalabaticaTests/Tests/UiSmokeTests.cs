using System.IO;
using System.Windows.Threading;
using CyberTodo;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.ViewModels;
using CyberTodo.Views;
using static CalabaticaTests.TestSupport;

namespace CalabaticaTests;
internal static class UiSmokeTests
{
    public static async Task Run(TestContext ctx)
    {
        void Check(bool ok, string name) => ctx.Check(ok, name);
        string dir = ctx.Dir;
        string path = ctx.DataPath;
        var clock = ctx.Clock;
        Task<MainViewModel> Fresh() => ctx.Fresh();

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

        // Erişilebilirlik: ekran okuyucu her düğmeye bir ad okuyabilmeli (AutomationProperties.Name, metin içeriği veya ipucu).
        var unnamed = new List<string>();
        foreach (var root in new System.Windows.DependencyObject[] { win, mini })
        {
            foreach (var button in FindAll<System.Windows.Controls.Primitives.ButtonBase>(root))
            {
                // Şablonda tetikleyiciyle gizlenenler (ör. eylemi olmayan toast düğmeleri) ekran okuyucuya zaten görünmez.
                if (button.Visibility == System.Windows.Visibility.Visible && !HasAccessibleName(button))
                {
                    unnamed.Add($"{root.GetType().Name}:{button.GetType().Name} name='{button.Name}' content={button.Content?.GetType().Name ?? "null"} dc={button.DataContext?.GetType().Name}");
                }
            }
        }

        Check(unnamed.Count == 0, $"a11y: every button has an accessible name ({unnamed.Count} without)" +
            (unnamed.Count > 0 ? "\n      " + string.Join("\n      ", unnamed.Distinct().Take(40)) : string.Empty));
        Check(bindingErrors.Count == 0, $"ui: no binding errors across all tabs/panels ({bindingErrors.Count})" +
            (bindingErrors.Count > 0 ? "\n      " + string.Join("\n      ", bindingErrors.Distinct().Take(8)) : string.Empty));
        Check(winRoot.ActualWidth > 0 && win.IsLoaded, "ui: main window loaded and laid out off-screen");
        Check(deferredViews.All(d => d.IsCreated), "ui: every deferred panel is created on first use");
        Check(deferredViews.Where(d => d.ViewType != typeof(OnboardingOverlay)).All(d => d.Visibility == System.Windows.Visibility.Collapsed || d.IsOpen),
            "ui: deferred panels follow their open flag");
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingListener);
        mini.CloseForShutdown();
        win.Hide();
    }

    /// <summary>Düğmenin ekran okuyucuya verdiği ad: AutomationProperties.Name, metin içerik (ya da içindeki metin) veya ipucu.</summary>
    private static bool HasAccessibleName(System.Windows.Controls.Primitives.ButtonBase button)
    {
        static bool Has(object? o) => o switch
        {
            string s => !string.IsNullOrWhiteSpace(s),
            System.Windows.Controls.TextBlock tb => !string.IsNullOrWhiteSpace(tb.Text),
            System.Windows.Controls.Panel p => p.Children.OfType<object>().Any(Has),
            System.Windows.Controls.ContentControl cc => Has(cc.Content),
            _ => false,
        };

        return !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(button))
            || Has(button.Content) || Has(button.ToolTip)
            || System.Windows.Automation.AutomationProperties.GetLabeledBy(button) is not null;
    }
}
