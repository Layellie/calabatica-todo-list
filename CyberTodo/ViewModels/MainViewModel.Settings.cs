using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CyberTodo.Models;
using CyberTodo.Services;

namespace CyberTodo.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty]
    private LocalizedOption<bool> _selectedAutoUpdateOption;

    // ───────── Tema / tepsi / başlangıç ─────────

    [ObservableProperty]
    private LocalizedOption<ThemeId> _selectedThemeOption;

    /// <summary>View bu değişince renk kaynaklarını değiştirir.</summary>
    [ObservableProperty]
    private ThemePalette _currentTheme;

    [ObservableProperty]
    private LocalizedOption<bool> _selectedTrayOption;

    public bool MinimizeToTray => _settings.MinimizeToTray;

    [ObservableProperty]
    private LocalizedOption<bool> _selectedStartupOption;

    [ObservableProperty]
    private LocalizedOption<bool> _selectedBreaksOption;

    [ObservableProperty]
    private LocalizedOption<bool> _selectedAutoNextOption;

    partial void OnSelectedBreaksOptionChanged(LocalizedOption<bool> value)
    {
        if (value is null)
        {
            return;
        }

        _settings.BreaksEnabled = value.Value;
        if (!value.Value)
        {
            Focus.CancelBreak();
        }

        SaveSettingsIfReady();
    }

    partial void OnSelectedAutoNextOptionChanged(LocalizedOption<bool> value)
    {
        if (value is null)
        {
            return;
        }

        _settings.AutoStartNextFocus = value.Value;
        SaveSettingsIfReady();
    }

    public IReadOnlyList<LocalizedOption<ThemeId>> ThemeOptions => OptionSets.Themes;

    public IReadOnlyList<LocalizedOption<TaskPriority>> PriorityOptions => OptionSets.Priorities;

    public IReadOnlyList<LocalizedOption<SortMode>> SortOptions => OptionSets.SortModes;

    public IReadOnlyList<LocalizedOption<AppLanguage>> LanguageOptions => OptionSets.Languages;

    public IReadOnlyList<LocalizedOption<double>> ScaleOptions => OptionSets.Scales;

    public IReadOnlyList<LocalizedOption<bool>> SoundOptions => OptionSets.OnOff;

    public IReadOnlyList<LocalizedOption<int>> FocusLengthOptions => OptionSets.FocusLengths;

    public bool SoundEnabled => _settings.ReminderSound;

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private LocalizedOption<AppLanguage> _selectedLanguageOption;

    [ObservableProperty]
    private LocalizedOption<double> _selectedScaleOption;

    [ObservableProperty]
    private LocalizedOption<bool> _selectedSoundOption;

    [ObservableProperty]
    private LocalizedOption<int> _selectedFocusLengthOption;

    /// <summary>İçerik ölçeği (LayoutTransform). Pencere boyutu değişmez.</summary>
    [ObservableProperty]
    private double _uiScale = AppSettings.DefaultUiScale;

    // ═════════════════════════════ Ayarlar / bildirim / grafik / kısayol komutları ═════════════════════════════

    [RelayCommand]
    private void OpenSettings()
    {
        CloseOverlays();
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    partial void OnSelectedThemeOptionChanged(LocalizedOption<ThemeId> value)
    {
        if (value is null)
        {
            return;
        }

        _settings.Theme = value.Value;
        ApplyEffectiveTheme(); // Windows yüksek kontrastı açıksa o öncelikli
        SaveSettingsIfReady();
    }

    partial void OnSelectedTrayOptionChanged(LocalizedOption<bool> value)
    {
        if (value is null)
        {
            return;
        }

        _settings.MinimizeToTray = value.Value;
        OnPropertyChanged(nameof(MinimizeToTray));
        SaveSettingsIfReady();
    }

    partial void OnSelectedAutoUpdateOptionChanged(LocalizedOption<bool> value)
    {
        if (value is null)
        {
            return;
        }

        _settings.AutoCheckUpdates = value.Value;
        SaveSettingsIfReady();
    }

    /// <summary>Kayıt defterine yazar; başarısızsa seçimi geri alır ve hatayı gösterir (sessiz yutma yok).</summary>
    partial void OnSelectedStartupOptionChanged(LocalizedOption<bool> value)
    {
        if (value is null || !_initialized || _applyingStartupState)
        {
            return;
        }

        try
        {
            _startup.SetEnabled(value.Value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            AppLog.Error("MainViewModel", ex);
            ErrorMessage = _loc.Format("Err_Startup", ex.Message);
            ReadStartupState();
        }
    }

    private bool _applyingStartupState;

    private void ReadStartupState()
    {
        _applyingStartupState = true;
        try
        {
            bool enabled;
            try
            {
                enabled = _startup.IsEnabled();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                AppLog.Error("MainViewModel", ex);
                enabled = false;
                ErrorMessage = _loc.Format("Err_Startup", ex.Message);
            }

            SelectedStartupOption = OptionSets.Find(OptionSets.OnOff, enabled);
        }
        finally
        {
            _applyingStartupState = false;
        }
    }

    partial void OnSelectedLanguageOptionChanged(LocalizedOption<AppLanguage> value)
    {
        if (value is null)
        {
            return;
        }

        _settings.Language = value.Value;
        _loc.SetLanguage(value.Value);
        SaveSettingsIfReady();
    }

    partial void OnSelectedScaleOptionChanged(LocalizedOption<double> value)
    {
        if (value is null)
        {
            return;
        }

        _settings.UiScale = value.Value;
        UiScale = value.Value;
        SaveSettingsIfReady();
    }

    partial void OnSelectedSoundOptionChanged(LocalizedOption<bool> value)
    {
        if (value is null)
        {
            return;
        }

        _settings.ReminderSound = value.Value;
        OnPropertyChanged(nameof(SoundEnabled));
        SaveSettingsIfReady();
    }

    partial void OnSelectedFocusLengthOptionChanged(LocalizedOption<int> value)
    {
        if (value is null)
        {
            return;
        }

        _settings.FocusMinutes = value.Value; // çalışan oturumu etkilemez, bir sonrakinden geçerli
        SaveSettingsIfReady();
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Localizer.Language) || !_initialized)
        {
            return;
        }

        var now = Now;
        foreach (var task in ActiveTasks.Concat(CompletedTasks))
        {
            RefreshDueInfo(task, now);
            RefreshTrackedText(task);
            RefreshRecurrenceText(task);
        }

        UpdateQuickAddPreview();

        RefreshWeekdayHeaders();

        if (_lastUpdateResult is { } result)
        {
            ApplyUpdateResult(result);
        }
        else
        {
            UpdateStatusText = _updates.IsConfigured ? string.Empty : _loc["Update_NotConfigured"];
        }

        RebuildChart(); // tarih etiketleri, istatistik, odak ve takvim dile bağlı
        OnPropertyChanged(nameof(Editor)); // düzenleyici başlığı
        OnPropertyChanged(nameof(UndoHint));
    }

    private void ApplySettingsToUi()
    {
        // _initialized=false olduğu için buradaki atamalar kayıt tetiklemez.
        SelectedLanguageOption = OptionSets.Find(OptionSets.Languages, _settings.Language);
        _loc.SetLanguage(_settings.Language);

        var nearestScale = OptionSets.Scales.MinBy(o => Math.Abs(o.Value - _settings.UiScale)) ?? OptionSets.Scales[0];
        SelectedScaleOption = nearestScale;
        UiScale = nearestScale.Value;

        SelectedSoundOption = OptionSets.Find(OptionSets.OnOff, _settings.ReminderSound);
        SelectedFocusLengthOption = OptionSets.Find(OptionSets.FocusLengths, _settings.FocusMinutes);
        SelectedSortOption = OptionSets.Find(OptionSets.SortModes, _settings.SortMode);
        ActiveView.CustomSort = new TaskComparer(_settings.SortMode);

        // Kayıtlı dil burada uygulanıyor ama _initialized=false olduğundan OnLanguageChanged erken dönüyor:
        // dile bağlı listeyi elle yenile (yoksa İngilizce açılışta takvim başlıkları Türkçe kalıyordu).
        RefreshWeekdayHeaders();

        SelectedThemeOption = OptionSets.Find(OptionSets.Themes, _settings.Theme);
        ApplyEffectiveTheme();
        SelectedTrayOption = OptionSets.Find(OptionSets.OnOff, _settings.MinimizeToTray);
        SelectedAutoUpdateOption = OptionSets.Find(OptionSets.OnOff, _settings.AutoCheckUpdates);
        SelectedBreaksOption = OptionSets.Find(OptionSets.OnOff, _settings.BreaksEnabled);
        SelectedAutoNextOption = OptionSets.Find(OptionSets.OnOff, _settings.AutoStartNextFocus);
        OnPropertyChanged(nameof(MinimizeToTray));
    }

    private void SaveSettingsIfReady()
    {
        if (_initialized)
        {
            _ = SaveAsync(); // SaveAsync hatayı kendi içinde UI'a yansıtır
        }
    }
}
