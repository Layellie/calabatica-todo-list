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

/// <summary>Akıllı liste çipi: etiket + canlı sayaç ("BUGÜN (2)").</summary>
public sealed partial class SmartViewOption : ObservableObject
{
    public SmartViewOption(SmartView value, string key)
    {
        Value = value;
        Key = key;
        Localizer.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Label));
    }

    public SmartView Value { get; }

    public string Key { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private int _count;

    public string Label => Value == SmartView.All
        ? Localizer.Instance[Key]
        : $"{Localizer.Instance[Key]} ({Count})";
}

public sealed partial class MainViewModel : ObservableObject
{
    private const int LongRangeDays = 30;
    private const int ShortRangeDays = 7;
    private const int UpcomingDays = 7;
    private const int MaxTitleLength = 120;
    private const int MaxCategoryLength = 40;
    private const int MaxToasts = 4;

    /// <summary>Tamamla/geri al sonrası satırlar kayar; bu süre içindeki ikinci tıklama yok sayılır.</summary>
    private const long ListMoveGuardMs = 350;

    private static readonly TimeSpan s_undoLifetime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan s_infoLifetime = TimeSpan.FromSeconds(4);

    /// <summary>Kategori filtresindeki "TÜMÜ" seçeneği (Value=null) — tek örnek, dil değişince etiketi güncellenir.</summary>
    private static readonly LocalizedOption<string?> s_allCategories = new(null, "Filter_All");

    private readonly Localizer _loc = Localizer.Instance;
    private readonly StorageService _storage;
    private readonly UpdateService _updates;
    private readonly IStartupRegistration _startup;
    private DateOnly? _calendarSelectedDate;
    private readonly Dictionary<DateOnly, DayActivity> _daysByDate = new();
    private readonly DispatcherTimer _clock;

    private AppSettings _settings = new();
    private ChartBar? _hoveredBar;
    private DayActivity? _observedDay;
    private DateOnly _chartAnchorDate;
    private DateTime _focusStartedAt;
    private TimeSpan _focusDuration;
    private int _lastClockMinute = -1;
    private int _nextTaskNumber = 1;
    private bool _initialized;
    private bool _persistenceDisabled;
    private bool _errorIsFromSave;
    private long _lastListMoveTick = long.MinValue / 2;

    /// <param name="updates">null → depo ayarsız (ağa çıkmaz); testler için.</param>
    /// <param name="startup">null → bellekte (kayıt defterine dokunmaz); testler için.</param>
    public MainViewModel(StorageService storage, UpdateService? updates = null, IStartupRegistration? startup = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _updates = updates ?? new UpdateService(null, UpdateService.ReadCurrentVersion());
        _startup = startup ?? new InMemoryStartupRegistration();
        _versionText = UpdateService.FormatVersion(_updates.CurrentVersion);
        _currentTheme = ThemePalette.All[0];
        _selectedThemeOption = OptionSets.Themes[0];
        _selectedTrayOption = OptionSets.OnOff[0];
        _selectedAutoUpdateOption = OptionSets.OnOff[0];
        _selectedStartupOption = OptionSets.OnOff[1];
        _calendarMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);

        WeekdayHeaders = [];
        RefreshWeekdayHeaders();

        SmartViews =
        [
            new SmartViewOption(SmartView.All, "Smart_All"),
            new SmartViewOption(SmartView.Today, "Smart_Today"),
            new SmartViewOption(SmartView.Upcoming, "Smart_Upcoming"),
            new SmartViewOption(SmartView.Overdue, "Smart_Overdue"),
        ];
        _selectedSmartView = SmartViews[0];

        ActiveView = new ListCollectionView(ActiveTasks)
        {
            Filter = item => MatchesFilter(item) && MatchesSmartView(item),
            CustomSort = new TaskComparer(SortMode.Created),
        };
        CompletedView = new ListCollectionView(CompletedTasks) { Filter = MatchesFilter };

        CategoryFilters.Add(s_allCategories);
        _selectedCategoryFilter = s_allCategories;
        _selectedSortOption = OptionSets.SortModes[0];
        _selectedLanguageOption = OptionSets.Languages[0];
        _selectedScaleOption = OptionSets.Find(OptionSets.Scales, AppSettings.DefaultUiScale);
        _selectedSoundOption = OptionSets.OnOff[0];
        _selectedFocusLengthOption = OptionSets.Find(OptionSets.FocusLengths, 25);
        _selectedPriorityOption = OptionSets.Priorities[0];

        // Tek saat: bildirim süreleri, hatırlatıcılar, odak sayacı, son tarih metinleri, gece yarısı geçişi.
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += OnClockTick;

