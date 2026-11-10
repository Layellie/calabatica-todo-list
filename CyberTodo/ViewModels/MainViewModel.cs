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

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Localizer _loc = Localizer.Instance;
    private readonly StorageService _storage;
    private readonly UpdateService _updates;
    private readonly IStartupRegistration _startup;
    private readonly TimeProvider _time;
    private readonly BackupService _backups;
    private readonly ArchiveService _archive;
    private readonly IFileDialogs _dialogs;
    private readonly UpdateInstaller? _installer;
    private readonly Dictionary<DateOnly, DayActivity> _daysByDate = new();

    private AppSettings _settings = new();
    private bool _initialized;

    /// <param name="updates">null → depo ayarsız (ağa çıkmaz); testler için.</param>
    /// <param name="startup">null → bellekte (kayıt defterine dokunmaz); testler için.</param>
    /// <param name="dialogs">null → hiçbir dosya seçilmez/onaylanmaz; testler sahte verir.</param>
    /// <param name="installer">null → kendini güncelleme kapalı (yalnızca yayın sayfası açılır).</param>
    /// <param name="time">null → sistem saati; testler elle ilerletilen saat verir.</param>
    public MainViewModel(
        StorageService storage,
        UpdateService? updates = null,
        IStartupRegistration? startup = null,
        IFileDialogs? dialogs = null,
        UpdateInstaller? installer = null,
        TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _updates = updates ?? new UpdateService(null, UpdateService.ReadCurrentVersion());
        _startup = startup ?? new InMemoryStartupRegistration();
        _backups = new BackupService(_storage.FilePath);
        _archive = new ArchiveService(_storage.FilePath);
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
        _calendarMonth = new DateOnly(Now.Date.Year, Now.Date.Month, 1);

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
        _undo.Changed += OnUndoStackChanged;
    }

    /// <summary>
    /// Kullanıcının dikkati gerekiyor (hatırlatma / odak bitti). Argüman: bildirim metni.
    /// View: görev çubuğunu yakar, ses çalar; pencere gizliyse Windows bildirimi gösterir.
    /// </summary>
    public event EventHandler<string>? AttentionRequested;

    public BulkObservableCollection<CyberTask> ActiveTasks { get; } = [];

    public BulkObservableCollection<CyberTask> CompletedTasks { get; } = [];

    /// <summary>Arama + kategori + akıllı liste filtresi ve sıralama uygulanmış görünüm.</summary>
    public ListCollectionView ActiveView { get; }

    public ListCollectionView CompletedView { get; }

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Yerel "şimdi" — tek kaynak: testler sahte saat verir, gece yarısı / mola / erteleme deterministik test edilir.</summary>
    private DateTime Now => _time.GetLocalNow().DateTime;

    private DateOnly Today => DateOnly.FromDateTime(Now);

    /// <summary>Tek seferde tek panel: düzenleyici, ayarlar, şablonlar ve veri paneli birbirini kapatır (üst üste binmesin).</summary>
    private void CloseOverlays()
    {
        Editor = null;
        IsSettingsOpen = false;
        IsTemplatesOpen = false;
        IsDataOpen = false;
        IsOnboardingOpen = false;
    }

    // ═════════════════════════════ Yaşam döngüsü ═════════════════════════════

    private Task<LoadResult>? _preload;

    /// <summary>Veri yüklendi ve arayüze işlendi (açılış tamam).</summary>
    public bool IsInitialized => _initialized;

    /// <summary>
    /// Veri dosyasını arka planda okumaya başlar; <see cref="InitializeAsync"/> sonucu bekler. Pencere XAML'i kurulurken
    /// (~430 ms) JSON yüklemesi (~145 ms, ilk çağrıda yansıma kurulumu dahil) paralel biter. Pencereden ÖNCE çağrılır;
    /// çağrılmazsa InitializeAsync dosyayı kendisi okur (davranış aynı). Birden çok çağrı tek okuma yapar.
    /// </summary>
    public void BeginPreload() => _preload ??= Task.Run(() => _storage.LoadAsync());

    public async Task InitializeAsync()    {
        if (_initialized)
        {
            return;
        }

        CyberData data;
        bool isNew = false;
        try
        {
            var result = await (_preload ?? _storage.LoadAsync());
            _preload = null;
            StartupTrace.Mark("json-loaded");
            data = result.Data;
            isNew = result.IsNew;
            if (result.CorruptBackupFile is not null)
            {
                ErrorMessage = _loc.Format("Err_LoadCorrupt", result.CorruptBackupFile);
            }

            if (result.NewerSchemaVersion is { } newer)
            {
                // Yeni sürümün dosyası: kaydetmek bilinmeyen alanları silerdi. Salt okunur çalış, kullanıcıya söyle.
                _persistenceDisabled = true;
                ErrorMessage = _loc.Format("Err_NewerData", newer);
                AppLog.Warn("Load", $"data schema {newer} is newer than {CyberData.CurrentSchemaVersion}; saving disabled");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("MainViewModel", ex);
            // Dosya okunamıyor (kilitli/yetki). Boş veriyle devam et ama kaydetme:
            // aksi halde kullanıcının gerçek verisinin üzerine boş veri yazılırdı.
            data = new CyberData();
            _persistenceDisabled = true;
            ErrorMessage = _loc.Format("Err_LoadFailed", ex.Message);
        }

        _settings = data.Settings;
        ApplySettingsToUi();

        var now = Now;
        ApplyData(data, now);

        _initialized = true;
        OnTasksChanged();
        RebuildChart();
        StartupTrace.Mark("data-applied");

        ReadStartupState();

        _lastClockMinute = now.Minute;
        _viewsDay = DateOnly.FromDateTime(now); // görünümler az önce bu gün için kuruldu
        _clock.Start();
        CheckReminders(now); // uygulama kapalıyken kaçırılan hatırlatmalar hemen gösterilir

        if (isNew && !_settings.OnboardingDone)
        {
            OpenOnboarding(); // ilk kurulum: kısa rehber
        }

        if (!isNew)
        {
            _ = CreateDailyBackupAsync(now); // yüklenen hâlin günlük yedeği (anlık görüntü senkron alınır); hata UI'a yansır
            _ = ArchiveExpiredTasksAsync(now); // yedekten SONRA: arşivlenen görevler bugünün yedeğinde de durur
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
        _archiveSummary = data.Archive;
        OnPropertyChanged(nameof(ArchiveText));

        _daysByDate.Clear();
        foreach (var day in data.Days)
        {
            _daysByDate[day.Date] = day; // aynı tarih iki kez varsa sonuncusu geçerli
        }

        SavedFilters.Clear();
        foreach (var filter in data.SavedFilters)
        {
            SavedFilters.Add(filter);
        }

        OnSavedFiltersChanged();

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
        if (StopFocusCore(Now))
        {
            await SaveAsync();
        }

        // Ertelenmiş kayıt varsa beklemeden yaz, sonra kuyruktaki yazmaların bitmesini bekle.
        _saveTimer.Stop();
        await SaveNowAsync();
        await _storage.WhenIdleAsync();
    }

    [RelayCommand]
    private void DismissError()
    {
        ErrorMessage = null;
        _errorIsFromSave = false;
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
        else if (IsOnboardingOpen)
        {
            CloseOnboarding();
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
}
