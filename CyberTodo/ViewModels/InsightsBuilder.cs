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

    // ───────────────────────────── Alışkanlıklar ─────────────────────────────

    public const int HabitStripDays = 28;
    private const int HabitLookbackDays = 400;

    /// <summary>
    /// Aktif bir kopyası olan her tekrar zinciri (SeriesId) için seri, en iyi seri, 30 günlük oran ve son 28 günün şeridi.
    /// Bir tekrarın "yapıldı" günü, tamamlanan kopyanın son tarih günüdür (geç tamamlansa da o güne sayılır);
    /// tarihsizse tamamlanma günü. Bugün planlı ama henüz yapılmamışsa seri bozulmuş sayılmaz.
    /// </summary>
    public static List<HabitRow> BuildHabits(
        IEnumerable<CyberTask> activeTasks,
        IEnumerable<CyberTask> completedTasks,
        DateOnly today,
        Localizer loc,
        Func<CyberTask, string> formatRecurrence)
    {
        var completedBySeries = completedTasks
            .Where(static t => t.SeriesId is not null)
            .GroupBy(static t => t.SeriesId!.Value)
            .ToDictionary(static g => g.Key, static g => g.ToList());

        var rows = new List<HabitRow>();
        var culture = loc.Culture;
        foreach (var current in activeTasks.Where(static t => t.Recurrence != Recurrence.None))
        {
            var seriesId = current.SeriesId ?? current.Id;
            var done = completedBySeries.TryGetValue(seriesId, out var completed) ? completed : [];
            var doneDates = done
                .Select(static t => DateOnly.FromDateTime(t.DueAt ?? t.CompletedAt ?? t.CreatedAt))
                .ToHashSet();

            var anchor = current.DueAt ?? current.CreatedAt;
            var start = DateOnly.FromDateTime(done.Select(static t => t.CreatedAt).Append(current.CreatedAt).Min());
            if (doneDates.Count > 0 && doneDates.Min() < start)
            {
                start = doneDates.Min();
            }

            bool IsScheduled(DateOnly date) => current.Recurrence switch
            {
                Recurrence.Daily => true,
                Recurrence.Weekly => date.DayOfWeek == anchor.DayOfWeek,
                Recurrence.Monthly => date.Day == Math.Min(anchor.Day, DateTime.DaysInMonth(date.Year, date.Month)),
                Recurrence.SelectedDays => RecurrenceRules.Contains(current.RepeatDays, date.DayOfWeek),
                _ => false,
            };

            // Bugün planlı ama yapılmamışsa bekliyor: seriyi bozmaz, oran hesabına girmez.
            bool todayPending = IsScheduled(today) && !doneDates.Contains(today);

            int streak = 0;
            for (var date = todayPending ? today.AddDays(-1) : today; date >= start && date > today.AddDays(-HabitLookbackDays); date = date.AddDays(-1))
            {
                if (!IsScheduled(date))
                {
                    continue;
                }

                if (!doneDates.Contains(date))
                {
                    break;
                }

                streak++;
            }

            int best = 0, run = 0;
            var from = start > today.AddDays(-HabitLookbackDays) ? start : today.AddDays(-HabitLookbackDays);
            for (var date = from; date <= today; date = date.AddDays(1))
            {
                if (!IsScheduled(date) || (date == today && todayPending))
                {
                    continue;
                }

                run = doneDates.Contains(date) ? run + 1 : 0;
                best = Math.Max(best, run);
            }

            int planned = 0, hit = 0;
            for (var date = today.AddDays(-29); date <= today; date = date.AddDays(1))
            {
                if (date < start || !IsScheduled(date) || (date == today && todayPending))
                {
                    continue;
                }

                planned++;
                if (doneDates.Contains(date))
                {
                    hit++;
                }
            }

            var cells = new List<HabitCell>(HabitStripDays);
            for (var date = today.AddDays(-(HabitStripDays - 1)); date <= today; date = date.AddDays(1))
            {
                var state = date < start || !IsScheduled(date) ? HabitCellState.Off
                    : doneDates.Contains(date) ? HabitCellState.Done
                    : date == today ? HabitCellState.Pending
                    : HabitCellState.Missed;
                string label = date.ToString(loc["Fmt_ShortDate"], culture).ToUpper(culture);
                cells.Add(new HabitCell(date, state, $"{label} · {loc["Habit_State_" + state]}"));
            }

            rows.Add(new HabitRow(
                $"{current.Code} {current.Title}",
                formatRecurrence(current),
                streak,
                streak.ToString(culture),
                loc.Format("Habit_Best", best),
                planned == 0 ? loc["Stat_None"] : ((double)hit / planned).ToString("0%", culture),
                cells));
        }

        return [.. rows.OrderByDescending(static r => r.Streak).ThenBy(static r => r.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    // ───────────────────────────── Odak ─────────────────────────────

    public const int FocusTopCount = 6;

    /// <summary>
    /// Son <paramref name="rangeDays"/> gündeki odak oturumları: toplam / oturum / önceki dönem kutucukları,
    /// kategori ve görev bazında en çok odaklanılan ilk <see cref="FocusTopCount"/> satır.
    /// Oturum, başladığı güne sayılır. Kategori karşılaştırması büyük/küçük harf duyarsız.
    /// </summary>
    public static (List<StatTile> Tiles, List<FocusSlice> ByCategory, List<FocusSlice> ByTask) BuildFocus(
        IReadOnlyCollection<FocusSession> sessions,
        DateOnly today,
        int rangeDays,
        Localizer loc,
        Func<int, string> formatMinutes)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        rangeDays = Math.Max(1, rangeDays);
        var from = today.AddDays(-(rangeDays - 1));
        var previousFrom = from.AddDays(-rangeDays);

        var current = sessions.Where(s => DateOnly.FromDateTime(s.Start) is var d && d >= from && d <= today).ToList();
        int previousTotal = sessions
            .Where(s => DateOnly.FromDateTime(s.Start) is var d && d >= previousFrom && d < from)
            .Sum(static s => s.Minutes);

        int total = current.Sum(static s => s.Minutes);
        string Minutes(int m) => m > 0 ? formatMinutes(m) : loc["Stat_None"];

        var byCategory = current
            .GroupBy(static s => s.Category?.Trim() ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => (Label: g.Key.Length == 0 ? loc["Focus_NoCategory"] : "#" + g.Key, Minutes: g.Sum(static s => s.Minutes)))
            .OrderByDescending(static x => x.Minutes)
            .ThenBy(static x => x.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Görev başlığı: en son oturumdaki hâli (sonradan yeniden adlandırılmışsa güncel ada yakın).
        var byTask = current
            .GroupBy(static s => s.TaskId)
            .Select(g =>
            {
                var latest = g.MaxBy(static s => s.Start)!;
                return (Label: $"#{latest.TaskNumber:000} {latest.Title}", Minutes: g.Sum(static s => s.Minutes));
            })
            .OrderByDescending(static x => x.Minutes)
            .ThenBy(static x => x.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var top = byCategory.FirstOrDefault();
        var tiles = new List<StatTile>
        {
            new(loc.Format("Focus_Total", rangeDays), Minutes(total), loc.Format("Focus_Previous", Minutes(previousTotal)), Highlight: total > 0),
            new(loc["Focus_Sessions"], current.Count.ToString(loc.Culture),
                current.Count > 0 ? loc.Format("Focus_Average", formatMinutes((int)Math.Round((double)total / current.Count))) : string.Empty),
            new(loc["Focus_TopCategory"], top.Minutes > 0 ? top.Label : loc["Stat_None"], top.Minutes > 0 ? formatMinutes(top.Minutes) : string.Empty),
        };

        return (tiles, ToSlices(byCategory, formatMinutes), ToSlices(byTask, formatMinutes));
    }

    private static List<FocusSlice> ToSlices(List<(string Label, int Minutes)> rows, Func<int, string> formatMinutes)
    {
        int max = rows.Count == 0 ? 0 : rows[0].Minutes;
        return
        [
            .. rows.Take(FocusTopCount).Select((r, i) =>
            {
                double ratio = max <= 0 ? 0 : Math.Clamp((double)r.Minutes / max, 0.02, 1);
                return new FocusSlice(r.Label, formatMinutes(r.Minutes),
                    new GridLength(ratio, GridUnitType.Star), new GridLength(1 - ratio, GridUnitType.Star), IsTop: i == 0);
            }),
        ];
    }

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
