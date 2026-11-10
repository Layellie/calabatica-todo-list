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
        ["Stat_Entries"] = "{0} kayıt",
        ["Stat_None"] = "—",
        ["Stat_Weekdays"] = "GÜNLERE GÖRE ORTALAMA SAAT (SON 8 HAFTA)",

        ["Cal_Empty"] = "// BU GÜN İÇİN GÖREV YOK",
        ["Tip_PrevMonth"] = "Önceki ay",
        ["Tip_NextMonth"] = "Sonraki ay",

        ["Templates_Button"] = "⧉ ŞABLONLAR",
        ["Template_NameHint"] = "şablon adı (ör. Sabah rutini)",
        ["Template_Create"] = "GÖRÜNEN GÖREVLERDEN OLUŞTUR",
        ["Template_Apply"] = "UYGULA",
        ["Templates_Empty"] = "// HENÜZ ŞABLON YOK",
        ["Template_Help"] = "Listeyi arama/kategori/akıllı liste ile süz, sonra görünen aktif görevleri şablon olarak kaydet. Başlık, kategori, öncelik, süre, not, alt görev, tekrar ve (bugüne göre) tarih saklanır.",
        ["Toast_TemplateApplied"] = "⧉ {0}: {1} görev eklendi",
        ["Toast_TemplateSaved"] = "⧉ Şablon kaydedildi: {0}",

        ["Settings_Theme"] = "TEMA RENKLERİ",
        ["Settings_Tray"] = "KAPATINCA TEPSİYE KÜÇÜLT",
        ["Settings_Startup"] = "WINDOWS İLE BAŞLAT",
        ["Settings_Updates"] = "GÜNCELLEMELER",
        ["Settings_AutoUpdate"] = "AÇILIŞTA DENETLE",
        ["Update_Check"] = "DENETLE",
        ["Update_Open"] = "SAYFAYI AÇ",
        ["Update_NotConfigured"] = "GitHub deposu henüz ayarlanmadı (yayın derlemesinde otomatik gelir).",
        ["Update_NoRelease"] = "Henüz yayınlanmış bir sürüm yok (ya da depo gizli).",
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
        ["Shortcuts_Text"] = "Ctrl+N  yeni görev     Ctrl+F  ara\nCtrl+Z  son işlemi geri al     Ctrl+,  ayarlar\nEnter  ekle / kaydet     Esc  kapat / aramayı temizle\n↑ ↓  görev seç     Space  tamamla     Enter  düzenle\nDel  sil     F  odak başlat/durdur     Ctrl+M  mini mod",
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

        ["Undo_Add"] = "{0} eklendi",
        ["Undo_Complete"] = "{0} tamamlandı",
        ["Undo_Restore"] = "{0} yeniden açıldı",
        ["Undo_Edit"] = "{0} düzenlendi",
        ["Undo_Move"] = "{0} taşındı",
        ["Undo_Delete"] = "{0} silindi",
        ["Undo_Template"] = "şablon: {0}",
        ["Toast_Undone"] = "↶ GERİ ALINDI: {0}",
        ["Tip_Undo"] = "Geri al (Ctrl+Z)",
        ["Tip_UndoLast"] = "Geri al: {0} (Ctrl+Z)",
        ["Btn_Undo"] = "↶ GERİ AL",

        ["Tab_Focus"] = "ODAK",
        ["Focus_Total"] = "ODAK (SON {0} GÜN)",
        ["Focus_Previous"] = "önceki dönem: {0}",
        ["Focus_Sessions"] = "OTURUM",
        ["Focus_Average"] = "ort. {0}",
        ["Focus_TopCategory"] = "EN ÇOK",
        ["Focus_NoCategory"] = "(kategorisiz)",
        ["Focus_ByCategory"] = "KATEGORİYE GÖRE",
        ["Focus_ByTask"] = "GÖREVE GÖRE",
        ["Focus_Empty"] = "// BU DÖNEMDE ODAK OTURUMU YOK — bir görevde ◉ ile odak başlat",
        ["Focus_7"] = "7 GÜN",
        ["Focus_30"] = "30 GÜN",

        ["Data_Button"] = "⛁ VERİ & YEDEK",
        ["Data_Backups"] = "YEDEKLER (günlük yedek, son 7 gün)",
        ["Data_BackupsEmpty"] = "// HENÜZ YEDEK YOK (ilk yedek bir sonraki açılışta alınır)",
        ["Data_BackupNow"] = "ŞİMDİ YEDEKLE",
        ["Data_Restore"] = "GERİ YÜKLE",
        ["Data_Daily"] = "GÜNLÜK",
        ["Data_Safety"] = "ÖNCESİ",
        ["Data_Export"] = "DIŞA AKTAR",
        ["Data_ExportJson"] = "JSON",
        ["Data_ExportCsv"] = "CSV (EXCEL)",
        ["Data_ExportIcs"] = "TAKVİM (.ICS)",
        ["Data_Import"] = "İÇE AKTAR (JSON)",
        ["Data_Help"] = "Geri yükleme ve içe aktarma mevcut görevlerin yerine geçer; önce mevcut hâl otomatik yedeklenir. Ayarlar değişmez.",
        ["Data_IcsHelp"] = "Takvim dosyası son tarihli aktif görevleri içerir (Google Takvim, Outlook, Apple Takvim).",
        ["Data_Folder"] = "YEDEK KLASÖRÜ",
        ["Fmt_BackupDate"] = "d MMMM yyyy HH:mm",
        ["Confirm_Restore"] = "{0} tarihli yedek geri yüklensin mi?\n\nYedekte {1} görev var ve mevcut görevlerin yerine geçecek. Mevcut veriniz önce otomatik olarak yedeklenecek.",
        ["Confirm_Import"] = "\"{0}\" içe aktarılsın mı?\n\nDosyada {1} görev var ve mevcut görevlerin yerine geçecek. Mevcut veriniz önce otomatik olarak yedeklenecek.",
        ["Toast_Restored"] = "↺ {0} görev geri yüklendi · önceki hâl: {1}",
        ["Toast_Imported"] = "⇩ {0} görev içe aktarıldı · önceki hâl: {1}",
        ["Toast_BackupSaved"] = "⛁ Yedek alındı: {0}",
        ["Toast_Exported"] = "⇧ Dışa aktarıldı: {0}",
        ["Toast_NoDatedTasks"] = "Son tarihli aktif görev yok.",
        ["Err_Backup"] = "YEDEK ALINAMADI: {0}",
        ["Err_ReadFile"] = "DOSYA OKUNAMADI ({0}): {1}",
        ["Err_Export"] = "DIŞA AKTARILAMADI: {0}",
        ["Err_PersistenceDisabled"] = "KAYIT DEVRE DIŞI: veri dosyası okunamadığı için geri yükleme/içe aktarma yapılamaz.",

        ["Update_Install"] = "KUR",
        ["Update_InstallButton"] = "İNDİR VE KUR",
        ["Update_Downloading"] = "İndiriliyor… %{0}",
        ["Update_Restarting"] = "Kuruldu, yeniden başlatılıyor…",
        ["Update_InstallFailed"] = "Kurulamadı: {0}",
        ["Toast_UpdateReady"] = "▲ Yeni sürüm hazır: {0}",

        ["Snooze_10"] = "10 DK",
        ["Snooze_60"] = "1 SA",
        ["Toast_Snoozed"] = "⏰ {0} ertelendi → {1}",
        ["Toast_BreakOver"] = "☕ MOLA BİTTİ — sıradaki odak için hazırsın",
        ["Toast_BreakOverResume"] = "☕ Mola bitti · odak devam ediyor: {0}",
        ["Break_Label"] = "MOLA",
        ["Break_Long"] = "UZUN MOLA",
        ["Tip_SkipBreak"] = "Molayı atla",
        ["Settings_Breaks"] = "MOLA (5 dk · her 4 odakta 15 dk)",
        ["Settings_AutoNext"] = "MOLADAN SONRA ODAK OTOMATİK BAŞLASIN",
        ["Tab_Habits"] = "ALIŞKANLIK",
        ["Habit_Streak"] = "SERİ",
        ["Habit_Best"] = "en iyi {0}",
        ["Habit_Rate"] = "30 GÜN",
        ["Habit_State_Off"] = "planlı değil",
        ["Habit_State_Done"] = "yapıldı",
        ["Habit_State_Missed"] = "kaçırıldı",
        ["Habit_State_Pending"] = "bugün bekliyor",
        ["Habits_Empty"] = "// TEKRARLAYAN GÖREV YOK — *günlük, *haftalık ya da *pzt,çar ile ekle",
        ["Tip_Mini"] = "Mini mod (Ctrl+M)",
        ["Tip_MiniExpand"] = "Tam görünüm (Ctrl+M)",
        ["Tip_MiniHide"] = "Gizle (tepside çalışmaya devam eder)",
        ["Mini_Today"] = "BUGÜN",
        ["Mini_Empty"] = "// BUGÜN İÇİN GÖREV YOK",
        ["Mini_Idle"] = "Bir görevde ▶ ile odak başlat",
        ["Tray_Mini"] = "Mini mod",
        ["Filter_Save"] = "★ GÖRÜNÜMÜ KAYDET",
        ["Tip_SaveFilter"] = "Şu anki arama + kategori + liste birleşimini kaydet",
        ["Tip_ApplyFilter"] = "Kayıtlı görünümü uygula",
        ["Tip_DeleteFilter"] = "Kayıtlı görünümü sil",
        ["Tip_Help"] = "Nasıl kullanılır?",
        ["Onb_Skip"] = "ATLA",
        ["Onb_Back"] = "‹ GERİ",
        ["Onb_Next"] = "İLERİ ›",
        ["Onb_Done"] = "BAŞLA",
        ["Onb_1_Title"] = "HIZLI EKLEME",
        ["Onb_1_Body"] = "Görevi yaz, Enter'a bas. Komutlar sembolle başlar, başlıktaki normal kelimeler karışmaz:\n\n#iş  → kategori\n!  → yüksek öncelik\n@yarın  @cuma  @25.11  @14:00  → son tarih / saat\n*günlük  *haftalık  *pzt,çar,cum  → tekrar\n~1,5  → tahmini süre (saat)\n\nÖrnek: Rapor yaz #iş @yarın @14:00 ! ~2",
        ["Onb_2_Title"] = "KLAVYE · ODAK · MOLA",
        ["Onb_2_Body"] = "↑ ↓ ile görev seç, Space tamamla, Enter düzenle, Del sil, F odak başlat/durdur. Ctrl+Z son işlemi geri alır.\n\nBir görevde ▶ ile odak (Pomodoro) başlar; bitince kısa mola, her 4 odakta uzun mola. Süreler günlük aktiviteye ve ODAK sekmesine işlenir.",
        ["Onb_3_Title"] = "MİNİ MOD · VERİ",
        ["Onb_3_Body"] = "Ctrl+M ile her zaman üstte duran küçük pencere: sayaç ve bugünün görevleri.\n\nVerin yalnızca bu bilgisayarda. Her gün otomatik yedek alınır; Ayarlar → VERİ & YEDEK'ten geri yükle, JSON/CSV ya da takvime (.ics) aktar.\n\nBu rehbere başlıktaki ? ile tekrar ulaşabilirsin.",
        ["Data_ArchiveInfo"] = "ARŞİV: {0} eski tamamlanmış görev (400 günden eski) → {1}",
        ["Err_Archive"] = "ARŞİVLENEMEDİ (veri yerinde kaldı): {0}",
        ["Err_NewerData"] = "VERİ DOSYASI DAHA YENİ BİR SÜRÜMDEN (şema {0}) — veri kaybı olmasın diye kayıt kapalı. Uygulamayı güncelle.",
        ["Crash_Message"] = "CALABATICA beklenmeyen bir hatayla karşılaştı ve kapanacak.\nBekleyen değişiklikler kaydedildi.\n\nHata raporu: {0}",
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
        ["Stat_Entries"] = "{0} entries",
        ["Stat_None"] = "—",
        ["Stat_Weekdays"] = "AVERAGE HOURS BY WEEKDAY (LAST 8 WEEKS)",

        ["Cal_Empty"] = "// NO TASKS FOR THIS DAY",
        ["Tip_PrevMonth"] = "Previous month",
        ["Tip_NextMonth"] = "Next month",

        ["Templates_Button"] = "⧉ TEMPLATES",
        ["Template_NameHint"] = "template name (e.g. Morning routine)",
        ["Template_Create"] = "CREATE FROM VISIBLE TASKS",
        ["Template_Apply"] = "APPLY",
        ["Templates_Empty"] = "// NO TEMPLATES YET",
        ["Template_Help"] = "Filter the list with search/category/smart list, then save the visible active tasks as a template. Title, category, priority, hours, notes, subtasks, repeat and (relative) dates are kept.",
        ["Toast_TemplateApplied"] = "⧉ {0}: {1} tasks added",
        ["Toast_TemplateSaved"] = "⧉ Template saved: {0}",

        ["Settings_Theme"] = "THEME COLORS",
        ["Settings_Tray"] = "CLOSE TO TRAY",
        ["Settings_Startup"] = "START WITH WINDOWS",
        ["Settings_Updates"] = "UPDATES",
        ["Settings_AutoUpdate"] = "CHECK ON STARTUP",
        ["Update_Check"] = "CHECK",
        ["Update_Open"] = "OPEN PAGE",
        ["Update_NotConfigured"] = "GitHub repository not configured yet (set automatically in release builds).",
        ["Update_NoRelease"] = "No published release yet (or the repository is private).",
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
        ["Shortcuts_Text"] = "Ctrl+N  new task     Ctrl+F  search\nCtrl+Z  undo last action     Ctrl+,  settings\nEnter  add / save     Esc  close / clear search\n↑ ↓  select task     Space  complete     Enter  edit\nDel  delete     F  start/stop focus     Ctrl+M  mini mode",
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

        ["Undo_Add"] = "{0} added",
        ["Undo_Complete"] = "{0} completed",
        ["Undo_Restore"] = "{0} reopened",
        ["Undo_Edit"] = "{0} edited",
        ["Undo_Move"] = "{0} moved",
        ["Undo_Delete"] = "{0} deleted",
        ["Undo_Template"] = "template: {0}",
        ["Toast_Undone"] = "↶ UNDONE: {0}",
        ["Tip_Undo"] = "Undo (Ctrl+Z)",
        ["Tip_UndoLast"] = "Undo: {0} (Ctrl+Z)",
        ["Btn_Undo"] = "↶ UNDO",

        ["Tab_Focus"] = "FOCUS",
        ["Focus_Total"] = "FOCUS (LAST {0} DAYS)",
        ["Focus_Previous"] = "previous period: {0}",
        ["Focus_Sessions"] = "SESSIONS",
        ["Focus_Average"] = "avg {0}",
        ["Focus_TopCategory"] = "TOP CATEGORY",
        ["Focus_NoCategory"] = "(no category)",
        ["Focus_ByCategory"] = "BY CATEGORY",
        ["Focus_ByTask"] = "BY TASK",
        ["Focus_Empty"] = "// NO FOCUS SESSIONS IN THIS PERIOD — start one with ◉ on a task",
        ["Focus_7"] = "7 DAYS",
        ["Focus_30"] = "30 DAYS",

        ["Data_Button"] = "⛁ DATA & BACKUP",
        ["Data_Backups"] = "BACKUPS (daily, last 7 days)",
        ["Data_BackupsEmpty"] = "// NO BACKUPS YET (the first one is taken on next launch)",
        ["Data_BackupNow"] = "BACK UP NOW",
        ["Data_Restore"] = "RESTORE",
        ["Data_Daily"] = "DAILY",
        ["Data_Safety"] = "BEFORE",
        ["Data_Export"] = "EXPORT",
        ["Data_ExportJson"] = "JSON",
        ["Data_ExportCsv"] = "CSV (EXCEL)",
        ["Data_ExportIcs"] = "CALENDAR (.ICS)",
        ["Data_Import"] = "IMPORT (JSON)",
        ["Data_Help"] = "Restore and import replace your current tasks; the current state is backed up automatically first. Settings are not changed.",
        ["Data_IcsHelp"] = "The calendar file contains active tasks with a due date (Google Calendar, Outlook, Apple Calendar).",
        ["Data_Folder"] = "BACKUP FOLDER",
        ["Fmt_BackupDate"] = "MMM d, yyyy HH:mm",
        ["Confirm_Restore"] = "Restore the backup from {0}?\n\nIt contains {1} tasks, which will replace your current tasks. Your current data will be backed up automatically first.",
        ["Confirm_Import"] = "Import \"{0}\"?\n\nThe file contains {1} tasks, which will replace your current tasks. Your current data will be backed up automatically first.",
        ["Toast_Restored"] = "↺ {0} tasks restored · previous state: {1}",
        ["Toast_Imported"] = "⇩ {0} tasks imported · previous state: {1}",
        ["Toast_BackupSaved"] = "⛁ Backup saved: {0}",
        ["Toast_Exported"] = "⇧ Exported: {0}",
        ["Toast_NoDatedTasks"] = "No active tasks with a due date.",
        ["Err_Backup"] = "BACKUP FAILED: {0}",
        ["Err_ReadFile"] = "COULD NOT READ FILE ({0}): {1}",
        ["Err_Export"] = "EXPORT FAILED: {0}",
        ["Err_PersistenceDisabled"] = "SAVING DISABLED: the data file could not be read, so restore/import is not available.",

        ["Update_Install"] = "INSTALL",
        ["Update_InstallButton"] = "DOWNLOAD & INSTALL",
        ["Update_Downloading"] = "Downloading… {0}%",
        ["Update_Restarting"] = "Installed, restarting…",
        ["Update_InstallFailed"] = "Install failed: {0}",
        ["Toast_UpdateReady"] = "▲ New version available: {0}",

        ["Snooze_10"] = "10 MIN",
        ["Snooze_60"] = "1 H",
        ["Toast_Snoozed"] = "⏰ {0} snoozed until {1}",
        ["Toast_BreakOver"] = "☕ BREAK OVER — ready for the next focus",
        ["Toast_BreakOverResume"] = "☕ Break over · focus resumed: {0}",
        ["Break_Label"] = "BREAK",
        ["Break_Long"] = "LONG BREAK",
        ["Tip_SkipBreak"] = "Skip break",
        ["Settings_Breaks"] = "BREAKS (5 min · 15 min every 4th focus)",
        ["Settings_AutoNext"] = "AUTO-START FOCUS AFTER BREAK",
        ["Tab_Habits"] = "HABITS",
        ["Habit_Streak"] = "STREAK",
        ["Habit_Best"] = "best {0}",
        ["Habit_Rate"] = "30 DAYS",
        ["Habit_State_Off"] = "not scheduled",
        ["Habit_State_Done"] = "done",
        ["Habit_State_Missed"] = "missed",
        ["Habit_State_Pending"] = "due today",
        ["Habits_Empty"] = "// NO RECURRING TASKS — add one with *daily, *weekly or *mon,wed",
        ["Tip_Mini"] = "Mini mode (Ctrl+M)",
        ["Tip_MiniExpand"] = "Full view (Ctrl+M)",
        ["Tip_MiniHide"] = "Hide (keeps running in the tray)",
        ["Mini_Today"] = "TODAY",
        ["Mini_Empty"] = "// NOTHING DUE TODAY",
        ["Mini_Idle"] = "Start focus on a task with ▶",
        ["Tray_Mini"] = "Mini mode",
        ["Filter_Save"] = "★ SAVE VIEW",
        ["Tip_SaveFilter"] = "Save the current search + category + list combination",
        ["Tip_ApplyFilter"] = "Apply saved view",
        ["Tip_DeleteFilter"] = "Delete saved view",
        ["Tip_Help"] = "How to use",
        ["Onb_Skip"] = "SKIP",
        ["Onb_Back"] = "‹ BACK",
        ["Onb_Next"] = "NEXT ›",
        ["Onb_Done"] = "START",
        ["Onb_1_Title"] = "QUICK ADD",
        ["Onb_1_Body"] = "Type a task and press Enter. Commands start with a symbol, so normal words in a title are never mistaken:\n\n#work  → category\n!  → high priority\n@tomorrow  @friday  @25.11  @14:00  → due date / time\n*daily  *weekly  *mon,wed,fri  → repeat\n~1.5  → estimated hours\n\nExample: Write report #work @tomorrow @14:00 ! ~2",
        ["Onb_2_Title"] = "KEYBOARD · FOCUS · BREAKS",
        ["Onb_2_Body"] = "Select tasks with ↑ ↓, Space completes, Enter edits, Del deletes, F starts/stops focus. Ctrl+Z undoes the last action.\n\n▶ on a task starts a focus (Pomodoro) session, followed by a short break and a long break every 4th focus. Time goes into your daily activity and the FOCUS tab.",
        ["Onb_3_Title"] = "MINI MODE · DATA",
        ["Onb_3_Body"] = "Ctrl+M opens a small always-on-top window with the timer and today's tasks.\n\nYour data stays on this computer. A backup is taken every day; restore it, or export to JSON/CSV or your calendar (.ics), from Settings → DATA & BACKUP.\n\nOpen this guide again with ? in the title bar.",
        ["Data_ArchiveInfo"] = "ARCHIVE: {0} old completed tasks (older than 400 days) → {1}",
        ["Err_Archive"] = "ARCHIVING FAILED (data kept in place): {0}",
        ["Err_NewerData"] = "DATA FILE IS FROM A NEWER VERSION (schema {0}) — saving is off to avoid data loss. Please update the app.",
        ["Crash_Message"] = "CALABATICA ran into an unexpected error and will close.\nPending changes were saved.\n\nError report: {0}",
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

    /// <summary>Tanımlı anahtarlar (kullanım taraması testi için).</summary>
    internal static IReadOnlyCollection<string> Keys(AppLanguage language) =>
        language == AppLanguage.English ? s_en.Keys : s_tr.Keys;

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
