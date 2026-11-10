using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CyberTodo.Models;

namespace CyberTodo.ViewModels;

public sealed partial class MainViewModel
{
    private const int LongRangeDays = 30;

    private const int ShortRangeDays = 7;

    private DateOnly? _calendarSelectedDate;

    private ChartBar? _hoveredBar;

    private DayActivity? _observedDay;

    private DateOnly _chartAnchorDate;

    // ───────── İstatistik / takvim ─────────

    public ObservableCollection<StatTile> StatTiles { get; } = [];

    public ObservableCollection<WeekdayStat> WeekdayStats { get; } = [];

    public ObservableCollection<CalendarCell> CalendarCells { get; } = [];

    public ObservableCollection<string> WeekdayHeaders { get; }

    public ObservableCollection<CalendarItem> SelectedCalendarItems { get; } = [];

    [ObservableProperty]
    private DateOnly _calendarMonth;

    [ObservableProperty]
    private string _calendarTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChartView), nameof(IsStatsView), nameof(IsCalendarView), nameof(IsFocusView), nameof(IsHabitsView))]
    private ChartMode _chartMode = ChartMode.Last30;

    public bool IsHabitsView => ChartMode == ChartMode.Habits;

    // ───────── Alışkanlıklar ─────────

    public ObservableCollection<HabitRow> Habits { get; } = [];

    public bool HasHabits => Habits.Count > 0;

    [RelayCommand]
    private void ShowHabits() => ChartMode = ChartMode.Habits;

    private void RefreshHabits()
    {
        var rows = InsightsBuilder.BuildHabits(ActiveTasks, CompletedTasks, Today, _loc, t => FormatRecurrence(t.Recurrence, t.RepeatDays));
        Habits.Clear();
        foreach (var row in rows)
        {
            Habits.Add(row);
        }

        OnPropertyChanged(nameof(HasHabits));
    }

    public bool IsChartView => ChartMode is ChartMode.Last30 or ChartMode.Last7;

    public bool IsStatsView => ChartMode == ChartMode.Stats;

    public bool IsCalendarView => ChartMode == ChartMode.Calendar;

    public bool IsFocusView => ChartMode == ChartMode.Focus;

    // ───────── Odak istatistiği ─────────

    public ObservableCollection<StatTile> FocusTiles { get; } = [];

    public ObservableCollection<FocusSlice> FocusByCategory { get; } = [];

    public ObservableCollection<FocusSlice> FocusByTask { get; } = [];

    /// <summary>Odak sekmesinin aralığı: 7 ya da 30 gün.</summary>
    [ObservableProperty]
    private int _focusRangeDays = 7;

    public bool HasFocusData => FocusByTask.Count > 0;

    public ObservableCollection<ChartBar> ChartBars { get; } = [];

    // ───────── Grafik / log ─────────

    /// <summary>Sağ üstteki log panelinin gösterdiği gün (fare grafikte değilse bugün).</summary>
    [ObservableProperty]
    private DayActivity? _selectedDay;

    [ObservableProperty]
    private string _selectedDayTitle = string.Empty;

    [ObservableProperty]
    private string _selectedDaySummary = string.Empty;

    /// <summary>Y ekseni etiketleri, yukarıdan aşağı: max, %75, %50, %25, 0.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _yAxisLabels = ["", "", "", "", ""];

    [RelayCommand]
    private void ShowLast30Days() => ChartMode = ChartMode.Last30;

    [RelayCommand]
    private void ShowLast7Days() => ChartMode = ChartMode.Last7;

    [RelayCommand]
    private void ShowStats() => ChartMode = ChartMode.Stats;

    [RelayCommand]
    private void ShowCalendar() => ChartMode = ChartMode.Calendar;

    [RelayCommand]
    private void ShowFocus() => ChartMode = ChartMode.Focus;

    [RelayCommand]
    private void SetFocusRange(string? days)
    {
        if (int.TryParse(days, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value is 7 or 30)
        {
            FocusRangeDays = value;
        }
    }

    partial void OnFocusRangeDaysChanged(int value)
    {
        if (_initialized && IsFocusView)
        {
            RefreshFocusStats();
        }
    }

    [RelayCommand]
    private void PreviousMonth() => CalendarMonth = CalendarMonth.AddMonths(-1);

    [RelayCommand]
    private void NextMonth() => CalendarMonth = CalendarMonth.AddMonths(1);

    /// <summary>Takvimde güne tıklandı: hücre seçilir, alt listede o günün görevleri, sağ üst logda o günün aktivitesi.</summary>
    [RelayCommand]
    private void SelectCalendarDay(CalendarCell? cell)
    {
        if (cell is null)
        {
            return;
        }

        _calendarSelectedDate = cell.Date;
        foreach (var c in CalendarCells)
        {
            c.IsSelected = c.Date == cell.Date;
        }

        SelectedCalendarItems.Clear();
        foreach (var item in cell.AllItems)
        {
            SelectedCalendarItems.Add(item);
        }

        SelectedDay = _daysByDate.TryGetValue(cell.Date, out var day) ? day : new DayActivity { Date = cell.Date };
    }

    // ═════════════════════════════ View'dan çağrılanlar ═════════════════════════════

    /// <summary>Fare bir sütunun üzerine geldi: nişangâh o sütunda, log o günü gösterir.</summary>
    public void HoverBar(ChartBar? bar)
    {
        if (bar is null || ReferenceEquals(bar, _hoveredBar))
        {
            return;
        }

        if (_hoveredBar is not null)
        {
            _hoveredBar.IsHovered = false;
        }

        bar.IsHovered = true;
        _hoveredBar = bar;
        SelectedDay = bar.Day;
    }

    /// <summary>Fare grafikten çıktı: nişangâh kaybolur, log bugüne döner.</summary>
    public void EndHover()
    {
        if (_hoveredBar is not null)
        {
            _hoveredBar.IsHovered = false;
            _hoveredBar = null;
        }

        SelectedDay = ChartBars.Count > 0 ? ChartBars[^1].Day : null;
    }

    partial void OnChartModeChanged(ChartMode value)
    {
        if (!_initialized)
        {
            return;
        }

        if (value != ChartMode.Calendar)
        {
            _calendarSelectedDate = null;
        }

        RebuildChart(); // log paneli ve (gerekirse) istatistik/takvim de yenilenir
    }

    partial void OnCalendarMonthChanged(DateOnly value)
    {
        if (_initialized && IsCalendarView)
        {
            RefreshCalendar();
        }
    }

    partial void OnSelectedDayChanged(DayActivity? oldValue, DayActivity? newValue)
    {
        if (_observedDay is not null)
        {
            _observedDay.PropertyChanged -= OnObservedDayChanged;
        }

        _observedDay = newValue;
        if (newValue is not null)
        {
            newValue.PropertyChanged += OnObservedDayChanged;
        }

        UpdateSelectedDayTexts();
    }

    private void OnObservedDayChanged(object? sender, PropertyChangedEventArgs e) => UpdateSelectedDayTexts();

    /// <summary>Takvim gün başlıkları (Pzt'den başlar) — aktif dilin kısaltmalarıyla.</summary>
    private void RefreshWeekdayHeaders()
    {
        var culture = _loc.Culture;
        WeekdayHeaders.Clear();
        foreach (var day in RecurrenceRules.MondayFirst)
        {
            WeekdayHeaders.Add(culture.DateTimeFormat.GetAbbreviatedDayName(day).ToUpper(culture));
        }
    }

    private void UpdateSelectedDayTexts()
    {
        var day = SelectedDay;
        if (day is null)
        {
            SelectedDayTitle = _loc["Log_Title"];
            SelectedDaySummary = string.Empty;
            return;
        }

        var culture = _loc.Culture;
        string date = day.Date.ToString(_loc["Fmt_LongDate"], culture).ToUpper(culture);
        SelectedDayTitle = $"{date} // {_loc["Log_Title"]}";
        SelectedDaySummary = _loc.Format("Log_Summary", day.CompletedCount, day.HoursWorked.ToString("0.#", culture));
    }

    /// <summary>
    /// Seçili aralığın sütunlarını yeniden üretir (en fazla 30 öğe; ucuz).
    /// Veri olmayan günler için kalıcı olmayan boş DayActivity kullanılır.
    /// </summary>
    private void RebuildChart()
    {
        var today = Today;
        _chartAnchorDate = today;

        int range = ChartMode == ChartMode.Last7 ? ShortRangeDays : LongRangeDays;
        var window = new DayActivity[range];
        double maxHours = 0;

        for (int i = 0; i < range; i++)
        {
            var date = today.AddDays(i - range + 1);
            var day = _daysByDate.TryGetValue(date, out var stored) ? stored : new DayActivity { Date = date };
            window[i] = day;
            maxHours = Math.Max(maxHours, day.HoursWorked);
        }

        // Eksen tepesi 4'ün katı → çeyrek çizgiler tam sayı saate denk gelir.
        double axisMax = Math.Max(4, Math.Ceiling(maxHours / 4) * 4);

        // Fare şu an grafiğin üzerindeyse aynı günde kalsın; değilse log bugünü gösterir.
        DateOnly? hoveredDate = _hoveredBar?.Day.Date;
        _hoveredBar = null;
        ChartBar? barToHover = null;

        var culture = _loc.Culture;
        string labelFormat = _loc["Fmt_ShortDate"];

        ChartBars.Clear();
        for (int i = 0; i < range; i++)
        {
            var day = window[i];

            // Sıfırdan büyük ama çok küçük değerler de görünür kalsın diye min %1.5.
            double ratio = day.HoursWorked <= 0 ? 0 : Math.Clamp(day.HoursWorked / axisMax, 0.015, 1);

            // 30 günde bugünden geriye her 5. gün etiketlenir (bugün her zaman etiketli).
            bool showLabel = range <= ShortRangeDays || (range - 1 - i) % 5 == 0;
            string label = day.Date.ToString(labelFormat, culture).ToUpper(culture);

            var bar = new ChartBar(day, label, ratio, GetIntensity(day.HoursWorked), showLabel, day.Date == today);
            ChartBars.Add(bar);

            if (day.Date == hoveredDate)
            {
                barToHover = bar;
            }
        }

        var axisCulture = CultureInfo.InvariantCulture;
        YAxisLabels =
        [
            axisMax.ToString("0.#", axisCulture),
            (axisMax * 0.75).ToString("0.#", axisCulture),
            (axisMax * 0.5).ToString("0.#", axisCulture),
            (axisMax * 0.25).ToString("0.#", axisCulture),
            "0",
        ];

        if (barToHover is not null)
        {
            HoverBar(barToHover);
        }
        else if (IsCalendarView && _calendarSelectedDate is { } calendarDate)
        {
            // Takvimde seçili gün varken log o günde kalsın.
            SelectedDay = _daysByDate.TryGetValue(calendarDate, out var selected) ? selected : new DayActivity { Date = calendarDate };
        }
        else
        {
            SelectedDay = ChartBars[^1].Day;
        }

        UpdateSelectedDayTexts();

        if (IsStatsView)
        {
            RefreshStats();
        }
        else if (IsCalendarView)
        {
            RefreshCalendar();
        }
        else if (IsFocusView)
        {
            RefreshFocusStats();
        }
        else if (IsHabitsView)
        {
            RefreshHabits();
        }
    }

    private void RefreshFocusStats()
    {
        var (tiles, categories, tasks) = InsightsBuilder.BuildFocus(_focusSessions, Today, FocusRangeDays, _loc, FormatMinutes);
        FocusTiles.Clear();
        foreach (var tile in tiles)
        {
            FocusTiles.Add(tile);
        }

        FocusByCategory.Clear();
        foreach (var slice in categories)
        {
            FocusByCategory.Add(slice);
        }

        FocusByTask.Clear();
        foreach (var slice in tasks)
        {
            FocusByTask.Add(slice);
        }

        OnPropertyChanged(nameof(HasFocusData));
    }

    private void RefreshStats()
    {
        var (tiles, weekdays) = InsightsBuilder.BuildStats(_daysByDate, ActiveTasks.Concat(CompletedTasks), Today, _loc, FormatMinutes, _archiveSummary.TrackedMinutes);
        StatTiles.Clear();
        foreach (var tile in tiles)
        {
            StatTiles.Add(tile);
        }

        WeekdayStats.Clear();
        foreach (var weekday in weekdays)
        {
            WeekdayStats.Add(weekday);
        }
    }

    private void RefreshCalendar()
    {
        var culture = _loc.Culture;
        CalendarTitle = CalendarMonth.ToString("MMMM yyyy", culture).ToUpper(culture);

        var cells = InsightsBuilder.BuildCalendar(CalendarMonth, Today, ActiveTasks, CompletedTasks, _daysByDate, GetIntensity, culture);
        CalendarCells.Clear();
        foreach (var cell in cells)
        {
            CalendarCells.Add(cell);
        }

        // Seçim yeni ızgarada varsa koru; yoksa (ay değişti) bugünü ya da ayın ilk gününü seç.
        var target = cells.FirstOrDefault(c => c.Date == _calendarSelectedDate)
            ?? cells.FirstOrDefault(c => c.IsToday && c.IsCurrentMonth)
            ?? cells.First(c => c.IsCurrentMonth);
        SelectCalendarDay(target);
    }

    private static BarIntensity GetIntensity(double hours) => hours switch
    {
        <= 0 => BarIntensity.None,
        < 3 => BarIntensity.Low,
        < 6 => BarIntensity.Medium,
        _ => BarIntensity.High,
    };
}
