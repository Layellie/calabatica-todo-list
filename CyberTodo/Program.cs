using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CyberTodo.Services;
using CyberTodo.ViewModels;

namespace CyberTodo;

/// <summary>App.xaml kullanılmadığı için giriş noktası burada.</summary>
public static class Program
{
    // "Local\" = oturum başına (kullanıcıya özel); başka kullanıcının uygulamasını etkilemez.
    private const string MutexName = @"Local\CALABATICA.SingleInstance";
    private const string ShowEventName = @"Local\CALABATICA.Show";

    /// <summary>Güncellemeden sonra yeni exe bu argümanla başlar: eski süreç kapanana kadar bekler.</summary>
    public const string AfterUpdateArgument = "--after-update";

    private static readonly TimeSpan s_afterUpdateWait = TimeSpan.FromSeconds(20);

    [STAThread]
    public static void Main(string[] args)
    {
        string? benchmarkReport = StartupBenchmark.ParseOutputPath(args);
        if (benchmarkReport is not null)
        {
            StartupBenchmark.RequireIsolatedData();
            StartupTrace.Enable();
            StartupBenchmark.EnableJitTraceIfRequested(args);
        }

        StartupTrace.Mark("main");
        // Arka plan thread'lerinde yakalanmamış hata: süreç zaten sonlanıyor, en azından rapor kalsın.
        AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                AppLog.WriteCrash(ex, "AppDomain");
            }
        };

        // Beklenmemiş (await edilmemiş) Task hataları: uygulamayı düşürmez ama kaybolmasın.
        TaskScheduler.UnobservedTaskException += static (_, e) =>
        {
            AppLog.Error("UnobservedTask", e.Exception);
            e.SetObserved();
        };

        // Tek örnek: tepside çalışırken exe'ye tekrar tıklanırsa ikinci kopya açılmaz (aynı JSON'a iki yazar olurdu),
        // mevcut pencereye "göster" sinyali gider.
        bool createdNew = true;
        using var mutex = benchmarkReport is null ? new Mutex(initiallyOwned: true, MutexName, out createdNew) : null; // kıyaslama: çalışan gerçek uygulamaya dokunma
        bool afterUpdate = args.Any(static a => string.Equals(a, AfterUpdateArgument, StringComparison.OrdinalIgnoreCase));
        if (!createdNew && afterUpdate)
        {
            // Eski sürüm kayıtlarını bitirip kapanıyor: onun bırakmasını bekle (sinyal gönderip çıkma).
            try
            {
                createdNew = mutex!.WaitOne(s_afterUpdateWait);
            }
            catch (AbandonedMutexException)
            {
                createdNew = true; // eski süreç mutex'i bırakmadan sonlandı; sahiplik bize geçti
            }
        }

        if (!createdNew)
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ShowEventName);
                existing.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // Bilinçli: ilk örnek henüz başlıyor (olay daha oluşturulmadı); sinyal gerekmez, zaten açılacak.
            }

            return;
        }

        using var showEvent = benchmarkReport is null ? new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName) : null;
        UpdateInstaller.CleanupLeftovers(Environment.ProcessPath); // önceki güncellemeden kalan .old/.new

        bool startHidden = args.Any(static a => string.Equals(a, RegistryStartupRegistration.TrayArgument, StringComparison.OrdinalIgnoreCase));
        RenderPolicy.Apply(); // ilk pencereden ÖNCE: yazılım render (varsayılan) ~115 MB tasarruf, CALABATICA_RENDER=gpu ile eski davranış
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Themes.ThemeResources.EnsureLoaded(app);
        StartupTrace.Mark("app+theme");
        // Composition root: servisler burada kurulur; pencere ve ViewModel bunları dışarıdan alır (testte sahteleri verilir).
        var viewModel = new MainViewModel(
            new StorageService(),
            benchmarkReport is null ? new UpdateService() : new UpdateService(null, UpdateService.ReadCurrentVersion()), // kıyaslama: ağa çıkma
            new RegistryStartupRegistration(),
            new WpfFileDialogs(static () => Application.Current?.Windows.OfType<Window>().FirstOrDefault(static w => w.IsActive)),
            new UpdateInstaller());
        viewModel.BeginPreload(); // dosya okuma/JSON, pencere XAML'i kurulurken arka planda
        StartupTrace.Mark("viewmodel+preload");
        // Tepsi (WinForms): gizli başlangıçta hemen, aksi halde ilk çizimden sonra (aşağıda) — kritik yoldan çıkar.
#pragma warning disable CA2000 // TrayIconHost sahipliği MainWindow'a geçer (OnClosed / ExitApplication / HandleFatalError'da Dispose edilir)
        var window = new MainWindow(viewModel, startHidden ? new TrayIconHost() : null, startHidden);
#pragma warning restore CA2000
        if (!startHidden)
        {
            window.ContentRendered += (_, _) =>
                window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
#pragma warning disable CA2000 // sahiplik AttachTray ile pencereye geçer
                    window.AttachTray(new TrayIconHost());
#pragma warning restore CA2000
                });
        }

        // UI thread'inde yakalanmamış hata: rapor yaz, bekleyen kaydı diske indir, kullanıcıya söyle, düzgün kapan.
        // Bilinmeyen bir hatadan sonra çalışmaya devam etmek veriyi tutarsız bırakabilir; bu yüzden devam edilmez.
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            window.HandleFatalError(e.Exception);
        };

        StartupTrace.Mark("window-ctor-done");
        var registration = showEvent is null
            ? null
            : ThreadPool.RegisterWaitForSingleObject(
                showEvent,
                (_, _) => app.Dispatcher.BeginInvoke(window.ShowFromTray),
                state: null,
                Timeout.Infinite,
                executeOnlyOnce: false);

        if (benchmarkReport is not null)
        {
            StartupBenchmark.MakeInvisibleToUser(window);
            StartupBenchmark.Attach(app, window, viewModel, benchmarkReport);
        }

        if (!startHidden)
        {
            window.Show();
            StartupTrace.Mark("window-shown");
        }

        app.Run();
        registration?.Unregister(null);
    }
}
