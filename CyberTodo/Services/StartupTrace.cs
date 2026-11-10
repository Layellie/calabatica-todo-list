using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CyberTodo.Services;

/// <summary>
/// Açılış zaman çizelgesi: etiketli noktalar, değer = SÜREÇ BAŞLANGICINDAN beri ms (çalışma zamanı başlangıcı dahil).
/// Yalnızca <see cref="Enable"/> çağrıldıysa (kıyaslama modu) kayıt tutar; aksi halde <see cref="Mark"/> tek bir
/// bayrak denetimidir, sıfıra yakın maliyet.
/// </summary>
public static class StartupTrace
{
    private static readonly List<(string Label, double Ms)> s_marks = [];
    private static DateTime s_processStart;
    private static volatile bool s_enabled;

    public static bool Enabled => s_enabled;

    public static void Enable()
    {
        using var process = Process.GetCurrentProcess();
        s_processStart = process.StartTime;
        s_enabled = true;
    }

    public static void Mark(string label)
    {
        if (!s_enabled)
        {
            return;
        }

        double ms = (DateTime.Now - s_processStart).TotalMilliseconds;
        lock (s_marks)
        {
            s_marks.Add((label, ms));
        }
    }

    /// <summary>"etiket: 123,4 ms (+12,3)" satırları: her satır bir önceki noktaya göre fark da gösterir.</summary>
    public static string Report()
    {
        var sb = new StringBuilder();
        lock (s_marks)
        {
            double previous = 0;
            foreach (var (label, ms) in s_marks)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"mark {label,-18} {ms,8:F1} ms  (+{ms - previous,6:F1})"));
                previous = ms;
            }
        }

        return sb.ToString();
    }
}
