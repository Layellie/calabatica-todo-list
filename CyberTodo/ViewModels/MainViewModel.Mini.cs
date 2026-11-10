using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CyberTodo.Services;

namespace CyberTodo.ViewModels;

public sealed partial class MainViewModel
{
    // ───────── Mini mod ─────────

    /// <summary>Mini pencerede: bugün ve gecikmiş görevler, son tarihe göre.</summary>
    public ListCollectionView MiniTasks { get; }

    public bool HasMiniTasks => !MiniTasks.IsEmpty;

    /// <summary>View bu değişince ana pencere ile mini pencere arasında geçiş yapar.</summary>
    [ObservableProperty]
    private bool _isMiniMode;

    partial void OnIsMiniModeChanged(bool value)
    {
        if (value)
        {
            MiniTasks.Refresh(); // kapalıyken yenilenmiyordu
            OnPropertyChanged(nameof(HasMiniTasks));
        }
    }

    [RelayCommand]
    private void ToggleMiniMode()
    {
        CloseOverlays();
        IsMiniMode = !IsMiniMode;
    }

    public double? MiniLeft => _settings.MiniLeft;

    public double? MiniTop => _settings.MiniTop;

    /// <summary>Mini pencere taşındı/kapandı: konum hatırlanır.</summary>
    public void SaveMiniPosition(double left, double top)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top))
        {
            return;
        }

        _settings.MiniLeft = Math.Round(left);
        _settings.MiniTop = Math.Round(top);
        SaveSettingsIfReady();
    }
}
