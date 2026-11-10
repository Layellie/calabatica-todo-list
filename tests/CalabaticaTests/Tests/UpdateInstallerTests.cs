using System.IO;
using System.Windows.Threading;
using CyberTodo;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.ViewModels;
using CyberTodo.Views;
using static CalabaticaTests.TestSupport;

namespace CalabaticaTests;
internal static class UpdateInstallerTests
{
    public static async Task Run(TestContext ctx)
    {
        void Check(bool ok, string name) => ctx.Check(ok, name);
        string dir = ctx.Dir;
        string path = ctx.DataPath;
        var clock = ctx.Clock;
        Task<MainViewModel> Fresh() => ctx.Fresh();

        // ══════════════ Otomatik güncelleme ══════════════
        Check(UpdateInstaller.ParseChecksum("ABCDEF0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789  CALABATICA-win-x64.zip\n")
            == "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", "update: checksum file parsed");
        Check(UpdateInstaller.IsAllowedDownloadUri(new Uri("https://objects.githubusercontent.com/x"))
            && !UpdateInstaller.IsAllowedDownloadUri(new Uri("http://github.com/x"))
            && !UpdateInstaller.IsAllowedDownloadUri(new Uri("https://github.com.evil.com/x"))
            && !UpdateInstaller.IsAllowedDownloadUri(new Uri("https://evilgithubusercontent.com/x")), "update: only https GitHub hosts");

        string realExe = Path.Combine(AppContext.BaseDirectory, "CALABATICA.exe"); // ürün adı/sürümü gömülü gerçek exe (1.0.0)
        string installDir = Path.Combine(dir, "install");
        Directory.CreateDirectory(installDir);
        string installedExe = Path.Combine(installDir, "CALABATICA.exe");
        File.WriteAllText(installedExe, "old build");
        Check(!new UpdateInstaller(installedExe).CanSelfUpdate, "update: not an installed copy without Uninstall.exe");
        File.WriteAllText(Path.Combine(installDir, "Uninstall.exe"), "marker");

        byte[] goodZip = MakeZip(("CALABATICA.exe", File.ReadAllBytes(realExe)));
        string goodSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(goodZip));
        var assetUri = new Uri("https://github.com/o/r/releases/download/v1.0.0/CALABATICA-win-x64.zip");
        var shaUri = new Uri("https://github.com/o/r/releases/download/v1.0.0/CALABATICA-win-x64.zip.sha256");
        var release = new ReleaseInfo(new Version(1, 0, 0), "v1.0.0", new Uri("https://github.com/o/r/releases/tag/v1.0.0"), assetUri, shaUri, null);

        async Task<string?> TryStage(FakeHttp http, ReleaseInfo rel)
        {
            try { return await new UpdateInstaller(installedExe, http).DownloadAndStageAsync(rel); }
            catch (InvalidDataException ex) { return "REJECTED:" + ex.Message; }
        }

        var badSha = new FakeHttp { [assetUri] = goodZip, [shaUri] = System.Text.Encoding.ASCII.GetBytes(new string('0', 64)) };
        string? stagedBad = await TryStage(badSha, release);
        Check(stagedBad?.StartsWith("REJECTED:SHA256") == true && !File.Exists(installedExe + ".new") && !File.Exists(Path.Combine(installDir, "CALABATICA.update.zip")),
            "update: SHA256 mismatch rejected, no files left: " + stagedBad);

        byte[] extraZip = MakeZip(("CALABATICA.exe", File.ReadAllBytes(realExe)), ("..\\evil.dll", [1, 2, 3]));
        var extra = new FakeHttp { [assetUri] = extraZip, [shaUri] = System.Text.Encoding.ASCII.GetBytes(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(extraZip))) };
        Check((await TryStage(extra, release))?.StartsWith("REJECTED:Unexpected update package content") == true, "update: package with extra/traversal entries rejected");

        var redirect = new FakeHttp { [assetUri] = goodZip, [shaUri] = System.Text.Encoding.ASCII.GetBytes(goodSha), RedirectTo = new Uri("https://evil.example.com/x.zip") };
        Check((await TryStage(redirect, release))?.StartsWith("REJECTED:Redirected") == true, "update: redirect to foreign host rejected");

        var good = new FakeHttp { [assetUri] = goodZip, [shaUri] = System.Text.Encoding.ASCII.GetBytes(goodSha + "  CALABATICA-win-x64.zip") };
        Check((await TryStage(good, release with { Version = new Version(9, 9, 9) }))?.StartsWith("REJECTED:Version mismatch") == true, "update: version mismatch rejected");

        string json = $$"""
            {"tag_name":"v1.0.0","html_url":"https://github.com/o/r/releases/tag/v1.0.0","draft":false,"prerelease":false,
             "assets":[{"name":"CALABATICA-win-x64.zip","browser_download_url":"{{assetUri}}"},
                       {"name":"CALABATICA-win-x64.zip.sha256","browser_download_url":"{{shaUri}}"}]}
            """;
        var updVm = new MainViewModel(new StorageService(Path.Combine(dir, "upd.json")),
            new UpdateService("o/r", new Version(0, 9, 0), _ => Task.FromResult(json)),
            installer: new UpdateInstaller(installedExe, good));
        await updVm.InitializeAsync();
        string? restartedWith = null;
        updVm.RestartRequested += (_, path) => restartedWith = path;
        await updVm.CheckUpdatesCommand.ExecuteAsync(null);
        Check(updVm.CanInstallUpdate && updVm.Toasts.Any(t => t.HasAction && t.ActionLabel == Localizer.Instance["Update_Install"]), "update: install offered (button + toast)");
        await updVm.InstallUpdateCommand.ExecuteAsync(null);
        Check(restartedWith == installedExe && File.ReadAllBytes(installedExe).SequenceEqual(File.ReadAllBytes(realExe))
            && File.ReadAllText(installedExe + ".old") == "old build", "update: verified exe swapped in, old kept, restart requested");
        UpdateInstaller.CleanupLeftovers(installedExe);
        Check(!File.Exists(installedExe + ".old"), "update: leftovers cleaned on next launch");

        var notInstalled = new MainViewModel(new StorageService(Path.Combine(dir, "upd2.json")),
            new UpdateService("o/r", new Version(0, 9, 0), _ => Task.FromResult(json)),
            installer: new UpdateInstaller(Path.Combine(dir, "CALABATICA.exe"), good));
        await notInstalled.InitializeAsync();
        await notInstalled.CheckUpdatesCommand.ExecuteAsync(null);
        Check(notInstalled.HasUpdate && !notInstalled.CanInstallUpdate, "update: dev build only offers the release page");
    }
}
