using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using CommunityToolkit.Mvvm.ComponentModel;
using CyberTodo.Models;

namespace CyberTodo.Services;

/// <summary>
/// Basit, bağımlılıksız TR/EN yerelleştirme. XAML'de <c>{loc:Tr Anahtar}</c> ile bağlanır;
/// dil değişince "Item[]" bildirimi tüm indexer binding'lerini tek seferde yeniler.
/// </summary>
public sealed class Localizer : ObservableObject
{
    private static readonly CultureInfo s_trCulture = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly CultureInfo s_enCulture = CultureInfo.GetCultureInfo("en-US");

    private static readonly Dictionary<string, string> s_tr = new()
    {
        ["Tasks_Header"] = "GÜNLÜK_GÖREVLER",
        ["Completed_Header"] = "TAMAMLANANLAR",
        ["Chart_Header"] = "GEÇMİŞ_VERİLER",
        ["Log_Title"] = "AKTİVİTE LOGU",
        ["Log_Summary"] = "[ DURUM: {0} GÖREV TAMAMLANDI // {1} SAAT ]",
        ["Tab_30"] = "SON 30 GÜN",
        ["Tab_7"] = "SON 7 GÜN",
        ["Tab_Stats"] = "İSTATİSTİK",
        ["Tab_Calendar"] = "TAKVİM",
        ["Sort_Manual"] = "ELLE",
        ["Tip_Drag"] = "Sürükleyerek sırala",

        ["Stat_Streak"] = "GÜNCEL SERİ",
        ["Stat_StreakSub"] = "her gün ≥1 görev",
        ["Stat_BestStreak"] = "EN UZUN SERİ",
        ["Stat_Week"] = "SON 7 GÜN",
        ["Stat_Total"] = "TOPLAM",
        ["Stat_Focus"] = "ODAK SÜRESİ",
        ["Stat_BestHour"] = "EN VERİMLİ SAAT",
        ["Stat_Days"] = "{0} gün",
        ["Stat_TasksHours"] = "{0} görev · {1} sa",
        ["Stat_Delta"] = "önceki 7 güne göre {0}",
        ["Stat_FocusToday"] = "bugün {0}",
        ["Stat_Entries"] = "{0} kayıt",
        ["Stat_None"] = "—",
        ["Stat_Weekdays"] = "GÜNLERE GÖRE ORTALAMA SAAT (SON 8 HAFTA)",

        ["Cal_More"] = "+{0} daha",
        ["Cal_Empty"] = "// BU GÜN İÇİN GÖREV YOK",
        ["Tip_PrevMonth"] = "Önceki ay",
        ["Tip_NextMonth"] = "Sonraki ay",

        ["Templates_Title"] = "ŞABLONLAR",
        ["Templates_Button"] = "⧉ ŞABLONLAR",
        ["Template_NameHint"] = "şablon adı (ör. Sabah rutini)",
        ["Template_Create"] = "GÖRÜNEN GÖREVLERDEN OLUŞTUR",
        ["Template_Apply"] = "UYGULA",
        ["Template_Items"] = "{0} görev",
        ["Templates_Empty"] = "// HENÜZ ŞABLON YOK",
        ["Template_Help"] = "Listeyi arama/kategori/akıllı liste ile süz, sonra görünen aktif görevleri şablon olarak kaydet. Başlık, kategori, öncelik, süre, not, alt görev, tekrar ve (bugüne göre) tarih saklanır.",
        ["Toast_TemplateApplied"] = "⧉ {0}: {1} görev eklendi",
        ["Toast_TemplateSaved"] = "⧉ Şablon kaydedildi: {0}",

        ["Settings_Theme"] = "TEMA RENKLERİ",
        ["Settings_Tray"] = "KAPATINCA TEPSİYE KÜÇÜLT",
        ["Settings_Startup"] = "WINDOWS İLE BAŞLAT",
        ["Settings_Updates"] = "GÜNCELLEMELER",
        ["Settings_AutoUpdate"] = "AÇILIŞTA DENETLE",
        ["Update_Current"] = "Kurulu sürüm: {0}",
        ["Update_Check"] = "DENETLE",
        ["Update_Open"] = "SAYFAYI AÇ",
        ["Update_NotConfigured"] = "GitHub deposu henüz ayarlanmadı (yayın derlemesinde otomatik gelir).",
        ["Update_Checking"] = "Denetleniyor…",
        ["Update_UpToDate"] = "Güncel ({0})",
        ["Update_Available"] = "Yeni sürüm mevcut: {0}",
        ["Update_Failed"] = "Denetlenemedi: {0}",
        ["Tip_UpdateAvailable"] = "Yeni sürüm mevcut — yayın sayfasını aç",
        ["Err_Startup"] = "WINDOWS BAŞLANGIÇ AYARI DEĞİŞTİRİLEMEDİ: {0}",

        ["Tray_Show"] = "Göster",
        ["Tray_Exit"] = "Çıkış",
        ["Tray_Running"] = "Arka planda çalışıyor. Çıkmak için simgeye sağ tıkla → Çıkış.",

        ["Add_TitleLabel"] = "> YENİ GÖREV",
        ["Add_TitleHint"] = "örn: Rapor yaz #iş @yarın @14:00 ! *haftalık ~2",
        ["QuickAdd_Help"] = "Hızlı ekleme komutları:\n#kategori  →  kategori\n!  →  yüksek öncelik\n@bugün @yarın @cuma @25.11 @14:00  →  son tarih / saat\n*günlük *haftalık *aylık  →  tekrar\n*haftaiçi *haftasonu *pzt,çar,cum  →  seçili günlerde tekrar\n~1.5  →  tahmini süre (saat)",
        ["Preview_Prefix"] = "→ ",
        ["Fmt_Minutes"] = "{0} dk",
        ["Unit_Hours"] = "sa",
        ["Fmt_HoursMinutes"] = "{0} sa {1} dk",

        ["Smart_All"] = "TÜMÜ",
        ["Smart_Today"] = "BUGÜN",
        ["Smart_Upcoming"] = "YAKLAŞAN",
        ["Smart_Overdue"] = "GECİKMİŞ",

        ["Field_Notes"] = "NOTLAR",
        ["Notes_Hint"] = "ayrıntılar, bağlantılar, notlar…",

        ["Focus_Label"] = "ODAK",
        ["Tip_Focus"] = "Odak zamanlayıcısını başlat (Pomodoro)",
        ["Tip_StopFocus"] = "Odağı durdur (geçen süre kaydedilir)",
        ["Toast_FocusDone"] = "◉ ODAK TAMAMLANDI: {0} ({1})",
        ["Settings_Focus"] = "ODAK SÜRESİ (DK)",
        ["Settings_Shortcuts"] = "KLAVYE KISAYOLLARI",
        ["Shortcuts_Text"] = "Ctrl+N  yeni görev     Ctrl+F  ara\nCtrl+Z  silmeyi geri al     Ctrl+,  ayarlar\nEnter  ekle / kaydet     Esc  kapat / aramayı temizle",
        ["Add_HoursLabel"] = "SÜRE (SA)",
        ["Add_CategoryLabel"] = "KATEGORİ",
        ["Add_CategoryHint"] = "opsiyonel",
        ["Add_Button"] = "+ EKLE",

        ["Prio_Active"] = "[AKTİF]",
        ["Prio_High"] = "[YÜKSEK_ÖNCELİK]",
        ["Prio_Decrypt"] = "[ÇÖZÜLÜYOR]",

        ["Search_Hint"] = "ara: başlık, kategori, alt görev…",
        ["Sort_Label"] = "SIRALA:",
        ["Sort_Created"] = "EKLENME",
        ["Sort_Due"] = "SON TARİH",
        ["Sort_Priority"] = "ÖNCELİK",
        ["Filter_All"] = "# TÜM KATEGORİLER",

        ["Empty_Active"] = "// AKTİF GÖREV YOK",
        ["Empty_Log"] = "// KAYITLI AKTİVİTE YOK",

        ["Log_Done"] = " [TAMAMLANDI]",
        ["Log_Passed"] = " [GEÇTİ]",
        ["Log_Failed"] = " [BAŞARISIZ]",
        ["Log_Focus"] = " [ODAK]",

        ["Due_Today"] = "BUGÜN",
        ["Due_Tomorrow"] = "YARIN",
        ["Fmt_ShortDate"] = "d MMM",
        ["Fmt_LongDate"] = "d MMMM",

        ["Tip_Complete"] = "Görevi tamamla",
        ["Tip_Restore"] = "Geri al (aktif listeye taşı)",
        ["Tip_Edit"] = "Düzenle",
        ["Tip_Delete"] = "Sil",
        ["Tip_Progress"] = "Tıklayarak ilerlemeyi ayarla (%5 adım). Alt görev varsa otomatik hesaplanır.",
        ["Tip_Subtasks"] = "Notları / alt görevleri göster / gizle",
        ["Tip_Settings"] = "Ayarlar",
        ["Tip_Minimize"] = "Küçült",
        ["Tip_Close"] = "Kapat",
        ["Tip_Dismiss"] = "Kapat",

        ["Settings_Title"] = "AYARLAR",
        ["Settings_Language"] = "DİL",
        ["Settings_Scale"] = "ARAYÜZ ÖLÇEĞİ",
        ["Settings_Sound"] = "HATIRLATICI SESİ",
        ["Settings_DataPath"] = "VERİ DOSYASI",
        ["Opt_On"] = "AÇIK",
        ["Opt_Off"] = "KAPALI",
        ["Btn_Close"] = "KAPAT",

        ["Edit_Title"] = "GÖREVİ DÜZENLE",
        ["Field_Title"] = "BAŞLIK",
        ["Field_Category"] = "KATEGORİ",
        ["Field_Priority"] = "ÖNCELİK",
        ["Field_Hours"] = "TAHMİNİ SÜRE (SA)",
        ["Field_DueDate"] = "SON TARİH (GG.AA.YYYY)",
        ["Field_DueTime"] = "SAAT (SS:DD)",
        ["Field_Recurrence"] = "TEKRAR",
        ["Field_Reminder"] = "HATIRLATICI",
        ["Field_Subtasks"] = "ALT GÖREVLER",
        ["Subtask_Hint"] = "alt görev yaz, Enter ile ekle…",
        ["Quick_Today"] = "BUGÜN",
        ["Quick_Tomorrow"] = "YARIN",
        ["Quick_NextWeek"] = "+1 HAFTA",
        ["Quick_Clear"] = "TEMİZLE",
        ["Rec_None"] = "YOK",
        ["Rec_Daily"] = "GÜNLÜK",
        ["Rec_Weekly"] = "HAFTALIK",
        ["Rec_Monthly"] = "AYLIK",
        ["Rec_Days"] = "SEÇİLİ GÜNLER",
        ["Rec_DaysLabel"] = "HANGİ GÜNLER?",
        ["Rec_Weekdays"] = "HAFTA İÇİ",
        ["Rec_Weekend"] = "HAFTA SONU",
        ["Rec_AllDays"] = "HER GÜN",
        ["Rec_Note"] = "Son tarih boşsa ilk uygun gün (bugün dahil) otomatik atanır. Tamamlayınca sıradaki gün oluşur.",
        ["Err_DaysRequired"] = "Seçili günler için en az bir gün işaretle.",
        ["Rem_Off"] = "KAPALI",
        ["Rem_AtTime"] = "ZAMANINDA",
        ["Rem_15"] = "15 DK ÖNCE",
        ["Rem_60"] = "1 SA ÖNCE",
        ["Rem_1440"] = "1 GÜN ÖNCE",
        ["Edit_Save"] = "KAYDET",
        ["Edit_Cancel"] = "İPTAL",
        ["Edit_ReminderNote"] = "Saat girilmezse: son tarih gün sonu (23:59), hatırlatma 09:00 kabul edilir.",

        ["Err_Title"] = "Başlık boş olamaz.",
        ["Err_Hours"] = "Süre 0'dan büyük, en fazla 24 olmalı.",
        ["Err_Date"] = "Tarih geçersiz (GG.AA.YYYY).",
        ["Err_Time"] = "Saat geçersiz (SS:DD).",
        ["Err_TimeNeedsDate"] = "Saat için son tarih de girilmeli.",
        ["Err_ReminderNeedsDate"] = "Hatırlatıcı için son tarih gerekli.",
        ["Err_LoadCorrupt"] = "VERİ DOSYASI BOZUK → YEDEKLENDİ: {0}",
        ["Err_LoadFailed"] = "VERİ OKUNAMADI, KAYIT DEVRE DIŞI: {0}",
        ["Err_SaveFailed"] = "KAYIT BAŞARISIZ: {0}",

        ["Toast_Deleted"] = "{0} silindi",
        ["Toast_Undo"] = "GERİ AL",
        ["Toast_Reminder"] = "⏰ HATIRLATMA: {0} {1}",
        ["Toast_Recurring"] = "↻ Sonraki tekrar eklendi: {0}",
        ["Toast_Ok"] = "TAMAM",
    };

