using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CyberTodo.Models;
using CyberTodo.Services;

namespace CyberTodo.ViewModels;

/// <summary>Uygulama genelinde paylaşılan seçim listeleri (dil değişince etiketler kendiliğinden güncellenir).</summary>
public static class OptionSets
{
    public static IReadOnlyList<LocalizedOption<TaskPriority>> Priorities { get; } =
    [
        new(TaskPriority.Active, "Prio_Active"),
        new(TaskPriority.HighPriority, "Prio_High"),
        new(TaskPriority.Decrypting, "Prio_Decrypt"),
    ];

    public static IReadOnlyList<LocalizedOption<Recurrence>> Recurrences { get; } =
    [
        new(Recurrence.None, "Rec_None"),
        new(Recurrence.Daily, "Rec_Daily"),
        new(Recurrence.Weekly, "Rec_Weekly"),
        new(Recurrence.Monthly, "Rec_Monthly"),
        new(Recurrence.SelectedDays, "Rec_Days"),
    ];

    public static IReadOnlyList<LocalizedOption<int?>> Reminders { get; } =
    [
        new(null, "Rem_Off"),
        new(0, "Rem_AtTime"),
        new(15, "Rem_15"),
        new(60, "Rem_60"),
        new(1440, "Rem_1440"),
    ];

    public static IReadOnlyList<LocalizedOption<SortMode>> SortModes { get; } =
    [
        new(SortMode.Created, "Sort_Created"),
        new(SortMode.DueDate, "Sort_Due"),
        new(SortMode.Priority, "Sort_Priority"),
        new(SortMode.Manual, "Sort_Manual"),
    ];

    public static IReadOnlyList<LocalizedOption<ThemeId>> Themes { get; } =
        [.. ThemePalette.All.Select(static p => new LocalizedOption<ThemeId>(p.Id, p.Name, literal: true))];

    public static IReadOnlyList<LocalizedOption<AppLanguage>> Languages { get; } =
    [
        new(AppLanguage.Turkish, "TÜRKÇE", literal: true),
        new(AppLanguage.English, "ENGLISH", literal: true),
    ];

    public static IReadOnlyList<LocalizedOption<double>> Scales { get; } =
    [
        new(0.7, "70%", literal: true),
        new(0.85, "85%", literal: true),
        new(1.0, "100%", literal: true),
    ];

    public static IReadOnlyList<LocalizedOption<int>> FocusLengths { get; } =
    [
        new(15, "15", literal: true),
        new(25, "25", literal: true),
        new(45, "45", literal: true),
        new(60, "60", literal: true),
    ];

    public static IReadOnlyList<LocalizedOption<bool>> OnOff { get; } =
    [
        new(true, "Opt_On"),
        new(false, "Opt_Off"),
    ];

    public static LocalizedOption<T> Find<T>(IReadOnlyList<LocalizedOption<T>> options, T value) =>
        options.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.Value, value)) ?? options[0];
}

/// <summary>Kullanıcı girdisi ayrıştırma; hem hızlı ekleme hem düzenleyici kullanır.</summary>
public static class InputParsing
{
    public const double MaxTaskHours = 24;

    private static readonly string[] s_dateFormats = ["dd.MM.yyyy", "d.M.yyyy", "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd"];
    private static readonly string[] s_timeFormats = ["HH:mm", "H:mm", "HH.mm", "H.mm"];

    /// <summary>"1.5" ve "1,5" ikisini de kabul eder; (0, 24] aralığı dışını reddeder.</summary>
    public static bool TryParseHours(string? text, out double hours)
    {
        hours = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string normalized = text.Trim().Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            || !double.IsFinite(parsed)
            || parsed <= 0
            || parsed > MaxTaskHours)
        {
            return false;
        }

        hours = Math.Round(parsed, 2);
        return true;
    }

    public static bool TryParseDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text.Trim(), s_dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    public static bool TryParseTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text.Trim(), s_timeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static string FormatDate(DateOnly date) => date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    public static string FormatHours(double hours) => hours.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>Gün seçici çipi (Pzt..Paz). Etiket dile göre kısaltılmış gün adı.</summary>
public sealed partial class DayToggle : ObservableObject
{
    public DayToggle(DayOfWeek day, bool isSelected)
    {
        Day = day;
        _isSelected = isSelected;

        // Zayıf abonelik: düzenleyici her açıldığında 7 çip oluşur; güçlü abonelik (+=) uygulama ömürlü
        // Localizer üzerinden onları sonsuza kadar bellekte tutuyordu (sızıntı).
        PropertyChangedEventManager.AddHandler(Localizer.Instance, OnLanguageChanged, string.Empty);
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(nameof(Label));

    public DayOfWeek Day { get; }

    public string Label
    {
        get
        {
            var culture = Localizer.Instance.Culture;
            return culture.DateTimeFormat.GetAbbreviatedDayName(Day).ToUpper(culture);
        }
    }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Düzenleme diyaloğu. Alanlar metin olarak tutulur; Kaydet'e basılana kadar asıl göreve dokunulmaz
/// (İptal güvenli). Doğrulama hatası dile göre üretilir ve diyalogda gösterilir.
/// </summary>
public sealed partial class TaskEditorViewModel : ObservableObject
{
    private const int MaxTitleLength = 120;
    private const int MaxCategoryLength = 40;
    private const int MaxNotesLength = 4000;

    public TaskEditorViewModel(CyberTask target)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));

