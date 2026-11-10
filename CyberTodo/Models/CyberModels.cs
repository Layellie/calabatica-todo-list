using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CyberTodo.Models;

/// <summary>Görev önceliği; UI'da renk ve durum etiketini belirler.</summary>
public enum TaskPriority
{
    Active,       // Cyan    -> [AKTİF]
    HighPriority, // Kırmızı -> [YÜKSEK_ÖNCELİK]
    Decrypting,   // Sarı    -> [ÇÖZÜLÜYOR]
}

public enum Recurrence
{
    None,
    Daily,
    Weekly,
    Monthly,
    SelectedDays, // CyberTask.RepeatDays içindeki günlerde (ör. Pzt/Çar/Cum, hafta içi)
}

/// <summary>Haftanın günleri bit maskesi (seçili günlerde tekrar için).</summary>
[Flags]
public enum WeekDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    All = Weekdays | Weekend,
}

/// <summary>Tekrar hesapları; ViewModel ve hızlı ekleme ayrıştırıcısı ortak kullanır.</summary>
public static class RecurrenceRules
{
    /// <summary>Pazartesiden başlayan sıra (UI'da gün çipleri bu sırayla gösterilir).</summary>
    public static IReadOnlyList<DayOfWeek> MondayFirst { get; } =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    public static WeekDays ToFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => WeekDays.Monday,
        DayOfWeek.Tuesday => WeekDays.Tuesday,
        DayOfWeek.Wednesday => WeekDays.Wednesday,
        DayOfWeek.Thursday => WeekDays.Thursday,
        DayOfWeek.Friday => WeekDays.Friday,
        DayOfWeek.Saturday => WeekDays.Saturday,
        _ => WeekDays.Sunday,
    };

    public static bool Contains(WeekDays mask, DayOfWeek day) => (mask & ToFlag(day)) != 0;

    /// <summary>Verilen tarih (dahil) veya sonrasındaki ilk eşleşen gün. Maske boşsa tarih aynen döner.</summary>
    public static DateOnly FirstMatchOnOrAfter(DateOnly date, WeekDays mask)
    {
        if ((mask & WeekDays.All) == 0)
        {
            return date;
        }

        for (int i = 0; i < 7; i++)
        {
            var candidate = date.AddDays(i);
            if (Contains(mask, candidate.DayOfWeek))
            {
                return candidate;
            }
        }

        return date;
    }

    /// <summary>Bir sonraki tekrar zamanı (saat korunur).</summary>
    public static DateTime Advance(DateTime value, Recurrence recurrence, WeekDays days) => recurrence switch
    {
        Recurrence.Daily => value.AddDays(1),
        Recurrence.Weekly => value.AddDays(7),
        Recurrence.Monthly => value.AddMonths(1),
        Recurrence.SelectedDays => FirstMatchOnOrAfter(DateOnly.FromDateTime(value).AddDays(1), days)
            .ToDateTime(TimeOnly.FromDateTime(value)),
        _ => throw new ArgumentOutOfRangeException(nameof(recurrence), recurrence, "Tekrar yok."),
    };
}

/// <summary>Son tarihe göre görünüm durumu (renk için). Kalıcı değil, hesaplanır.</summary>
public enum DueState
{
    None,
    Upcoming,
    Today,
    Overdue,
}

public enum SortMode
{
    Created,
    DueDate,
    Priority,
    Manual, // sürükle-bırak sırası (CyberTask.SortOrder)
}

/// <summary>Sağ alt paneldeki görünüm.</summary>
public enum ChartMode
{
    Last30,
    Last7,
    Stats,
    Calendar,
    Focus,
    Habits,
}

public enum ThemeId
{
    Neon,
    Synthwave,
    Matrix,
    Amber,
    Arctic,
    HighContrast,
}

/// <summary>
/// Tema paleti: Primary = başlık/vurgu (varsayılan sarı), Secondary = çerçeve/ikincil (varsayılan cyan).
/// Kırmızı/yeşil anlam taşıdığı (öncelik/durum) için temadan bağımsız kalır.
/// </summary>
public sealed record ThemePalette(ThemeId Id, string Name, Color Primary, Color Secondary)
{
    /// <summary>Yüksek kontrast: soluk/gri tonlar da tam renge çekilir (erişilebilirlik; Windows yüksek kontrastta otomatik).</summary>
    public bool IsHighContrast => Id == ThemeId.HighContrast;

