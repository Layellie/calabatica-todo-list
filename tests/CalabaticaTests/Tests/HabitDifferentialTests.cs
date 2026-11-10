using System.Security.Cryptography;
using System.Text;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.ViewModels;

namespace CalabaticaTests;

/// <summary>
/// InsightsBuilder.BuildHabits: bağımsız, saf "gün gün" referans uygulamayla karşılaştırma (seri, en iyi seri, 30 gün oranı,
/// 28 günlük şerit) + sabit tohumlu senaryolar üzerinde tüm çıktının özeti (golden hash; bölme sırasında davranış kayması olmasın).
/// Referans kasten saf ve yavaştır: aynı kuralları ayrı yazar, böylece iki uygulamanın aynı hataya sahip olması olası değildir.
/// </summary>
internal static class HabitDifferentialTests
{
    private const string GoldenHash = "E7264CB7C738E5F24455B3FA3120CC150AD4BF1BFEF542B5FE590A4E0753FB07";

    public static Task Run(TestContext ctx)
    {
        var loc = Localizer.Instance;
        var today = new DateOnly(2026, 3, 4); // Çarşamba
        var rng = new Random(20260304);
        var summary = new StringBuilder();
        int mismatches = 0, cases = 0;
        string firstMismatch = "";

        var recurrences = new (Recurrence Kind, WeekDays Days)[]
        {
            (Recurrence.Daily, WeekDays.None), (Recurrence.Weekly, WeekDays.None), (Recurrence.Monthly, WeekDays.None),
            (Recurrence.SelectedDays, WeekDays.Monday | WeekDays.Wednesday | WeekDays.Friday), (Recurrence.SelectedDays, WeekDays.Weekend),
            (Recurrence.SelectedDays, WeekDays.Wednesday), (Recurrence.SelectedDays, WeekDays.All),
        };

        foreach (var (kind, days) in recurrences)
        {
            for (int n = 0; n < 60; n++)
            {
                cases++;
                var seriesId = Guid.NewGuid();
                int createdAgo = rng.Next(0, 90);
                var created = today.AddDays(-createdAgo).ToDateTime(new TimeOnly(9, 0));
                var anchorDay = today.AddDays(rng.Next(-3, 4));
                bool hasDue = rng.Next(4) != 0;
                var active = new CyberTask
                {
                    Number = n + 1, Title = "h" + n, SeriesId = rng.Next(3) == 0 ? null : seriesId, Recurrence = kind, RepeatDays = days,
                    CreatedAt = created, DueAt = hasDue ? anchorDay.ToDateTime(new TimeOnly(10, 0)) : null,
                };
                var chainId = active.SeriesId ?? active.Id;
                var done = new List<CyberTask>();
                double density = rng.NextDouble();
                for (int back = 0; back < createdAgo + 5; back++)
                {
                    if (rng.NextDouble() > density)
                    {
                        continue;
                    }

                    var day = today.AddDays(-back);
                    bool withDue = rng.Next(3) != 0;
                    done.Add(new CyberTask
                    {
                        SeriesId = chainId, IsCompleted = true, Recurrence = kind, RepeatDays = days,
                        CreatedAt = today.AddDays(-Math.Min(createdAgo, back + rng.Next(0, 3))).ToDateTime(new TimeOnly(8, 0)),
                        DueAt = withDue ? day.ToDateTime(new TimeOnly(11, 0)) : null,
                        CompletedAt = day.AddDays(withDue ? rng.Next(0, 3) : 0).ToDateTime(new TimeOnly(20, 0)),
                    });
                }

                // Başka bir zincirin kopyaları karışmamalı
                done.Add(new CyberTask { SeriesId = Guid.NewGuid(), IsCompleted = true, DueAt = today.ToDateTime(TimeOnly.MinValue), CompletedAt = DateTime.Now });

                var row = InsightsBuilder.BuildHabits([active], done, today, loc, _ => "R").Single();
                var expected = Reference(active, done, today);
                bool match = row.Streak == expected.Streak
                    && row.BestText == loc.Format("Habit_Best", expected.Best)
                    && row.RateText == expected.RateText(loc)
                    && row.Cells.Count == InsightsBuilder.HabitStripDays
                    && row.Cells.Select(static c => c.State).SequenceEqual(expected.Cells);
                if (!match)
                {
                    mismatches++;
                    firstMismatch = firstMismatch.Length > 0 ? firstMismatch : $"{kind}/{days} case {n}: streak {row.Streak} vs {expected.Streak}, best '{row.BestText}' vs {expected.Best}, rate '{row.RateText}' vs '{expected.RateText(loc)}'";
                }

                summary.Append(kind).Append('|').Append(row.Streak).Append('|').Append(row.StreakText).Append('|').Append(row.BestText).Append('|').Append(row.RateText).Append('|')
                    .Append(string.Concat(row.Cells.Select(static c => (char)('0' + (int)c.State)))).Append('\n');
            }
        }

        ctx.Check(mismatches == 0, $"habits: differential vs naive reference over {cases} generated chains ({mismatches} mismatches){(mismatches > 0 ? " e.g. " + firstMismatch : string.Empty)}");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(summary.ToString())));
        Console.WriteLine($"INFO  habits golden hash: {hash}");
        ctx.Check(hash == GoldenHash, "habits: output over the seeded scenarios is unchanged (golden hash)");
        return Task.CompletedTask;
    }

    private sealed record Expected(int Streak, int Best, int Planned, int Hit, List<HabitCellState> Cells)
    {
        public string RateText(Localizer loc) => Planned == 0 ? loc["Stat_None"] : ((double)Hit / Planned).ToString("0%", loc.Culture);
    }

    /// <summary>Saf referans: her gün için "planlı mı / yapıldı mı" tablosundan, döngüsüz tanımlarla.</summary>
    private static Expected Reference(CyberTask current, List<CyberTask> allCompleted, DateOnly today)
    {
        var chain = current.SeriesId ?? current.Id;
        var done = allCompleted.Where(t => t.SeriesId == chain).ToList();
        var doneDays = done.Select(static t => DateOnly.FromDateTime(t.DueAt ?? t.CompletedAt ?? t.CreatedAt)).ToHashSet();
        var anchor = current.DueAt ?? current.CreatedAt;
        var start = DateOnly.FromDateTime(done.Select(static t => t.CreatedAt).Append(current.CreatedAt).Min());
        if (doneDays.Count > 0 && doneDays.Min() < start)
        {
            start = doneDays.Min();
        }

        bool Scheduled(DateOnly d) => current.Recurrence switch
        {
            Recurrence.Daily => true,
            Recurrence.Weekly => d.DayOfWeek == anchor.DayOfWeek,
            Recurrence.Monthly => d.Day == Math.Min(anchor.Day, DateTime.DaysInMonth(d.Year, d.Month)),
            Recurrence.SelectedDays => (current.RepeatDays & ToFlag(d.DayOfWeek)) != 0,
            _ => false,
        };

        bool pending = Scheduled(today) && !doneDays.Contains(today);

        // Seri: bugünden (bekliyorsa dünden) geriye, planlı günler peş peşe yapılmış olduğu sürece; en çok 400 gün geriye.
        var scheduledDesc = Enumerable.Range(0, 400).Select(i => (pending ? today.AddDays(-1) : today).AddDays(-i))
            .Where(d => d >= start && d > today.AddDays(-400) && Scheduled(d)).ToList();
        int streak = scheduledDesc.TakeWhile(doneDays.Contains).Count();

        // En iyi seri: kronolojik, planlı (bugün bekliyorsa bugün hariç) günlerdeki en uzun yapıldı dizisi.
        var from = start > today.AddDays(-400) ? start : today.AddDays(-400);
        var seq = Enumerable.Range(0, today.DayNumber - from.DayNumber + 1).Select(i => from.AddDays(i))
            .Where(d => Scheduled(d) && !(d == today && pending)).Select(doneDays.Contains).ToList();
        int best = 0, run = 0;
        foreach (bool hit in seq)
        {
            run = hit ? run + 1 : 0;
            best = Math.Max(best, run);
        }

        // Son 30 gün oranı.
        var window = Enumerable.Range(0, 30).Select(i => today.AddDays(-29 + i)).Where(d => d >= start && Scheduled(d) && !(d == today && pending)).ToList();
        int planned = window.Count, hits = window.Count(doneDays.Contains);

        var cells = Enumerable.Range(0, InsightsBuilder.HabitStripDays).Select(i => today.AddDays(-(InsightsBuilder.HabitStripDays - 1) + i))
            .Select(d => d < start || !Scheduled(d) ? HabitCellState.Off
                : doneDays.Contains(d) ? HabitCellState.Done
                : d == today ? HabitCellState.Pending
                : HabitCellState.Missed).ToList();
        return new Expected(streak, best, planned, hits, cells);
    }

    private static WeekDays ToFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => WeekDays.Monday,
        DayOfWeek.Tuesday => WeekDays.Tuesday,
        DayOfWeek.Wednesday => WeekDays.Wednesday,
        DayOfWeek.Thursday => WeekDays.Thursday,
        DayOfWeek.Friday => WeekDays.Friday,
        DayOfWeek.Saturday => WeekDays.Saturday,
        _ => WeekDays.Sunday,
    };
}
