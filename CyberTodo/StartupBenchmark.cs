using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CyberTodo.Services;
using CyberTodo.ViewModels;

namespace CyberTodo;

/// <summary>
/// Gerçek açılış yolunu ölçen tanılama modu: <c>CALABATICA.exe --startup-benchmark &lt;rapor.txt&gt;</c>.
/// Normal Program.Main yolunu (tepsi simgesi, ön yükleme, XAML, render) çalıştırır; farklar:
///  - pencere EKRAN DIŞINDA açılır ve odak/görev çubuğu almaz (oyun/başka uygulama bozulmaz),
///  - tek-örnek kilidi ve "göster" sinyali atlanır (çalışan gerçek uygulamaya dokunmaz),
///  - GERÇEK veri klasörü kullanılmaz: <c>CALABATICA_DATA_DIR</c> zorunludur (yoksa başlamaz).
/// Pencere çizildikten ve veri yüklendikten sonra rapor yazılır (zaman çizelgesi, JIT sayısı/süresi, bellek,
/// yüklü derlemeler); sonra "&lt;rapor&gt;.exit" dosyası belirene kadar (en çok 60 sn) açık kalır ki dışarıdan
/// bellek örneklenebilsin, ardından temiz kapanır (tepside hayalet simge kalmaz).
/// </summary>
internal static class StartupBenchmark
{
    public const string Argument = "--startup-benchmark";
    public const string JitTraceArgument = "--jit-trace";

    private static JitListener? s_jit;

    private static readonly TimeSpan s_maxHold = TimeSpan.FromSeconds(60);

    /// <summary>Argüman varsa rapor yolu; yoksa null.</summary>
    public static string? ParseOutputPath(string[] args)
    {
        int i = Array.FindIndex(args, static a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));
        if (i < 0)
        {
            return null;
        }

        return i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : throw new ArgumentException($"{Argument} needs an output file path.");
    }

    /// <summary>Argüman varsa JIT'lenen metotları ad alanına göre sayar (rapora yazılır). Olay dinleyici maliyet ekler: yalnızca tanı için.</summary>
    public static void EnableJitTraceIfRequested(string[] args)
    {
        if (args.Any(static a => string.Equals(a, JitTraceArgument, StringComparison.OrdinalIgnoreCase)))
        {
            s_jit = new JitListener();
        }
    }

    /// <summary>Gerçek veriye dokunulmasın: veri klasörü ortam değişkeniyle verilmek zorunda.</summary>
    public static void RequireIsolatedData()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(StorageService.DataDirectoryVariable)))
        {
            throw new InvalidOperationException($"{Argument} requires {StorageService.DataDirectoryVariable} (it must never touch your real data).");
        }
    }

    /// <summary>Pencereyi ekran dışına alır ve odak/görev çubuğu almasını engeller (Show'dan önce çağrılır).</summary>
    public static void MakeInvisibleToUser(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
    }

    public static void Attach(Application app, MainWindow window, MainViewModel viewModel, string reportPath)
    {
        bool rendered = false;
        window.ContentRendered += (_, _) =>
        {
            rendered = true;
            StartupTrace.Mark("content-rendered");
        };

        var poll = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(5) };
        poll.Tick += (_, _) =>
        {
            if (!rendered || !viewModel.IsInitialized)
            {
                return;
            }

            poll.Stop();
            StartupTrace.Mark("ready");
            // Boşta (tüm ilk düzen/çizim bitti) iken raporla: ölçümün kendisi gecikme eklemesin.
            app.Dispatcher.InvokeAsync(() =>
            {
                StartupTrace.Mark("idle");
                File.WriteAllText(reportPath, BuildReport(), new UTF8Encoding(false));
                HoldUntilReleased(window, reportPath + ".exit");
            }, DispatcherPriority.ApplicationIdle);
        };
        poll.Start();
    }

    private static void HoldUntilReleased(MainWindow window, string sentinel)
    {
        var started = DateTime.Now;
        var wait = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        wait.Tick += (_, _) =>
        {
            if (File.Exists(sentinel) || DateTime.Now - started > s_maxHold)
            {
                wait.Stop();
                window.ExitForBenchmark();
            }
        };
        wait.Start();
    }

    private static string BuildReport()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var sb = new StringBuilder();
        sb.Append(StartupTrace.Report());
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"jit methods compiled      {JitInfo.GetCompiledMethodCount(currentThread: false)}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"jit time (all threads)   {JitInfo.GetCompilationTime(currentThread: false).TotalMilliseconds:F0} ms"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"working set              {process.WorkingSet64 / 1048576.0:F0} MB"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"private bytes            {process.PrivateMemorySize64 / 1048576.0:F0} MB"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"managed heap             {GC.GetTotalMemory(forceFullCollection: false) / 1048576.0:F1} MB"));
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(static a => a.GetName().Name ?? "?").Order().ToList();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"loaded assemblies        {assemblies.Count}"));
        sb.AppendLine("assemblies: " + string.Join(",", assemblies));
        var modules = process.Modules.Cast<ProcessModule>().OrderByDescending(static m => m.ModuleMemorySize).Take(12)
            .Select(static m => string.Create(CultureInfo.InvariantCulture, $"{m.ModuleName}={m.ModuleMemorySize / 1048576.0:F1}MB"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"native modules           {process.Modules.Count}"));
        sb.AppendLine("largest modules: " + string.Join(", ", modules));
        if (s_jit is not null)
        {
            sb.AppendLine("jit methods by namespace (top 25):");
            foreach (var (ns, count) in s_jit.Counts.OrderByDescending(static kv => kv.Value).Take(25))
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {count,5}  {ns}"));
            }
        }

        return sb.ToString();
    }

    /// <summary>Çalışma zamanının JIT olaylarını dinler; "MethodLoadVerbose" = bir metot JIT'lendi.</summary>
    private sealed class JitListener : EventListener
    {
        public ConcurrentDictionary<string, int> Counts { get; } = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x10); // JitKeyword
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName is not { } name || !name.StartsWith("MethodLoadVerbose", StringComparison.Ordinal) || e.PayloadNames is null || e.Payload is null)
            {
                return;
            }

            int i = e.PayloadNames.IndexOf("MethodNamespace");
            if (i < 0 || e.Payload[i] is not string ns)
            {
                return;
            }

            int cut = ns.IndexOfAny(['`', '[']);
            string clean = cut >= 0 ? ns[..cut] : ns;
            string[] parts = clean.Split('.');
            string key = parts.Length > 2 && parts[0] is "System" or "CyberTodo" or "MS" ? string.Join('.', parts.Take(3)) : string.Join('.', parts.Take(2));
            Counts.AddOrUpdate(key, 1, static (_, c) => c + 1);
        }
    }
}
