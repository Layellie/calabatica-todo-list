using CyberTodo.Models;

namespace CyberTodo.Services;

/// <summary>
/// Veri dosyası şema geçişleri: sıralı, her adım bir hedef sürüm. Dosyanın sürümünden büyük adımlar sırayla
/// uygulanır; her adım bir kez çalışır ve tekrar çalışsa da aynı sonucu verir (idempotent).
/// Yeni şema = <see cref="CyberData.CurrentSchemaVersion"/>'ı artır + buraya adım ekle + örnek eski dosyayla test yaz.
/// Null/sınır temizliği burada değil, StorageService.Normalize'da (her yüklemede çalışır).
/// </summary>
public static class SchemaMigrations
{
    private static readonly (int Version, string Description, Action<CyberData> Apply)[] s_steps =
    [
        (3, "completion credit = estimated hours for tasks completed before v3", ToV3),
        (4, "focus sessions list (new, empty)", static _ => { }),
        (5, "link copies of recurring tasks into series (SeriesId)", ToV5),
    ];

    public static int LatestVersion => s_steps[^1].Version;

    /// <summary>Gereken adımları uygular ve sürümü günceller. Uygulanan adım sayısını döndürür.</summary>
    public static int Apply(CyberData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        int applied = 0;
        foreach (var (version, _, apply) in s_steps)
        {
            if (data.SchemaVersion < version)
            {
                apply(data);
                data.SchemaVersion = version;
                applied++;
            }
        }

        return applied;
    }

    /// <summary>v1/v2 → v3: o sürümlerde tamamlanınca daima tahmini süre eklenirdi; geri almada aynısı düşülsün.</summary>
    private static void ToV3(CyberData data)
    {
        foreach (var task in data.Tasks.Where(static t => t.IsCompleted))
        {
            task.CompletionCreditHours = task.EstimatedHours;
        }
    }

    /// <summary>
    /// v4 → v5: eski sürümler tekrar kopyalarını birbirine bağlamıyordu. Aynı başlık + kategori + tekrar kuralına
    /// sahip tekrarlayan görevler tek zincir sayılır; zincir kimliği en eski kopyanın Id'si (kararlı).
    /// </summary>
    private static void ToV5(CyberData data)
    {
        var groups = data.Tasks
            .Where(static t => t.Recurrence != Recurrence.None && t.SeriesId is null)
            .GroupBy(static t => (
                Title: (t.Title ?? string.Empty).Trim().ToUpperInvariant(),
                Category: (t.Category ?? string.Empty).Trim().ToUpperInvariant(),
                t.Recurrence,
                t.RepeatDays));
        foreach (var group in groups)
        {
            var seriesId = group.MinBy(static t => t.CreatedAt)!.Id;
            foreach (var task in group)
            {
                task.SeriesId = seriesId;
            }
        }
    }
}
