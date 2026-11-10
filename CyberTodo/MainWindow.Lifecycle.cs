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
using CyberTodo.ViewModels;

namespace CyberTodo;

public partial class MainWindow
{
    private bool _handlingFatalError;

    /// <summary>
    /// Tepsi simgesini bağlar. Normal açılışta pencere ilk kez çizildikten SONRA çağrılır: WinForms'u (NotifyIcon)
    /// yüklemek açılışın kritik yolunda yüzlerce ms tutuyordu; gizli başlangıçta (--tray) ise hemen bağlanır.
    /// </summary>
    public void AttachTray(TrayIconHost tray)
    {
        ArgumentNullException.ThrowIfNull(tray);
        if (_tray is not null)
        {
            tray.Dispose(); // zaten bağlı (çift çağrı)
            return;
        }

        _tray = tray;
        tray.ShowRequested += (_, _) => ShowFromTray();
        tray.ExitRequested += (_, _) => ExitApplication();
        tray.MiniRequested += (_, _) =>
        {
            if (_viewModel.IsMiniMode)
            {
                ShowMini(); // gizlenmişse geri getir
            }
            else
            {
                _viewModel.ToggleMiniModeCommand.Execute(null);
            }
        };
        StartupTrace.Mark("tray-attached");
    }

    /// <summary>Kıyaslama modu: kayıt/tepsi hayaletine yol açmadan temiz kapan.</summary>
    internal void ExitForBenchmark()
    {
        _exitRequested = true;
        _closeConfirmed = true;
        Close();
    }

    /// <summary>Yakalanmamış UI hatası: rapor + acil kayıt + bilgi kutusu + kapanış. İkinci hata gelirse doğrudan kapanır.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Çökme sınırı: acil kayıt başarısız olsa bile asıl hata raporu ve kullanıcı bilgilendirmesi engellenmemeli.")]
    public void HandleFatalError(Exception exception)
    {
        if (_handlingFatalError)
        {
            Application.Current.Shutdown();
            return;
        }

        _handlingFatalError = true;
        string? reportPath = AppLog.WriteCrash(exception, "Dispatcher");

        try
        {
            _viewModel.FlushNow(); // kendi I/O hatalarını yakalar; buradaki geniş catch çökme sınırı içindir
        }
        catch (Exception flushError)
        {
            AppLog.Error("FlushOnCrash", flushError);
        }

        MessageBox.Show(
            Localizer.Instance.Format("Crash_Message", reportPath ?? AppLog.Directory),
            "CALABATICA",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        _mini?.CloseForShutdown();
        _tray?.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>Tepsiden / ikinci örnekten / bildirime tıklamadan pencereyi öne getirir (mini moddaysa tam görünüme döner).</summary>
    public void ShowFromTray()
    {
        if (_viewModel.IsMiniMode)
        {
            _viewModel.IsMiniMode = false; // değişiklik işleyicisi bu metodu yeniden çağırır
            return;
        }

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
        _tray?.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Güncelleme kuruldu: bekleyen kayıtları yaz, yeni exe'yi "--after-update" ile başlat (eski süreç kapanana kadar
    /// bekler, tek örnek kilidine takılmaz), sonra kapan. Başlatılamazsa uygulama açık kalır ve hata gösterilir.
    /// </summary>
    private async void OnRestartRequested(object? sender, string exePath)
    {
        await FlushAsync();
        try
        {
            Process.Start(new ProcessStartInfo(exePath, Program.AfterUpdateArgument)
            {
                UseShellExecute = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(exePath)!,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Error("MainWindow", ex);
            // Yeni exe yerinde; bir sonraki elle açılışta yeni sürüm çalışır.
            _viewModel.ErrorMessage = ex.Message;
            return;
        }

        _exitRequested = true;
        _closeConfirmed = true; // kayıtlar zaten yazıldı
        if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
        {
            Close();
        }
        else
        {
            _tray?.Dispose();
            Application.Current.Shutdown();
        }
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
                _tray?.Notify(Localizer.Instance["Tray_Running"]);
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
        _trimTimer.Stop();
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged; // statik olay: pencereyi canlı tutmasın
        _mini?.CloseForShutdown();
        _tray?.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>WindowStyle=None + maksimize edildiğinde pencere ekran kenarından taşar; kenar boşluğuyla telafi edilir.</summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        EvaluateTrim();
    }

    /// <summary>Kullanıcı hiçbir pencereye bakmıyor: ana pencere gizli/küçültülmüş, mini pencere görünmüyor.</summary>
    private bool IsBackgroundIdle => (!IsVisible || WindowState == WindowState.Minimized) && (_mini is null || !_mini.IsVisible);

    /// <summary>Arka plana geçildiyse kırpmayı zamanlar; öne dönüldüyse iptal eder.</summary>
    private void EvaluateTrim()
    {
        _trimTimer.Stop();
        if (IsBackgroundIdle)
        {
            _trimTimer.Start();
        }
    }
}
