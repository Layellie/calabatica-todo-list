using System.Globalization;
using System.Text;
using CyberTodo.Models;

namespace CyberTodo.Services;

/// <summary>Görevleri CSV (Excel) ve iCalendar (.ics) biçimine çevirir. Saf fonksiyonlar; dosya yazmayı çağıran yapar.</summary>
public static class DataExporter
{
    // ───────────────────────────── CSV ─────────────────────────────

    /// <summary>
    /// Excel'in çift tıklamayla doğru açması için ayırıcı ve ondalık işareti verilen kültürden alınır
    /// (tr-TR → ";" ve "0,5"; en-US → "," ve "0.5"). Dosya UTF-8 BOM ile yazılmalı (Türkçe karakterler).
    /// Tarihler ISO (yyyy-MM-dd HH:mm): sıralanabilir ve kültürden bağımsız.
    /// </summary>
    public static string ToCsv(IEnumerable<CyberTask> tasks, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(culture);

        string sep = culture.TextInfo.ListSeparator;
        if (string.IsNullOrEmpty(sep) || sep.Length != 1)
        {
            sep = ",";
        }

        var sb = new StringBuilder();
        void Row(params string[] cells) => sb.Append(string.Join(sep, cells.Select(c => Quote(c, sep[0])))).Append("\r\n");

        Row("Code", "Title", "Category", "Priority", "Status", "Due", "Recurrence", "EstimatedHours",
            "TrackedMinutes", "Progress", "CreatedAt", "CompletedAt", "Notes", "Subtasks");

        foreach (var t in tasks)
        {
            string due = t.DueAt is { } d ? (t.HasDueTime ? Iso(d) : d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : string.Empty;
            string recurrence = t.Recurrence == Recurrence.SelectedDays ? $"SelectedDays({t.RepeatDays})" : t.Recurrence.ToString();
            string subtasks = string.Join(" | ", t.Subtasks.Select(static s => (s.IsDone ? "[x] " : "[ ] ") + s.Title));

            Row(
                t.Code,
                Text(t.Title),
                Text(t.Category ?? string.Empty),
                t.Priority.ToString(),
                t.IsCompleted ? "Completed" : "Active",
                due,
                recurrence,
                t.EstimatedHours.ToString("0.##", culture),
                t.TrackedMinutes.ToString(CultureInfo.InvariantCulture),
                t.Progress.ToString("0", culture),
                Iso(t.CreatedAt),
                t.CompletedAt is { } c ? Iso(c) : string.Empty,
                Text(t.Notes ?? string.Empty),
                Text(subtasks));
        }

        return sb.ToString();
    }

    private static string Iso(DateTime value) => value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// CSV/formül enjeksiyonu: "=", "+", "-", "@", sekme veya CR ile başlayan kullanıcı metni Excel'de formül
    /// olarak çalışabilir; başına ' eklenerek düz metin kalması sağlanır (OWASP önerisi).
    /// </summary>
    private static string Text(string value) =>
        value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;

    private static string Quote(string value, char separator)
    {
        bool needs = value.IndexOfAny([separator, '"', '\r', '\n']) >= 0 || value.StartsWith(' ') || value.EndsWith(' ');
        return needs ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    // ───────────────────────────── iCalendar ─────────────────────────────

    /// <summary>
    /// Son tarihi olan aktif görevler → VEVENT (Google Takvim / Outlook VTODO'yu göstermez).
    /// Saatli: başlangıç = son tarih, süre = tahmini süre (15 dk – 12 sa). Saatsiz: tüm gün.
    /// Saatler "floating" (saat dilimsiz) yazılır: içe aktaran takvimin yerel saati kullanılır.
    /// Tekrar → RRULE, hatırlatıcı → VALARM (saatsizde uygulamadaki gibi 09:00 tabanlı).
    /// </summary>
    public static string ToIcs(IEnumerable<CyberTask> tasks, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//CALABATICA//Task Export//EN",
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH",
            "X-WR-CALNAME:CALABATICA",
        };

        string stamp = utcNow.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        foreach (var t in tasks.Where(static t => !t.IsCompleted && t.DueAt is not null))
        {
            var due = t.DueAt!.Value;
            lines.Add("BEGIN:VEVENT");
            lines.Add($"UID:{t.Id:D}@calabatica");
            lines.Add($"DTSTAMP:{stamp}");

            if (t.HasDueTime)
            {
                double hours = double.IsFinite(t.EstimatedHours) ? Math.Clamp(t.EstimatedHours, 0.25, 12) : 1;
                lines.Add($"DTSTART:{Local(due)}");
                lines.Add($"DTEND:{Local(due.AddMinutes(Math.Round(hours * 60)))}");
            }
            else
            {
                lines.Add($"DTSTART;VALUE=DATE:{due:yyyyMMdd}");
                lines.Add($"DTEND;VALUE=DATE:{due.AddDays(1):yyyyMMdd}");
            }

            string summary = $"{t.Code} {t.Title}";
            lines.Add("SUMMARY:" + Escape(summary));

            var description = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(t.Notes))
            {
                description.Append(t.Notes.Trim());
            }

            foreach (var s in t.Subtasks)
            {
                if (description.Length > 0)
                {
                    description.Append('\n');
                }

                description.Append(s.IsDone ? "[x] " : "[ ] ").Append(s.Title);
            }

            if (description.Length > 0)
            {
                lines.Add("DESCRIPTION:" + Escape(description.ToString()));
            }

            if (!string.IsNullOrWhiteSpace(t.Category))
            {
                lines.Add("CATEGORIES:" + Escape(t.Category.Trim()));
            }

            if (RRule(t) is { } rule)
            {
                lines.Add("RRULE:" + rule);
            }

            if (t.ReminderMinutesBefore is { } before)
            {
                int offset = (t.HasDueTime ? 0 : 9 * 60) - before; // saatsiz: uygulamadaki gibi o gün 09:00 tabanı
                lines.Add("BEGIN:VALARM");
                lines.Add("ACTION:DISPLAY");
                lines.Add("DESCRIPTION:" + Escape(summary));
                lines.Add($"TRIGGER:{(offset < 0 ? "-" : string.Empty)}PT{Math.Abs(offset)}M");
                lines.Add("END:VALARM");
            }

            lines.Add("END:VEVENT");
        }

        lines.Add("END:VCALENDAR");

        var sb = new StringBuilder();
        foreach (string line in lines)
        {
            Fold(sb, line);
        }

        return sb.ToString();
    }

