using System.Globalization;
using System.IO;
using CyberTodo.Models;

namespace CyberTodo.Services;

/// <summary>
/// Veri dosyasının yedekleri: &lt;veri klasörü&gt;\backups\.
///  - Günlük: cyber_data-yyyyMMdd.json, günde bir kez (açılışta / gün dönünce), son <see cref="KeepDaily"/> tanesi tutulur.
///  - Güvenlik: cyber_data-before-&lt;neden&gt;-yyyyMMdd-HHmmss.json, geri yükleme / içe aktarma / elle yedek öncesi,
///    son <see cref="KeepSafety"/> tanesi tutulur.
/// Yedek, ana dosya kopyalanarak değil uygulamanın anlık görüntüsü serileştirilerek yazılır: ana dosyanın
/// atomik değiştirilmesiyle (Move) dosya kilidi yarışına girmez.
/// </summary>
public sealed class BackupService
{
    public const int KeepDaily = 7;
    public const int KeepSafety = 5;

    private const string DailyPrefix = "cyber_data-";
    private const string SafetyPrefix = "cyber_data-before-";

    public BackupService(string dataFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFilePath);
        string directory = Path.GetDirectoryName(Path.GetFullPath(dataFilePath))
            ?? throw new ArgumentException("Invalid data path.", nameof(dataFilePath));
        BackupDirectory = Path.Combine(directory, "backups");
    }

    public string BackupDirectory { get; }

    public static string DailyFileName(DateOnly date) =>
        DailyPrefix + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".json";

    /// <summary>O gün için yedek yoksa yazar ve eskileri budar. Yazıldıysa dosya yolu, zaten varsa null.</summary>
    public async Task<string?> CreateDailyAsync(byte[] payload, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(payload);
        string path = Path.Combine(BackupDirectory, DailyFileName(date));
        if (File.Exists(path))
        {
            return null;
        }

        await StorageService.WriteAtomicAsync(path, payload).ConfigureAwait(false);
        Prune(isDaily: true, KeepDaily);
        return path;
    }

    /// <param name="reason">Dosya adına girer; yalnızca harf/rakam (yol enjeksiyonu yok).</param>
    public async Task<string> CreateSafetyAsync(byte[] payload, DateTime now, string reason)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrEmpty(reason) || !reason.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("Reason must be ASCII letters/digits.", nameof(reason));
        }

        string name = $"{SafetyPrefix}{reason}-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json";
        string path = Path.Combine(BackupDirectory, name);
        await StorageService.WriteAtomicAsync(path, payload).ConfigureAwait(false);
        Prune(isDaily: false, KeepSafety);
        return path;
    }

    /// <summary>Yedekler, en yeni önce. Klasör yoksa boş liste.</summary>
    public IReadOnlyList<BackupInfo> List()
    {
        if (!Directory.Exists(BackupDirectory))
        {
            return [];
        }

        return
        [
            .. new DirectoryInfo(BackupDirectory)
                .EnumerateFiles(DailyPrefix + "*.json")
                .Select(static f => new BackupInfo(f.FullName, f.Name, f.LastWriteTime, f.Length, IsDailyName(f.Name)))
                .OrderByDescending(static b => b.CreatedAt),
        ];
    }

    /// <summary>Yol gerçekten bu yedek klasöründe mi (geri yüklemede dışarıdan yol verilemesin).</summary>
    public bool Contains(string path)
    {
        string full = Path.GetFullPath(path);
        return string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(BackupDirectory), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(full).StartsWith(DailyPrefix, StringComparison.OrdinalIgnoreCase)
            && full.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDailyName(string name) => !name.StartsWith(SafetyPrefix, StringComparison.OrdinalIgnoreCase);

    private void Prune(bool isDaily, int keep)
    {
        var files = new DirectoryInfo(BackupDirectory)
            .EnumerateFiles(DailyPrefix + "*.json")
            .Where(f => IsDailyName(f.Name) == isDaily);

        // Günlük adlar yalnızca tarih içerir → ad sırası = gün sırası (saat değişse de kararlı).
        // Güvenlik yedeklerinde nedenler farklı olduğundan ad sırası karışır → yazılma zamanı.
        var stale = (isDaily
                ? files.OrderByDescending(static f => f.Name, StringComparer.Ordinal)
                : files.OrderByDescending(static f => f.LastWriteTimeUtc))
            .Skip(keep);

        foreach (var file in stale)
        {
            file.Delete();
        }
    }
}
