using System.IO;
using System.Windows.Threading;
using CyberTodo;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.ViewModels;
using CyberTodo.Views;
using static CalabaticaTests.TestSupport;

namespace CalabaticaTests;
internal static class DataAndPlatformTests
{
    public static async Task Run(TestContext ctx)
    {
        void Check(bool ok, string name) => ctx.Check(ok, name);
        string dir = ctx.Dir;
        string path = ctx.DataPath;
        var clock = ctx.Clock;
        Task<MainViewModel> Fresh() => ctx.Fresh();

        // ══════════════ Şema geçişleri (örnek eski dosyalar) ══════════════
        Check(SchemaMigrations.LatestVersion == CyberData.CurrentSchemaVersion, "migrations: last step matches CurrentSchemaVersion");
        string v2Json = """
            {"schemaVersion":2,"nextTaskNumber":3,"tasks":[
              {"id":"6f1f8e8e-0000-4000-8000-000000000001","number":1,"title":"Old done","estimatedHours":2.5,"isCompleted":true,"completedAt":"2025-01-02T10:00:00"},
              {"id":"6f1f8e8e-0000-4000-8000-000000000002","number":2,"title":"Daily","recurrence":"Daily","createdAt":"2025-01-01T09:00:00"}]}
            """;
        string v2Path = Path.Combine(dir, "v2.json");
        File.WriteAllText(v2Path, v2Json);
        var v2 = await StorageService.ReadFileAsync(v2Path);
        Check(v2.SchemaVersion == CyberData.CurrentSchemaVersion && v2.Tasks[0].CompletionCreditHours == 2.5
            && v2.Tasks[1].SeriesId == v2.Tasks[1].Id && v2.FocusSessions.Count == 0 && v2.Archive.TaskCount == 0,
            "migrations: v2 file -> v3 credit, v5 series, new lists initialized");
        var again = await StorageService.ReadFileAsync(v2Path);
        SchemaMigrations.Apply(again);
        Check(again.Tasks[0].CompletionCreditHours == 2.5, "migrations: re-applying on current data is a no-op");

        string newerPath = Path.Combine(dir, "newer", "cyber_data.json");
        Directory.CreateDirectory(Path.GetDirectoryName(newerPath)!);
        string newerJson = """{"schemaVersion":99,"nextTaskNumber":2,"futureField":{"x":1},"tasks":[{"number":1,"title":"From the future"}]}""";
        File.WriteAllText(newerPath, newerJson);
        var nv = new MainViewModel(new StorageService(newerPath));
        await nv.InitializeAsync();
        nv.NewTaskTitle = "change";
        await nv.AddTaskCommand.ExecuteAsync(null);
        nv.FlushNow();
        Check(nv.ErrorMessage is not null && File.ReadAllText(newerPath) == newerJson && nv.ActiveTasks.Any(t => t.Title == "From the future"),
            "migrations: file from a newer version opens read-only (unknown fields never overwritten)");
        bool importRejected = false;
        try { await StorageService.ReadFileAsync(newerPath); } catch (InvalidDataException) { importRejected = true; }
        Check(importRejected, "migrations: importing/restoring a newer-version file is refused");

        // ══════════════ Arşiv (400 günden eski tamamlananlar) ══════════════
        string arcPath = Path.Combine(dir, "arc", "cyber_data.json");
        var arcNow = new DateTime(2026, 11, 10, 12, 0, 0);
        var arcData = new CyberData();
        arcData.Tasks.Add(new CyberTask { Number = 1, Title = "Ancient", IsCompleted = true, CompletedAt = arcNow.AddDays(-700), TrackedMinutes = 30 });
        arcData.Tasks.Add(new CyberTask { Number = 2, Title = "Old", IsCompleted = true, CompletedAt = arcNow.AddDays(-401), TrackedMinutes = 15 });
        arcData.Tasks.Add(new CyberTask { Number = 3, Title = "Recent", IsCompleted = true, CompletedAt = arcNow.AddDays(-399) });
        arcData.Tasks.Add(new CyberTask { Number = 4, Title = "Active" });
        arcData.NextTaskNumber = 5;
        await StorageService.WriteAtomicAsync(arcPath, StorageService.Serialize(arcData));
        var av = new MainViewModel(new StorageService(arcPath), time: new ManualTime(arcNow));
        await av.InitializeAsync();
        for (int i = 0; i < 50 && av.CompletedTasks.Count != 1; i++) { await Task.Delay(20); } // arşiv açılışta arka planda
        var arcFiles = new ArchiveService(arcPath).List();
        Check(av.CompletedTasks.Select(t => t.Title).SequenceEqual(["Recent"]) && av.ActiveTasks.Single().Title == "Active",
            "archive: tasks completed > 400 days ago leave the main data");
        Check(arcFiles.Count == 2 && arcFiles.All(f => Path.GetFileName(f).StartsWith("completed-20")), "archive: one file per completion year: " + string.Join(",", arcFiles.Select(Path.GetFileName)));
        var archivedTitles = new List<string>();
        foreach (var f in arcFiles) { archivedTitles.AddRange((await StorageService.ReadFileAsync(f)).Tasks.Select(t => t.Title)); }
        Check(archivedTitles.Order().SequenceEqual(["Ancient", "Old"]), "archive: archived tasks readable with the normal reader");
        av.FlushNow();
        var arcSaved = await StorageService.ReadFileAsync(arcPath);
        Check(arcSaved.Archive.TaskCount == 2 && arcSaved.Archive.TrackedMinutes == 45 && arcSaved.Tasks.Count == 2 && av.ArchiveText.Contains('2'),
            "archive: summary keeps totals (2 tasks, 45 focus min)");
        av.ShowStatsCommand.Execute(null);
        Check(av.StatTiles.Any(t => t.Value.Contains("45")), "archive: focus total in stats includes archived minutes");

        // Çökme benzetimi: görev hem arşivde hem ana veride → tekrar arşivlenince kopya olmaz, sayaç şişmez.
        var dupData = await StorageService.ReadFileAsync(arcPath);
        dupData.Tasks.Add(new CyberTask { Id = (await StorageService.ReadFileAsync(arcFiles[^1])).Tasks[0].Id, Title = "Ancient", IsCompleted = true, CompletedAt = arcNow.AddDays(-700) });
        await StorageService.WriteAtomicAsync(arcPath, StorageService.Serialize(dupData));
        var av2 = new MainViewModel(new StorageService(arcPath), time: new ManualTime(arcNow));
        await av2.InitializeAsync();
        for (int i = 0; i < 50 && av2.CompletedTasks.Count != 1; i++) { await Task.Delay(20); }
        av2.FlushNow();
        int ancientCopies = 0;
        foreach (var f in new ArchiveService(arcPath).List()) { ancientCopies += (await StorageService.ReadFileAsync(f)).Tasks.Count(t => t.Title == "Ancient"); }
        Check(ancientCopies == 1 && (await StorageService.ReadFileAsync(arcPath)).Archive.TaskCount == 2, "archive: re-archiving after a crash creates no duplicates");

        // Arşiv yazılamazsa hiçbir şey silinmez.
        string blockedPath = Path.Combine(dir, "arc-blocked", "cyber_data.json");
        await StorageService.WriteAtomicAsync(blockedPath, StorageService.Serialize(arcData));
        File.WriteAllText(Path.Combine(dir, "arc-blocked", "archive"), "not a folder");
        var bv = new MainViewModel(new StorageService(blockedPath), time: new ManualTime(arcNow));
        await bv.InitializeAsync();
        int archivedNow = await bv.ArchiveExpiredTasksAsync(arcNow);
        Check(archivedNow == 0 && bv.CompletedTasks.Count == 3 && bv.ErrorMessage is not null, "archive: write failure keeps every task in place");

        // ══════════════ Render modu ve bellek kırpma ══════════════
        Check(RenderPolicy.Parse(null) == RenderPreference.Software && RenderPolicy.Parse("") == RenderPreference.Software
            && RenderPolicy.Parse("software") == RenderPreference.Software && RenderPolicy.Parse("bogus") == RenderPreference.Software
            && RenderPolicy.Parse("gpu") == RenderPreference.Gpu && RenderPolicy.Parse("  GPU ") == RenderPreference.Gpu,
            "render: CALABATICA_RENDER=gpu is the only way to opt out of software rendering");

        static long WorkingSetMb() { using var p = System.Diagnostics.Process.GetCurrentProcess(); return p.WorkingSet64 / 1048576; }
        static void AllocateAndRelease() { var big = new byte[160 * 1024 * 1024]; Array.Fill(big, (byte)7); GC.KeepAlive(big); }
        AllocateAndRelease();
        long beforeTrim = WorkingSetMb();
        bool trimmed = MemoryTrimmer.Trim();
        long afterTrim = WorkingSetMb();
        Check(trimmed && afterTrim < beforeTrim - 80, $"memory trim: working set {beforeTrim} MB -> {afterTrim} MB after releasing 160 MB");

        // ══════════════ Veriyi önceden yükleme (BeginPreload) ══════════════
        string prePath = Path.Combine(dir, "preload", "cyber_data.json");
        var preSeed = new CyberData();
        preSeed.Tasks.Add(new CyberTask { Number = 1, Title = "Preloaded 1" });
        preSeed.Tasks.Add(new CyberTask { Number = 2, Title = "Preloaded 2", IsCompleted = true, CompletedAt = DateTime.Now });
        preSeed.NextTaskNumber = 3;
        await StorageService.WriteAtomicAsync(prePath, StorageService.Serialize(preSeed));
        var pv = new MainViewModel(new StorageService(prePath));
        pv.BeginPreload();
        pv.BeginPreload(); // ikinci çağrı ikinci okuma başlatmaz
        await pv.InitializeAsync();
        Check(pv.ActiveTasks.Single().Title == "Preloaded 1" && pv.CompletedTasks.Single().Title == "Preloaded 2" && pv.ErrorMessage is null,
            "preload: initialize uses the pre-started load (same data as a normal load)");

        string preBadPath = Path.Combine(dir, "preload-bad", "cyber_data.json");
        Directory.CreateDirectory(Path.GetDirectoryName(preBadPath)!);
        File.WriteAllText(preBadPath, "{ not json");
        var pbv = new MainViewModel(new StorageService(preBadPath));
        pbv.BeginPreload();
        await pbv.InitializeAsync();
        Check(pbv.ErrorMessage is not null && pbv.ActiveTasks.Count == 0 && Directory.GetFiles(Path.GetDirectoryName(preBadPath)!, "cyber_data.corrupt-*.json").Length == 1,
            "preload: corrupt file is still backed up and reported");

        string preLockPath = Path.Combine(dir, "preload-lock", "cyber_data.json");
        await StorageService.WriteAtomicAsync(preLockPath, StorageService.Serialize(preSeed));
        string lockedContent = File.ReadAllText(preLockPath);
        using (var exclusive = new FileStream(preLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var plv = new MainViewModel(new StorageService(preLockPath));
            plv.BeginPreload();
            await plv.InitializeAsync();
            plv.NewTaskTitle = "must not overwrite";
            await plv.AddTaskCommand.ExecuteAsync(null);
            plv.FlushNow();
            Check(plv.ErrorMessage is not null, "preload: a locked file reports an error and disables saving");
        }

        Check(File.ReadAllText(preLockPath) == lockedContent, "preload: locked file content was never overwritten");

        // ══════════════ Sürüm etiketi sağlamlığı ══════════════
        bool versionThrew = false; bool versionParsed = true;
        try { versionParsed = UpdateService.TryParseVersion("v\u0661.\u0662", out _); } // Arapça-Hint rakamlar: regex \d eşler, int.Parse reddeder
        catch (FormatException) { versionThrew = true; }
        Check(!versionThrew && !versionParsed, "update: non-ASCII digits in a release tag are rejected without throwing");
        Check(UpdateService.TryParseVersion("v1.2.3", out var v123) && v123 == new Version(1, 2, 3)
            && UpdateService.TryParseVersion("2.1", out var v21) && v21 == new Version(2, 1, 0)
            && !UpdateService.TryParseVersion("v1.2.3-beta", out _) && !UpdateService.TryParseVersion("1.2.3.4", out _) && !UpdateService.TryParseVersion("", out _)
            && !UpdateService.TryParseVersion("v123456", out _), "update: normal tags still parse, malformed ones are rejected");
    }
}