    private static readonly Dictionary<string, string> s_en = new()
    {
        ["Tasks_Header"] = "DAILY_OBJECTIVES",
        ["Completed_Header"] = "COMPLETED",
        ["Chart_Header"] = "CHRONO_ACTIVITY",
        ["Log_Title"] = "ACTIVITY LOG",
        ["Log_Summary"] = "[ STATUS: {0} OBJECTIVES COMPLETED // {1} HOURS ]",
        ["Tab_30"] = "LAST 30 DAYS",
        ["Tab_7"] = "LAST 7 DAYS",
        ["Tab_Stats"] = "STATS",
        ["Tab_Calendar"] = "CALENDAR",
        ["Sort_Manual"] = "MANUAL",
        ["Tip_Drag"] = "Drag to reorder",

        ["Stat_Streak"] = "CURRENT STREAK",
        ["Stat_StreakSub"] = "≥1 task every day",
        ["Stat_BestStreak"] = "LONGEST STREAK",
        ["Stat_Week"] = "LAST 7 DAYS",
        ["Stat_Total"] = "ALL TIME",
        ["Stat_Focus"] = "FOCUS TIME",
        ["Stat_BestHour"] = "PEAK HOUR",
        ["Stat_Days"] = "{0} days",
        ["Stat_TasksHours"] = "{0} tasks · {1} h",
        ["Stat_Delta"] = "vs previous 7 days: {0}",
        ["Stat_FocusToday"] = "today {0}",
        ["Stat_Entries"] = "{0} entries",
        ["Stat_None"] = "—",
        ["Stat_Weekdays"] = "AVERAGE HOURS BY WEEKDAY (LAST 8 WEEKS)",

        ["Cal_More"] = "+{0} more",
        ["Cal_Empty"] = "// NO TASKS FOR THIS DAY",
        ["Tip_PrevMonth"] = "Previous month",
        ["Tip_NextMonth"] = "Next month",

        ["Templates_Title"] = "TEMPLATES",
        ["Templates_Button"] = "⧉ TEMPLATES",
        ["Template_NameHint"] = "template name (e.g. Morning routine)",
        ["Template_Create"] = "CREATE FROM VISIBLE TASKS",
        ["Template_Apply"] = "APPLY",
        ["Template_Items"] = "{0} tasks",
        ["Templates_Empty"] = "// NO TEMPLATES YET",
        ["Template_Help"] = "Filter the list with search/category/smart list, then save the visible active tasks as a template. Title, category, priority, hours, notes, subtasks, repeat and (relative) dates are kept.",
        ["Toast_TemplateApplied"] = "⧉ {0}: {1} tasks added",
        ["Toast_TemplateSaved"] = "⧉ Template saved: {0}",

        ["Settings_Theme"] = "THEME COLORS",
        ["Settings_Tray"] = "CLOSE TO TRAY",
        ["Settings_Startup"] = "START WITH WINDOWS",
        ["Settings_Updates"] = "UPDATES",
        ["Settings_AutoUpdate"] = "CHECK ON STARTUP",
        ["Update_Current"] = "Installed version: {0}",
        ["Update_Check"] = "CHECK",
        ["Update_Open"] = "OPEN PAGE",
        ["Update_NotConfigured"] = "GitHub repository not configured yet (set automatically in release builds).",
        ["Update_Checking"] = "Checking…",
        ["Update_UpToDate"] = "Up to date ({0})",
        ["Update_Available"] = "New version available: {0}",
        ["Update_Failed"] = "Check failed: {0}",
        ["Tip_UpdateAvailable"] = "New version available — open release page",
        ["Err_Startup"] = "COULD NOT CHANGE WINDOWS STARTUP SETTING: {0}",

        ["Tray_Show"] = "Show",
        ["Tray_Exit"] = "Exit",
        ["Tray_Running"] = "Still running in the background. Right-click the icon → Exit to quit.",

        ["Add_TitleLabel"] = "> NEW_OBJECTIVE",
        ["Add_TitleHint"] = "e.g. Write report #work @tomorrow @14:00 ! *weekly ~2",
        ["QuickAdd_Help"] = "Quick add commands:\n#category  →  category\n!  →  high priority\n@today @tomorrow @friday @25.11 @14:00  →  due date / time\n*daily *weekly *monthly  →  repeat\n*weekdays *weekend *mon,wed,fri  →  repeat on selected days\n~1.5  →  estimated hours",
        ["Preview_Prefix"] = "→ ",
        ["Fmt_Minutes"] = "{0} min",
        ["Unit_Hours"] = "h",
        ["Fmt_HoursMinutes"] = "{0} h {1} min",

        ["Smart_All"] = "ALL",
        ["Smart_Today"] = "TODAY",
        ["Smart_Upcoming"] = "UPCOMING",
        ["Smart_Overdue"] = "OVERDUE",

        ["Field_Notes"] = "NOTES",
        ["Notes_Hint"] = "details, links, notes…",

        ["Focus_Label"] = "FOCUS",
        ["Tip_Focus"] = "Start focus timer (Pomodoro)",
        ["Tip_StopFocus"] = "Stop focus (elapsed time is logged)",
        ["Toast_FocusDone"] = "◉ FOCUS COMPLETE: {0} ({1})",
        ["Settings_Focus"] = "FOCUS LENGTH (MIN)",
        ["Settings_Shortcuts"] = "KEYBOARD SHORTCUTS",
        ["Shortcuts_Text"] = "Ctrl+N  new task     Ctrl+F  search\nCtrl+Z  undo delete     Ctrl+,  settings\nEnter  add / save     Esc  close / clear search",
        ["Add_HoursLabel"] = "HOURS",
        ["Add_CategoryLabel"] = "CATEGORY",
        ["Add_CategoryHint"] = "optional",
        ["Add_Button"] = "+ ADD",

        ["Prio_Active"] = "[ACTIVE]",
        ["Prio_High"] = "[HIGH_PRIORITY]",
        ["Prio_Decrypt"] = "[DECRYPTING]",

        ["Search_Hint"] = "search: title, category, subtask…",
        ["Sort_Label"] = "SORT:",
        ["Sort_Created"] = "CREATED",
        ["Sort_Due"] = "DUE DATE",
        ["Sort_Priority"] = "PRIORITY",
        ["Filter_All"] = "# ALL CATEGORIES",

        ["Empty_Active"] = "// NO ACTIVE OBJECTIVES",
        ["Empty_Log"] = "// NO ACTIVITY RECORDED",

        ["Log_Done"] = " [DONE]",
        ["Log_Passed"] = " [PASSED]",
        ["Log_Failed"] = " [FAILED]",
        ["Log_Focus"] = " [FOCUS]",

        ["Due_Today"] = "TODAY",
        ["Due_Tomorrow"] = "TOMORROW",
        ["Fmt_ShortDate"] = "MMM d",
        ["Fmt_LongDate"] = "MMMM d",

        ["Tip_Complete"] = "Complete task",
        ["Tip_Restore"] = "Restore (move back to active)",
        ["Tip_Edit"] = "Edit",
        ["Tip_Delete"] = "Delete",
        ["Tip_Progress"] = "Click to set progress (5% steps). Calculated automatically when subtasks exist.",
        ["Tip_Subtasks"] = "Show / hide notes and subtasks",
        ["Tip_Settings"] = "Settings",
        ["Tip_Minimize"] = "Minimize",
        ["Tip_Close"] = "Close",
        ["Tip_Dismiss"] = "Dismiss",

        ["Settings_Title"] = "SETTINGS",
        ["Settings_Language"] = "LANGUAGE",
        ["Settings_Scale"] = "INTERFACE SCALE",
        ["Settings_Sound"] = "REMINDER SOUND",
        ["Settings_DataPath"] = "DATA FILE",
        ["Opt_On"] = "ON",
        ["Opt_Off"] = "OFF",
        ["Btn_Close"] = "CLOSE",

        ["Edit_Title"] = "EDIT OBJECTIVE",
        ["Field_Title"] = "TITLE",
        ["Field_Category"] = "CATEGORY",
        ["Field_Priority"] = "PRIORITY",
        ["Field_Hours"] = "ESTIMATED HOURS",
        ["Field_DueDate"] = "DUE DATE (DD.MM.YYYY)",
        ["Field_DueTime"] = "TIME (HH:MM)",
        ["Field_Recurrence"] = "REPEAT",
        ["Field_Reminder"] = "REMINDER",
        ["Field_Subtasks"] = "SUBTASKS",
        ["Subtask_Hint"] = "type a subtask, press Enter to add…",
        ["Quick_Today"] = "TODAY",
        ["Quick_Tomorrow"] = "TOMORROW",
        ["Quick_NextWeek"] = "+1 WEEK",
        ["Quick_Clear"] = "CLEAR",
        ["Rec_None"] = "NONE",
        ["Rec_Daily"] = "DAILY",
        ["Rec_Weekly"] = "WEEKLY",
        ["Rec_Monthly"] = "MONTHLY",
        ["Rec_Days"] = "SELECTED DAYS",
        ["Rec_DaysLabel"] = "WHICH DAYS?",
        ["Rec_Weekdays"] = "WEEKDAYS",
        ["Rec_Weekend"] = "WEEKEND",
        ["Rec_AllDays"] = "EVERY DAY",
        ["Rec_Note"] = "If no due date is set, the first matching day (including today) is used. Completing creates the next one.",
        ["Err_DaysRequired"] = "Pick at least one day for selected-days repeat.",
        ["Rem_Off"] = "OFF",
        ["Rem_AtTime"] = "ON TIME",
        ["Rem_15"] = "15 MIN BEFORE",
        ["Rem_60"] = "1 H BEFORE",
        ["Rem_1440"] = "1 DAY BEFORE",
        ["Edit_Save"] = "SAVE",
        ["Edit_Cancel"] = "CANCEL",
        ["Edit_ReminderNote"] = "Without a time: due at end of day (23:59), reminder at 09:00.",

        ["Err_Title"] = "Title cannot be empty.",
        ["Err_Hours"] = "Hours must be greater than 0 and at most 24.",
        ["Err_Date"] = "Invalid date (DD.MM.YYYY).",
        ["Err_Time"] = "Invalid time (HH:MM).",
        ["Err_TimeNeedsDate"] = "A time requires a due date.",
        ["Err_ReminderNeedsDate"] = "A reminder requires a due date.",
        ["Err_LoadCorrupt"] = "DATA FILE CORRUPT → BACKED UP: {0}",
        ["Err_LoadFailed"] = "COULD NOT READ DATA, SAVING DISABLED: {0}",
        ["Err_SaveFailed"] = "SAVE FAILED: {0}",

        ["Toast_Deleted"] = "{0} deleted",
        ["Toast_Undo"] = "UNDO",
        ["Toast_Reminder"] = "⏰ REMINDER: {0} {1}",
        ["Toast_Recurring"] = "↻ Next occurrence added: {0}",
        ["Toast_Ok"] = "OK",
    };