    private static string Local(DateTime value) => value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

    private static string? RRule(CyberTask t) => t.Recurrence switch
    {
        Recurrence.Daily => "FREQ=DAILY",
        Recurrence.Weekly => "FREQ=WEEKLY",
        Recurrence.Monthly => "FREQ=MONTHLY",
        Recurrence.SelectedDays when (t.RepeatDays & WeekDays.All) != 0 =>
            "FREQ=WEEKLY;BYDAY=" + string.Join(",", RecurrenceRules.MondayFirst
                .Where(d => RecurrenceRules.Contains(t.RepeatDays, d))
                .Select(static d => d.ToString()[..2].ToUpperInvariant())),
        _ => null,
    };

    /// <summary>RFC 5545 §3.3.11: \ ; , ve satır sonu kaçışlanır.</summary>
    public static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace(";", "\\;", StringComparison.Ordinal)
             .Replace(",", "\\,", StringComparison.Ordinal)
             .Replace("\r\n", "\n", StringComparison.Ordinal)
             .Replace("\r", "\n", StringComparison.Ordinal)
             .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>
    /// RFC 5545 §3.1: satırlar 75 oktetten uzun olamaz; devam satırı tek boşlukla başlar.
    /// UTF-8 çok baytlı karakterler (ğ, ş, emoji) ortadan bölünmez.
    /// </summary>
    private static void Fold(StringBuilder sb, string line)
    {
        int lineBytes = 0;
        int limit = 75;
        for (int i = 0; i < line.Length; i++)
        {
            int length = char.IsHighSurrogate(line[i]) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]) ? 2 : 1;
            int bytes = Encoding.UTF8.GetByteCount(line.AsSpan(i, length));
            if (lineBytes + bytes > limit)
            {
                sb.Append("\r\n ");
                lineBytes = 1; // baştaki boşluk
            }

            sb.Append(line, i, length);
            lineBytes += bytes;
            i += length - 1;
        }

        sb.Append("\r\n");
    }
}