        _title = target.Title;
        _notes = target.Notes ?? string.Empty;
        _category = target.Category ?? string.Empty;
        _hoursText = InputParsing.FormatHours(target.EstimatedHours);
        _dueDateText = target.DueAt is { } due ? InputParsing.FormatDate(DateOnly.FromDateTime(due)) : string.Empty;
        _dueTimeText = target.DueAt is { } dueWithTime && target.HasDueTime
            ? dueWithTime.ToString("HH:mm", CultureInfo.InvariantCulture)
            : string.Empty;
        _selectedPriority = OptionSets.Find(OptionSets.Priorities, target.Priority);
        _selectedRecurrence = OptionSets.Find(OptionSets.Recurrences, target.Recurrence);
        _selectedReminder = OptionSets.Find(OptionSets.Reminders, target.ReminderMinutesBefore);

        foreach (var day in RecurrenceRules.MondayFirst)
        {
            DayToggles.Add(new DayToggle(day, RecurrenceRules.Contains(target.RepeatDays, day)));
        }

        // Taslak kopyalar: İptal edilirse asıl alt görevler değişmemiş olur.
        foreach (var subtask in target.Subtasks)
        {
            Subtasks.Add(new SubTask { Title = subtask.Title, IsDone = subtask.IsDone });
        }
    }

    public CyberTask Target { get; }

    public IReadOnlyList<LocalizedOption<TaskPriority>> PriorityOptions => OptionSets.Priorities;

    public IReadOnlyList<LocalizedOption<Recurrence>> RecurrenceOptions => OptionSets.Recurrences;

    public IReadOnlyList<LocalizedOption<int?>> ReminderOptions => OptionSets.Reminders;

    public ObservableCollection<SubTask> Subtasks { get; } = [];

    /// <summary>Pazartesiden pazara gün çipleri (sadece "SEÇİLİ GÜNLER" tekrarında görünür).</summary>
    public ObservableCollection<DayToggle> DayToggles { get; } = [];

    public bool IsDaysRecurrence => SelectedRecurrence?.Value == Recurrence.SelectedDays;

    public bool IsAnyRecurrence => SelectedRecurrence?.Value is { } r && r != Recurrence.None;

    partial void OnSelectedRecurrenceChanged(LocalizedOption<Recurrence> value)
    {
        OnPropertyChanged(nameof(IsDaysRecurrence));
        OnPropertyChanged(nameof(IsAnyRecurrence));
    }

    [RelayCommand]
    private void ToggleDay(DayToggle? toggle)
    {
        if (toggle is not null)
        {
            toggle.IsSelected = !toggle.IsSelected;
        }
    }

    [RelayCommand]
    private void SelectWeekdays() => SetDays(WeekDays.Weekdays);

    [RelayCommand]
    private void SelectWeekend() => SetDays(WeekDays.Weekend);

    [RelayCommand]
    private void SelectAllDays() => SetDays(WeekDays.All);

    private void SetDays(WeekDays mask)
    {
        foreach (var toggle in DayToggles)
        {
            toggle.IsSelected = RecurrenceRules.Contains(mask, toggle.Day);
        }
    }

    private WeekDays SelectedMask() =>
        DayToggles.Where(static t => t.IsSelected).Aggregate(WeekDays.None, static (m, t) => m | RecurrenceRules.ToFlag(t.Day));

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _notes;

    [ObservableProperty]
    private string _category;

    [ObservableProperty]
    private string _hoursText;

    [ObservableProperty]
    private string _dueDateText;

    [ObservableProperty]
    private string _dueTimeText;

    [ObservableProperty]
    private LocalizedOption<TaskPriority> _selectedPriority;

    [ObservableProperty]
    private LocalizedOption<Recurrence> _selectedRecurrence;

    [ObservableProperty]
    private LocalizedOption<int?> _selectedReminder;

    [ObservableProperty]
    private string _newSubtaskTitle = string.Empty;

    [ObservableProperty]
    private string? _validationError;

    public string Heading => $"{Localizer.Instance["Edit_Title"]} {Target.Code}";

    [RelayCommand]
    private void SetDueToday() => DueDateText = InputParsing.FormatDate(DateOnly.FromDateTime(DateTime.Today));

    [RelayCommand]
    private void SetDueTomorrow() => DueDateText = InputParsing.FormatDate(DateOnly.FromDateTime(DateTime.Today).AddDays(1));

    [RelayCommand]
    private void SetDueNextWeek() => DueDateText = InputParsing.FormatDate(DateOnly.FromDateTime(DateTime.Today).AddDays(7));

    [RelayCommand]
    private void ClearDue()
    {
        DueDateText = string.Empty;
        DueTimeText = string.Empty;
    }

    [RelayCommand]
    private void AddSubtask()
    {
        string title = NewSubtaskTitle.Trim();
        if (title.Length == 0)
        {
            return;
        }

        Subtasks.Add(new SubTask { Title = title.Length > MaxTitleLength ? title[..MaxTitleLength] : title });
        NewSubtaskTitle = string.Empty;
    }

    [RelayCommand]
    private void RemoveSubtask(SubTask? subtask)
    {
        if (subtask is not null)
        {
            Subtasks.Remove(subtask);
        }
    }

    /// <summary>Doğrular ve geçerliyse hedef göreve yazar. Geçersizse ValidationError dolar, görev değişmez.</summary>
    public bool TryApply()
    {
        var loc = Localizer.Instance;

        string title = Title.Trim();
        if (title.Length == 0)
        {
            ValidationError = loc["Err_Title"];
            return false;
        }

        if (!InputParsing.TryParseHours(HoursText, out double hours))
        {
            ValidationError = loc["Err_Hours"];
            return false;
        }

        DateOnly? dueDate = null;
        if (!string.IsNullOrWhiteSpace(DueDateText))
        {
            if (!InputParsing.TryParseDate(DueDateText, out var parsedDate))
            {
                ValidationError = loc["Err_Date"];
                return false;
            }

            dueDate = parsedDate;
        }

        TimeOnly? dueTime = null;
        if (!string.IsNullOrWhiteSpace(DueTimeText))
        {
            // Tekrarlayan görevde tarih aşağıda otomatik atanır; saat tek başına geçerli.
            if (dueDate is null && (SelectedRecurrence?.Value ?? Recurrence.None) == Recurrence.None)
            {
                ValidationError = loc["Err_TimeNeedsDate"];
                return false;
            }

            if (!InputParsing.TryParseTime(DueTimeText, out var parsedTime))
            {
                ValidationError = loc["Err_Time"];
                return false;
            }

            dueTime = parsedTime;
        }

        var recurrence = SelectedRecurrence?.Value ?? Recurrence.None;
        var repeatDays = recurrence == Recurrence.SelectedDays ? SelectedMask() : WeekDays.None;
        if (recurrence == Recurrence.SelectedDays && repeatDays == WeekDays.None)
        {
            ValidationError = loc["Err_DaysRequired"];
            return false;
        }

        // Tekrarlayan görevde tarih boşsa: ilk uygun gün (bugün dahil). Seçili günlerde bugün uygun değilse sıradaki gün.
        if (recurrence != Recurrence.None && dueDate is null)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            dueDate = recurrence == Recurrence.SelectedDays ? RecurrenceRules.FirstMatchOnOrAfter(today, repeatDays) : today;
        }

        int? reminder = SelectedReminder?.Value;
        if (reminder is not null && dueDate is null)
        {
            ValidationError = loc["Err_ReminderNeedsDate"];
            return false;
        }

        DateTime? dueAt = dueDate?.ToDateTime(dueTime ?? TimeOnly.MinValue);
        bool hasDueTime = dueTime is not null;

        // Zaman veya hatırlatıcı değiştiyse hatırlatma yeniden kurulmalı.
        if (dueAt != Target.DueAt || hasDueTime != Target.HasDueTime || reminder != Target.ReminderMinutesBefore)
        {
            Target.ReminderFired = false;
            Target.SnoozedUntil = null; // eski zamana göre ertelenmişti
        }

        string category = Category.Trim();
        string notes = Notes.Trim();
        Target.Title = title.Length > MaxTitleLength ? title[..MaxTitleLength] : title;
        Target.Notes = notes.Length == 0 ? null : (notes.Length > MaxNotesLength ? notes[..MaxNotesLength] : notes);
        Target.Category = category.Length == 0 ? null : (category.Length > MaxCategoryLength ? category[..MaxCategoryLength] : category);
        Target.EstimatedHours = hours;
        Target.Priority = SelectedPriority?.Value ?? TaskPriority.Active;
        Target.DueAt = dueAt;
        Target.HasDueTime = hasDueTime;
        Target.Recurrence = recurrence;
        Target.RepeatDays = repeatDays;
        Target.ReminderMinutesBefore = reminder;

        Target.Subtasks.Clear();
        foreach (var draft in Subtasks)
        {
            Target.Subtasks.Add(new SubTask { Title = draft.Title, IsDone = draft.IsDone });
        }

        ValidationError = null;
        return true;
    }
}
