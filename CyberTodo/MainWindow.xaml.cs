using System.ComponentModel;
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
using CyberTodo.ViewModels;

namespace CyberTodo;

/// <summary>App.xaml kullanılmadığı için giriş noktası burada.</summary>
public static class Program
{
    // "Local\" = oturum başına (kullanıcıya özel); başka kullanıcının uygulamasını etkilemez.
    private const string MutexName = @"Local\CALABATICA.SingleInstance";
    private const string ShowEventName = @"Local\CALABATICA.Show";

    [STAThread]
    public static void Main(string[] args)
    {
        // Tek örnek: tepside çalışırken exe'ye tekrar tıklanırsa ikinci kopya açılmaz (aynı JSON'a iki yazar olurdu),
        // mevcut pencereye "göster" sinyali gider.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
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

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        bool startHidden = args.Any(static a => string.Equals(a, RegistryStartupRegistration.TrayArgument, StringComparison.OrdinalIgnoreCase));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow(startHidden);

        var registration = ThreadPool.RegisterWaitForSingleObject(
            showEvent,
            (_, _) => app.Dispatcher.BeginInvoke(window.ShowFromTray),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        if (!startHidden)
        {
            window.Show();
        }

        app.Run();
        registration.Unregister(null);
    }
}

public partial class MainWindow : Window
{
    private static readonly TimeSpan s_shutdownSaveTimeout = TimeSpan.FromSeconds(3);

    private readonly MainViewModel _viewModel;
    private readonly TrayIconHost _tray;
    private bool _closeConfirmed;
    private bool _exitRequested;
    private bool _trayHintShown;
    private Point _dragStart;
    private CyberTask? _dragCandidate;

    public MainWindow(bool startHidden = false)
    {
        InitializeComponent();

        _viewModel = new MainViewModel(new StorageService(), new UpdateService(), new RegistryStartupRegistration());
        DataContext = _viewModel;

        _tray = new TrayIconHost();
        _tray.ShowRequested += (_, _) => ShowFromTray();
        _tray.ExitRequested += (_, _) => ExitApplication();

        _viewModel.AttentionRequested += OnAttentionRequested;
        _viewModel.OpenUrlRequested += OnOpenUrlRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Closed += OnClosed;
        StateChanged += OnStateChanged;

        // Oturum kapanırken (çıkış/yeniden başlatma) tepsiye gizleme yapılmasın.
        Application.Current.SessionEnding += (_, _) => _exitRequested = true;

        ApplyTheme(_viewModel.CurrentTheme);

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

    /// <summary>Tepsiden / ikinci örnekten / bildirime tıklamadan pencereyi öne getirir.</summary>
    public void ShowFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>Gerçek çıkış (tepsi menüsü → Çıkış). Pencere hiç gösterilmediyse de düzgün kapanır.</summary>
    private async void ExitApplication()
    {
        _exitRequested = true;
        if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
        {
            Close(); // OnClosing kayıtları boşaltır, OnClosed uygulamayı kapatır
            return;
        }

        await FlushAsync();
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    private async Task FlushAsync()
    {
        try
        {
            await _viewModel.ShutdownAsync().WaitAsync(s_shutdownSaveTimeout);
        }
        catch (TimeoutException)
        {
            // Bilinçli: zaman aşımında yine de kapan. Son yazma .tmp üzerinden yapıldığı için
            // ana dosya bozulmaz; en kötü ihtimalle son değişiklik kaybolur.
        }
    }

    /// <summary>
    /// ✕: ayar açıksa tepsiye gizler (uygulama ve hatırlatıcılar çalışmaya devam eder).
    /// Gerçek kapanışta bir kez iptal edip bekleyen JSON yazmalarını bitirir, sonra kapatır.
    /// </summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }

        e.Cancel = true;

        if (!_exitRequested && _viewModel.MinimizeToTray)
        {
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.Notify(Localizer.Instance["Tray_Running"]);
            }

            return;
        }

        await FlushAsync();
        _closeConfirmed = true;

        // Bekleyen kayıt yoksa await senkron tamamlanır ve hâlâ Closing içinde oluruz; Close() orada fırlatır → ertele.
        _ = Dispatcher.InvokeAsync(Close);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>WindowStyle=None + maksimize edildiğinde pencere ekran kenarından taşar; kenar boşluğuyla telafi edilir.</summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
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
                ApplyTheme(_viewModel.CurrentTheme);
                break;
        }
    }

