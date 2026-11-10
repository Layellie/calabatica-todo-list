using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using CyberTodo.Models;

namespace CyberTodo.Services;

/// <summary>
/// Yükleme sonucu. CorruptBackupFile null değilse eski dosya bozuktu ve bu adla yedeklendi;
/// mesaj dile göre ViewModel'de üretilir.
/// </summary>
public sealed record LoadResult(CyberData Data, bool IsNew, string? CorruptBackupFile);

/// <summary>
/// cyber_data.json okuma/yazma. Yazma işlemleri:
///  - serileştirme çağıran thread'de (UI) senkron yapılır → koleksiyonlar yazma sırasında değişse bile tutarlı snapshot,
///  - disk I/O asenkron ve SemaphoreSlim ile sıralı,
///  - önce .tmp dosyasına yazılıp atomik Move ile değiştirilir (yarım yazılmış dosya kalmaz).
/// </summary>
public sealed class StorageService
{
    public const string FileName = "cyber_data.json";

    private static readonly JsonSerializerOptions s_options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Türkçe karakterler (ğ, ş, İ, ı...) \uXXXX yerine okunabilir yazılsın.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Latin1Supplement, UnicodeRanges.LatinExtendedA),
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _snapshotVersion;
    private long _writtenVersion;

    /// <summary>Varsayılan konum: %LOCALAPPDATA%\CyberTodo\cyber_data.json.</summary>
    public StorageService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CyberTodo",
            FileName))
    {
    }

    public StorageService(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    /// <summary>Uygulamanın kayıt biçimiyle (girintili, okunabilir Türkçe) JSON üretir. Yedek ve dışa aktarma da bunu kullanır.</summary>
    public static byte[] Serialize(CyberData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return JsonSerializer.SerializeToUtf8Bytes(data, s_options);
    }

    /// <summary>
    /// Başka bir dosyayı (yedek / içe aktarma) okur ve normalize eder. Ana dosyadan farklı olarak bozuk dosyaya
    /// dokunmaz: geçersiz JSON <see cref="InvalidDataException"/> olarak fırlatılır.
    /// </summary>
    /// <param name="maxBytes">Kötü niyetli/yanlış seçilmiş dev dosyaya karşı sınır.</param>
    public static async Task<CyberData> ReadFileAsync(string path, long maxBytes = 64L * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException(null, path);
        }

        if (info.Length > maxBytes)
        {
            throw new InvalidDataException($"File too large ({info.Length / 1024 / 1024} MB).");
        }

        CyberData? data;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            data = await JsonSerializer.DeserializeAsync<CyberData>(stream, s_options, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }

        if (data is null)
        {
            throw new InvalidDataException("Empty JSON.");
        }

        Normalize(data);
        return data;
    }

    /// <summary>Hedefe atomik yazar (.tmp → Move); yarım dosya kalmaz.</summary>
    public static async Task WriteAtomicAsync(string path, byte[] payload, CancellationToken cancellationToken = default)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException($"Invalid path: {path}");
        Directory.CreateDirectory(directory);
        string temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, payload, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Dosyayı yükler. Dosya yoksa boş veriyle başlar (örnek veri yok). JSON bozuksa dosyayı silmez,
    /// yedek adla taşır ve boş veriyle devam eder. I/O hataları (kilitli dosya, yetki) çağırana fırlatılır.
    /// </summary>
    public async Task<LoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return new LoadResult(new CyberData(), IsNew: true, CorruptBackupFile: null);
        }

        CyberData? data;
        try
        {
            await using var stream = new FileStream(
                FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

            data = await JsonSerializer.DeserializeAsync<CyberData>(stream, s_options, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return BackUpCorruptFile();
        }

        if (data is null)
        {
            return BackUpCorruptFile();
        }

        Normalize(data);
        return new LoadResult(data, IsNew: false, CorruptBackupFile: null);
    }

    /// <summary>
    /// Veriyi kaydeder. Serileştirme ilk await'ten ÖNCE, çağıran thread'de yapılır;
    /// bu yüzden UI thread'inden çağrılmalıdır (ObservableCollection'lar UI'a ait).
    /// Daha yeni bir snapshot diske yazılmışsa eski snapshot atlanır.
    /// </summary>
    public async Task SaveAsync(CyberData data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(data, s_options);
        long version = Interlocked.Increment(ref _snapshotVersion);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (version <= _writtenVersion)
            {
                return; // daha yeni bir kayıt zaten yazıldı
            }

            string tempPath = PrepareTempPath();
            await File.WriteAllBytesAsync(tempPath, payload, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, FilePath, overwrite: true);

            _writtenVersion = version;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Senkron kayıt: Windows oturumu kapanırken async beklemeye zaman kalmayabilir.
    /// Asenkron sürümle aynı kilit + sürüm sırası + atomik yazma (.tmp → Move).
    /// Kilitteki async yazma iş parçacığı havuzunda bittiği için UI thread'inden çağrılması kilitlenme yaratmaz.
    /// </summary>
    public void Save(CyberData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(data, s_options);
        long version = Interlocked.Increment(ref _snapshotVersion);

        _writeGate.Wait();
        try
        {
            if (version <= _writtenVersion)
            {
                return;
            }

            string tempPath = PrepareTempPath();
            File.WriteAllBytes(tempPath, payload);
            File.Move(tempPath, FilePath, overwrite: true);

            _writtenVersion = version;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private string PrepareTempPath()
    {
        string directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException($"Geçersiz veri yolu: {FilePath}");
        Directory.CreateDirectory(directory);
        return FilePath + ".tmp";
    }

    /// <summary>Kuyruktaki tüm yazma işlemleri bitene kadar bekler (kapanışta veri kaybını önler).</summary>
    public async Task WhenIdleAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        _writeGate.Release();
    }

    private LoadResult BackUpCorruptFile()
    {
        string directory = Path.GetDirectoryName(FilePath)!;
        string backupPath = Path.Combine(directory, $"cyber_data.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.Move(FilePath, backupPath, overwrite: false);
        return new LoadResult(new CyberData(), IsNew: true, CorruptBackupFile: Path.GetFileName(backupPath));
    }

    /// <summary>Elle düzenlenmiş / eski (v1) JSON'lara karşı savunma: null listeler, sınır dışı değerler, numara çakışması.</summary>
    private static void Normalize(CyberData data)
    {
        data.Settings ??= new AppSettings();
        data.Tasks ??= [];
        data.Days ??= [];
        data.Templates ??= [];
        data.FocusSessions ??= [];
        data.FocusSessions.RemoveAll(static s => s is null || s.Minutes <= 0);
        foreach (var session in data.FocusSessions)
        {
            session.Minutes = Math.Min(session.Minutes, 24 * 60);
            session.Title ??= string.Empty;
        }

        data.Templates.RemoveAll(static t => t is null || t.Items is null);
        foreach (var template in data.Templates)
        {
            template.Name ??= string.Empty;
            template.Items.RemoveAll(static i => i is null || string.IsNullOrWhiteSpace(i.Title));
            foreach (var item in template.Items)
            {
                item.Subtasks ??= [];
                item.EstimatedHours = double.IsFinite(item.EstimatedHours) ? Math.Clamp(item.EstimatedHours, 0.01, 24) : 1;
            }
        }

        if (!Enum.IsDefined(data.Settings.Theme))
        {
            data.Settings.Theme = ThemeId.Neon;
        }
        data.Tasks.RemoveAll(static t => t is null);
        data.Days.RemoveAll(static d => d is null);

        var settings = data.Settings;
        if (!double.IsFinite(settings.UiScale) || settings.UiScale < 0.5 || settings.UiScale > 1.5)
        {
            settings.UiScale = AppSettings.DefaultUiScale;
        }

        if (settings.FocusMinutes is < 5 or > 180)
        {
            settings.FocusMinutes = 25;
        }

        // v1/v2 → v3: o sürümlerde tamamlanınca daima tahmini süre eklenirdi; geri almada aynısı düşülsün.
        if (data.SchemaVersion < 3)
        {
            foreach (var task in data.Tasks.Where(static t => t.IsCompleted))
            {
                task.CompletionCreditHours = task.EstimatedHours;
            }
        }

        data.SchemaVersion = CyberData.CurrentSchemaVersion;

        foreach (var task in data.Tasks)
        {
            task.TrackedMinutes = Math.Max(0, task.TrackedMinutes);
            task.CompletionCreditHours = double.IsFinite(task.CompletionCreditHours) ? Math.Max(0, task.CompletionCreditHours) : 0;
            task.Title ??= string.Empty;
            task.Subtasks ??= [];
            task.Subtasks.RemoveAll(static s => s is null);
            task.Progress = double.IsFinite(task.Progress) ? Math.Clamp(task.Progress, 0, 100) : 0;
            task.EstimatedHours = double.IsFinite(task.EstimatedHours) ? Math.Clamp(task.EstimatedHours, 0, 24) : 0;
            if (!task.IsCompleted)
            {
                task.CompletedAt = null;
            }

            if (task.DueAt is null)
            {
                task.HasDueTime = false;
            }

            // Gün seçilmemiş "seçili günler" tekrarı anlamsız → tekrarsız kabul et.
            task.RepeatDays &= WeekDays.All;
            if (task.Recurrence == Recurrence.SelectedDays && task.RepeatDays == WeekDays.None)
            {
                task.Recurrence = Recurrence.None;
            }
        }

        foreach (var day in data.Days)
        {
            day.Entries ??= [];
            day.HoursWorked = double.IsFinite(day.HoursWorked) ? Math.Max(0, day.HoursWorked) : 0;
            day.CompletedCount = Math.Max(0, day.CompletedCount);
        }

        int maxNumber = data.Tasks.Count == 0 ? 0 : data.Tasks.Max(static t => t.Number);
        if (data.NextTaskNumber <= maxNumber)
        {
            data.NextTaskNumber = maxNumber + 1;
        }
    }
}

internal static class CollectionExtensions
{
    /// <summary>ObservableCollection için List.RemoveAll eşdeğeri (normalize sırasında, UI'a bağlanmadan önce).</summary>
    public static void RemoveAll<T>(this System.Collections.ObjectModel.ObservableCollection<T> collection, Func<T, bool> predicate)
    {
        for (int i = collection.Count - 1; i >= 0; i--)
        {
            if (predicate(collection[i]))
            {
                collection.RemoveAt(i);
            }
        }
    }
}
