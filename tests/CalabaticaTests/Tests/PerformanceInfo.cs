using System.IO;
using System.Windows.Threading;
using CyberTodo;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.ViewModels;
using CyberTodo.Views;
using static CalabaticaTests.TestSupport;

namespace CalabaticaTests;
internal static class PerformanceInfo
{
    public static async Task Run(TestContext ctx)
    {
        void Check(bool ok, string name) => ctx.Check(ok, name);
        string dir = ctx.Dir;
        string path = ctx.DataPath;
        var clock = ctx.Clock;
        Task<MainViewModel> Fresh() => ctx.Fresh();

        // ── Bilgi (test değil): 2500 görevlik veride bir kaydın maliyeti ──
        var big = new CyberData();
        for (int i = 1; i <= 2500; i++)
        {
            big.Tasks.Add(new CyberTask
            {
                Number = i, Title = $"Task number {i} with a reasonably long title", Category = $"cat{i % 8}",
                Notes = $"note {i}", EstimatedHours = 1, DueAt = DateTime.Today.AddDays(i % 20), HasDueTime = true,
                Subtasks = [new SubTask { Title = "a", IsDone = true }, new SubTask { Title = "b" }],
                IsCompleted = i > 500, CompletedAt = i > 500 ? DateTime.Today.AddDays(-(i % 60)) : null,
            });
        }
        var bigStorage = new StorageService(Path.Combine(dir, "big.json"));
        await bigStorage.SaveAsync(big); // ısınma
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 5; i++) { await bigStorage.SaveAsync(big); }
        Console.WriteLine($"INFO  save 2500 tasks: {sw.Elapsed.TotalMilliseconds / 5:F1} ms avg, file {new FileInfo(Path.Combine(dir, "big.json")).Length / 1024} KB");
    }
}
