using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using CyberTodo.Models;

namespace CyberTodo.ViewModels;

/// <summary>Kayıtlı görünümler: arama + kategori + akıllı liste birleşimini tek tıkla geri getirir.</summary>
public sealed partial class MainViewModel
{
    public const int MaxSavedFilters = 12;

    public ObservableCollection<SavedFilter> SavedFilters { get; } = [];

    public bool HasSavedFilters => SavedFilters.Count > 0;

    private string? CurrentCategory => SelectedCategoryFilter?.Value;

    private SmartView CurrentSmartView => SelectedSmartView?.Value ?? SmartView.All;

    private string? CurrentSearch => SearchText.Trim() is { Length: > 0 } s ? s : null;

    /// <summary>Kaydedilecek bir filtre var mı (hepsi varsayılandaysa kaydetmenin anlamı yok) ve aynısı zaten kayıtlı değil mi.</summary>
    private bool CanSaveFilter() =>
        (CurrentSearch is not null || CurrentCategory is not null || CurrentSmartView != SmartView.All)
        && SavedFilters.Count < MaxSavedFilters
        && !SavedFilters.Any(IsCurrent);

    [RelayCommand(CanExecute = nameof(CanSaveFilter))]
    private async Task SaveCurrentFilterAsync()
    {
        if (!CanSaveFilter())
        {
            return;
        }

        // Ad otomatik: "#iş · BUGÜN · “rapor”" — metin kutusu/diyalog gerekmez, ne kaydedildiği adından belli.
        var parts = new List<string>(3);
        if (CurrentCategory is { } category)
        {
            parts.Add("#" + category);
        }

        if (CurrentSmartView != SmartView.All)
        {
            parts.Add(_loc[SmartViews.First(o => o.Value == CurrentSmartView).Key]);
        }

        if (CurrentSearch is { } search)
        {
            parts.Add($"“{search}”");
        }

        var filter = new SavedFilter
        {
            Name = string.Join(" · ", parts),
            Search = CurrentSearch,
            Category = CurrentCategory,
            SmartView = CurrentSmartView,
        };
        SavedFilters.Add(filter);
        OnSavedFiltersChanged();
        await SaveAsync();
    }

    [RelayCommand]
    private void ApplySavedFilter(SavedFilter? filter)
    {
        if (filter is null)
        {
            return;
        }

        SearchText = filter.Search ?? string.Empty;
        SelectedCategoryFilter = CategoryFilters.FirstOrDefault(o =>
            string.Equals(o.Value, filter.Category, StringComparison.CurrentCultureIgnoreCase)) ?? s_allCategories;
        SelectedSmartView = SmartViews.First(o => o.Value == filter.SmartView);
    }

    [RelayCommand]
    private async Task DeleteSavedFilterAsync(SavedFilter? filter)
    {
        if (filter is not null && SavedFilters.Remove(filter))
        {
            OnSavedFiltersChanged();
            await SaveAsync();
        }
    }

    private bool IsCurrent(SavedFilter filter) =>
        string.Equals(filter.Search ?? string.Empty, CurrentSearch ?? string.Empty, StringComparison.CurrentCultureIgnoreCase)
        && string.Equals(filter.Category, CurrentCategory, StringComparison.CurrentCultureIgnoreCase)
        && filter.SmartView == CurrentSmartView;

    /// <summary>Filtre değişti: kayıtlı çiplerin vurgusu ve "kaydet" düğmesinin durumu.</summary>
    private void OnFilterChanged()
    {
        foreach (var filter in SavedFilters)
        {
            filter.IsActive = IsCurrent(filter);
        }

        SaveCurrentFilterCommand.NotifyCanExecuteChanged();
    }

    private void OnSavedFiltersChanged()
    {
        OnPropertyChanged(nameof(HasSavedFilters));
        OnFilterChanged();
    }
}