    public static IReadOnlyList<ThemePalette> All { get; } =
    [
        new(ThemeId.Neon, "NEON", Color.FromRgb(0xFC, 0xEE, 0x0A), Color.FromRgb(0x00, 0xF0, 0xFF)),
        new(ThemeId.Synthwave, "SYNTHWAVE", Color.FromRgb(0xFF, 0x71, 0xCE), Color.FromRgb(0x01, 0xCD, 0xFE)),
        new(ThemeId.Matrix, "MATRIX", Color.FromRgb(0x39, 0xFF, 0x14), Color.FromRgb(0x00, 0xC8, 0x96)),
        new(ThemeId.Amber, "AMBER", Color.FromRgb(0xFF, 0xB0, 0x00), Color.FromRgb(0x00, 0xE5, 0xC7)),
        new(ThemeId.Arctic, "ARCTIC", Color.FromRgb(0xE8, 0xF6, 0xFF), Color.FromRgb(0x4D, 0xA3, 0xFF)),
        new(ThemeId.HighContrast, "HI-CONTRAST", Color.FromRgb(0xFF, 0xFF, 0x00), Color.FromRgb(0xFF, 0xFF, 0xFF)),
    ];

    public static ThemePalette Get(ThemeId id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];
}

/// <summary>Şablondaki tek görev. Tarih, uygulandığı güne göre göreli saklanır.</summary>
public sealed class TemplateItem
{
    public string Title { get; set; } = string.Empty;

    public string? Category { get; set; }

    public string? Notes { get; set; }

    public TaskPriority Priority { get; set; }

    public double EstimatedHours { get; set; } = 1;

    public Recurrence Recurrence { get; set; }

    public WeekDays RepeatDays { get; set; }

    /// <summary>Uygulama gününe göre kaç gün sonra (null = tarihsiz).</summary>
    public int? DueDayOffset { get; set; }

    public TimeOnly? DueTime { get; set; }

    public int? ReminderMinutesBefore { get; set; }

    public List<string> Subtasks { get; set; } = [];
}

public sealed class TaskTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public List<TemplateItem> Items { get; set; } = [];
}

/// <summary>İstatistik kutucuğu.</summary>
public sealed record StatTile(string Label, string Value, string Sub, bool Highlight = false);

/// <summary>Haftanın günlerine göre ortalama çalışma (mini sütun).</summary>
public sealed record WeekdayStat(string Label, string ValueText, GridLength FillHeight, GridLength EmptyHeight, bool IsBest);

/// <summary>Takvim hücresindeki satır (gerçek görev, tekrar izdüşümü veya tamamlanmış).</summary>
public sealed record CalendarItem(string Time, string Title, TaskPriority Priority, bool IsDone, bool IsProjected)
{
    public string Display => string.IsNullOrEmpty(Time) ? Title : $"{Time} {Title}";
}

public sealed partial class CalendarCell : ObservableObject
{
    public CalendarCell(DateOnly date, bool isCurrentMonth, bool isToday, IReadOnlyList<CalendarItem> items, BarIntensity intensity, string hoursText)
    {
        Date = date;
        IsCurrentMonth = isCurrentMonth;
        IsToday = isToday;
        AllItems = items;
        Intensity = intensity;
        HoursText = hoursText;
    }

    public DateOnly Date { get; }

    public int DayNumber => Date.Day;

    public bool IsCurrentMonth { get; }

    public bool IsToday { get; }

    public IReadOnlyList<CalendarItem> AllItems { get; }

    /// <summary>Hücrede gösterilen ilk 2 satır.</summary>
    public IEnumerable<CalendarItem> PreviewItems => AllItems.Take(2);

    public int MoreCount => Math.Max(0, AllItems.Count - 2);

    public BarIntensity Intensity { get; }

