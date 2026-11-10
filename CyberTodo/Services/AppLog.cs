using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CyberTodo.Services;

/// <summary>
/// Yerel log (ağa gönderilmez): %LOCALAPPDATA%\CyberTodo\logs\
///  - app.log: yakalanan hatalar (kayıt/yedek/güncelleme…); 1 MB'ı geçince app.1.log'a döner, 3 dosya tutulur.
///  - crash-*.log: yakalanmamış hata başına bir dosya; son <see cref="MaxCrashFiles"/> tutulur.
/// Log yazmak hiçbir zaman fırlatmaz: log hatası asıl hatayı gölgelememeli.
/// </summary>
public static class AppLog
{
    public const int MaxCrashFiles = 10;
    public const long MaxAppLogBytes = 1024 * 1024;
    public const int AppLogGenerations = 3;

    private static readonly Lock s_gate = new();

    public static string Directory { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CyberTodo", "logs");

    /// <summary>Test için farklı klasör.</summary>
    public static void Configure(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
    }

    public static void Error(string context, Exception exception) =>
        Append("ERROR", context, exception.ToString());

    public static void Warn(string context, string message) =>
        Append("WARN", context, message);

    /// <summary>Yakalanmamış hata raporu yazar; dosya yolunu (yazılamadıysa null) döndürür.</summary>
    public static string? WriteCrash(Exception exception, string source)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var now = DateTime.Now;
        var report = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"CALABATICA crash report  {now:yyyy-MM-dd HH:mm:ss.fff}")
            .AppendLine(CultureInfo.InvariantCulture, $"Version : {UpdateService.ReadCurrentVersion()}")
            .AppendLine(CultureInfo.InvariantCulture, $"Runtime : {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})")
            .AppendLine(CultureInfo.InvariantCulture, $"OS      : {RuntimeInformation.OSDescription}")
            .AppendLine(CultureInfo.InvariantCulture, $"Source  : {source}")
            .AppendLine()
            .AppendLine(exception.ToString())
            .ToString();

        lock (s_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                string path = Path.Combine(Directory, $"crash-{now:yyyyMMdd-HHmmss-fff}.log");
                File.WriteAllText(path, report);
                PruneCrashFiles();
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null; // Bilinçli: rapor yazılamıyorsa yapacak başka bir şey yok (disk dolu / yetki)
            }
        }
    }

    private static void Append(string level, string context, string text)
    {
        lock (s_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                string path = Path.Combine(Directory, "app.log");
                RotateIfNeeded(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{context}] {text}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Bilinçli: log yazılamaması uygulamayı etkilememeli; asıl hata zaten arayüzde gösteriliyor.
            }
        }
    }

    private static void RotateIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxAppLogBytes)
        {
            return;
        }

        string Generation(int i) => Path.Combine(Directory, $"app.{i}.log");
        File.Delete(Generation(AppLogGenerations - 1));
        for (int i = AppLogGenerations - 2; i >= 1; i--)
        {
            if (File.Exists(Generation(i)))
            {
                File.Move(Generation(i), Generation(i + 1));
            }
        }

        File.Move(path, Generation(1));
    }

    private static void PruneCrashFiles()
    {
        foreach (var stale in new DirectoryInfo(Directory).EnumerateFiles("crash-*.log")
                     .OrderByDescending(static f => f.Name, StringComparer.Ordinal)
                     .Skip(MaxCrashFiles))
        {
            stale.Delete();
        }
    }
}
