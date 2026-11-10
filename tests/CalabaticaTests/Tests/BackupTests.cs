using System.IO;
using System.Windows.Threading;
using CyberTodo;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.ViewModels;
using CyberTodo.Views;
using static CalabaticaTests.TestSupport;

namespace CalabaticaTests;
internal static class BackupTests
{
    public static async Task Run(TestContext ctx)
    {
        void Check(bool ok, string name) => ctx.Check(ok, name);
        string dir = ctx.Dir;
        string path = ctx.DataPath;
        var clock = ctx.Clock;
        Task<MainViewModel> Fresh() => ctx.Fresh();

        // ══════════════ Yedekler ══════════════
        var fToday = DateOnly.FromDateTime(DateTime.Today);
        string bkData = Path.Combine(dir, "bk", "cyber_data.json");
        var backups = new BackupService(bkData);
        byte[] payload = StorageService.Serialize(new CyberData());
        for (int i = 0; i < 9; i++)
        {
            await backups.CreateDailyAsync(payload, fToday.AddDays(-i));
        }

        Check(backups.List().Count(b => b.IsDaily) == BackupService.KeepDaily
            && backups.List().All(b => b.FileName != BackupService.DailyFileName(fToday.AddDays(-8))), "backup: keeps newest 7 daily backups");
        Check(await backups.CreateDailyAsync(payload, fToday) is null, "backup: once per day");
        for (int i = 0; i < 7; i++)
        {
            await backups.CreateSafetyAsync(payload, DateTime.Now.AddSeconds(i), "manual");
        }

        Check(backups.List().Count(b => !b.IsDaily) == BackupService.KeepSafety, "backup: keeps 5 safety backups");
        Check(!backups.Contains(Path.Combine(dir, "bk", "cyber_data.json"))
            && !backups.Contains(Path.Combine(backups.BackupDirectory, "..", "cyber_data-x.json"))
            && backups.Contains(backups.List()[0].FullPath), "backup: only files inside backup folder are accepted");
        bool rejected = false;
        try { await backups.CreateSafetyAsync(payload, DateTime.Now, "..\\evil"); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "backup: reason cannot inject a path");

        // VM: açılışta günlük yedek, geri yükleme, içe/dışa aktarma
        string vmData = Path.Combine(dir, "vmdata", "cyber_data.json");
        var seed = new CyberData();
        seed.Tasks.Add(new CyberTask { Number = 1, Title = "Original" });
        seed.NextTaskNumber = 2;
        await StorageService.WriteAtomicAsync(vmData, StorageService.Serialize(seed));
        var dialogs = new FakeDialogs();
        var dv = new MainViewModel(new StorageService(vmData), dialogs: dialogs);
        await dv.InitializeAsync();
        await Task.Delay(300);
        var dvBackups = new BackupService(vmData);
        Check(dvBackups.List().Any(b => b.FileName == BackupService.DailyFileName(fToday)), "backup: daily backup taken on launch");

        var other = new CyberData();
        other.Tasks.Add(new CyberTask { Number = 1, Title = "From backup 1" });
        other.Tasks.Add(new CyberTask { Number = 2, Title = "From backup 2", IsCompleted = true, CompletedAt = DateTime.Now });
        string otherPath = await dvBackups.CreateSafetyAsync(StorageService.Serialize(other), DateTime.Now.AddMinutes(-5), "test");
        dv.OpenDataCommand.Execute(null);
        var otherInfo = dv.Backups.First(b => b.FullPath == otherPath);

        dialogs.ConfirmResult = false;
        await dv.RestoreBackupCommand.ExecuteAsync(otherInfo);
        Check(dv.ActiveTasks.Single().Title == "Original" && dialogs.LastConfirm.Contains("2"), "restore: declined -> nothing changes");

        dialogs.ConfirmResult = true;
        dv.NewTaskTitle = "Undo me later";
        await dv.AddTaskCommand.ExecuteAsync(null);
        await dv.RestoreBackupCommand.ExecuteAsync(otherInfo);
        Check(dv.ActiveTasks.Single().Title == "From backup 1" && dv.CompletedTasks.Single().Title == "From backup 2", "restore: data replaced");
        Check(!dv.CanUndo, "restore: undo history cleared (would point to old data)");
        Check(File.ReadAllText(vmData).Contains("From backup 1"), "restore: written to disk immediately");
        Check(dvBackups.List().Any(b => b.FileName.Contains("before-restore")) && !dv.IsDataOpen, "restore: safety backup of previous state, panel closed");
        var safetyFile = dvBackups.List().First(b => b.FileName.Contains("before-restore"));
        Check((await StorageService.ReadFileAsync(safetyFile.FullPath)).Tasks.Any(t => t.Title == "Undo me later"), "restore: safety backup contains the replaced data");

        string exportPath = Path.Combine(dir, "export", "out.json");
        dialogs.SavePath = exportPath;
        await dv.ExportJsonCommand.ExecuteAsync(null);
        Check((await StorageService.ReadFileAsync(exportPath)).Tasks.Count == 2, "export: JSON round-trips");
        dialogs.SavePath = Path.Combine(dir, "export", "out.csv");
        await dv.ExportCsvCommand.ExecuteAsync(null);
        byte[] csvBytes = File.ReadAllBytes(dialogs.SavePath);
        Check(csvBytes.Length > 3 && csvBytes[0] == 0xEF && csvBytes[1] == 0xBB && csvBytes[2] == 0xBF, "export: CSV has UTF-8 BOM for Excel");

        string badImport = Path.Combine(dir, "export", "bad.json");
        File.WriteAllText(badImport, "{ not json");
        dialogs.OpenPath = badImport;
        await dv.ImportJsonCommand.ExecuteAsync(null);
        Check(dv.ErrorMessage is not null && dv.ActiveTasks.Single().Title == "From backup 1", "import: invalid file -> error, data untouched");
        dv.DismissErrorCommand.Execute(null);

        seed.Tasks.Add(new CyberTask { Number = 2, Title = "Imported 2" });
        string goodImport = Path.Combine(dir, "export", "good.json");
        await StorageService.WriteAtomicAsync(goodImport, StorageService.Serialize(seed));
        dialogs.OpenPath = goodImport;
        await dv.ImportJsonCommand.ExecuteAsync(null);
        Check(dv.ActiveTasks.Count == 2 && dv.ActiveTasks.Any(t => t.Title == "Imported 2")
            && dvBackups.List().Any(b => b.FileName.Contains("before-import")), "import: replaces data after safety backup");
    }
}
