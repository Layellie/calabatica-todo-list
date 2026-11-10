using System.Globalization;
using System.Windows;
using CyberTodo.Models;
using CyberTodo.Services;

namespace CyberTodo.ViewModels;

/// <summary>İstatistik ve takvim hesapları (saf fonksiyonlar; UI'dan bağımsız test edilebilir).</summary>
public static class InsightsBuilder
{
    private const int CalendarCells = 42; // 6 hafta × 7 gün
    private const int WeekdayLookbackDays = 56; // 8 hafta

    // ───────────────────────────── İstatistik ─────────────────────────────

    /// <summary>Güncel seri: bugünden geriye her gün ≥1 tamamlanan görev. Bugün henüz boşsa dünden sayılır (seri bozulmuş sayılmaz).</summary>
    public static int CurrentStreak(IReadOnlyDictionary<DateOnly, DayActivity> days, DateOnly today)
    {
        var cursor = HasCompletion(days, today) ? today : today.AddDays(-1);
        int streak = 0;
        while (HasCompletion(days, cursor))
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }

        return streak;
    }

    public static int LongestStreak(IReadOnlyDictionary<DateOnly, DayActivity> days)
    {
        int best = 0, run = 0;
        DateOnly? previous = null;
        foreach (var date in days.Where(static kv => kv.Value.CompletedCount > 0).Select(static kv => kv.Key).Order())
        {
            run = previous is { } p && p.AddDays(1) == date ? run + 1 : 1;
            best = Math.Max(best, run);
            previous = date;
        }

        return best;
    }

    public static (List<StatTile> Tiles, List<WeekdayStat> Weekdays) BuildStats(
        IReadOnlyDictionary<DateOnly, DayActivity> days,
        IEnumerable<CyberTask> allTasks,
        DateOnly today,
        Localizer loc,
        Func<int, string> formatMinutes)
    {
        var culture = loc.Culture;
        string Hours(double h) => h.ToString("0.#", culture);

        int current = CurrentStreak(days, today);
        int longest = Math.Max(LongestStreak(days), current);

        (int count, double hours) Range(int fromOffset, int toOffset)
        {
            int c = 0;
            double h = 0;
            for (int i = fromOffset; i <= toOffset; i++)
            {
                if (days.TryGetValue(today.AddDays(-i), out var d))
                {
                    c += d.CompletedCount;
                    h += d.HoursWorked;
                }
            }

            return (c, h);
        }

        var last7 = Range(0, 6);
        var prev7 = Range(7, 13);
        string delta = prev7.hours <= 0
            ? loc["Stat_None"]
            : ((last7.hours - prev7.hours) / prev7.hours).ToString("+0%;-0%;0%", culture);

        int totalCompleted = days.Values.Sum(static d => d.CompletedCount);
        double totalHours = days.Values.Sum(static d => d.HoursWorked);

        int focusMinutes = allTasks.Sum(static t => t.TrackedMinutes);
        int focusSessions = days.Values.Sum(static d => d.Entries.Count(static e => e.Status == LogStatus.Focus));

        // En verimli saat: tamamlanma + odak kayıtlarının saat dağılımı.
        var hourBuckets = new int[24];
        foreach (var entry in days.Values.SelectMany(static d => d.Entries))
        {
            if (entry.Status is LogStatus.Done or LogStatus.Focus
                && entry.Time.Length >= 2
                && int.TryParse(entry.Time.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hour)
                && hour is >= 0 and < 24)
            {
                hourBuckets[hour]++;
            }
        }

        int peakCount = hourBuckets.Max();
        int peakHour = Array.IndexOf(hourBuckets, peakCount);
        string peak = peakCount == 0 ? loc["Stat_None"] : $"{peakHour:00}:00–{(peakHour + 1) % 24:00}:00";

        var tiles = new List<StatTile>
        {
            new(loc["Stat_Streak"], loc.Format("Stat_Days", current), loc["Stat_StreakSub"], Highlight: current > 0),
            new(loc["Stat_BestStreak"], loc.Format("Stat_Days", longest), string.Empty),
            new(loc["Stat_Week"], loc.Format("Stat_TasksHours", last7.count, Hours(last7.hours)), loc.Format("Stat_Delta", delta)),
            new(loc["Stat_Total"], loc.Format("Stat_TasksHours", totalCompleted, Hours(totalHours)), string.Empty),
            new(loc["Stat_Focus"], focusMinutes > 0 ? formatMinutes(focusMinutes) : loc["Stat_None"], loc.Format("Stat_Entries", focusSessions)),
            new(loc["Stat_BestHour"], peak, peakCount > 0 ? loc.Format("Stat_Entries", peakCount) : string.Empty),
        };

        // Haftanın günlerine göre ortalama saat (son 8 hafta; her gün tipinden 8 örnek).
        var sums = new double[7];
        var counts = new int[7];
        for (int i = 0; i < WeekdayLookbackDays; i++)
        {
            var date = today.AddDays(-i);
            int index = (int)date.DayOfWeek;
            counts[index]++;
            if (days.TryGetValue(date, out var d))
            {
                sums[index] += d.HoursWorked;
            }
        }

        var averages = RecurrenceRules.MondayFirst.Select(day => (day, avg: counts[(int)day] == 0 ? 0 : sums[(int)day] / counts[(int)day])).ToList();
        double maxAvg = averages.Max(static a => a.avg);
        var weekdays = averages.Select(a =>
        {
            double ratio = maxAvg <= 0 ? 0 : Math.Clamp(a.avg / maxAvg, a.avg > 0 ? 0.03 : 0, 1);
            return new WeekdayStat(
                culture.DateTimeFormat.GetAbbreviatedDayName(a.day).ToUpper(culture),
                a.avg.ToString("0.#", culture),
                new GridLength(ratio, GridUnitType.Star),
                new GridLength(1 - ratio, GridUnitType.Star),
                IsBest: maxAvg > 0 && a.avg.Equals(maxAvg));
        }).ToList();

        return (tiles, weekdays);
    }

    private static bool HasCompletion(IReadOnlyDictionary<DateOnly, DayActivity> days, DateOnly date) =>
        days.TryGetValue(date, out var day) && day.CompletedCount > 0;

    // ───────────────────────────── Takvim ─────────────────────────────

    /// <summary>Ayın ilk gününü içeren haftanın pazartesisi.</summary>
    public static DateOnly GridStart(DateOnly monthFirst) =>
        monthFirst.AddDays(-(((int)monthFirst.DayOfWeek + 6) % 7));

    /// <summary>
    /// 42 hücrelik ay ızgarası. Aktif görevler son tarihlerinde; tekrarlayanlar ızgara boyunca izdüşüm olarak
    /// (IsProjected) gösterilir; tamamlananlar tamamlandıkları gün (IsDone).
    /// </summary>
    public static List<CalendarCell> BuildCalendar(
        DateOnly monthFirst,
        DateOnly today,
        IEnumerable<CyberTask> activeTasks,
        IEnumerable<CyberTask> completedTasks,
        IReadOnlyDictionary<DateOnly, DayActivity> days,
        Func<double, BarIntensity> intensity,
        CultureInfo culture)
    {
        var start = GridStart(monthFirst);
        var end = start.AddDays(CalendarCells - 1);
        var byDate = new Dictionary<DateOnly, List<(TimeOnly? Time, CalendarItem Item)>>();

        void Add(DateOnly date, TimeOnly? time, CalendarItem item)
        {
            if (date < start || date > end)
            {
                return;
            }

            if (!byDate.TryGetValue(date, out var list))
            {
                byDate[date] = list = [];
            }

            list.Add((time, item));
        }

        foreach (var task in activeTasks)
        {
            if (task.DueAt is not { } due)
            {
                continue;
            }

            TimeOnly? time = task.HasDueTime ? TimeOnly.FromDateTime(due) : null;
            string timeText = time?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;
            Add(DateOnly.FromDateTime(due), time, new CalendarItem(timeText, task.Title, task.Priority, IsDone: false, IsProjected: false));

            if (task.Recurrence == Recurrence.None)
            {
                continue;
            }

            var next = due;
            for (int guard = 0; guard < 60; guard++)
            {
                next = RecurrenceRules.Advance(next, task.Recurrence, task.RepeatDays);
                var nextDate = DateOnly.FromDateTime(next);
                if (nextDate > end)
                {
                    break;
                }

                Add(nextDate, time, new CalendarItem(timeText, task.Title, task.Priority, IsDone: false, IsProjected: true));
            }
        }

        foreach (var task in completedTasks)
        {
            if (task.CompletedAt is { } done)
            {
                Add(DateOnly.FromDateTime(done), TimeOnly.FromDateTime(done),
                    new CalendarItem(done.ToString("HH:mm", CultureInfo.InvariantCulture), task.Title, task.Priority, IsDone: true, IsProjected: false));
            }
        }

        var cells = new List<CalendarCell>(CalendarCells);
        for (int i = 0; i < CalendarCells; i++)
        {
            var date = start.AddDays(i);
            var items = byDate.TryGetValue(date, out var list)
                ? list.OrderBy(static x => x.Item.IsDone)                 // yapılacaklar önce
                      .ThenBy(static x => x.Time ?? TimeOnly.MaxValue)    // saatsizler sonda
                      .ThenBy(static x => x.Item.Title, StringComparer.CurrentCultureIgnoreCase)
                      .Select(static x => x.Item)
                      .ToList()
                : [];

            double hours = days.TryGetValue(date, out var day) ? day.HoursWorked : 0;
            cells.Add(new CalendarCell(
                date,
                isCurrentMonth: date.Month == monthFirst.Month && date.Year == monthFirst.Year,
                isToday: date == today,
                items,
                intensity(hours),
                hours > 0 ? hours.ToString("0.#", culture) : string.Empty));
        }

        return cells;
    }
}
