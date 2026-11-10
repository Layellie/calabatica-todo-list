using System.Globalization;
using CyberTodo.Models;

namespace CyberTodo.ViewModels;

/// <summary>Hızlı ekleme satırının ayrıştırılmış hali.</summary>
public sealed record QuickAddResult(
    string Title,
    string? Category,
    TaskPriority? Priority,
    DateOnly? DueDate,
    TimeOnly? DueTime,
    Recurrence? Recurrence,
    double? Hours,
    WeekDays RepeatDays = WeekDays.None)
{
    public bool HasTokens =>
        Category is not null || Priority is not null || DueDate is not null || DueTime is not null || Recurrence is not null || Hours is not null;
}

/// <summary>
/// Doğal dil hızlı ekleme. Yanlış eşleşmeyi önlemek için her komut bir işaretle başlar
/// (ör. "Cuma namazı" başlığı tarih sanılmasın):
///   #kategori        → kategori
///   !                → yüksek öncelik
///   @bugün @yarın @cuma @25.11 @25.11.2026 @14:00 (TR/EN) → son tarih / saat
///   *günlük *haftalık *aylık (daily/weekly/monthly)      → tekrar
///   ~1.5             → tahmini süre (saat)
/// Tanınmayan işaretli kelimeler başlıkta kalır; sessizce yutulmaz.
/// </summary>
public static class QuickAddParser
{
    private static readonly CultureInfo s_tr = CultureInfo.GetCultureInfo("tr-TR");

    private static readonly Dictionary<string, DayOfWeek> s_weekdays = new(StringComparer.Ordinal)
    {
        ["pazartesi"] = DayOfWeek.Monday, ["monday"] = DayOfWeek.Monday,
        ["salı"] = DayOfWeek.Tuesday, ["tuesday"] = DayOfWeek.Tuesday,
        ["çarşamba"] = DayOfWeek.Wednesday, ["wednesday"] = DayOfWeek.Wednesday,
        ["perşembe"] = DayOfWeek.Thursday, ["thursday"] = DayOfWeek.Thursday,
        ["cuma"] = DayOfWeek.Friday, ["friday"] = DayOfWeek.Friday,
        ["cumartesi"] = DayOfWeek.Saturday, ["saturday"] = DayOfWeek.Saturday,
        ["pazar"] = DayOfWeek.Sunday, ["sunday"] = DayOfWeek.Sunday,
    };

    private static readonly Dictionary<string, Recurrence> s_recurrences = new(StringComparer.Ordinal)
    {
        ["günlük"] = Recurrence.Daily, ["daily"] = Recurrence.Daily,
        ["haftalık"] = Recurrence.Weekly, ["weekly"] = Recurrence.Weekly,
        ["aylık"] = Recurrence.Monthly, ["monthly"] = Recurrence.Monthly,
    };

    /// <summary>"*pzt,çar,cum" listelerinde kabul edilen gün adları (kısa + uzun, TR + EN).</summary>
    private static readonly Dictionary<string, WeekDays> s_dayNames = new(StringComparer.Ordinal)
    {
        ["pzt"] = WeekDays.Monday, ["pazartesi"] = WeekDays.Monday, ["mon"] = WeekDays.Monday, ["monday"] = WeekDays.Monday,
        ["sal"] = WeekDays.Tuesday, ["salı"] = WeekDays.Tuesday, ["tue"] = WeekDays.Tuesday, ["tuesday"] = WeekDays.Tuesday,
        ["çar"] = WeekDays.Wednesday, ["çarşamba"] = WeekDays.Wednesday, ["wed"] = WeekDays.Wednesday, ["wednesday"] = WeekDays.Wednesday,
        ["per"] = WeekDays.Thursday, ["perşembe"] = WeekDays.Thursday, ["thu"] = WeekDays.Thursday, ["thursday"] = WeekDays.Thursday,
        ["cum"] = WeekDays.Friday, ["cuma"] = WeekDays.Friday, ["fri"] = WeekDays.Friday, ["friday"] = WeekDays.Friday,
        ["cmt"] = WeekDays.Saturday, ["cumartesi"] = WeekDays.Saturday, ["sat"] = WeekDays.Saturday, ["saturday"] = WeekDays.Saturday,
        ["paz"] = WeekDays.Sunday, ["pazar"] = WeekDays.Sunday, ["sun"] = WeekDays.Sunday, ["sunday"] = WeekDays.Sunday,
    };

    private static readonly Dictionary<string, WeekDays> s_dayGroups = new(StringComparer.Ordinal)
    {
        ["haftaiçi"] = WeekDays.Weekdays, ["hafta-içi"] = WeekDays.Weekdays, ["weekdays"] = WeekDays.Weekdays,
        ["haftasonu"] = WeekDays.Weekend, ["hafta-sonu"] = WeekDays.Weekend, ["weekend"] = WeekDays.Weekend,
    };

