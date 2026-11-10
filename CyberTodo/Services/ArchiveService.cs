using System.IO;
using CyberTodo.Models;

namespace CyberTodo.Services;

/// <summary>
/// Eski tamamlanmış görevlerin yıllık arşivi: &lt;veri klasörü&gt;\archive\completed-YYYY.json.
/// Ana dosya yıllar içinde sınırsız büyümesin (her kayıt tüm dosyayı yazar). Arşiv dosyaları ana dosyayla aynı
/// biçimdedir (CyberData, yalnızca Tasks dolu): içe aktarma / yedekten açma ile okunabilir.
/// Sıra: önce arşiv atomik yazılır, sonra çağıran görevleri ana veriden çıkarır. Arada çökme olursa görev iki
/// yerde birden kalabilir; bir sonraki arşivlemede Id'ye göre birleştirildiği için kopya oluşmaz.
/// </summary>
public sealed class ArchiveService
{
    /// <summary>Bu kadar günden eski tamamlanmış görevler arşivlenir (alışkanlık görünümü 400 güne bakar).</summary>
    public const int KeepDays = 400;

    private const long MaxArchiveBytes = 256L * 1024 * 1024;

    public ArchiveService(string dataFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFilePath);
        string directory = Path.GetDirectoryName(Path.GetFullPath(dataFilePath))
            ?? throw new ArgumentException("Invalid data path.", nameof(dataFilePath));
        ArchiveDirectory = Path.Combine(directory, "archive");
    }

    public string ArchiveDirectory { get; }

    public static string FileName(int year) => $"completed-{year}.json";

    /// <summary>Arşivlenecek görevler: tamamlanma anı kesim tarihinden eski olanlar.</summary>
    public static List<CyberTask> SelectExpired(IEnumerable<CyberTask> completed, DateTime now) =>
        [.. completed.Where(t => t.IsCompleted && (t.CompletedAt ?? t.CreatedAt) < now.AddDays(-KeepDays))];

    /// <summary>
    /// Görevleri tamamlanma yılına göre dosyalara ekler (Id'ye göre tekil, tarihe göre sıralı).
    /// Arşivde zaten olanlar (ör. eski bir yedek geri yüklendi) tekrar sayılmaz.
    /// </summary>
    /// <returns>Yeni eklenen görevler (özet sayaçlara yalnızca bunlar eklenir).</returns>
    public async Task<List<CyberTask>> AppendAsync(IReadOnlyCollection<CyberTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var added = new List<CyberTask>();
        foreach (var year in tasks.GroupBy(static t => (t.CompletedAt ?? t.CreatedAt).Year))
        {
            string path = Path.Combine(ArchiveDirectory, FileName(year.Key));
            var archive = File.Exists(path) ? await StorageService.ReadFileAsync(path, MaxArchiveBytes).ConfigureAwait(false) : new CyberData();
            var known = archive.Tasks.Select(static t => t.Id).ToHashSet();
            var fresh = year.Where(t => known.Add(t.Id)).ToList();
            if (fresh.Count == 0)
            {
                continue;
            }

            archive.Tasks.AddRange(fresh);
            added.AddRange(fresh);
            archive.Tasks.Sort(static (a, b) => Nullable.Compare(a.CompletedAt, b.CompletedAt));
            archive.NextTaskNumber = 1; // arşivde anlamsız
            await StorageService.WriteAtomicAsync(path, StorageService.Serialize(archive)).ConfigureAwait(false);
        }

        return added;
    }

    /// <summary>Arşiv dosyaları (yıl, yol), yeniden eskiye.</summary>
    public IReadOnlyList<string> List() =>
        Directory.Exists(ArchiveDirectory)
            ? [.. Directory.EnumerateFiles(ArchiveDirectory, "completed-*.json").OrderDescending(StringComparer.Ordinal)]
            : [];
}
