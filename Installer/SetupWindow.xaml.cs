using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace CalabaticaSetup;

public partial class SetupWindow : Window
{
    private readonly bool _uninstall;
    private readonly Texts _t;
    private bool _busy;
    private bool _done;

    public SetupWindow(bool uninstall, Texts texts)
    {
        InitializeComponent();
        _uninstall = uninstall;
        _t = texts;

        TitleSuffix.Text = $" // {(uninstall ? texts.UninstallTitle : texts.InstallTitle)}  v{InstallerEngine.SetupVersion}";
        LeadText.Text = uninstall ? texts.UninstallLead
            : InstallerEngine.IsInstalled ? texts.Reinstall
            : texts.InstallLead;
        LocationLabel.Text = texts.Location;
        LocationText.Text = InstallerEngine.InstallDir;
        LocationText.ToolTip = InstallerEngine.InstallDir;

        DesktopCheck.Content = texts.Desktop;
        LaunchCheck.Content = texts.LaunchAfter;
        DeleteDataCheck.Content = texts.DeleteData;
        DeleteDataCheck.ToolTip = InstallerEngine.DataDir;

        if (uninstall)
        {
            DesktopCheck.Visibility = Visibility.Collapsed;
            LaunchCheck.Visibility = Visibility.Collapsed;
            DeleteDataCheck.Visibility = Visibility.Visible;
            RuntimeText.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateRuntimeText();
        }

        PrimaryButton.Content = uninstall ? texts.Uninstall : texts.Install;
        CancelButton.Content = texts.Cancel;
        LaunchButton.Content = texts.Launch;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Kopyalama sürerken kapatılırsa yarım kurulum kalmasın.
        e.Cancel = _busy;
        base.OnClosing(e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            InstallerEngine.LaunchApp();
            Close();
        }
        catch (Win32Exception ex)
        {
            StatusText.Text = _t.Failed + ex.Message;
        }
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_done)
        {
            Close();
            return;
        }

        if (InstallerEngine.IsAppRunning()
            && MessageBox.Show(this, _t.AppRunning, "CALABATICA", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        bool desktop = DesktopCheck.IsChecked == true;
        bool deleteData = DeleteDataCheck.IsChecked == true;
        void Report(string step) => Dispatcher.Invoke(() => StatusText.Text = _t.Step(step));

        SetBusy(true);
        try
        {
            if (_uninstall)
            {
                await Task.Run(() => InstallerEngine.Uninstall(deleteData, Report));
            }
            else
            {
                // Uygulama framework-dependent: önce .NET 10 Desktop Runtime (yoksa) kurulur.
                if (!RuntimeInstaller.IsInstalled())
                {
                    await RuntimeInstaller.InstallAsync(
                        Report,
                        percent => Dispatcher.Invoke(() => StatusText.Text = string.Format(_t.RuntimeDownload, percent)));
                    UpdateRuntimeText();
                }

                await Task.Run(() => InstallerEngine.Install(desktop, Report));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or COMException or System.Security.SecurityException
                                       or Win32Exception or System.Net.WebException)
        {
            // Win32Exception: yönetici onayı (UAC) iptal edildi; WebException: indirme başarısız (internet yok vb.)
            SetBusy(false);
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("Red");
            StatusText.Text = _t.Failed + ex.Message;
            return;
        }

        SetBusy(false);
        _done = true;
        StatusText.Text = _uninstall ? _t.Uninstalled : _t.Installed;
        CancelButton.Visibility = Visibility.Collapsed;
        PrimaryButton.Content = _t.Close;

        if (!_uninstall)
        {
            if (LaunchCheck.IsChecked == true)
            {
                Launch_Click(this, new RoutedEventArgs());
                return;
            }

            LaunchButton.Visibility = Visibility.Visible;
        }
    }

    private void UpdateRuntimeText()
    {
        bool ok = RuntimeInstaller.IsInstalled();
        RuntimeText.Text = ok ? _t.RuntimeOk : _t.RuntimeMissing;
        RuntimeText.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "Cyan" : "Yellow");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        PrimaryButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        DesktopCheck.IsEnabled = !busy;
        LaunchCheck.IsEnabled = !busy;
        DeleteDataCheck.IsEnabled = !busy;
        if (busy)
        {
            StatusText.Text = _t.Working;
        }
    }
}