    public string HoursText { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Akıllı liste: aktif görevleri son tarih durumuna göre süzer.</summary>
public enum SmartView
{
    All,
    Today,
    Upcoming, // önümüzdeki 7 gün (bugün hariç)
    Overdue,
}

public enum AppLanguage
{
    Turkish,
    English,
}

/// <summary>Aktivite logundaki satırın renkli durum etiketi.</summary>
public enum LogStatus
{
    None,
    Passed,
    Done,
    Failed,
    Focus,
}

/// <summary>Grafik sütununun yoğunluk sınıfı (renk seçimi için).</summary>
public enum BarIntensity
{
    None,   // 0 saat  -> gri
    Low,    // < 3 sa  -> cyan
    Medium, // < 6 sa  -> sarı
    High,   // >= 6 sa -> kırmızı
}

public sealed partial class SubTask : ObservableObject
{
    public string Title { get; set; } = string.Empty;

    [ObservableProperty]
    private bool _isDone;
}

/// <summary>Bir görev. Düzenlenebilen alanlar observable; [JsonIgnore] alanlar sadece görünüm içindir.</summary>
public sealed partial class CyberTask : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int Number { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>Elle (sürükle-bırak) sıralama anahtarı; küçük olan üstte.</summary>
    public double SortOrder { get; set; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private TaskPriority _priority;

    /// <summary>Tamamlanınca o günün çalışma saatine eklenen tahmini süre.</summary>
    [ObservableProperty]
    private double _estimatedHours;

    [ObservableProperty]
    private string? _category;

    /// <summary>Son tarih. HasDueTime=false ise yalnızca tarih kısmı anlamlıdır.</summary>
    [ObservableProperty]
    private DateTime? _dueAt;

    [ObservableProperty]
    private bool _hasDueTime;

    [ObservableProperty]
    private Recurrence _recurrence;

    /// <summary>Recurrence=SelectedDays iken hangi günler.</summary>
    [ObservableProperty]
    private WeekDays _repeatDays;

    /// <summary>null = hatırlatıcı kapalı; 0 = zamanında; 15/60/1440 = o kadar dakika önce.</summary>
    [ObservableProperty]
    private int? _reminderMinutesBefore;

    /// <summary>Hatırlatma gösterildi mi (tekrar tekrar çıkmasın diye kalıcı).</summary>
    [ObservableProperty]
    private bool _reminderFired;

    /// <summary>Ertelenen hatırlatma bu anda tekrar gösterilir (null = erteleme yok).</summary>
    [ObservableProperty]
    private DateTime? _snoozedUntil;

    /// <summary>Tekrarlayan görev tamamlanınca üretilen sonraki kopya; geri alınırsa o kopya da kaldırılır.</summary>
    public Guid? SpawnedNextId { get; set; }

    /// <summary>
    /// Tekrar zinciri kimliği: bir tekrarlayan görevin tüm kopyaları aynı değeri taşır (alışkanlık görünümü).
    /// İlk kopyanın Id'si; tekrarsız görevlerde null.
    /// </summary>
    public Guid? SeriesId { get; set; }

    public ObservableCollection<SubTask> Subtasks { get; set; } = [];

    [ObservableProperty]
    private string? _notes;

    /// <summary>Odak zamanlayıcısıyla bu göreve harcanan toplam dakika (gün grafiğine anında işlenir).</summary>
    [ObservableProperty]
    private int _trackedMinutes;

    /// <summary>
    /// Tamamlanırken güne eklenen saat. Odak süresi kaydedilmişse 0 (süre zaten işlendi), yoksa tahmini süre.
    /// Geri almada tam olarak bu kadar düşülür.
    /// </summary>
    public double CompletionCreditHours { get; set; }

    /// <summary>0-100 arası ilerleme yüzdesi. Alt görev varsa onlardan hesaplanır.</summary>
    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isCompleted;

    [ObservableProperty]
    private DateTime? _completedAt;

    // ───────── Görünüm durumu (kaydedilmez) ─────────

    [JsonIgnore]
    public string Code => $"#{Number:000}";

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isExpanded;

    [ObservableProperty]
    [property: JsonIgnore]
    private string _dueText = string.Empty;

    [ObservableProperty]
    [property: JsonIgnore]
    private DueState _dueState;

    [ObservableProperty]
    [property: JsonIgnore]
    private string _subtaskSummary = string.Empty;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _hasSubtasks;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _hasNotes;

    /// <summary>Satır genişletilebilir mi (not veya alt görev var).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _hasDetails;

    /// <summary>Şu an odak zamanlayıcısı bu görevde mi.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isFocused;

    [ObservableProperty]
    [property: JsonIgnore]
    private string _trackedText = string.Empty;

    /// <summary>Sürüklenen görev bu satırın üstüne bırakılacak (görsel çizgi).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isDropTarget;

    /// <summary>Bırakma hedefi bu satırın ALTI (true) ya da ÜSTÜ (false).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isDropAfter;

    /// <summary>Klavyeyle seçili satır (vurgu).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isSelected;

    /// <summary>Satırdaki tekrar etiketi: "↻ GÜNLÜK", "↻ PZT · ÇAR · CUM".</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private string _recurrenceText = string.Empty;
}

/// <summary>Aktivite logundaki tek satır. Oluşturulduktan sonra değişmez.</summary>
public sealed class ActivityLogEntry
{
    /// <summary>"HH:mm"; aynı format olduğu için string karşılaştırma sıralama için yeterli.</summary>
    public string Time { get; set; } = "00:00";

    public string Description { get; set; } = string.Empty;

    public LogStatus Status { get; set; }

    /// <summary>Bu satırı üreten görev (geri alma işleminde satırı bulmak için).</summary>
    public Guid? TaskId { get; set; }
}

/// <summary>Bir günün aktivite özeti. Saat/sayaç değişimleri log panelini anında günceller.</summary>
public sealed partial class DayActivity : ObservableObject
{
    public DateOnly Date { get; set; }

    [ObservableProperty]
    private double _hoursWorked;

    [ObservableProperty]
    private int _completedCount;

    public ObservableCollection<ActivityLogEntry> Entries { get; set; } = [];
}

public sealed class AppSettings
{
    public AppLanguage Language { get; set; } = AppLanguage.Turkish;

    public const double DefaultUiScale = 0.85;

    /// <summary>Pencere içeriğinin ölçeği (pencere boyutu değil).</summary>
    public double UiScale { get; set; } = DefaultUiScale;

    public bool ReminderSound { get; set; } = true;

    public SortMode SortMode { get; set; } = SortMode.Created;

    /// <summary>Odak (Pomodoro) oturumu uzunluğu, dakika.</summary>
    public int FocusMinutes { get; set; } = 25;

    public ThemeId Theme { get; set; } = ThemeId.Neon;

    /// <summary>Kapat (✕) uygulamayı sonlandırmaz, sistem tepsisine gizler; hatırlatıcılar çalışmaya devam eder.</summary>
    public bool MinimizeToTray { get; set; } = true;

    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Odak bitince mola zamanlayıcısı (5 dk; her 4 odakta bir 15 dk).</summary>
    public bool BreaksEnabled { get; set; } = true;

    /// <summary>Mola bitince aynı görevde yeni odak otomatik başlasın.</summary>
    public bool AutoStartNextFocus { get; set; }

    /// <summary>İlk açılış rehberi gösterildi/kapatıldı.</summary>
    public bool OnboardingDone { get; set; }

    /// <summary>Mini pencerenin son konumu (null = ekranın sağ üstü).</summary>
    public double? MiniLeft { get; set; }

    public double? MiniTop { get; set; }
}

/// <summary>Alışkanlık şeridindeki tek gün.</summary>
public enum HabitCellState
{
    Off,     // planlı değil / seri başlamadan önce
    Done,    // planlı ve yapıldı
    Missed,  // planlı, geçti, yapılmadı
    Pending, // bugün planlı, henüz yapılmadı
}

public sealed record HabitCell(DateOnly Date, HabitCellState State, string Tip);

/// <summary>Alışkanlık görünümünde bir tekrarlayan görev zinciri.</summary>
public sealed record HabitRow(
    string Title,
    string RecurrenceText,
    int Streak,
    string StreakText,
    string BestText,
    string RateText,
    IReadOnlyList<HabitCell> Cells)
{
    public bool IsHot => Streak > 0;
}

/// <summary>
/// Tek odak (Pomodoro) oturumu. Görev silinse/yeniden adlandırılsa da istatistik doğru kalsın diye
/// başlık ve kategori oturum anındaki hâliyle saklanır.
/// </summary>
public sealed class FocusSession
{
    public DateTime Start { get; set; }

    public int Minutes { get; set; }

    public Guid TaskId { get; set; }

    public int TaskNumber { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Category { get; set; }
}

/// <summary>Odak istatistiğinde yatay çubuk satırı (kategori ya da görev).</summary>
public sealed record FocusSlice(string Label, string ValueText, GridLength FillWidth, GridLength EmptyWidth, bool IsTop);

/// <summary>Ayarlardaki yedek listesi satırı.</summary>
public sealed record BackupInfo(string FullPath, string FileName, DateTime CreatedAt, long SizeBytes, bool IsDaily)
{
    public string SizeText => SizeBytes < 1024 * 1024 ? $"{SizeBytes / 1024.0:0.#} KB" : $"{SizeBytes / 1024.0 / 1024.0:0.#} MB";
}

/// <summary>JSON dosyasının kök nesnesi.</summary>
public sealed class CyberData
{
    /// <summary>v4: odak oturumları (FocusSessions). v5: tekrar zinciri kimliği (CyberTask.SeriesId).</summary>
    public const int CurrentSchemaVersion = 5;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public int NextTaskNumber { get; set; } = 1;

    public AppSettings Settings { get; set; } = new();

    public List<CyberTask> Tasks { get; set; } = [];

    public List<DayActivity> Days { get; set; } = [];

    public List<TaskTemplate> Templates { get; set; } = [];

    public List<FocusSession> FocusSessions { get; set; } = [];

    public List<SavedFilter> SavedFilters { get; set; } = [];

    /// <summary>Arşive taşınan tamamlanmış görevlerin özeti (toplam istatistikler arşivden sonra da doğru kalsın).</summary>
    public ArchiveSummary Archive { get; set; } = new();
}

/// <summary>Kayıtlı görünüm: arama + kategori + akıllı liste birleşimi ("#iş · BUGÜN").</summary>
public sealed partial class SavedFilter : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string? Search { get; set; }

    public string? Category { get; set; }

    public SmartView SmartView { get; set; }

    /// <summary>Şu anki filtre bununla aynı mı (çip vurgusu).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isActive;
}

public sealed class ArchiveSummary
{
    public int TaskCount { get; set; }

    /// <summary>Arşivlenen görevlerin odak dakikaları toplamı.</summary>
    public int TrackedMinutes { get; set; }
}

/// <summary>
/// Grafikteki tek sütun. Yükseklik, iki satırlı Grid'in yıldız (star) oranlarıyla verilir;
/// böylece converter veya piksel hesabı olmadan grafik pencereyle birlikte ölçeklenir.
/// </summary>
public sealed partial class ChartBar : ObservableObject
{
    public ChartBar(DayActivity day, string label, double fillRatio, BarIntensity intensity, bool showLabel, bool isToday)
    {
        ArgumentNullException.ThrowIfNull(day);

        double ratio = double.IsFinite(fillRatio) ? Math.Clamp(fillRatio, 0, 1) : 0;
        Day = day;
        Label = label;
        Intensity = intensity;
        ShowLabel = showLabel;
        IsToday = isToday;
        FillHeight = new GridLength(ratio, GridUnitType.Star);
        EmptyHeight = new GridLength(1 - ratio, GridUnitType.Star);
    }

    public DayActivity Day { get; }

    public string Label { get; }

    public BarIntensity Intensity { get; }

    public bool ShowLabel { get; }

    public bool IsToday { get; }

    public GridLength FillHeight { get; }

    public GridLength EmptyHeight { get; }

    /// <summary>Sadece fare sütunun üzerindeyken true (nişangâh + beyaz çerçeve).</summary>
    [ObservableProperty]
    private bool _isHovered;
}

public enum ToastKind
{
    Info,
    Reminder,
}

/// <summary>Sağ alttaki bildirim. ExpiresAt null ise kullanıcı kapatana kadar kalır.</summary>
public sealed class ToastItem
{
    public required string Message { get; init; }

    public ToastKind Kind { get; init; }

    public string? ActionLabel { get; init; }

    public Func<Task>? Action { get; init; }

    public DateTime? ExpiresAt { get; init; }

    public bool HasAction => ActionLabel is not null && Action is not null;

    /// <summary>İkinci düğme (ör. hatırlatmada "1 SA" erteleme).</summary>
    public string? SecondaryActionLabel { get; init; }

    public Func<Task>? SecondaryAction { get; init; }

    public bool HasSecondaryAction => SecondaryActionLabel is not null && SecondaryAction is not null;
}
