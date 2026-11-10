using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CyberTodo.Models;
using CyberTodo.Services;

namespace CyberTodo.ViewModels;

public sealed partial class MainViewModel
{
    // ───────── Veri / yedek ─────────

    public ObservableCollection<BackupInfo> Backups { get; } = [];

    [ObservableProperty]
    private bool _isDataOpen;

    public string BackupDirectory => _backups.BackupDirectory;

    // ───────── Veri: yedek / geri yükleme / dışa-içe aktarma ─────────

    private const string JsonFilter = "JSON (*.json)|*.json";

    [RelayCommand]
    private void OpenData()
    {
        Editor = null;
        IsSettingsOpen = false;
        IsTemplatesOpen = false;
        RefreshBackups();
        IsDataOpen = true;
    }

    [RelayCommand]
    private void CloseData() => IsDataOpen = false;

    private void RefreshBackups()
    {
        Backups.Clear();
        try
        {
            foreach (var backup in _backups.List())
            {
                Backups.Add(backup);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("MainViewModel", ex);
            ErrorMessage = _loc.Format("Err_Backup", ex.Message);
        }
    }

    /// <summary>O gün için yedek yoksa alır (açılışta ve gün dönünce). Hata yutulmaz, başlıkta gösterilir.</summary>
    private async Task CreateDailyBackupAsync(DateTime now)
    {
        if (_persistenceDisabled)
        {
            return;
        }

        byte[] payload = StorageService.Serialize(CreateSnapshot()); // UI thread'inde tutarlı anlık görüntü
        try
        {
            await _backups.CreateDailyAsync(payload, DateOnly.FromDateTime(now));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("MainViewModel", ex);
            ErrorMessage = _loc.Format("Err_Backup", ex.Message);
        }
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        try
        {
            string path = await _backups.CreateSafetyAsync(StorageService.Serialize(CreateSnapshot()), Now, "manual");
            ShowToast(_loc.Format("Toast_BackupSaved", Path.GetFileName(path)), ToastKind.Info, lifetime: s_infoLifetime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("MainViewModel", ex);
            ErrorMessage = _loc.Format("Err_Backup", ex.Message);
        }

        RefreshBackups();
    }

    [RelayCommand]
    private async Task RestoreBackupAsync(BackupInfo? backup)
    {
        if (backup is null || !_backups.Contains(backup.FullPath))
        {
            return;
        }

        var culture = _loc.Culture;
        string when = backup.CreatedAt.ToString(_loc["Fmt_BackupDate"], culture);
        await ReplaceFromFileAsync(backup.FullPath, "restore", data => _loc.Format("Confirm_Restore", when, data.Tasks.Count), "Toast_Restored");
    }

    [RelayCommand]
    private async Task ImportJsonAsync()
    {
        string? path = _dialogs.PickOpenPath(JsonFilter);
        if (path is null)
        {
            return;
        }

        await ReplaceFromFileAsync(path, "import", data => _loc.Format("Confirm_Import", Path.GetFileName(path), data.Tasks.Count), "Toast_Imported");
    }

    /// <summary>
    /// Dosyayı okur → kullanıcıya sorar → mevcut verinin güvenlik yedeğini alır → veriyi değiştirir → hemen kaydeder.
    /// Okuma veya yedekleme başarısızsa mevcut veriye dokunulmaz.
    /// </summary>
    private async Task ReplaceFromFileAsync(string path, string reason, Func<CyberData, string> confirmText, string toastKey)
    {
        if (_persistenceDisabled)
        {
            ErrorMessage = _loc["Err_PersistenceDisabled"];
            return;
        }

        CyberData data;
        try
        {
            data = await StorageService.ReadFileAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Error("MainViewModel", ex);
            ErrorMessage = _loc.Format("Err_ReadFile", Path.GetFileName(path), ex.Message);
            return;
        }

        if (!_dialogs.Confirm(confirmText(data)))
        {
            return;
        }

        string safety;
        try
        {
            safety = await _backups.CreateSafetyAsync(StorageService.Serialize(CreateSnapshot()), Now, reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("MainViewModel", ex);
            ErrorMessage = _loc.Format("Err_Backup", ex.Message); // yedeksiz değiştirme yapılmaz
            return;
        }

        ReplaceData(data);
        _saveDirty = true;
        _saveTimer.Stop();
        await SaveNowAsync();

        IsDataOpen = false;
        ShowToast(_loc.Format(toastKey, data.Tasks.Count, Path.GetFileName(safety)), ToastKind.Info, lifetime: TimeSpan.FromSeconds(8));
    }

    /// <summary>Tüm veriyi değiştirir. Odak oturumu kredilendirilmeden kapanır (eski veriye aitti), geri alma geçmişi silinir.</summary>
    private void ReplaceData(CyberData data)
    {
        Focus.Reset();
        SelectedTask = null;
        Editor = null;
        ClearUndo();

        var now = Now;
        ApplyData(data, now);
        _viewsDay = DateOnly.FromDateTime(now);
        OnTasksChanged();
        RebuildChart();
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        string? path = _dialogs.PickSavePath($"calabatica-{Now:yyyyMMdd}.json", JsonFilter);
        if (path is not null)
        {
            await ExportAsync(path, StorageService.Serialize(CreateSnapshot()));
        }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        string? path = _dialogs.PickSavePath($"calabatica-{Now:yyyyMMdd}.csv", "CSV (*.csv)|*.csv");
        if (path is null)
        {
            return;
        }

        // Excel UTF-8'i BOM ile tanır; ayırıcı/ondalık Windows bölge ayarından (Excel de onu kullanır).
        string csv = DataExporter.ToCsv([.. ActiveTasks, .. CompletedTasks], CultureInfo.CurrentCulture);
        await ExportAsync(path, [.. System.Text.Encoding.UTF8.GetPreamble(), .. System.Text.Encoding.UTF8.GetBytes(csv)]);
    }

    [RelayCommand]
    private async Task ExportIcsAsync()
    {
        if (!ActiveTasks.Any(static t => t.DueAt is not null))
        {
            ShowToast(_loc["Toast_NoDatedTasks"], ToastKind.Info, lifetime: s_infoLifetime);
            return;
        }

        string? path = _dialogs.PickSavePath($"calabatica-{Now:yyyyMMdd}.ics", "iCalendar (*.ics)|*.ics");
        if (path is not null)
        {
            await ExportAsync(path, System.Text.Encoding.UTF8.GetBytes(DataExporter.ToIcs(ActiveTasks, DateTime.UtcNow)));
        }
    }

    private async Task ExportAsync(string path, byte[] payload)
    {
        try
        {
            await StorageService.WriteAtomicAsync(path, payload);
            ShowToast(_loc.Format("Toast_Exported", Path.GetFileName(path)), ToastKind.Info, lifetime: s_infoLifetime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("MainViewModel", ex);
            ErrorMessage = _loc.Format("Err_Export", ex.Message);
        }
    }
}
