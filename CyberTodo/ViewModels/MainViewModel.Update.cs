using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CyberTodo.Models;
using CyberTodo.Services;

namespace CyberTodo.ViewModels;

public sealed partial class MainViewModel
{
    private ReleaseInfo? _latestRelease;

    private Version? _toastedUpdateVersion;

    /// <summary>Tarayıcıda açılacak adres (yayın sayfası). Sadece doğrulanmış https://github.com adresleri.</summary>
    public event EventHandler<Uri>? OpenUrlRequested;

    // ───────── Sürüm / güncelleme ─────────

    /// <summary>Başlıktaki "v1.0": exe'nin kendi sürümü (yayın derlemesinde GitHub etiketinden gelir).</summary>
    [ObservableProperty]
    private string _versionText;

    /// <summary>"▲ v1.1" — GitHub'da daha yeni sürüm varsa; yoksa boş.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    private string _updateBadgeText = string.Empty;

    public bool HasUpdate => UpdateBadgeText.Length > 0;

    [ObservableProperty]
    private string _updateStatusText = string.Empty;

    private Uri? _releasePageUrl;

    // ───────── Güncelleme kurulumu ─────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallUpdate))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    private bool _isInstallingUpdate;

    /// <summary>Kurulu kopya + yayında güncelleme paketi var + şu an kurulmuyor.</summary>
    public bool CanInstallUpdate =>
        !IsInstallingUpdate
        && _installer is { CanSelfUpdate: true }
        && _latestRelease is { AssetUrl: not null, ChecksumUrl: not null } release
        && release.Version > _updates.CurrentVersion;

    /// <summary>Yeni exe yerine kondu: View kayıtları boşaltıp bu exe'yi başlatmalı ve kapanmalı.</summary>
    public event EventHandler<string>? RestartRequested;

    // ───────── Güncelleme ─────────

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        UpdateStatusText = _updates.IsConfigured ? _loc["Update_Checking"] : _loc["Update_NotConfigured"];
        _lastUpdateResult = await _updates.CheckAsync();
        ApplyUpdateResult(_lastUpdateResult);
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (_releasePageUrl is not null)
        {
            OpenUrlRequested?.Invoke(this, _releasePageUrl);
        }
    }

    private void ApplyUpdateResult(UpdateCheckResult result)
    {
        switch (result.Status)
        {
            case UpdateCheckStatus.NotConfigured:
                UpdateStatusText = _loc["Update_NotConfigured"];
                break;
            case UpdateCheckStatus.NoRelease:
                UpdateStatusText = _loc["Update_NoRelease"];
                UpdateBadgeText = string.Empty;
                break;
            case UpdateCheckStatus.UpToDate:
                UpdateStatusText = _loc.Format("Update_UpToDate", UpdateService.FormatVersion(result.Latest!.Version));
                UpdateBadgeText = string.Empty;
                break;
            case UpdateCheckStatus.UpdateAvailable:
                string latest = UpdateService.FormatVersion(result.Latest!.Version);
                UpdateStatusText = _loc.Format("Update_Available", latest);
                UpdateBadgeText = "▲ " + latest;
                _releasePageUrl = result.Latest.PageUrl;
                _latestRelease = result.Latest;
                OnPropertyChanged(nameof(CanInstallUpdate));
                InstallUpdateCommand.NotifyCanExecuteChanged();

                // Aynı sürüm için bir kez "KUR" bildirimi (her denetimde tekrar çıkmasın).
                if (CanInstallUpdate && _toastedUpdateVersion != result.Latest.Version)
                {
                    _toastedUpdateVersion = result.Latest.Version;
                    ShowToast(_loc.Format("Toast_UpdateReady", latest), ToastKind.Info, _loc["Update_Install"], InstallUpdateAsync);
                }

                break;
            default:
                UpdateStatusText = _loc.Format("Update_Failed", result.Error);
                break;
        }
    }

    /// <summary>
    /// İndir → SHA256 doğrula → exe'yi değiştir → yeniden başlat. Hata olursa uygulama olduğu gibi çalışmaya devam eder
    /// (yarım dosya kalmaz, çalışan exe yerinde kalır) ve neden ayarlar panelinde yazar.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdateAsync()
    {
        if (!CanInstallUpdate || _installer is null || _latestRelease is not { } release)
        {
            return;
        }

        IsInstallingUpdate = true;
        try
        {
            UpdateStatusText = _loc.Format("Update_Downloading", 0);
            var progress = new Progress<int>(p => UpdateStatusText = _loc.Format("Update_Downloading", p));
            string staged = await _installer.DownloadAndStageAsync(release, progress);
            _installer.Swap(staged);
            _installer.RecordInstalledVersion(release.Version);
            UpdateStatusText = _loc["Update_Restarting"];
            RestartRequested?.Invoke(this, _installer.ExePath!);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or InvalidDataException
                                       or UnauthorizedAccessException or TaskCanceledException or InvalidOperationException)
        {
            AppLog.Error("MainViewModel", ex);
            UpdateStatusText = _loc.Format("Update_InstallFailed", ex.Message);
            ShowToast(UpdateStatusText, ToastKind.Info, lifetime: TimeSpan.FromSeconds(8));
        }
        finally
        {
            IsInstallingUpdate = false;
        }
    }

    private UpdateCheckResult? _lastUpdateResult;

    /// <summary>Açılışta arka planda denetim; hata uygulamayı etkilemez, sadece ayarlar panelinde yazar.</summary>
    private async Task AutoCheckUpdatesAsync()
    {
        _lastUpdateResult = await _updates.CheckAsync();
        ApplyUpdateResult(_lastUpdateResult);
    }
}
