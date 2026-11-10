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
    private const int UpcomingDays = 7;

    /// <summary>Kategori filtresindeki "TÜMÜ" seçeneği (Value=null) — tek örnek, dil değişince etiketi güncellenir.</summary>
    private static readonly LocalizedOption<string?> s_allCategories = new(null, "Filter_All");

    public ObservableCollection<LocalizedOption<string?>> CategoryFilters { get; } = [];

    public IReadOnlyList<SmartViewOption> SmartViews { get; }

    public bool HasCategories => CategoryFilters.Count > 1;

    // ───────── Arama / filtre / sıralama ─────────

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private LocalizedOption<string?>? _selectedCategoryFilter;

    [ObservableProperty]
    private SmartViewOption? _selectedSmartView;

    [ObservableProperty]
    private LocalizedOption<SortMode> _selectedSortOption;

    partial void OnSearchTextChanged(string value)
    {
        RefreshViews();
        OnFilterChanged();
    }

    partial void OnSelectedCategoryFilterChanged(LocalizedOption<string?>? value)
    {
        RefreshViews();
        OnFilterChanged();
    }

    partial void OnSelectedSmartViewChanged(SmartViewOption? value)
    {
        RefreshViews();
        OnFilterChanged();
    }

    partial void OnSelectedSortOptionChanged(LocalizedOption<SortMode> value)
    {
        if (value is null)
        {
            return;
        }

        ActiveView.CustomSort = new TaskComparer(value.Value);
        _settings.SortMode = value.Value;
        SaveSettingsIfReady();
    }

    /// <summary>Görev listelerinde ne değişti (görünümün ne kadar yenileneceğini belirler).</summary>
    private enum TaskChange
    {
        /// <summary>
        /// Yalnızca ekleme/çıkarma (ekle, tamamla, geri aç, sil ve bunların geri alınması). ListCollectionView bu
        /// olayları tek tek işler: yeni öğeyi filtreden geçirip sıralı yerine koyar. Tam Refresh gereksiz ve pahalı
        /// (2.500 görevde işlem başına ~70-100 ms'nin büyük kısmı).
        /// </summary>
        Membership,

        /// <summary>Var olan görevin sıralama/filtre anahtarları değişmiş olabilir (düzenleme, toplu değişim): tam yenileme.</summary>
        Content,
    }

    /// <summary>Görev eklendi/silindi/değişti: kategori çipleri, akıllı liste sayaçları ve görünümler güncellenir.</summary>
    private void OnTasksChanged(TaskChange change = TaskChange.Content)
    {
        RefreshCategories();
        if (change == TaskChange.Content)
        {
            RefreshViews();
        }
        else
        {
            UpdateSmartViewCounts();
            if (IsMiniMode)
            {
                OnPropertyChanged(nameof(HasMiniTasks));
            }
        }

        if (!_initialized)
        {
            return;
        }

        if (IsStatsView)
        {
            RefreshStats();
        }
        else if (IsCalendarView)
        {
            RefreshCalendar();
        }
        else if (IsHabitsView)
        {
            RefreshHabits();
        }
    }

    private void RefreshViews()
    {
        UpdateSmartViewCounts();
        ActiveView.Refresh();
        CompletedView.Refresh();
        if (IsMiniMode)
        {
            MiniTasks.Refresh(); // gizliyken gereksiz liste kurulumu yapılmasın
            OnPropertyChanged(nameof(HasMiniTasks));
        }
    }

    private void UpdateSmartViewCounts()
    {
        var today = Today;
        foreach (var option in SmartViews)
        {
            option.Count = option.Value == SmartView.All
                ? ActiveTasks.Count
                : ActiveTasks.Count(t => IsInSmartView(t, option.Value, today));
        }
    }

    private bool MatchesSmartView(object item) =>
        item is CyberTask task && IsInSmartView(task, SelectedSmartView?.Value ?? SmartView.All, Today);

    private static bool IsInSmartView(CyberTask task, SmartView view, DateOnly today) => view switch
    {
        SmartView.Today => task.DueState == DueState.Today,
        SmartView.Overdue => task.DueState == DueState.Overdue,
        SmartView.Upcoming => task.DueAt is { } due
            && DateOnly.FromDateTime(due) > today
            && DateOnly.FromDateTime(due) <= today.AddDays(UpcomingDays),
        _ => true,
    };

    private bool MatchesFilter(object item)
    {
        if (item is not CyberTask task)
        {
            return false;
        }

        if (SelectedCategoryFilter?.Value is { } category
            && !string.Equals(task.Category, category, StringComparison.CurrentCultureIgnoreCase))
        {
            return false;
        }

        string query = SearchText.Trim();
        if (query.Length == 0)
        {
            return true;
        }

        return Contains(task.Title, query)
            || Contains(task.Category, query)
            || Contains(task.Code, query)
            || Contains(task.Notes, query)
            || task.Subtasks.Any(s => Contains(s.Title, query));
    }

    /// <summary>
    /// Büyük/küçük harf ve aksan duyarsız arama. IgnoreNonSpace sayesinde "istanbul" ↔ "İSTANBUL"
    /// ve "fixer" ↔ "FIXER" ikisi de eşleşir (tr-TR kültürünün i/I tuzağına düşmez).
    /// </summary>
    private static bool Contains(string? text, string query) =>
        text is not null
        && CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    private void RefreshCategories()
    {
        var categories = ActiveTasks.Concat(CompletedTasks)
            .Select(static t => t.Category?.Trim())
            .Where(static c => !string.IsNullOrEmpty(c))
            .Select(static c => c!)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (CategoryFilters.Skip(1).Select(static o => o.Value!).SequenceEqual(categories, StringComparer.Ordinal))
        {
            return;
        }

        string? currentValue = SelectedCategoryFilter?.Value;
        CategoryFilters.Clear();
        CategoryFilters.Add(s_allCategories);
        foreach (string category in categories)
        {
            CategoryFilters.Add(new LocalizedOption<string?>(category, "#" + category, literal: true));
        }

        SelectedCategoryFilter = CategoryFilters.FirstOrDefault(o =>
            string.Equals(o.Value, currentValue, StringComparison.CurrentCultureIgnoreCase)) ?? s_allCategories;
        OnPropertyChanged(nameof(HasCategories));
    }

    private void UpdateDerivedState(CyberTask task, DateTime now)
    {
        UpdateSubtaskInfo(task);
        RefreshDueInfo(task, now);
        RefreshTrackedText(task);
        RefreshRecurrenceText(task);
    }

    private static void UpdateSubtaskInfo(CyberTask task)
    {
        int total = task.Subtasks.Count;
        int done = task.Subtasks.Count(static s => s.IsDone);
        task.HasSubtasks = total > 0;
        task.HasNotes = !string.IsNullOrWhiteSpace(task.Notes);
        task.HasDetails = task.HasSubtasks || task.HasNotes;
        task.SubtaskSummary = total > 0 ? $"{done}/{total}" : string.Empty;
        if (total > 0 && !task.IsCompleted)
        {
            task.Progress = Math.Round(done * 100.0 / total);
        }
    }

    private void RefreshTrackedText(CyberTask task) =>
        task.TrackedText = task.TrackedMinutes > 0 ? FormatMinutes(task.TrackedMinutes) : string.Empty;

    private string FormatMinutes(int minutes) =>
        minutes < 60 ? _loc.Format("Fmt_Minutes", minutes) : _loc.Format("Fmt_HoursMinutes", minutes / 60, minutes % 60);

    private void RefreshDueInfo(CyberTask task, DateTime now)
    {
        if (task.DueAt is not { } due)
        {
            task.DueText = string.Empty;
            task.DueState = DueState.None;
            return;
        }

        var dueDate = DateOnly.FromDateTime(due);
        task.DueText = FormatDue(dueDate, task.HasDueTime ? TimeOnly.FromDateTime(due) : null, now);
        task.DueState = GetEffectiveDue(task) < now ? DueState.Overdue
            : dueDate == DateOnly.FromDateTime(now) ? DueState.Today
            : DueState.Upcoming;
    }

    /// <summary>"BUGÜN 14:00", "YARIN", "24 KAS", "24 KAS 2027" (dile göre).</summary>
    private string FormatDue(DateOnly date, TimeOnly? time, DateTime now)
    {
        var culture = _loc.Culture;
        var today = DateOnly.FromDateTime(now);

        string dayText = date == today ? _loc["Due_Today"]
            : date == today.AddDays(1) ? _loc["Due_Tomorrow"]
            : date.ToString(_loc["Fmt_ShortDate"], culture).ToUpper(culture)
              + (date.Year != today.Year ? $" {date.Year}" : string.Empty);

        return time is { } t ? $"{dayText} {t:HH\\:mm}" : dayText;
    }

    /// <summary>Saat girilmemişse son tarih o günün sonu kabul edilir.</summary>
    private static DateTime GetEffectiveDue(CyberTask task) =>
        task.DueAt is not { } due ? DateTime.MaxValue
        : task.HasDueTime ? due
        : due.Date.AddDays(1).AddTicks(-1);

    /// <summary>"GÜNLÜK", "HAFTA İÇİ", "PZT · ÇAR · CUM" (dile göre). Tekrar yoksa boş.</summary>
    private string FormatRecurrence(Recurrence recurrence, WeekDays days)
    {
        if (recurrence != Recurrence.SelectedDays)
        {
            return recurrence == Recurrence.None ? string.Empty : OptionSets.Find(OptionSets.Recurrences, recurrence).Label;
        }

        var mask = days & WeekDays.All;
        if (mask == WeekDays.All)
        {
            return _loc["Rec_AllDays"];
        }

        if (mask == WeekDays.Weekdays)
        {
            return _loc["Rec_Weekdays"];
        }

        if (mask == WeekDays.Weekend)
        {
            return _loc["Rec_Weekend"];
        }

        var culture = _loc.Culture;
        return string.Join(" · ", RecurrenceRules.MondayFirst
            .Where(d => RecurrenceRules.Contains(mask, d))
            .Select(d => culture.DateTimeFormat.GetAbbreviatedDayName(d).ToUpper(culture)));
    }

    private void RefreshRecurrenceText(CyberTask task) =>
        task.RecurrenceText = FormatRecurrence(task.Recurrence, task.RepeatDays);

    /// <summary>Aktif liste sıralaması. Eşitlikte eklenme zamanı (kararlı sıra).</summary>
    private sealed class TaskComparer(SortMode mode) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not CyberTask a || y is not CyberTask b)
            {
                return 0;
            }

            int result = mode switch
            {
                // Tarihi olmayanlar en sona.
                SortMode.DueDate => GetEffectiveDue(a).CompareTo(GetEffectiveDue(b)),
                SortMode.Priority => Rank(a.Priority).CompareTo(Rank(b.Priority)),
                SortMode.Manual => a.SortOrder.CompareTo(b.SortOrder),
                _ => 0,
            };

            return result != 0 ? result : a.CreatedAt.CompareTo(b.CreatedAt);
        }

        private static int Rank(TaskPriority priority) => priority switch
        {
            TaskPriority.HighPriority => 0,
            TaskPriority.Decrypting => 1,
            _ => 2,
        };
    }
}
