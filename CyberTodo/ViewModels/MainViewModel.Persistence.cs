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
    /// <summary>Art arda değişiklikleri tek yazıma toplama süresi (kayıp penceresi en fazla bu kadar).</summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly DispatcherTimer _saveTimer;

    private bool _saveDirty;

    private bool _persistenceDisabled;

    private bool _errorIsFromSave;

    public string DataFilePath => _storage.FilePath;

    /// <summary>
    /// Kaydı planlar: art arda gelen değişiklikler (tamamla, ilerleme, alt görev…) <see cref="SaveDelay"/> içinde
    /// tek yazıma toplanır. Kayıp yalnızca süreç zorla öldürülürse ve en fazla bu kadar sürelik değişiklik olabilir;
    /// kapanışta (<see cref="ShutdownAsync"/>) ve oturum kapanırken (<see cref="FlushNow"/>) bekleyen kayıt hemen yazılır.
    /// </summary>
    private Task SaveAsync()
    {
        if (_persistenceDisabled)
        {
            return Task.CompletedTask;
        }

        _saveDirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
        return Task.CompletedTask;
    }

    private void OnSaveTimerTick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        _ = SaveNowAsync(); // hatalar SaveNowAsync içinde UI'a yansıtılır
    }

    /// <summary>
    /// Windows oturumu kapanırken: bekleyen kaydı UI thread'inde senkron yazar
    /// (async beklemeye zaman kalmayabilir).
    /// </summary>
    public void FlushNow()
    {
        _saveTimer.Stop();
        if (!_saveDirty || _persistenceDisabled)
        {
            return;
        }

        _saveDirty = false;
        try
        {
            _storage.Save(CreateSnapshot());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("MainViewModel", ex);
            _saveDirty = true;
            ErrorMessage = _loc.Format("Err_SaveFailed", ex.Message);
            _errorIsFromSave = true;
        }
    }

    /// <summary>
    /// UI thread'inde anlık görüntü alıp diske asenkron yazar.
    /// Hata yutulmaz; başlıktaki kırmızı hata satırında gösterilir (ve kayıt "bekliyor" olarak kalır).
    /// </summary>
    private async Task SaveNowAsync()
    {
        if (_persistenceDisabled || !_saveDirty)
        {
            return;
        }

        _saveDirty = false;
        var snapshot = CreateSnapshot();

        try
        {
            await _storage.SaveAsync(snapshot);
            if (_errorIsFromSave)
            {
                ErrorMessage = null;
                _errorIsFromSave = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("MainViewModel", ex);
            _saveDirty = true; // bir sonraki değişiklikte / kapanışta yeniden denenir
            ErrorMessage = _loc.Format("Err_SaveFailed", ex.Message);
            _errorIsFromSave = true;
        }
    }

    private CyberData CreateSnapshot() => new()
    {
        NextTaskNumber = _nextTaskNumber,
        Settings = _settings,
        Templates = [.. Templates],
        Tasks = [.. ActiveTasks, .. CompletedTasks],
        FocusSessions = [.. _focusSessions],
        Days =
        [
            .. _daysByDate.Values
                .Where(static d => d.Entries.Count > 0 || d.HoursWorked > 0 || d.CompletedCount > 0)
                .OrderBy(static d => d.Date),
        ],
    };
}
