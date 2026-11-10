using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.Themes;
using CyberTodo.ViewModels;

namespace CyberTodo;

public partial class MainWindow : Window
{
    private static readonly TimeSpan s_shutdownSaveTimeout = TimeSpan.FromSeconds(3);

    private readonly MainViewModel _viewModel;
    private TrayIconHost? _tray; // null: testlerde yok; normal açılışta ilk çizimden SONRA eklenir (AttachTray)
    private bool _closeConfirmed;
    private bool _exitRequested;
    private bool _trayHintShown;
    private Point _dragStart;
    private CyberTask? _dragCandidate;
    private MiniWindow? _mini;

    /// <summary>Gizlendikten sonra bellek kırpmadan önce beklenen süre (hızlı aç-kapa kırpmayı tetiklemesin).</summary>
    private static readonly TimeSpan s_trimDelay = TimeSpan.FromSeconds(8);

    private readonly DispatcherTimer _trimTimer = new(DispatcherPriority.Background) { Interval = s_trimDelay };

    /// <param name="tray">null → tepsi simgesi yok (UI testleri).</param>
    public MainWindow(MainViewModel viewModel, TrayIconHost? tray, bool startHidden = false)
    {
        InitializeComponent();
        StartupTrace.Mark("window-xaml-done");

        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        if (tray is not null)
        {
            AttachTray(tray);
        }

        _viewModel.AttentionRequested += OnAttentionRequested;
        _viewModel.OpenUrlRequested += OnOpenUrlRequested;
        _viewModel.RestartRequested += OnRestartRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Closed += OnClosed;
        StateChanged += OnStateChanged;

        // Oturum kapanırken (çıkış/yeniden başlatma): tepsiye gizleme yapılmasın, ertelenmiş kayıt hemen yazılsın.
        if (Application.Current is { } app)
        {
            app.SessionEnding += (_, _) =>
            {
                _exitRequested = true;
                _viewModel.FlushNow();
            };
        }

        // Windows yüksek kontrast modu: açılışta ve değişince temaya yansır.
        _viewModel.SetSystemHighContrast(SystemParameters.HighContrast);
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        ThemeApplier.Apply(Resources, _viewModel.CurrentTheme);

        // Tepside bellek kırpma: ana pencere gizli/küçültülmüş ve mini pencere kapalıyken tek seferlik.
        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer.Stop();
            if (IsBackgroundIdle)
            {
                MemoryTrimmer.Trim();
            }
        };
        IsVisibleChanged += (_, _) => EvaluateTrim();
        EvaluateTrim(); // gizli başlangıç (--tray): pencere hiç gösterilmeyebilir

