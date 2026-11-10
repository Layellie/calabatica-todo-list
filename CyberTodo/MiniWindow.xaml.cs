using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using CyberTodo.ViewModels;

namespace CyberTodo;

/// <summary>
/// Mini mod penceresi: her zaman üstte, küçük. Ana pencere gizliyken odak sayacı ve bugünün görevleri.
/// Konum ayarlara yazılır; ekran dışında kalan (ör. çıkarılmış monitör) konum kullanılmaz.
/// </summary>
public partial class MiniWindow : Window
{
    private const double EdgeMargin = 24;

    private readonly MainViewModel _viewModel;
    private bool _allowClose;

    public MiniWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        Closing += OnClosing;
    }

    /// <summary>✕: gizle (uygulama tepside sürer). View karar verir.</summary>
    public event EventHandler? HideRequested;

    /// <summary>Kaydedilmiş konum görünür bir ekrandaysa onu, değilse çalışma alanının sağ üstünü kullanır.</summary>
    public void PlaceOnScreen()
    {
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        if (_viewModel.MiniLeft is { } left && _viewModel.MiniTop is { } top
            && screen.Contains(new Point(left + 40, top + 20))) // başlığın bir kısmı görünür olsun (tutup taşınabilsin)
        {
            Left = left;
            Top = top;
            return;
        }

        var work = SystemParameters.WorkArea;
        double width = double.IsNaN(ActualWidth) || ActualWidth <= 0 ? 360 * _viewModel.UiScale : ActualWidth;
        Left = work.Right - width - EdgeMargin;
        Top = work.Top + EdgeMargin;
    }

    /// <summary>Uygulama kapanırken gerçekten kapanmasına izin ver (aksi halde ✕/Alt+F4 yalnızca gizler).</summary>
    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true; // Alt+F4: pencere yok edilmesin, yeniden kullanılacak
        HideRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        DragMove(); // bırakılınca döner
        _viewModel.SaveMiniPosition(Left, Top);
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke(this, EventArgs.Empty);
}
