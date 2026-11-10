using System.Runtime;
using System.Runtime.InteropServices;

namespace CyberTodo.Services;

/// <summary>
/// Tepside (pencere gizli / küçültülmüş) bellek kırpma: sıkıştıran bir GC + çalışma kümesini boşaltma.
/// Ölçüm (2.500 görev): gizli pencerede çalışma kümesi ~270-300 MB → ~3-8 MB; saat çalışırken 5 sn sonra
/// 14-43 MB. Sayfalar atılmaz, bekleme listesine geçer ve gerekince geri gelir (hafif sayfa hatası).
/// Not: "private bytes" (ayrılmış bellek) aynı oranda düşmez; kazanç, başka bir uygulama (ör. oyun) bellek
/// istediğinde sistemin bu sayfaları hemen geri alabilmesi ve Görev Yöneticisi'nde görünen değerdir.
/// Tek seferlik (gizlendikten sonra) çağrılır; periyodik kırpma sayfa hatası yağmuru yaratırdı.
/// </summary>
public static class MemoryTrimmer
{
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>Kırpar. Çalışma kümesi boşaltılamazsa false (GC yine de yapılmıştır); hiçbir zaman fırlatmaz.</summary>
    public static bool Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        bool emptied = EmptyWorkingSet(GetCurrentProcess());
        if (!emptied)
        {
            AppLog.Warn("MemoryTrimmer", $"EmptyWorkingSet failed (error {Marshal.GetLastWin32Error()})");
        }

        return emptied;
    }
}
