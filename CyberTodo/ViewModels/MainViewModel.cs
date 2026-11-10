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
        PropertyChangedEventManager.AddHandler(Localizer.Instance, OnLanguageChanged, string.Empty); // zayıf abonelik
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(nameof(Label));

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

    private const int MaxUndo = 30;
    private const int MaxFocusSessions = 20_000; // ~yıllarca günde birkaç oturum; dosya şişmesin

    private readonly Localizer _loc = Localizer.Instance;
    private readonly StorageService _storage;
    private readonly UpdateService _updates;
    private readonly IStartupRegistration _startup;
    private readonly BackupService _backups;
    private readonly IFileDialogs _dialogs;
    private readonly UpdateInstaller? _installer;
    private readonly List<UndoEntry> _undoStack = [];
    private readonly List<FocusSession> _focusSessions = [];
    private ReleaseInfo? _latestRelease;
    private Version? _toastedUpdateVersion;
    private DateOnly? _calendarSelectedDate;
    private readonly Dictionary<DateOnly, DayActivity> _daysByDate = new();
    private readonly DispatcherTimer _clock;

    /// <summary>Art arda değişiklikleri tek yazıma toplama süresi (kayıp penceresi en fazla bu kadar).</summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly DispatcherTimer _saveTimer;
    private bool _saveDirty;

    private AppSettings _settings = new();
    private ChartBar? _hoveredBar;
    private DayActivity? _observedDay;
    private DateOnly _chartAnchorDate;
    private DateTime _focusStartedAt;
    private TimeSpan _focusDuration;
    private int _lastClockMinute = -1;
    private DateOnly _viewsDay;
    private int _nextTaskNumber = 1;
    private bool _initialized;
    private bool _persistenceDisabled;
    private bool _errorIsFromSave;
    private long _lastListMoveTick = long.MinValue / 2;

    /// <param name="updates">null → depo ayarsız (ağa çıkmaz); testler için.</param>
    /// <param name="startup">null → bellekte (kayıt defterine dokunmaz); testler için.</param>
    /// <param name="dialogs">null → hiçbir dosya seçilmez/onaylanmaz; testler sahte verir.</param>
    /// <param name="installer">null → kendini güncelleme kapalı (yalnızca yayın sayfası açılır).</param>
    public MainViewModel(
        StorageService storage,
        UpdateService? updates = null,
        IStartupRegistration? startup = null,
        IFileDialogs? dialogs = null,
        UpdateInstaller? installer = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _updates = updates ?? new UpdateService(null, UpdateService.ReadCurrentVersion());
        _startup = startup ?? new InMemoryStartupRegistration();
        _backups = new BackupService(_storage.FilePath);
        _dialogs = dialogs ?? new NullFileDialogs();
        _installer = installer;
        _versionText = UpdateService.FormatVersion(_updates.CurrentVersion);
        _currentTheme = ThemePalette.All[0];
        _selectedThemeOption = OptionSets.Themes[0];
        _selectedTrayOption = OptionSets.OnOff[0];
        _selectedAutoUpdateOption = OptionSets.OnOff[0];
        _selectedStartupOption = OptionSets.OnOff[1];
        _selectedBreaksOption = OptionSets.OnOff[0];
        _selectedAutoNextOption = OptionSets.OnOff[1];
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
        MiniTasks = new ListCollectionView(ActiveTasks)
        {
            Filter = static item => item is CyberTask { DueState: DueState.Today or DueState.Overdue },
            CustomSort = new TaskComparer(SortMode.DueDate),
        };

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

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SaveDelay };
        _saveTimer.Tick += OnSaveTimerTick;

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
            CancelBreak();
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

    // ───────── Klavyeyle liste yönetimi ─────────

    /// <summary>Ok tuşlarıyla seçilen aktif görev (görünür listede).</summary>
    [ObservableProperty]
    private CyberTask? _selectedTask;

    partial void OnSelectedTaskChanged(CyberTask? oldValue, CyberTask? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    private List<CyberTask> VisibleActiveTasks() => [.. ActiveView.Cast<CyberTask>()];

    /// <summary>↑/↓: seçimi görünür sırada kaydırır. Seçim yoksa ilk (↓) ya da son (↑) satır seçilir.</summary>
    public void MoveSelection(int delta)
    {
        var visible = VisibleActiveTasks();
        if (visible.Count == 0)
        {
            SelectedTask = null;
            return;
        }

        int index = SelectedTask is null ? -1 : visible.IndexOf(SelectedTask);
        int next = index < 0 ? (delta >= 0 ? 0 : visible.Count - 1) : Math.Clamp(index + delta, 0, visible.Count - 1);
        SelectedTask = visible[next];
    }

    /// <summary>Home / End.</summary>
    public void SelectEdge(bool last)
    {
        var visible = VisibleActiveTasks();
        SelectedTask = visible.Count == 0 ? null : (last ? visible[^1] : visible[0]);
    }

    /// <summary>Space: seçili görevi tamamlar; seçim aynı sıradaki (bir sonraki) satıra geçer.</summary>
    public async Task CompleteSelectedAsync()
    {
        if (SelectedTask is not { } task)
        {
            return;
        }

        int index = VisibleActiveTasks().IndexOf(task);
        await CompleteTaskCommand.ExecuteAsync(task);
        if (task.IsCompleted)
        {
            SelectNearIndex(index);
        }
    }

    /// <summary>Delete: seçili görevi siler (GERİ AL ile geri gelir); seçim komşu satıra geçer.</summary>
    public async Task DeleteSelectedAsync()
    {
        if (SelectedTask is not { } task)
        {
            return;
        }

        int index = VisibleActiveTasks().IndexOf(task);
        await DeleteTaskCommand.ExecuteAsync(task);
        SelectNearIndex(index);
    }

    public void EditSelected()
    {
        if (SelectedTask is { } task)
        {
            EditTaskCommand.Execute(task);
        }
    }

    public Task ToggleFocusSelectedAsync() =>
        SelectedTask is { } task ? StartFocusCommand.ExecuteAsync(task) : Task.CompletedTask;

    private void SelectNearIndex(int index)
    {
        var visible = VisibleActiveTasks();
        SelectedTask = visible.Count == 0 ? null : visible[Math.Clamp(index, 0, visible.Count - 1)];
    }

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
        Editor = null;
        IsSettingsOpen = false;
        IsTemplatesOpen = false;
        IsDataOpen = false;
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

    // ───────── Veri / yedek ─────────

    public ObservableCollection<BackupInfo> Backups { get; } = [];

    [ObservableProperty]
    private bool _isDataOpen;

    public string BackupDirectory => _backups.BackupDirectory;

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

    public BulkObservableCollection<CyberTask> ActiveTasks { get; } = [];

    public BulkObservableCollection<CyberTask> CompletedTasks { get; } = [];

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
        bool isNew = false;
        try
        {
            var result = await _storage.LoadAsync();
            data = result.Data;
            isNew = result.IsNew;
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
        ApplySettingsToUi();

        var now = DateTime.Now;
        ApplyData(data, now);

        _initialized = true;
        OnTasksChanged();
        RebuildChart();

        ReadStartupState();

        _lastClockMinute = now.Minute;
        _viewsDay = DateOnly.FromDateTime(now); // görünümler az önce bu gün için kuruldu
        _clock.Start();
        CheckReminders(now); // uygulama kapalıyken kaçırılan hatırlatmalar hemen gösterilir

        if (!isNew)
        {
            _ = CreateDailyBackupAsync(now); // yüklenen hâlin günlük yedeği; hata UI'a yansır
        }

        UpdateStatusText = _updates.IsConfigured ? string.Empty : _loc["Update_NotConfigured"];
        if (_settings.AutoCheckUpdates && _updates.IsConfigured)
        {
            _ = AutoCheckUpdatesAsync(); // ağ beklenmez; hatalar CheckAsync içinde sonuca dönüşür
        }
    }

    /// <summary>
    /// Görev, gün, şablon ve odak verisini arayüz koleksiyonlarına yükler (açılış, yedekten geri yükleme, içe aktarma).
    /// Ayarlar bilinçli olarak dokunulmaz: geri yükleme/içe aktarma dil, tema, ölçek gibi tercihleri değiştirmez.
    /// </summary>
    private void ApplyData(CyberData data, DateTime now)
    {
        _nextTaskNumber = data.NextTaskNumber;

        _daysByDate.Clear();
        foreach (var day in data.Days)
        {
            _daysByDate[day.Date] = day; // aynı tarih iki kez varsa sonuncusu geçerli
        }

        Templates.Clear();
        foreach (var template in data.Templates)
        {
            Templates.Add(template);
        }

        _focusSessions.Clear();
        _focusSessions.AddRange(data.FocusSessions.OrderBy(static s => s.Start));

        var active = data.Tasks.Where(static t => !t.IsCompleted).OrderBy(static t => t.CreatedAt).ToList();

        // Eski kayıtlarda SortOrder yok (hepsi 0): eklenme sırasıyla numarala ki "ELLE" sıralama anlamlı başlasın.
        if (active.Count > 1 && active.All(static t => t.SortOrder == 0))
        {
            for (int i = 0; i < active.Count; i++)
            {
                active[i].SortOrder = i;
            }
        }

        // Toplu yükleme: tek "Reset" bildirimi → görünüm bir kez sıralar/filtreler, liste bir kez oluşturulur
        // (öğe başına Add bildirimi binlerce görevde açılışı saniyelerce uzatıyordu).
        // Not: DeferRefresh kullanılamaz; ertelenmişken kaynak koleksiyonun değişmesine izin vermiyor.
        var completed = data.Tasks.Where(static t => t.IsCompleted).OrderByDescending(static t => t.CompletedAt).ToList();
        foreach (var task in active.Concat(completed))
        {
            UpdateDerivedState(task, now);
        }

        if (ActiveTasks.Count > 0)
        {
            ActiveTasks.Clear();
        }

        if (CompletedTasks.Count > 0)
        {
            CompletedTasks.Clear();
        }

        ActiveTasks.AddRange(active);
        CompletedTasks.AddRange(completed);
    }

    /// <summary>Kapanışta: çalışan odak oturumunun geçen süresini kaydet, saati durdur, yazmaların bitmesini bekle.</summary>
    public async Task ShutdownAsync()
    {
        _clock.Stop();
        if (StopFocusCore(DateTime.Now))
        {
            await SaveAsync();
        }

        // Ertelenmiş kayıt varsa beklemeden yaz, sonra kuyruktaki yazmaların bitmesini bekle.
        _saveTimer.Stop();
        await SaveNowAsync();
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

        PushUndo(_loc.Format("Undo_Add", task.Code), async () =>
        {
            if (!ActiveTasks.Contains(task))
            {
                return;
            }

            if (FocusTask == task)
            {
                StopFocusCore(DateTime.Now); // harcanan süre günlüğe işlenir, kaybolmaz
            }

            ActiveTasks.Remove(task);
            OnTasksChanged();
            await SaveAsync();
        });

        await SaveAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CompleteTaskAsync(CyberTask? task)
    {
        if (task is null || task.IsCompleted || !TryBeginListMove())
        {
            return;
        }

        double sortOrder = task.SortOrder;
        CompleteCore(task, DateTime.Now);
        PushUndo(_loc.Format("Undo_Complete", task.Code), async () =>
        {
            if (!task.IsCompleted || !CompletedTasks.Contains(task))
            {
                return;
            }

            RestoreCore(task, sortOrder);
            await SaveAsync();
        });

        await SaveAsync();
    }

    /// <summary>Tamamlama: log satırı, güne saat, listeler arası taşıma, tekrar kopyası. Geri alma kaydı üretmez.</summary>
    private void CompleteCore(CyberTask task, DateTime now)
    {
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
    }

    /// <summary>Tamamlanan görevi geri alır; log satırını, eklenen saati ve (varsa) üretilen tekrar kopyasını geri çeker.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RestoreTaskAsync(CyberTask? task)
    {
        if (task is null || !task.IsCompleted || !TryBeginListMove())
        {
            return;
        }

        RestoreCore(task, sortOrder: null);
        PushUndo(_loc.Format("Undo_Restore", task.Code), async () =>
        {
            if (task.IsCompleted || !ActiveTasks.Contains(task))
            {
                return;
            }

            CompleteCore(task, DateTime.Now);
            await SaveAsync();
        });

        await SaveAsync();
    }

    /// <param name="sortOrder">null → listenin sonuna; değer → eski yerine (tamamlamayı geri alırken).</param>
    private void RestoreCore(CyberTask task, double? sortOrder)
    {
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
        task.SortOrder = sortOrder ?? NextSortOrder();

        CompletedTasks.Remove(task);
        ActiveTasks.Add(task);

        OnTasksChanged();
        RebuildChart();
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

        var entry = PushUndo(_loc.Format("Undo_Delete", task.Code), async () =>
        {
            if (list.Contains(task))
            {
                return;
            }

            list.Insert(Math.Min(index, list.Count), task);
            OnTasksChanged();
            await SaveAsync();
        });

        entry.Toast = ShowToast(
            _loc.Format("Toast_Deleted", task.Code),
            ToastKind.Info,
            _loc["Toast_Undo"],
            () => RevertAsync(entry),
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
        if (Editor is not { } editor)
        {
            return;
        }

        var before = TaskMemento.Capture(editor.Target);
        if (!editor.TryApply())
        {
            return; // geçersizse diyalog açık kalır, hata metni gösterilir
        }

        var task = editor.Target;
        UpdateDerivedState(task, DateTime.Now);
        Editor = null;

        OnTasksChanged(); // sıralama anahtarı / akıllı liste üyeliği değişmiş olabilir
        PushUndo(_loc.Format("Undo_Edit", task.Code), async () =>
        {
            if (!ActiveTasks.Contains(task))
            {
                return;
            }

            if (Editor?.Target == task)
            {
                Editor = null; // açık taslak eski değerlerin üzerine yazmasın
            }

            before.ApplyTo(task);
            UpdateDerivedState(task, DateTime.Now);
            OnTasksChanged();
            await SaveAsync();
        });

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

        CancelBreak(); // molada elle odak başlatıldı
        BeginFocus(task, now);

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

    /// <summary>Test için: odak oturumunu verilen anda bitirir (gerçek dakikalarca beklemeden).</summary>
    internal bool StopFocusAt(DateTime now) => StopFocusCore(now);

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

        _focusSessions.Add(new FocusSession
        {
            Start = now.AddMinutes(-minutes),
            Minutes = minutes,
            TaskId = task.Id,
            TaskNumber = task.Number,
            Title = task.Title,
            Category = string.IsNullOrWhiteSpace(task.Category) ? null : task.Category.Trim(),
        });
        if (_focusSessions.Count > MaxFocusSessions)
        {
            _focusSessions.RemoveRange(0, _focusSessions.Count - MaxFocusSessions); // en eskiler
        }

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

    private void BeginFocus(CyberTask task, DateTime now)
    {
        _focusStartedAt = now;
        _focusDuration = TimeSpan.FromMinutes(_settings.FocusMinutes);
        task.IsFocused = true;
        FocusTask = task;
        UpdateFocusDisplay(now);
    }

    // ───────── Mola ─────────

    public const int ShortBreakMinutes = 5;
    public const int LongBreakMinutes = 15;
    public const int FocusesPerLongBreak = 4;

    private DateTime _breakStartedAt;
    private DateTime _breakEndsAt;
    private CyberTask? _breakTask;
    private int _completedFocusCount;

    [ObservableProperty]
    private bool _isOnBreak;

    [ObservableProperty]
    private bool _isLongBreak;

    [ObservableProperty]
    private string _breakRemainingText = string.Empty;

    [ObservableProperty]
    private double _breakProgress;

    /// <summary>Odak bitti: kısa mola; her <see cref="FocusesPerLongBreak"/>. odaktan sonra uzun mola.</summary>
    private void StartBreak(CyberTask task, DateTime now)
    {
        IsLongBreak = _completedFocusCount > 0 && _completedFocusCount % FocusesPerLongBreak == 0;
        _breakTask = task;
        _breakStartedAt = now;
        _breakEndsAt = now.AddMinutes(IsLongBreak ? LongBreakMinutes : ShortBreakMinutes);
        IsOnBreak = true;
        UpdateBreakDisplay(now);
    }

    /// <summary>Mola bitti (süre doldu ya da atlandı). Ayar açıksa ve görev hâlâ aktifse aynı görevde odak başlar.</summary>
    private void EndBreak(DateTime now, bool notify)
    {
        var task = _breakTask;
        CancelBreak();

        bool resume = _settings.AutoStartNextFocus && task is not null && !task.IsCompleted && ActiveTasks.Contains(task) && FocusTask is null;
        if (notify)
        {
            string message = resume ? _loc.Format("Toast_BreakOverResume", $"{task!.Code} {task.Title}") : _loc["Toast_BreakOver"];
            ShowToast(message, ToastKind.Reminder, lifetime: resume ? s_infoLifetime : null);
            AttentionRequested?.Invoke(this, message);
        }

        if (resume)
        {
            BeginFocus(task!, now);
        }
    }

    private void CancelBreak()
    {
        if (!IsOnBreak)
        {
            return;
        }

        IsOnBreak = false;
        IsLongBreak = false;
        _breakTask = null;
        BreakRemainingText = string.Empty;
        BreakProgress = 0;
    }

    /// <summary>Molayı atla: hemen biter (otomatik devam ayarı açıksa odak başlar).</summary>
    [RelayCommand]
    private void SkipBreak()
    {
        if (IsOnBreak)
        {
            EndBreak(DateTime.Now, notify: false);
        }
    }

    private void UpdateBreakDisplay(DateTime now)
    {
        var total = _breakEndsAt - _breakStartedAt;
        var remaining = _breakEndsAt - now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        BreakRemainingText = $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
        BreakProgress = total.TotalSeconds <= 0 ? 1 : Math.Clamp(1 - remaining.TotalSeconds / total.TotalSeconds, 0, 1);
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
            ErrorMessage = _loc.Format("Err_Backup", ex.Message);
        }
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        try
        {
            string path = await _backups.CreateSafetyAsync(StorageService.Serialize(CreateSnapshot()), DateTime.Now, "manual");
            ShowToast(_loc.Format("Toast_BackupSaved", Path.GetFileName(path)), ToastKind.Info, lifetime: s_infoLifetime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
            safety = await _backups.CreateSafetyAsync(StorageService.Serialize(CreateSnapshot()), DateTime.Now, reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
        if (FocusTask is { } focused)
        {
            focused.IsFocused = false;
            FocusTask = null;
            FocusRemainingText = string.Empty;
            FocusProgress = 0;
        }

        CancelBreak();
        SelectedTask = null;
        Editor = null;
        ClearUndo();

        var now = DateTime.Now;
        ApplyData(data, now);
        _viewsDay = DateOnly.FromDateTime(now);
        OnTasksChanged();
        RebuildChart();
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        string? path = _dialogs.PickSavePath($"calabatica-{DateTime.Now:yyyyMMdd}.json", JsonFilter);
        if (path is not null)
        {
            await ExportAsync(path, StorageService.Serialize(CreateSnapshot()));
        }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        string? path = _dialogs.PickSavePath($"calabatica-{DateTime.Now:yyyyMMdd}.csv", "CSV (*.csv)|*.csv");
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

        string? path = _dialogs.PickSavePath($"calabatica-{DateTime.Now:yyyyMMdd}.ics", "iCalendar (*.ics)|*.ics");
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
            ErrorMessage = _loc.Format("Err_Export", ex.Message);
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
        var added = new List<CyberTask>(template.Items.Count);
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
            added.Add(task);
        }

        IsTemplatesOpen = false;
        OnTasksChanged();
        PushUndo(_loc.Format("Undo_Template", template.Name), async () =>
        {
            foreach (var task in added)
            {
                if (FocusTask == task)
                {
                    StopFocusCore(DateTime.Now);
                }

                ActiveTasks.Remove(task); // tamamlanmış/silinmiş olanlar zaten listede yok
            }

            OnTasksChanged();
            await SaveAsync();
        });
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
            UpdateStatusText = _loc.Format("Update_InstallFailed", ex.Message);
            ShowToast(UpdateStatusText, ToastKind.Info, lifetime: TimeSpan.FromSeconds(8));
        }
        finally
        {
            IsInstallingUpdate = false;
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

        var previousOrders = ActiveTasks.Select(static t => (Task: t, t.SortOrder)).ToList();
        var previousSort = SelectedSortOption;

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

        PushUndo(_loc.Format("Undo_Move", dragged.Code), async () =>
        {
            foreach (var (task, sortOrder) in previousOrders)
            {
                task.SortOrder = sortOrder;
            }

            if (SelectedSortOption == previousSort)
            {
                ActiveView.Refresh(); // mod değişmediyse CustomSort yenilenmez → elle yenile
            }
            else
            {
                SelectedSortOption = previousSort;
            }

            await SaveAsync();
        });

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

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunToastSecondaryActionAsync(ToastItem? toast)
    {
        if (toast is null || !Toasts.Remove(toast) || toast.SecondaryAction is null)
        {
            return;
        }

        await toast.SecondaryAction();
    }

    // ───────── Geri alma ─────────

    public bool CanUndo => _undoStack.Count > 0;

    /// <summary>Son geri alınabilir işlemin açıklaması (düğme ipucu); yoksa boş.</summary>
    public string UndoHint => _undoStack.Count > 0 ? _loc.Format("Tip_UndoLast", _undoStack[^1].Message) : _loc["Tip_Undo"];

    /// <summary>Ctrl+Z / ↶: en son işlemi (ekle, tamamla, geri aç, düzenle, taşı, sil, şablon) geri alır.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo), AllowConcurrentExecutions = true)]
    private async Task UndoAsync()
    {
        if (_undoStack.Count > 0)
        {
            await RevertAsync(_undoStack[^1]);
        }
    }

    private UndoEntry PushUndo(string message, Func<Task> revert)
    {
        var entry = new UndoEntry(message, revert);
        _undoStack.Add(entry);
        if (_undoStack.Count > MaxUndo)
        {
            _undoStack.RemoveAt(0);
        }

        OnUndoStackChanged();
        return entry;
    }

    /// <summary>Kaydı yığından çıkarıp geri alır. Kayıt zaten kullanılmışsa (bildirimden + Ctrl+Z) hiçbir şey yapmaz.</summary>
    private async Task RevertAsync(UndoEntry entry)
    {
        if (!_undoStack.Remove(entry))
        {
            return;
        }

        OnUndoStackChanged();
        if (entry.Toast is { } toast)
        {
            Toasts.Remove(toast);
        }

        await entry.Revert();
        ShowToast(_loc.Format("Toast_Undone", entry.Message), ToastKind.Info, lifetime: s_infoLifetime);
    }

    private void ClearUndo()
    {
        foreach (var toast in _undoStack.Select(static e => e.Toast).OfType<ToastItem>())
        {
            Toasts.Remove(toast);
        }

        _undoStack.Clear();
        OnUndoStackChanged();
    }

    private void OnUndoStackChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoHint));
        UndoCommand.NotifyCanExecuteChanged();
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
        else if (IsDataOpen)
        {
            IsDataOpen = false;
        }
        else if (SearchText.Length > 0)
        {
            SearchText = string.Empty;
        }
        else
        {
            SelectedTask = null;
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

        RebuildChart(); // tarih etiketleri, istatistik, odak ve takvim dile bağlı
        OnPropertyChanged(nameof(Editor)); // düzenleyici başlığı
        OnPropertyChanged(nameof(UndoHint));
    }

    private void OnClockTick(object? sender, EventArgs e) => Tick(DateTime.Now);

    /// <summary>Saniyelik saat işi. Testler zamanı ileri sarmak için doğrudan çağırır.</summary>
    internal void Tick(DateTime now)
    {
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
                _completedFocusCount++;
                string message = _loc.Format("Toast_FocusDone", $"{focused.Code} {focused.Title}", FormatMinutes(minutes));
                ShowToast(message, ToastKind.Reminder);
                AttentionRequested?.Invoke(this, message);
                _ = SaveAsync();

                if (_settings.BreaksEnabled)
                {
                    StartBreak(focused, now);
                }
            }
            else
            {
                UpdateFocusDisplay(now);
            }
        }
        else if (IsOnBreak)
        {
            if (now >= _breakEndsAt)
            {
                EndBreak(now, notify: true);
            }
            else
            {
                UpdateBreakDisplay(now);
            }
        }

        CheckReminders(now);

        if (now.Minute != _lastClockMinute)
        {
            _lastClockMinute = now.Minute;
            RefreshTimeDependentState(now);
        }

        if (Today != _chartAnchorDate)
        {
            RebuildChart(); // gece yarısı geçildi
        }
    }

    /// <summary>
    /// Dakikalık saat işi: son tarih metinlerini/durumlarını günceller; görünümleri yalnızca bir görevin
    /// <see cref="DueState"/>'i veya takvim günü değiştiyse yeniler. Koşulsuz <c>Refresh()</c> her dakika tüm
    /// satırları yeniden kuruyordu; boşta, JIT'in bu yolları dakikalarca parça parça yeniden derlemesine
    /// (tier-up/PGO) ve küçük listede bile ~1–2 sn tek çekirdek CPU sıçramalarına yol açıyordu.
    /// </summary>
    /// <returns>Görünümler yenilendiyse <see langword="true"/>.</returns>
    internal bool RefreshTimeDependentState(DateTime now)
    {
        bool stateChanged = false;
        foreach (var task in ActiveTasks)
        {
            var before = task.DueState;
            RefreshDueInfo(task, now); // metin bağlama üzerinden güncellenir, liste yenilemesi gerektirmez
            stateChanged |= task.DueState != before; // "BUGÜN" → gecikmiş: akıllı liste üyeliği değişir
        }

        // "Yaklaşan" listesi bugüne göre hesaplanır: gün dönünce üyelik değişebilir.
        var today = DateOnly.FromDateTime(now);
        if (today != _viewsDay)
        {
            _viewsDay = today;
            stateChanged = true;
            _ = CreateDailyBackupAsync(now); // tepside günlerce açık kalsa da her gün bir yedek
        }

        if (stateChanged)
        {
            RefreshViews();
            UpdateQuickAddPreview(); // "@yarın" önizlemesi güne bağlı
        }

        return stateChanged;
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
        SelectedBreaksOption = OptionSets.Find(OptionSets.OnOff, _settings.BreaksEnabled);
        SelectedAutoNextOption = OptionSets.Find(OptionSets.OnOff, _settings.AutoStartNextFocus);
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
            if (task.ReminderMinutesBefore is not { } minutes || task.DueAt is null)
            {
                continue;
            }

            if (task.SnoozedUntil is { } until)
            {
                if (now < until)
                {
                    continue;
                }

                task.SnoozedUntil = null; // ertelenen hatırlatma zamanı geldi
            }
            else if (task.ReminderFired || now < GetReminderMoment(task, minutes))
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

            // Kullanıcı kapatana kadar kalır; 10 dk / 1 sa erteleme düğmeleri.
            Toasts.Add(new ToastItem
            {
                Message = message,
                Kind = ToastKind.Reminder,
                ActionLabel = _loc["Snooze_10"],
                Action = () => SnoozeAsync(task, 10),
                SecondaryActionLabel = _loc["Snooze_60"],
                SecondaryAction = () => SnoozeAsync(task, 60),
            });
            while (Toasts.Count > MaxToasts)
            {
                Toasts.RemoveAt(0);
            }

            AttentionRequested?.Invoke(this, message);
        }

        if (changed)
        {
            _ = SaveAsync();
        }
    }

    /// <summary>Hatırlatmayı <paramref name="minutes"/> dakika sonra yeniden gösterir (görev hâlâ aktifse).</summary>
    private async Task SnoozeAsync(CyberTask task, int minutes)
    {
        if (!ActiveTasks.Contains(task) || task.ReminderMinutesBefore is null)
        {
            return;
        }

        var until = DateTime.Now.AddMinutes(minutes);
        task.SnoozedUntil = until;
        ShowToast(_loc.Format("Toast_Snoozed", task.Code, until.ToString("HH:mm", CultureInfo.InvariantCulture)), ToastKind.Info, lifetime: s_infoLifetime);
        await SaveAsync();
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

        source.SeriesId ??= source.Id; // zincirin ilk halkası: kimlik kendi Id'si
        return new CyberTask
        {
            SeriesId = source.SeriesId,
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

    private ToastItem ShowToast(string message, ToastKind kind, string? actionLabel = null, Func<Task>? action = null, TimeSpan? lifetime = null)
    {
        var toast = new ToastItem
        {
            Message = message,
            Kind = kind,
            ActionLabel = actionLabel,
            Action = action,
            ExpiresAt = lifetime is { } span ? DateTime.Now + span : null,
        };
        Toasts.Add(toast);

        while (Toasts.Count > MaxToasts)
        {
            Toasts.RemoveAt(0);
        }

        return toast;
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