    /// <summary>
    /// Tema: DynamicResource kullanan tüm fırçaları yeni (dondurulmuş) fırçalarla değiştirir.
    /// Primary = başlık/vurgu ("Yellow" anahtarları), Secondary = çerçeve/ikincil ("Cyan" anahtarları).
    /// </summary>
    private void ApplyTheme(ThemePalette palette)
    {
        static SolidColorBrush Brush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

        static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)Math.Round(a.R + ((b.R - a.R) * t)),
            (byte)Math.Round(a.G + ((b.G - a.G) * t)),
            (byte)Math.Round(a.B + ((b.B - a.B) * t)));

        Resources["PrimaryColor"] = palette.Primary;
        Resources["SecondaryColor"] = palette.Secondary;
        Resources["YellowBrush"] = Brush(palette.Primary);
        Resources["CyanBrush"] = Brush(palette.Secondary);
        Resources["YellowDimBrush"] = Brush(WithAlpha(palette.Primary, 0x55));
        Resources["CyanDimBrush"] = Brush(WithAlpha(palette.Secondary, 0x66));
        Resources["CyanFaintBrush"] = Brush(WithAlpha(palette.Secondary, 0x1F));
        Resources["AxisTextBrush"] = Brush(Mix(palette.Secondary, Color.FromRgb(0x8A, 0x94, 0xA6), 0.55));
        Resources["PrimaryDeepBrush"] = Brush(Mix(palette.Primary, Colors.Black, 0.9));
    }

    /// <summary>
    /// Odak kısayolları (VM komutu değil, View işi): Ctrl+N yeni görev kutusu, Ctrl+F arama.
    /// Diyalog açıkken arka plandaki alanlara odak taşınmaz.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || _viewModel.Editor is not null || _viewModel.IsSettingsOpen || _viewModel.IsTemplatesOpen)
        {
            return;
        }

        TextBox? target = e.Key switch
        {
            Key.N => TaskTitleBox,
            Key.F => SearchBox,
            _ => null,
        };

        if (target is not null)
        {
            target.Focus();
            target.SelectAll();
            e.Handled = true;
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

    // ───────────────────────────── Sürükle-bırak ─────────────────────────────

    private void DragSource_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CyberTask task })
        {
            _dragCandidate = task;
            _dragStart = e.GetPosition(this);
        }
    }

    /// <summary>Sistem eşiği kadar hareket edince sürükleme başlar (tıklamayla karışmaz).</summary>
    private void DragSource_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragCandidate = null;
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var task = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(CyberTask), task), DragDropEffects.Move);
        _viewModel.SetDropTarget(null); // iptal / pencere dışına bırakma
    }

    private static bool IsLowerHalf(FrameworkElement row, DragEventArgs e) => e.GetPosition(row).Y > row.ActualHeight / 2;

    private void Row_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(CyberTask)) is CyberTask dragged
            && sender is FrameworkElement { DataContext: CyberTask target } row
            && !ReferenceEquals(dragged, target))
        {
            e.Effects = DragDropEffects.Move;
            _viewModel.SetDropTarget(target, IsLowerHalf(row, e));
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private async void Row_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(CyberTask)) is CyberTask dragged && sender is FrameworkElement { DataContext: CyberTask target } row)
        {
            await _viewModel.MoveTaskAsync(dragged, target, IsLowerHalf(row, e));
        }
    }

    /// <summary>Satırların altındaki boş alan: listenin sonuna taşı.</summary>
    private void ListEnd_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(CyberTask)) ? DragDropEffects.Move : DragDropEffects.None;
        _viewModel.SetDropTarget(null);
        e.Handled = true;
    }

    private async void ListEnd_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(CyberTask)) is CyberTask dragged)
        {
            await _viewModel.MoveTaskAsync(dragged, null);
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
            _tray.Notify(message);
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

[Flags]
public enum ChamferCorners
{
    None = 0,
    TopLeft = 1,
    TopRight = 2,
    BottomRight = 4,
    BottomLeft = 8,
    All = TopLeft | TopRight | BottomRight | BottomLeft,
}

/// <summary>
/// 45° kesik köşeli HUD çerçevesi. Path + Stretch="Fill" yeniden boyutlanınca açıları bozduğu için
/// geometri her boyutta gerçek piksel ölçüsüyle (StreamGeometry, dondurulmuş) yeniden üretilir.
/// Sadece boyut veya özellik değişince render olur; içerik değişimleri bu elemanı tetiklemez.
/// </summary>
public sealed class ChamferFrame : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(ChamferFrame),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(ChamferFrame),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(ChamferFrame),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender),
        static v => v is double d && double.IsFinite(d) && d >= 0);

    public static readonly DependencyProperty ChamferProperty = DependencyProperty.Register(
        nameof(Chamfer), typeof(double), typeof(ChamferFrame),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender),
        static v => v is double d && double.IsFinite(d) && d >= 0);

    public static readonly DependencyProperty CornersProperty = DependencyProperty.Register(
        nameof(Corners), typeof(ChamferCorners), typeof(ChamferFrame),
        new FrameworkPropertyMetadata(ChamferCorners.TopLeft | ChamferCorners.BottomRight, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Doluysa kesik köşelerin DIŞINDA kalan üçgenler bu renkle boyanır (kontur üstte kalır).
    /// Böylece pencere dikdörtgen kalır ama köşeler renkli aksan olarak görünür.
    /// </summary>
    public static readonly DependencyProperty CornerFillProperty = DependencyProperty.Register(
        nameof(CornerFill), typeof(Brush), typeof(ChamferFrame),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? CornerFill
    {
        get => (Brush?)GetValue(CornerFillProperty);
        set => SetValue(CornerFillProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double Chamfer
    {
        get => (double)GetValue(ChamferProperty);
        set => SetValue(ChamferProperty, value);
    }

    public ChamferCorners Corners
    {
        get => (ChamferCorners)GetValue(CornersProperty);
        set => SetValue(CornersProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double thickness = StrokeThickness;
        double half = thickness / 2; // kontur dışarı taşmasın diye yarım kalınlık içeri
        double left = half, top = half, right = width - half, bottom = height - half;
        if (right <= left || bottom <= top)
        {
            return;
        }

        double c = Math.Min(Chamfer, Math.Min(right - left, bottom - top) / 2);
        var corners = Corners;
        bool tl = corners.HasFlag(ChamferCorners.TopLeft);
        bool tr = corners.HasFlag(ChamferCorners.TopRight);
        bool br = corners.HasFlag(ChamferCorners.BottomRight);
        bool bl = corners.HasFlag(ChamferCorners.BottomLeft);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(left + (tl ? c : 0), top), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(right - (tr ? c : 0), top), true, false);
            if (tr)
            {
                ctx.LineTo(new Point(right, top + c), true, false);
            }

            ctx.LineTo(new Point(right, bottom - (br ? c : 0)), true, false);
            if (br)
            {
                ctx.LineTo(new Point(right - c, bottom), true, false);
            }

            ctx.LineTo(new Point(left + (bl ? c : 0), bottom), true, false);
            if (bl)
            {
                ctx.LineTo(new Point(left, bottom - c), true, false);
            }

            ctx.LineTo(new Point(left, top + (tl ? c : 0)), true, false);
        }

        geometry.Freeze();

        Pen? pen = null;
        if (Stroke is { } stroke && thickness > 0)
        {
            pen = new Pen(stroke, thickness) { LineJoin = PenLineJoin.Miter };
            pen.Freeze();
        }

        // Köşe üçgenleri önce çizilir; ana kontur üstüne gelip kesik çizgiyi temiz bırakır.
        // Üçgenin hipotenüsü kontur merkez çizgisinin biraz içine uzanır → arada boşluk/aliasing kalmaz.
        if (CornerFill is { } cornerBrush && c > 0)
        {
            double reach = c + thickness;
            var cornerGeometry = new StreamGeometry();
            using (var ctx = cornerGeometry.Open())
            {
                void Triangle(Point a, Point b, Point d)
                {
                    ctx.BeginFigure(a, isFilled: true, isClosed: true);
                    ctx.LineTo(b, false, false);
                    ctx.LineTo(d, false, false);
                }

                if (tl) Triangle(new Point(0, 0), new Point(reach, 0), new Point(0, reach));
                if (tr) Triangle(new Point(width, 0), new Point(width, reach), new Point(width - reach, 0));
                if (br) Triangle(new Point(width, height), new Point(width - reach, height), new Point(width, height - reach));
                if (bl) Triangle(new Point(0, height), new Point(0, height - reach), new Point(reach, height));
            }

            cornerGeometry.Freeze();
            drawingContext.DrawGeometry(cornerBrush, null, cornerGeometry);
        }

        drawingContext.DrawGeometry(Fill, pen, geometry);
    }
}