    private static readonly string[] s_dayMonthFormats = ["d.M", "dd.MM", "d/M", "dd/MM"];

    public static QuickAddResult Parse(string? input, DateOnly today)
    {
        var titleWords = new List<string>();
        string? category = null;
        TaskPriority? priority = null;
        DateOnly? date = null;
        TimeOnly? time = null;
        Recurrence? recurrence = null;
        double? hours = null;
        var repeatDays = WeekDays.None;

        foreach (string word in (input ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryApply(word, today, ref category, ref priority, ref date, ref time, ref recurrence, ref hours, ref repeatDays))
            {
                continue;
            }

            titleWords.Add(word);
        }

        // Tekrarlayan ama tarihsiz: ilk uygun gün (bugün dahil). Düzenleyiciyle aynı kural.
        if (recurrence is { } rec && date is null)
        {
            date = rec == Recurrence.SelectedDays ? RecurrenceRules.FirstMatchOnOrAfter(today, repeatDays) : today;
        }

        // Sadece saat verildiyse bugün kabul edilir.
        if (time is not null && date is null)
        {
            date = today;
        }

        return new QuickAddResult(string.Join(' ', titleWords), category, priority, date, time, recurrence, hours, repeatDays);
    }

    private static bool TryApply(
        string word, DateOnly today,
        ref string? category, ref TaskPriority? priority, ref DateOnly? date, ref TimeOnly? time,
        ref Recurrence? recurrence, ref double? hours, ref WeekDays repeatDays)
    {
        if (word.Length >= 2 && word[0] == '#')
        {
            category = word[1..];
            return true;
        }

        if (word is "!" or "!!")
        {
            priority = TaskPriority.HighPriority;
            return true;
        }

        if (word.Length >= 2 && word[0] == '~' && InputParsing.TryParseHours(word[1..], out double parsedHours))
        {
            hours = parsedHours;
            return true;
        }

        if (word.Length >= 2 && word[0] == '*')
        {
            string body = word[1..];
            if (s_recurrences.TryGetValue(Normalize(body), out var rec))
            {
                recurrence = rec;
                return true;
            }

            if (s_dayGroups.TryGetValue(Normalize(body), out var group))
            {
                recurrence = Recurrence.SelectedDays;
                repeatDays = group;
                return true;
            }

            // "*pzt,çar,cum": tüm parçalar gün adı olmalı; biri bile tanınmazsa kelime başlıkta kalır.
            var mask = WeekDays.None;
            foreach (string part in body.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!s_dayNames.TryGetValue(Normalize(part), out var flag))
                {
                    return false;
                }

                mask |= flag;
            }

            if (mask != WeekDays.None)
            {
                recurrence = Recurrence.SelectedDays;
                repeatDays = mask;
                return true;
            }

            return false;
        }

        if (word.Length >= 2 && word[0] == '@')
        {
            string token = word[1..];
            string key = Normalize(token);

            if (key is "bugün" or "today")
            {
                date = today;
                return true;
            }

            if (key is "yarın" or "tomorrow")
            {
                date = today.AddDays(1);
                return true;
            }

            if (s_weekdays.TryGetValue(key, out var weekday))
            {
                // Bugün o günse bugün; değilse önümüzdeki ilk o gün.
                int delta = ((int)weekday - (int)today.DayOfWeek + 7) % 7;
                date = today.AddDays(delta);
                return true;
            }

            // Saat yalnızca ":" ile: "@1.12" 1 Aralık'tır, 01:12 değil.
            if (token.Contains(':') && InputParsing.TryParseTime(token, out var parsedTime))
            {
                time = parsedTime;
                return true;
            }

            if (InputParsing.TryParseDate(token, out var fullDate))
            {
                date = fullDate;
                return true;
            }

            // Yıl verilmemiş gün.ay: bu yıl, geçmişte kaldıysa gelecek yıl.
            if (DateTime.TryParseExact(token, s_dayMonthFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dayMonth))
            {
                var candidate = new DateOnly(today.Year, dayMonth.Month, dayMonth.Day);
                date = candidate < today ? candidate.AddYears(1) : candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Türkçe ve İngilizce kelimeler aynı sözlükte: "YARIN" → "yarın" (tr), "FRIDAY" → "friday" (invariant).
    /// tr-TR ile "FRIDAY" "frıday" olurdu; bu yüzden önce invariant, eşleşmezse tr denenir.
    /// </summary>
    private static string Normalize(string token)
    {
        string invariant = token.ToLowerInvariant();
        if (s_weekdays.ContainsKey(invariant) || s_recurrences.ContainsKey(invariant) || s_dayNames.ContainsKey(invariant)
            || s_dayGroups.ContainsKey(invariant) || invariant is "today" or "tomorrow")
        {
            return invariant;
        }

        return token.ToLower(s_tr);
    }
}