        // Loaded yerine: pencere tepside gizli başlasa da veri yüklensin, hatırlatıcılar çalışsın.
        Dispatcher.BeginInvoke(InitializeAfterConstruction, DispatcherPriority.Loaded);
        _ = startHidden; // gizli başlangıçta Show() Program'da çağrılmaz
    }

    /// <summary>async void bilinçli: hata dispatcher'a düşer ve görünür olur (sessizce kaybolmaz).</summary>
    private async void InitializeAfterConstruction()
    {
        await _viewModel.InitializeAsync();
        if (IsVisible)
        {
            TaskTitleBox.Focus();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // Düzenleyici açılınca başlık alanına odaklan.
            case nameof(MainViewModel.Editor) when _viewModel.Editor is not null:
                Dispatcher.InvokeAsync(() =>
                {
                    EditorTitleBox.Focus();
                    EditorTitleBox.CaretIndex = EditorTitleBox.Text.Length;
                }, DispatcherPriority.Input);
                break;

            case nameof(MainViewModel.CurrentTheme):
                ThemeApplier.Apply(Resources, _viewModel.CurrentTheme);
                if (_mini is not null)
                {
                    ThemeApplier.Apply(_mini.Resources, _viewModel.CurrentTheme);
                }

                break;

            case nameof(MainViewModel.IsMiniMode):
                if (_viewModel.IsMiniMode)
                {
                    Hide();
                    ShowMini();
                }
                else
                {
                    _mini?.Hide();
                    ShowFromTray();
                }

                break;

            case nameof(MainViewModel.SelectedTask) when _viewModel.SelectedTask is { } selected:
                ScrollTaskIntoView(selected);
                break;
        }
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            Dispatcher.InvokeAsync(() => _viewModel.SetSystemHighContrast(SystemParameters.HighContrast));
        }
    }

    /// <summary>Klavyeyle seçilen satırı sanal listede görünür yap (oluşturulmamışsa önce dizinle getir).</summary>
    private void ScrollTaskIntoView(CyberTask task)
    {
        int index = _viewModel.ActiveView.IndexOf(task);
        if (index < 0)
        {
            return;
        }

        if (FindVisualChild<VirtualizingStackPanel>(ActiveList) is { } panel)
        {
            panel.BringIndexIntoViewPublic(index);
        }

        if (ActiveList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
        {
            container.BringIntoView();
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>Satıra tıklandı: klavye seçimi o satıra geçer, liste klavye odağını alır.</summary>
    private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CyberTask task })
        {
            _viewModel.SelectedTask = task;
            ActiveList.Focus();
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.ClickCount == 1)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Grafik sütununa gelindi: nişangâh o sütuna, log o güne.</summary>
    private void Bar_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChartBar bar })
        {
            _viewModel.HoverBar(bar);
        }
    }

    /// <summary>Fare grafik alanından çıktı: nişangâh kaybolur, log bugüne döner.</summary>
    private void Chart_MouseLeave(object sender, MouseEventArgs e) => _viewModel.EndHover();

    /// <summary>İlerleme çubuğunda tıklanan yatay konumu yüzdeye çevirir.</summary>
    private async void Progress_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CyberTask task } element || element.ActualWidth <= 0)
        {
            return;
        }

        e.Handled = true;
        double percent = e.GetPosition(element).X / element.ActualWidth * 100;
        await _viewModel.SetTaskProgressAsync(task, percent);
    }

    /// <summary>Düzenleyicideki taslak alt görevin işaretini değiştirir (Kaydet'e kadar asıl göreve yazılmaz).</summary>
    private void EditorSubtaskToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SubTask draft })
        {
            draft.IsDone = !draft.IsDone;
        }
    }

    // ───────────────────────────── Bildirim / URL ─────────────────────────────

    /// <summary>
    /// Hatırlatma / odak bitti: ses (ayar açıksa); pencere gizli/küçültülmüşse Windows bildirimi,
    /// açık ama arka plandaysa görev çubuğu yanıp söner.
    /// </summary>
    private void OnAttentionRequested(object? sender, string message)
    {
        if (_viewModel.SoundEnabled)
        {
            SystemSounds.Exclamation.Play();
        }

        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            _tray?.Notify(message);
        }
        else if (!IsActive)
        {
            NativeMethods.FlashUntilForeground(new WindowInteropHelper(this).Handle);
        }
    }

    /// <summary>Adres UpdateService'te https://github.com ile sınırlandırıldı; varsayılan tarayıcıda açılır.</summary>
    private void OnOpenUrlRequested(object? sender, Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Error("MainWindow", ex);
            _viewModel.ErrorMessage = ex.Message;
        }
    }

    private static class NativeMethods
    {
        private const uint FlashwAll = 0x3;
        private const uint FlashwTimerNoForeground = 0xC;

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashWInfo
        {
            public uint CbSize;
            public IntPtr Hwnd;
            public uint DwFlags;
            public uint UCount;
            public uint DwTimeout;
        }

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FlashWInfo info);

        public static void FlashUntilForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var info = new FlashWInfo
            {
                CbSize = (uint)Marshal.SizeOf<FlashWInfo>(),
                Hwnd = hwnd,
                DwFlags = FlashwAll | FlashwTimerNoForeground,
                UCount = uint.MaxValue,
                DwTimeout = 0,
            };

            // Dönüş değeri "önceden yanıp sönüyor muydu" bilgisidir, hata değildir; yok sayılması doğru.
            FlashWindowEx(ref info);
        }
    }
}