    private Dictionary<string, string> _strings = s_tr;

    private Localizer()
    {
    }

    public static Localizer Instance { get; } = new();

    public AppLanguage Language { get; private set; } = AppLanguage.Turkish;

    public CultureInfo Culture => Language == AppLanguage.English ? s_enCulture : s_trCulture;

    /// <summary>Eksik anahtar sessizce boş dönmez; ekranda !anahtar! olarak görünür ki fark edilsin.</summary>
    public string this[string key] => _strings.TryGetValue(key, out var value) ? value : $"!{key}!";

    public string Format(string key, params object?[] args) => string.Format(Culture, this[key], args);

    public void SetLanguage(AppLanguage language)
    {
        if (language == Language)
        {
            return;
        }

        Language = language;
        _strings = language == AppLanguage.English ? s_en : s_tr;
        OnPropertyChanged("Item[]");
        OnPropertyChanged(nameof(Language));
        OnPropertyChanged(nameof(Culture));
    }

    /// <summary>Test/tutarlılık kontrolü: iki dilde de aynı anahtar seti olmalı.</summary>
    public static IReadOnlyCollection<string> MissingKeys() =>
        [.. s_tr.Keys.Except(s_en.Keys), .. s_en.Keys.Except(s_tr.Keys)];
}

/// <summary>XAML kısayolu: <c>Text="{loc:Tr Tasks_Header}"</c> → dil değişince otomatik güncellenen OneWay binding.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Localizer.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

/// <summary>
/// Seçim listeleri (öncelik, tekrar, hatırlatıcı, sıralama...) için değer + dile duyarlı etiket.
/// Literal=true ise Key doğrudan etiket olarak kullanılır (ör. kategori adı, "70%").
/// </summary>
public sealed class LocalizedOption<T> : ObservableObject
{
    public LocalizedOption(T value, string key, bool literal = false)
    {
        Value = value;
        Key = key;
        IsLiteral = literal;
        if (!literal)
        {
            // Seçenekler uygulama ömrü boyunca yaşar; statik olaya abone olmak sızıntı yaratmaz.
            Localizer.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Label));
        }
    }

    public T Value { get; }

    public string Key { get; }

    public bool IsLiteral { get; }

    public string Label => IsLiteral ? Key : Localizer.Instance[Key];
}