        _loc.PropertyChanged += OnLanguageChanged;
    }

    /// <summary>
    /// Kullanıcının dikkati gerekiyor (hatırlatma / odak bitti). Argüman: bildirim metni.
    /// View: görev çubuğunu yakar, ses çalar; pencere gizliyse Windows bildirimi gösterir.
    /// </summary>
    public event EventHandler<string>? AttentionRequested;

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

    public IReadOnlyList<LocalizedOption<ThemeId>> ThemeOptions => OptionSets.Themes;

    // ───────── Şablonlar ─────────

    public ObservableCollection<TaskTemplate> Templates { get; } = [];

    [ObservableProperty]
    private bool _isTemplatesOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateTemplateCommand))]
    private string _newTemplateName = string.Empty;

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
    [NotifyPropertyChangedFor(nameof(IsChartView), nameof(IsStatsView), nameof(IsCalendarView))]
    private ChartMode _chartMode = ChartMode.Last30;

    public bool IsChartView => ChartMode is ChartMode.Last30 or ChartMode.Last7;

    public bool IsStatsView => ChartMode == ChartMode.Stats;

    public bool IsCalendarView => ChartMode == ChartMode.Calendar;

    public ObservableCollection<CyberTask> ActiveTasks { get; } = [];

    public ObservableCollection<CyberTask> CompletedTasks { get; } = [];

    /// <summary>Arama + kategori + akıllı liste filtresi ve sıralama uygulanmış görünüm.</summary>
    public ListCollectionView ActiveView { get; }

    public ListCollectionView CompletedView { get; }

    public ObservableCollection<ChartBar> ChartBars { get; } = [];

    public ObservableCollection<ToastItem> Toasts { get; } = [];

    public ObservableCollection<LocalizedOption<string?>> CategoryFilters { get; } = [];

    public IReadOnlyList<SmartViewOption> SmartViews { get; }

    public bool HasCategories => CategoryFilters.Count > 1;

    public IReadOnlyList<LocalizedOption<TaskPriority>> PriorityOptions => OptionSets.Priorities;

    public IReadOnlyList<LocalizedOption<SortMode>> SortOptions => OptionSets.SortModes;

    public IReadOnlyList<LocalizedOption<AppLanguage>> LanguageOptions => OptionSets.Languages;

    public IReadOnlyList<LocalizedOption<double>> ScaleOptions => OptionSets.Scales;

    public IReadOnlyList<LocalizedOption<bool>> SoundOptions => OptionSets.OnOff;

    public IReadOnlyList<LocalizedOption<int>> FocusLengthOptions => OptionSets.FocusLengths;

    public string DataFilePath => _storage.FilePath;

    public bool SoundEnabled => _settings.ReminderSound;

    // ───────── Hızlı ekleme ─────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTaskCommand))]
    private string _newTaskTitle = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTaskCommand))]
    private string _newTaskHours = "1";

    [ObservableProperty]
    private string _newTaskCategory = string.Empty;

    [ObservableProperty]
    private LocalizedOption<TaskPriority> _selectedPriorityOption;

    /// <summary>Hızlı ekleme satırındaki komutların canlı önizlemesi ("→ #iş · YARIN 14:00").</summary>
    [ObservableProperty]
    private string _quickAddPreview = string.Empty;

    // ───────── Arama / filtre / sıralama ─────────

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private LocalizedOption<string?>? _selectedCategoryFilter;

    [ObservableProperty]
    private SmartViewOption? _selectedSmartView;

    [ObservableProperty]
    private LocalizedOption<SortMode> _selectedSortOption;

    // ───────── Odak (Pomodoro) ─────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFocusRunning))]
    private CyberTask? _focusTask;

    [ObservableProperty]
    private string _focusRemainingText = string.Empty;

    /// <summary>0..1 geçen süre oranı (başlıktaki ince çubuk).</summary>
    [ObservableProperty]
    private double _focusProgress;

    public bool IsFocusRunning => FocusTask is not null;

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

    // ───────── Diyaloglar / ayarlar ─────────

    [ObservableProperty]
    private TaskEditorViewModel? _editor;

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

    [ObservableProperty]
    private string? _errorMessage;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    // ═════════════════════════════ Yaşam döngüsü ═════════════════════════════

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        CyberData data;
        try
        {
            var result = await _storage.LoadAsync();
            data = result.Data;
            if (result.CorruptBackupFile is not null)
            {
                ErrorMessage = _loc.Format("Err_LoadCorrupt", result.CorruptBackupFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Dosya okunamıyor (kilitli/yetki). Boş veriyle devam et ama kaydetme:
            // aksi halde kullanıcının gerçek verisinin üzerine boş veri yazılırdı.
            data = new CyberData();
            _persistenceDisabled = true;
            ErrorMessage = _loc.Format("Err_LoadFailed", ex.Message);
        }

        _settings = data.Settings;
        _nextTaskNumber = data.NextTaskNumber;
        ApplySettingsToUi();

        _daysByDate.Clear();
        foreach (var day in data.Days)
        {
            _daysByDate[day.Date] = day; // aynı tarih iki kez varsa sonuncusu geçerli
        }

        foreach (var template in data.Templates)
        {
            Templates.Add(template);
        }

        var now = DateTime.Now;
        var active = data.Tasks.Where(static t => !t.IsCompleted).OrderBy(static t => t.CreatedAt).ToList();

        // Eski kayıtlarda SortOrder yok (hepsi 0): eklenme sırasıyla numarala ki "ELLE" sıralama anlamlı başlasın.
        if (active.Count > 1 && active.All(static t => t.SortOrder == 0))
        {
            for (int i = 0; i < active.Count; i++)
            {
                active[i].SortOrder = i;
            }
        }

        foreach (var task in active)
        {
            UpdateDerivedState(task, now);
            ActiveTasks.Add(task);
        }

        foreach (var task in data.Tasks.Where(static t => t.IsCompleted).OrderByDescending(static t => t.CompletedAt))
        {
            UpdateDerivedState(task, now);
            CompletedTasks.Add(task);
        }

        _initialized = true;
        OnTasksChanged();
        RebuildChart();

        ReadStartupState();

        _lastClockMinute = now.Minute;
        _clock.Start();
        CheckReminders(now); // uygulama kapalıyken kaçırılan hatırlatmalar hemen gösterilir

        UpdateStatusText = _updates.IsConfigured ? string.Empty : _loc["Update_NotConfigured"];
        if (_settings.AutoCheckUpdates && _updates.IsConfigured)
        {
            _ = AutoCheckUpdatesAsync(); // ağ beklenmez; hatalar CheckAsync içinde sonuca dönüşür
        }
    }

    /// <summary>Kapanışta: çalışan odak oturumunun geçen süresini kaydet, saati durdur, yazmaların bitmesini bekle.</summary>
    public async Task ShutdownAsync()
    {
        _clock.Stop();
        if (StopFocusCore(DateTime.Now))
        {
            await SaveAsync();
        }

        await _storage.WhenIdleAsync();
    }

    // ═════════════════════════════ Görev komutları ═════════════════════════════

    private bool CanAddTask()
    {
        var parsed = QuickAddParser.Parse(NewTaskTitle, Today);
        return !string.IsNullOrWhiteSpace(parsed.Title)
            && (parsed.Hours is not null || InputParsing.TryParseHours(NewTaskHours, out _));
    }

    /// <summary>Hızlı ekleme: satırdaki #kategori, !, @tarih/@saat, *tekrar, ~süre komutları uygulanır.</summary>
    [RelayCommand(CanExecute = nameof(CanAddTask))]
    private async Task AddTaskAsync()
    {
        var parsed = QuickAddParser.Parse(NewTaskTitle, Today);
        if (string.IsNullOrWhiteSpace(parsed.Title))
        {
            return;
        }

        double hours;
        if (parsed.Hours is { } parsedHours)
        {
            hours = parsedHours;
        }
        else if (!InputParsing.TryParseHours(NewTaskHours, out hours))
        {
            return;
        }

        string title = parsed.Title.Trim();
        string category = (parsed.Category ?? NewTaskCategory).Trim();

        var task = new CyberTask
        {
            Number = _nextTaskNumber++,
            Title = title.Length > MaxTitleLength ? title[..MaxTitleLength] : title,
            Category = category.Length == 0 ? null : (category.Length > MaxCategoryLength ? category[..MaxCategoryLength] : category),
            Priority = parsed.Priority ?? SelectedPriorityOption?.Value ?? TaskPriority.Active,
            EstimatedHours = hours,
            DueAt = parsed.DueDate?.ToDateTime(parsed.DueTime ?? TimeOnly.MinValue),
            HasDueTime = parsed.DueTime is not null,
            Recurrence = parsed.Recurrence ?? Recurrence.None,
            RepeatDays = parsed.RepeatDays,
            CreatedAt = DateTime.Now,
            SortOrder = NextSortOrder(),
        };

        UpdateDerivedState(task, DateTime.Now);
        ActiveTasks.Add(task);
        NewTaskTitle = string.Empty; // kategori kutusu bilinçli korunur: aynı kategoriye art arda ekleme kolay olsun
        OnTasksChanged();

        await SaveAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CompleteTaskAsync(CyberTask? task)
    {
        if (task is null || task.IsCompleted || !TryBeginListMove())
        {
            return;
        }

        var now = DateTime.Now;

        // Bu görevde odak çalışıyorsa önce geçen süre işlenir.
        if (FocusTask == task)
        {
            StopFocusCore(now);
        }

        var day = GetOrCreateDay(DateOnly.FromDateTime(now));

        // Odak süresi kaydedilmişse o süre zaten güne işlendi; tahmini süre ayrıca eklenmez (çift sayım olmaz).
        double credit = task.TrackedMinutes > 0 ? 0 : task.EstimatedHours;
        task.CompletionCreditHours = credit;
        task.IsCompleted = true;
        task.CompletedAt = now;
        task.IsExpanded = false;

        InsertEntrySorted(day, new ActivityLogEntry
        {
            Time = now.ToString("HH:mm", CultureInfo.InvariantCulture),
            Description = $"{task.Code} {task.Title}",
            Status = LogStatus.Done,
            TaskId = task.Id,
        });
        day.HoursWorked = Math.Round(day.HoursWorked + credit, 2);
        day.CompletedCount++;

        ActiveTasks.Remove(task);
        CompletedTasks.Insert(0, task);

        // Tekrarlayan görev: bir sonraki kopya hemen aktif listeye eklenir.
        if (task.Recurrence != Recurrence.None)
        {
            var next = CreateNextOccurrence(task, now);
            task.SpawnedNextId = next.Id;
            UpdateDerivedState(next, now);
            ActiveTasks.Add(next);
            ShowToast(_loc.Format("Toast_Recurring", $"{next.Code} {next.DueText}"), ToastKind.Info, lifetime: s_infoLifetime);
        }

        OnTasksChanged();
        RebuildChart(); // ölçek (max saat) değişmiş olabilir
        await SaveAsync();
    }

    /// <summary>Tamamlanan görevi geri alır; log satırını, eklenen saati ve (varsa) üretilen tekrar kopyasını geri çeker.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RestoreTaskAsync(CyberTask? task)
    {
        if (task is null || !task.IsCompleted || !TryBeginListMove())
        {
            return;
        }

        if (task.CompletedAt is { } completedAt
            && _daysByDate.TryGetValue(DateOnly.FromDateTime(completedAt), out var day))
        {
            var entry = day.Entries.FirstOrDefault(e => e.TaskId == task.Id && e.Status == LogStatus.Done);
            if (entry is not null)
            {
                day.Entries.Remove(entry);
                day.HoursWorked = Math.Max(0, Math.Round(day.HoursWorked - task.CompletionCreditHours, 2));
                day.CompletedCount = Math.Max(0, day.CompletedCount - 1);
            }
        }

        // Tekrardan doğan kopya hâlâ aktifse kaldır (aksi halde çift görev oluşurdu).
        if (task.SpawnedNextId is { } spawnedId)
        {
            var spawned = ActiveTasks.FirstOrDefault(t => t.Id == spawnedId);
            if (spawned is not null)
            {
                if (FocusTask == spawned)
                {
                    StopFocusCore(DateTime.Now);
                }

                ActiveTasks.Remove(spawned);
            }

            task.SpawnedNextId = null;
        }

        task.CompletionCreditHours = 0;
        task.IsCompleted = false;
        task.CompletedAt = null;
        task.SortOrder = NextSortOrder();

        CompletedTasks.Remove(task);
        ActiveTasks.Add(task);

        OnTasksChanged();
        RebuildChart();
        await SaveAsync();
    }

    /// <summary>Siler; 6 sn boyunca "GERİ AL" (veya Ctrl+Z) ile aynı yerine geri konabilir. Log geçmişi korunur.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DeleteTaskAsync(CyberTask? task)
    {
        if (task is null)
        {
            return;
        }

        var list = task.IsCompleted ? CompletedTasks : ActiveTasks;
        int index = list.IndexOf(task);
        if (index < 0)
        {
            return;
        }

        if (Editor?.Target == task)
        {
            Editor = null;
        }

        if (FocusTask == task)
        {
            StopFocusCore(DateTime.Now); // harcanan süre kaybolmasın
        }

        list.RemoveAt(index);
        OnTasksChanged();
        await SaveAsync();

        ShowToast(
            _loc.Format("Toast_Deleted", task.Code),
            ToastKind.Info,
            _loc["Toast_Undo"],
            async () =>
            {
                list.Insert(Math.Min(index, list.Count), task);
                OnTasksChanged();
                await SaveAsync();
            },
            s_undoLifetime);
    }

    [RelayCommand]
    private void EditTask(CyberTask? task)
    {
        if (task is null || task.IsCompleted)
        {
            return;
        }

        IsSettingsOpen = false;
        Editor = new TaskEditorViewModel(task);
    }

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        if (Editor is not { } editor || !editor.TryApply())
        {
            return; // geçersizse diyalog açık kalır, hata metni gösterilir
        }

        var task = editor.Target;
        UpdateDerivedState(task, DateTime.Now);
        Editor = null;

        OnTasksChanged(); // sıralama anahtarı / akıllı liste üyeliği değişmiş olabilir
        await SaveAsync();
    }

    [RelayCommand]
    private void CancelEdit() => Editor = null;

    [RelayCommand]
    private void ToggleExpand(CyberTask? task)
    {
        if (task is not null)
        {
            task.IsExpanded = !task.IsExpanded;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleSubtaskAsync(SubTask? subtask)
    {
        if (subtask is null)
        {
            return;
        }

        var owner = ActiveTasks.FirstOrDefault(t => t.Subtasks.Contains(subtask));
        if (owner is null)
        {
            return;
        }

        subtask.IsDone = !subtask.IsDone;
        UpdateSubtaskInfo(owner);
        await SaveAsync();
    }

    // ═════════════════════════════ Odak (Pomodoro) ═════════════════════════════

    /// <summary>Görevde odak başlatır; aynı görevde çalışıyorsa durdurur. Başka görevde çalışan oturum önce kaydedilir.</summary>
    [RelayCommand]
    private async Task StartFocusAsync(CyberTask? task)
    {
        if (task is null || task.IsCompleted)
        {
            return;
        }

        var now = DateTime.Now;
        bool credited = false;
        if (FocusTask is not null)
        {
            bool sameTask = FocusTask == task;
            credited = StopFocusCore(now);
            if (sameTask)
            {
                if (credited)
                {
                    await SaveAsync();
                }

                return;
            }
        }

        _focusStartedAt = now;
        _focusDuration = TimeSpan.FromMinutes(_settings.FocusMinutes);
        task.IsFocused = true;
        FocusTask = task;
        UpdateFocusDisplay(now);

        if (credited)
        {
            await SaveAsync();
        }
    }

    [RelayCommand]
    private async Task StopFocusAsync()
    {
        if (StopFocusCore(DateTime.Now))
        {
            await SaveAsync();
        }
    }

    /// <summary>Oturumu kapatır; en az 1 dakika geçtiyse süreyi göreve ve güne işler. İşlendiyse true.</summary>
    private bool StopFocusCore(DateTime now)
    {
        if (FocusTask is not { } task)
        {
            return false;
        }

        var elapsed = now - _focusStartedAt;
        if (elapsed > _focusDuration)
        {
            elapsed = _focusDuration;
        }

        task.IsFocused = false;
        FocusTask = null;
        FocusRemainingText = string.Empty;
        FocusProgress = 0;

        int minutes = (int)Math.Floor(elapsed.TotalMinutes);
        if (minutes < 1)
        {
            return false;
        }

        CreditFocus(task, minutes, now);
        return true;
    }

    private void CreditFocus(CyberTask task, int minutes, DateTime now)
    {
        task.TrackedMinutes += minutes;
        RefreshTrackedText(task);

        var day = GetOrCreateDay(DateOnly.FromDateTime(now));
        day.HoursWorked = Math.Round(day.HoursWorked + minutes / 60.0, 2);

        // TaskId bilinçli boş: geri alma yalnızca "tamamlandı" satırını aramalı, odak kayıtları tarihtir.
        InsertEntrySorted(day, new ActivityLogEntry
        {
            Time = now.ToString("HH:mm", CultureInfo.InvariantCulture),
            Description = $"{task.Code} {task.Title} · {FormatMinutes(minutes)}",
            Status = LogStatus.Focus,
        });

        RebuildChart();
    }

    private void UpdateFocusDisplay(DateTime now)
    {
        var remaining = _focusDuration - (now - _focusStartedAt);
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        FocusRemainingText = $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
        FocusProgress = _focusDuration.TotalSeconds <= 0 ? 1 : Math.Clamp(1 - remaining.TotalSeconds / _focusDuration.TotalSeconds, 0, 1);
    }

    // ═════════════════════════════ Ayarlar / bildirim / grafik / kısayol komutları ═════════════════════════════

    [RelayCommand]
    private void OpenSettings()
    {
        Editor = null;
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private void ShowLast30Days() => ChartMode = ChartMode.Last30;

    [RelayCommand]
    private void ShowLast7Days() => ChartMode = ChartMode.Last7;

    [RelayCommand]
    private void ShowStats() => ChartMode = ChartMode.Stats;

    [RelayCommand]
    private void ShowCalendar() => ChartMode = ChartMode.Calendar;

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

    // ───────── Şablonlar ─────────

    [RelayCommand]
    private void OpenTemplates()
    {
        Editor = null;
        IsSettingsOpen = false;
        IsTemplatesOpen = true;
    }

    [RelayCommand]
    private void CloseTemplates() => IsTemplatesOpen = false;

    private bool CanCreateTemplate() => !string.IsNullOrWhiteSpace(NewTemplateName);

    /// <summary>Görünen (filtrelenmiş) aktif görevlerden şablon. Tarihler bugüne göre gün farkı olarak saklanır.</summary>
    [RelayCommand(CanExecute = nameof(CanCreateTemplate))]
    private async Task CreateTemplateAsync()
    {
        var visible = ActiveView.Cast<CyberTask>().ToList();
        string name = NewTemplateName.Trim();
        if (visible.Count == 0 || name.Length == 0)
        {
            return;
        }

        var today = Today;
        var template = new TaskTemplate
        {
            Name = name.Length > 60 ? name[..60] : name,
            Items =
            [
                .. visible.Select(t => new TemplateItem
                {
                    Title = t.Title,
                    Category = t.Category,
                    Notes = t.Notes,
                    Priority = t.Priority,
                    EstimatedHours = t.EstimatedHours,
                    Recurrence = t.Recurrence,
                    RepeatDays = t.RepeatDays,
                    DueDayOffset = t.DueAt is { } due ? Math.Max(0, DateOnly.FromDateTime(due).DayNumber - today.DayNumber) : null,
                    DueTime = t.DueAt is { } dueTime && t.HasDueTime ? TimeOnly.FromDateTime(dueTime) : null,
                    ReminderMinutesBefore = t.ReminderMinutesBefore,
                    Subtasks = [.. t.Subtasks.Select(static s => s.Title)],
                }),
            ],
        };

        Templates.Add(template);
        NewTemplateName = string.Empty;
        ShowToast(_loc.Format("Toast_TemplateSaved", template.Name), ToastKind.Info, lifetime: s_infoLifetime);
        await SaveAsync();
    }

    /// <summary>Şablondaki görevleri yeni görev olarak ekler (yeni numaralar, tarihler bugüne göre).</summary>
    [RelayCommand]
    private async Task ApplyTemplateAsync(TaskTemplate? template)
    {
        if (template is null || template.Items.Count == 0)
        {
            return;
        }

        var now = DateTime.Now;
        var today = Today;
        foreach (var item in template.Items)
        {
            var dueDate = item.DueDayOffset is { } offset ? today.AddDays(offset) : (DateOnly?)null;
            if (item.Recurrence == Recurrence.SelectedDays && dueDate is { } d)
            {
                dueDate = RecurrenceRules.FirstMatchOnOrAfter(d, item.RepeatDays);
            }

            var task = new CyberTask
            {
                Number = _nextTaskNumber++,
                Title = item.Title,
                Category = item.Category,
                Notes = item.Notes,
                Priority = item.Priority,
                EstimatedHours = item.EstimatedHours,
                Recurrence = item.Recurrence,
                RepeatDays = item.RepeatDays,
                DueAt = dueDate?.ToDateTime(item.DueTime ?? TimeOnly.MinValue),
                HasDueTime = dueDate is not null && item.DueTime is not null,
                ReminderMinutesBefore = dueDate is null ? null : item.ReminderMinutesBefore,
                Subtasks = [.. item.Subtasks.Select(static s => new SubTask { Title = s })],
                CreatedAt = now,
                SortOrder = NextSortOrder(),
            };

            UpdateDerivedState(task, now);
            ActiveTasks.Add(task);
        }

        IsTemplatesOpen = false;
        OnTasksChanged();
        ShowToast(_loc.Format("Toast_TemplateApplied", template.Name, template.Items.Count), ToastKind.Info, lifetime: s_infoLifetime);
        await SaveAsync();
    }

    [RelayCommand]
    private async Task DeleteTemplateAsync(TaskTemplate? template)
    {
        if (template is not null && Templates.Remove(template))
        {
            await SaveAsync();
        }
    }

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
            case UpdateCheckStatus.UpToDate:
                UpdateStatusText = _loc.Format("Update_UpToDate", UpdateService.FormatVersion(result.Latest!.Version));
                UpdateBadgeText = string.Empty;
                break;
            case UpdateCheckStatus.UpdateAvailable:
                string latest = UpdateService.FormatVersion(result.Latest!.Version);
                UpdateStatusText = _loc.Format("Update_Available", latest);
                UpdateBadgeText = "▲ " + latest;
                _releasePageUrl = result.Latest.PageUrl;
                break;
            default:
                UpdateStatusText = _loc.Format("Update_Failed", result.Error);
                break;
        }
    }

    // ───────── Sürükle-bırak ─────────

    /// <summary>Sürükleme sırasında hedef satır işareti (tek satır; üst ya da alt çizgi).</summary>
    public void SetDropTarget(CyberTask? target, bool after = false)
    {
        foreach (var task in ActiveTasks)
        {
            bool isTarget = ReferenceEquals(task, target);
            if (task.IsDropTarget != isTarget)
            {
                task.IsDropTarget = isTarget;
            }

            bool isAfter = isTarget && after;
            if (task.IsDropAfter != isAfter)
            {
                task.IsDropAfter = isAfter;
            }
        }
    }

    /// <summary>
    /// Sürüklenen görevi hedefin önüne/arkasına (hedef null ise listenin sonuna) taşır. Mevcut görünür sıra esas alınır,
    /// tüm aktif görevler 0..n yeniden numaralanır ve sıralama "ELLE" moduna geçer.
    /// </summary>
    public async Task MoveTaskAsync(CyberTask dragged, CyberTask? target, bool after = false)
    {
        ArgumentNullException.ThrowIfNull(dragged);
        SetDropTarget(null);
        if (ReferenceEquals(dragged, target) || !ActiveTasks.Contains(dragged))
        {
            return;
        }

        var comparer = new TaskComparer(SelectedSortOption?.Value ?? SortMode.Created);
        var ordered = ActiveTasks.OrderBy(t => t, Comparer<CyberTask>.Create((a, b) => comparer.Compare(a, b))).ToList();
        ordered.Remove(dragged);
        int index = target is null ? ordered.Count : Math.Max(0, ordered.IndexOf(target)) + (after ? 1 : 0);
        ordered.Insert(Math.Min(index, ordered.Count), dragged);

        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].SortOrder = i;
        }

        SelectedSortOption = OptionSets.Find(OptionSets.SortModes, SortMode.Manual); // CustomSort'u da ayarlar + kaydeder
        ActiveView.Refresh();
        await SaveAsync();
    }

    private double NextSortOrder() => ActiveTasks.Count == 0 ? 0 : ActiveTasks.Max(static t => t.SortOrder) + 1;

    [RelayCommand]
    private void DismissError()
    {
        ErrorMessage = null;
        _errorIsFromSave = false;
    }

    [RelayCommand]
    private void DismissToast(ToastItem? toast)
    {
        if (toast is not null)
        {
            Toasts.Remove(toast);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunToastActionAsync(ToastItem? toast)
    {
        // Remove başarısızsa bildirim zaten süresi dolup kalkmış ya da kullanılmıştır → çift "geri al" olmaz.
        if (toast is null || !Toasts.Remove(toast) || toast.Action is null)
        {
            return;
        }

        await toast.Action();
    }

    /// <summary>Ctrl+Z: en son geri alınabilir işlemi (silme) geri al.</summary>
    [RelayCommand]
    private async Task UndoAsync()
    {
        var toast = Toasts.LastOrDefault(static t => t.HasAction);
        if (toast is not null)
        {
            await RunToastActionAsync(toast);
        }
    }

    /// <summary>Esc: açık diyaloğu kapat; yoksa aramayı temizle.</summary>
    [RelayCommand]
    private void Escape()
    {
        if (Editor is not null)
        {
            Editor = null;
        }
        else if (IsSettingsOpen)
        {
            IsSettingsOpen = false;
        }
        else if (IsTemplatesOpen)
        {
            IsTemplatesOpen = false;
        }
        else if (SearchText.Length > 0)
        {
            SearchText = string.Empty;
        }
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

    /// <summary>İlerleme çubuğuna tıklanınca çağrılır. %5 adımlara yuvarlar. Alt görev varsa yok sayılır.</summary>
    public async Task SetTaskProgressAsync(CyberTask task, double percent)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsCompleted || task.HasSubtasks || !double.IsFinite(percent))
        {
            return;
        }

        double snapped = Math.Clamp(Math.Round(percent / 5) * 5, 0, 100);
        if (snapped.Equals(task.Progress))
        {
            return;
        }

        task.Progress = snapped;
        await SaveAsync();
    }

    // ═════════════════════════════ Property değişim kancaları ═════════════════════════════

    partial void OnNewTaskTitleChanged(string value) => UpdateQuickAddPreview();

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

    partial void OnSelectedThemeOptionChanged(LocalizedOption<ThemeId> value)
    {
        if (value is null)
        {
            return;
        }

        _settings.Theme = value.Value;
        CurrentTheme = ThemePalette.Get(value.Value);
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

    private UpdateCheckResult? _lastUpdateResult;

    /// <summary>Açılışta arka planda denetim; hata uygulamayı etkilemez, sadece ayarlar panelinde yazar.</summary>
    private async Task AutoCheckUpdatesAsync()
    {
        _lastUpdateResult = await _updates.CheckAsync();
        ApplyUpdateResult(_lastUpdateResult);
    }

    partial void OnSearchTextChanged(string value) => RefreshViews();

    partial void OnSelectedCategoryFilterChanged(LocalizedOption<string?>? value) => RefreshViews();

    partial void OnSelectedSmartViewChanged(SmartViewOption? value) => RefreshViews();

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

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Localizer.Language) || !_initialized)
        {
            return;
        }

        var now = DateTime.Now;
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

        RebuildChart(); // tarih etiketleri, istatistik ve takvim dile bağlı
        OnPropertyChanged(nameof(Editor)); // düzenleyici başlığı
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;

        for (int i = Toasts.Count - 1; i >= 0; i--)
        {
            if (Toasts[i].ExpiresAt is { } expires && now >= expires)
            {
                Toasts.RemoveAt(i);
            }
        }

        if (FocusTask is { } focused)
        {
            if (now - _focusStartedAt >= _focusDuration)
            {
                int minutes = (int)Math.Round(_focusDuration.TotalMinutes);
                StopFocusCore(now);
                string message = _loc.Format("Toast_FocusDone", $"{focused.Code} {focused.Title}", FormatMinutes(minutes));
                ShowToast(message, ToastKind.Reminder);
                AttentionRequested?.Invoke(this, message);
                _ = SaveAsync();
            }
            else
            {
                UpdateFocusDisplay(now);
            }
        }

        CheckReminders(now);

        if (now.Minute != _lastClockMinute)
        {
            _lastClockMinute = now.Minute;
            foreach (var task in ActiveTasks)
            {
                RefreshDueInfo(task, now); // "BUGÜN" → gecikmiş geçişleri
            }

            RefreshViews(); // akıllı liste üyelikleri ve sayaçlar
            UpdateQuickAddPreview();
        }

        if (Today != _chartAnchorDate)
        {
            RebuildChart(); // gece yarısı geçildi
        }
    }

    // ═════════════════════════════ İç işler ═════════════════════════════

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
        CurrentTheme = ThemePalette.Get(_settings.Theme);
        SelectedTrayOption = OptionSets.Find(OptionSets.OnOff, _settings.MinimizeToTray);
        SelectedAutoUpdateOption = OptionSets.Find(OptionSets.OnOff, _settings.AutoCheckUpdates);
        OnPropertyChanged(nameof(MinimizeToTray));
    }

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

    private void SaveSettingsIfReady()
    {
        if (_initialized)
        {
            _ = SaveAsync(); // SaveAsync hatayı kendi içinde UI'a yansıtır
        }
    }

    /// <summary>Görev eklendi/silindi/değişti: kategori çipleri, akıllı liste sayaçları ve görünümler güncellenir.</summary>
    private void OnTasksChanged()
    {
        RefreshCategories();
        RefreshViews();

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
    }

    private void RefreshViews()
    {
        UpdateSmartViewCounts();
        ActiveView.Refresh();
        CompletedView.Refresh();
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

    private void UpdateQuickAddPreview()
    {
        var parsed = QuickAddParser.Parse(NewTaskTitle, Today);
        if (!parsed.HasTokens)
        {
            QuickAddPreview = string.Empty;
            return;
        }

        var parts = new List<string>();
        if (parsed.Category is { } category)
        {
            parts.Add("#" + category);
        }

        if (parsed.Priority is not null)
        {
            parts.Add(_loc["Prio_High"]);
        }

        if (parsed.DueDate is { } date)
        {
            parts.Add("⏱ " + FormatDue(date, parsed.DueTime, DateTime.Now));
        }

        if (parsed.Recurrence is { } recurrence)
        {
            parts.Add("↻ " + FormatRecurrence(recurrence, parsed.RepeatDays));
        }

        if (parsed.Hours is { } hours)
        {
            parts.Add($"~{InputParsing.FormatHours(hours)} {_loc["Unit_Hours"]}");
        }

        QuickAddPreview = _loc["Preview_Prefix"] + string.Join("  ·  ", parts);
    }

    /// <summary>Saat girilmemişse son tarih o günün sonu kabul edilir.</summary>
    private static DateTime GetEffectiveDue(CyberTask task) =>
        task.DueAt is not { } due ? DateTime.MaxValue
        : task.HasDueTime ? due
        : due.Date.AddDays(1).AddTicks(-1);

    /// <summary>Saat girilmemişse hatırlatma tabanı o gün 09:00.</summary>
    private static DateTime GetReminderMoment(CyberTask task, int minutesBefore)
    {
        var due = task.DueAt!.Value;
        var basis = task.HasDueTime ? due : due.Date.AddHours(9);
        return basis.AddMinutes(-minutesBefore);
    }

    private void CheckReminders(DateTime now)
    {
        bool changed = false;
        foreach (var task in ActiveTasks)
        {
            if (task.ReminderFired || task.ReminderMinutesBefore is not { } minutes || task.DueAt is null)
            {
                continue;
            }

            if (now < GetReminderMoment(task, minutes))
            {
                continue;
            }

            task.ReminderFired = true;
            changed = true;

            string message = _loc.Format("Toast_Reminder", task.Code, task.Title);
            if (task.DueText.Length > 0)
            {
                message += $" ({task.DueText})";
            }

            ShowToast(message, ToastKind.Reminder); // kullanıcı kapatana kadar kalır
            AttentionRequested?.Invoke(this, message);
        }

        if (changed)
        {
            _ = SaveAsync();
        }
    }

    private CyberTask CreateNextOccurrence(CyberTask source, DateTime now)
    {
        var basis = source.DueAt ?? now.Date;
        var next = RecurrenceRules.Advance(basis, source.Recurrence, source.RepeatDays);

        // Gecikmiş bir tekrarı tamamlamak, geçmiş tarihli yeni bir gecikmiş görev üretmesin.
        var probe = new CyberTask { DueAt = next, HasDueTime = source.HasDueTime };
        for (int guard = 0; guard < 1000 && GetEffectiveDue(probe) < now; guard++)
        {
            next = RecurrenceRules.Advance(next, source.Recurrence, source.RepeatDays);
            probe.DueAt = next;
        }

        return new CyberTask
        {
            Number = _nextTaskNumber++,
            Title = source.Title,
            Category = source.Category,
            Notes = source.Notes,
            Priority = source.Priority,
            EstimatedHours = source.EstimatedHours,
            DueAt = next,
            HasDueTime = source.HasDueTime,
            Recurrence = source.Recurrence,
            RepeatDays = source.RepeatDays,
            ReminderMinutesBefore = source.ReminderMinutesBefore,
            Subtasks = [.. source.Subtasks.Select(static s => new SubTask { Title = s.Title })],
            CreatedAt = now,
            SortOrder = source.SortOrder, // elle sıralamada aynı yerde kalsın
        };
    }

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

    private void ShowToast(string message, ToastKind kind, string? actionLabel = null, Func<Task>? action = null, TimeSpan? lifetime = null)
    {
        Toasts.Add(new ToastItem
        {
            Message = message,
            Kind = kind,
            ActionLabel = actionLabel,
            Action = action,
            ExpiresAt = lifetime is { } span ? DateTime.Now + span : null,
        });

        while (Toasts.Count > MaxToasts)
        {
            Toasts.RemoveAt(0);
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
    }

    private void RefreshStats()
    {
        var (tiles, weekdays) = InsightsBuilder.BuildStats(_daysByDate, ActiveTasks.Concat(CompletedTasks), Today, _loc, FormatMinutes);
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

    /// <summary>
    /// Bir satır listeden çıkınca alttaki satırın kutusu imlecin altına kayar. Çift/hızlı tıklama
    /// istemeden ikinci görevi de tamamlamasın (veya geri almasın) diye kısa süreli kilit.
    /// </summary>
    private bool TryBeginListMove()
    {
        long now = Environment.TickCount64;
        if (now - _lastListMoveTick < ListMoveGuardMs)
        {
            return false;
        }

        _lastListMoveTick = now;
        return true;
    }

    private DayActivity GetOrCreateDay(DateOnly date)
    {
        if (!_daysByDate.TryGetValue(date, out var day))
        {
            day = new DayActivity { Date = date };
            _daysByDate[date] = day;
        }

        return day;
    }

    private static void InsertEntrySorted(DayActivity day, ActivityLogEntry entry)
    {
        int index = 0;
        while (index < day.Entries.Count && string.CompareOrdinal(day.Entries[index].Time, entry.Time) <= 0)
        {
            index++;
        }

        day.Entries.Insert(index, entry);
    }

    /// <summary>
    /// UI thread'inde anlık görüntü alıp diske asenkron yazar.
    /// Hata yutulmaz; başlıktaki kırmızı hata satırında gösterilir.
    /// </summary>
    private async Task SaveAsync()
    {
        if (_persistenceDisabled)
        {
            return;
        }

        var snapshot = new CyberData
        {
            NextTaskNumber = _nextTaskNumber,
            Settings = _settings,
            Templates = [.. Templates],
            Tasks = [.. ActiveTasks, .. CompletedTasks],
            Days =
            [
                .. _daysByDate.Values
                    .Where(static d => d.Entries.Count > 0 || d.HoursWorked > 0 || d.CompletedCount > 0)
                    .OrderBy(static d => d.Date),
            ],
        };

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
            ErrorMessage = _loc.Format("Err_SaveFailed", ex.Message);
            _errorIsFromSave = true;
        }
    }

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
