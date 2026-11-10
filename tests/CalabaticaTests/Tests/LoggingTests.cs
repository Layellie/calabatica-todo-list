using System.IO;
using System.Windows.Threading;
using CyberTodo;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.ViewModels;
using CyberTodo.Views;
using static CalabaticaTests.TestSupport;

namespace CalabaticaTests;
internal static class LoggingTests
{
    public static async Task Run(TestContext ctx)
    {
        void Check(bool ok, string name) => ctx.Check(ok, name);
        string dir = ctx.Dir;
        string path = ctx.DataPath;
        var clock = ctx.Clock;
        Task<MainViewModel> Fresh() => ctx.Fresh();

        // ══════════════ Log ══════════════
        Check(File.Exists(Path.Combine(dir, "logs", "app.log")) && File.ReadAllText(Path.Combine(dir, "logs", "app.log")).Contains("ERROR [MainViewModel]"),
            "log: caught errors (e.g. invalid import) are written to app.log");
        string? crash1 = null;
        for (int i = 0; i < AppLog.MaxCrashFiles + 3; i++)
        {
            crash1 ??= AppLog.WriteCrash(new InvalidOperationException("boom " + i), "Test");
            Thread.Sleep(2); // dosya adı milisaniyeli
            AppLog.WriteCrash(new InvalidOperationException("boom " + i), "Test");
        }

        var crashFiles = Directory.GetFiles(Path.Combine(dir, "logs"), "crash-*.log");
        Check(crashFiles.Length == AppLog.MaxCrashFiles && File.ReadAllText(crashFiles.Max()!).Contains("boom")
            && File.ReadAllText(crashFiles.Max()!).Contains("Version"), "log: crash report written, newest 10 kept");
        string bigLog = Path.Combine(dir, "logs", "app.log");
        File.WriteAllText(bigLog, new string('x', (int)AppLog.MaxAppLogBytes + 10));
        AppLog.Warn("Test", "after rotate");
        Check(File.Exists(Path.Combine(dir, "logs", "app.1.log")) && new FileInfo(bigLog).Length < 1000, "log: app.log rotates at 1 MB");

        Check(Localizer.MissingKeys().Count == 0, "TR/EN key sets identical after new features: " + string.Join(",", Localizer.MissingKeys()));
    }
}
